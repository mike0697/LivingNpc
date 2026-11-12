using System;
using System.Collections.Generic;
using LivingNPC.Content;
using LivingNPC.Simulation;
using Unity.Entities;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// The bar of pictures at the top of the game screen (hud.json) and the screens its buttons open. Part of the view: it
    /// reads the ECS state and only writes GameCommands (CommandType.Button, for the events with "trigger": "button").
    /// A button opens a screen (status, npcs), opens / folds a panel of the game screen (journal, backpack, present), and
    /// every press runs its events. No hud.json = no bar.
    /// </summary>
    public class HudView
    {
        /// <summary>The panels of the game screen a button can open and fold: hud.json name -> DraggablePanel title.</summary>
        static readonly Dictionary<string, string> s_Panels = new Dictionary<string, string>
        {
            ["journal"] = "Journal", ["backpack"] = "Backpack", ["present"] = "Present",
        };

        readonly Action<int> _press;
        RectTransform _bar, _tooltip;
        Text _tooltipText;
        readonly HudScreens _screens = new HudScreens();
        readonly OwnedTextures _icons = new OwnedTextures(true, true);

        EntityManager _em;
        Entity _world, _player;
        CompiledContent _compiled;
        CompiledHud _hud;

        class Slot
        {
            public CompiledHudButton Def;
            public GameObject Go;
            public Button Button;
            public Image Plate;
            public RawImage Icon;
            public Key Key = Key.None;
        }
        readonly List<Slot> _slots = new List<Slot>();

        bool _busy, _dialog;

        /// <param name="press">Sends CommandType.Button with the hashed id of the pressed button.</param>
        public HudView(Action<int> press) { _press = press; }

        /// <summary>The bar, under the top bar between the side panels (created with the bars, so the panels and dialogue draw over it).</summary>
        public void BuildBar(Transform canvas)
        {
            _bar = UiKit.Rect(canvas, "HudBar", new Vector2(0.23f, 0.94f), new Vector2(0.77f, 0.94f));
            _bar.pivot = new Vector2(0.5f, 1);
            var h = _bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 10; h.childControlWidth = true; h.childControlHeight = true; h.childForceExpandWidth = false; h.childForceExpandHeight = false;

        }

        /// <summary>The screens and the name of the button under the pointer: above the panels and the quest pictures, below the dialogue (which closes them).</summary>
        public void BuildScreens(Transform canvas)
        {
            _screens.Build(canvas, () => CloseScreen());
            // the name of the button under the pointer, below it
            _tooltip = UiKit.Panel(canvas, "HudTooltip", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Color(0.04f, 0.05f, 0.08f, 0.92f));
            _tooltip.pivot = new Vector2(0.5f, 1);
            _tooltip.GetComponent<Image>().raycastTarget = false;
            var th = _tooltip.gameObject.AddComponent<HorizontalLayoutGroup>();
            th.padding = new RectOffset(12, 12, 4, 6); th.childControlWidth = true; th.childControlHeight = true;
            _tooltip.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            _tooltip.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _tooltipText = UiKit.Label(_tooltip, "Text", 22, TextAnchor.MiddleCenter, Vector2.zero, Vector2.one);
            _tooltipText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _tooltip.gameObject.SetActive(false);
        }

        public void Bind(EntityManager em, Entity world, Entity player, ContentDatabase db, CompiledContent compiled)
        {
            _em = em; _world = world; _player = player; _compiled = compiled;
            _hud = compiled.Hud;
            UiKit.Clear(_bar);
            _slots.Clear();
            _icons.Clear();
            _tooltip.gameObject.SetActive(false);
            _screens.Bind(em, world, player, db, compiled);
            if (_hud == null) { _screens.Close(); return; }

            float plate = _hud.Size + 10;
            _bar.sizeDelta = new Vector2(0, plate);
            _bar.anchoredPosition = new Vector2(0, -6);
            var layout = _bar.GetComponent<HorizontalLayoutGroup>();
            layout.childAlignment = _hud.Align == 0 ? TextAnchor.UpperLeft : _hud.Align == 2 ? TextAnchor.UpperRight : TextAnchor.UpperCenter;
            _screens.Top = plate + 12;

            foreach (var def in _hud.Buttons) _slots.Add(MakeSlot(def, plate));
            if (_screens.Current != null && !HasScreenButton(_screens.Current)) _screens.Close();      // its button was removed (F5)
        }

        Slot MakeSlot(CompiledHudButton def, float plate)
        {
            var s = new Slot { Def = def };
            if (def.Key != null && Enum.TryParse(def.Key, out Key key)) s.Key = key;
            string label = TextTable.Get(def.LabelText);
            var tex = _icons.Get(def.Icon);
            if (tex != null)
            {
                var rt = UiKit.Panel(_bar, "Hud_" + def.Id, Vector2.zero, Vector2.one, UiKit.PanelColor);
                s.Go = rt.gameObject;
                s.Plate = rt.GetComponent<Image>();
                var le = s.Go.AddComponent<LayoutElement>();
                le.preferredWidth = le.minWidth = plate; le.preferredHeight = le.minHeight = plate;
                var inner = UiKit.Rect(rt, "Icon", Vector2.zero, Vector2.one);
                inner.offsetMin = new Vector2(5, 5); inner.offsetMax = new Vector2(-5, -5);
                var pic = UiKit.Rect(inner, "Picture", Vector2.zero, Vector2.one);
                var fit = pic.gameObject.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fit.aspectRatio = (float)tex.width / tex.height;
                s.Icon = pic.gameObject.AddComponent<RawImage>();
                s.Icon.texture = tex;
                s.Icon.raycastTarget = false;
                s.Button = s.Go.AddComponent<Button>();
                s.Button.targetGraphic = s.Plate;
                var colors = s.Button.colors;
                colors.highlightedColor = new Color(1.4f, 1.4f, 1.4f, 1f);
                colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
                colors.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);
                s.Button.colors = colors;
                s.Button.onClick.AddListener(() => Press(s));
            }
            else
            {
                // no picture: a button of the game with the label (it takes the pictures of title.json "buttons")
                s.Button = UiKit.Button(_bar, "Hud_" + def.Id, label, 22, () => Press(s));
                s.Go = s.Button.gameObject;
                s.Plate = s.Go.GetComponent<Image>();
                var le = s.Go.AddComponent<LayoutElement>();
                le.preferredWidth = le.minWidth = Mathf.Max(plate * 1.6f, label.Length * 13 + 32);
                le.preferredHeight = le.minHeight = plate;
            }
            var hover = s.Go.AddComponent<HudHover>();
            string tip = s.Key != Key.None ? $"{label}  <color=#8a93a6>[{KeyLabel(def.Key)}]</color>" : label;
            hover.Enter = () => ShowTooltip(s, tip);
            hover.Exit = () => { if (s.Go != null) s.Go.transform.localScale = Vector3.one; _tooltip.gameObject.SetActive(false); };
            return s;
        }

        static string KeyLabel(string key) => key.StartsWith("Digit") ? key.Substring(5) : key;

        void ShowTooltip(Slot s, string text)
        {
            if (string.IsNullOrEmpty(text) || s.Go == null) return;
            if (s.Icon != null) s.Go.transform.localScale = Vector3.one * 1.08f;
            _tooltipText.text = text;
            var rt = (RectTransform)s.Go.transform;
            _tooltip.position = rt.TransformPoint(new Vector3(rt.rect.center.x, rt.rect.yMin - 6, 0));
            _tooltip.gameObject.SetActive(true);
        }

        bool HasScreenButton(string screen)
        {
            foreach (var s in _slots) if (s.Def.Open == screen) return true;
            return false;
        }

        void Press(Slot s)
        {
            if (_dialog) return;
            string open = s.Def.Open;
            if (open != null)
            {
                if (s_Panels.TryGetValue(open, out var panel)) DraggablePanel.Toggle(panel);
                else if (_screens.Current == open) _screens.Close();
                else _screens.Open(open);
            }
            // the events of the button (the simulation refuses them while time passes or the player is on the way)
            if (!_busy) _press(s.Def.Hash);
            Highlight();
        }

        /// <summary>The open screen (status | npcs), null when none.</summary>
        public string CurrentScreen => _screens.Current;

        /// <summary>The buttons on the bar now (their "when" holds), by id.</summary>
        public List<string> ShownButtons()
        {
            var ids = new List<string>();
            foreach (var s in _slots) if (s.Go != null && s.Go.activeSelf) ids.Add(s.Def.Id);
            return ids;
        }

        /// <summary>Presses a button by id as a click would (tools and tests); false when it is not on the bar.</summary>
        public bool PressButton(string id)
        {
            foreach (var s in _slots)
                if (s.Def.Id == id && s.Go != null && s.Go.activeSelf) { Press(s); return true; }
            return false;
        }

        /// <summary>The rows of the open screen's lists (the npcs listed, the lines of the page), for tests.</summary>
        public int ScreenRows(string list) => _screens.Rows(list);

        /// <summary>Esc: closes the open screen. False when none was open (Esc then opens the game menu).</summary>
        public bool CloseScreen()
        {
            if (_screens.Current == null) return false;
            _screens.Close();
            Highlight();
            return true;
        }

        /// <summary>Keys of the buttons; <paramref name="allowed"/> is false while a menu or the debug panel is over the game.</summary>
        public void Tick(bool allowed)
        {
            if (!allowed || _dialog || _slots.Count == 0) return;
            var kb = Keyboard.current;
            if (kb == null) return;
            foreach (var s in _slots)
                if (s.Key != Key.None && s.Go != null && s.Go.activeSelf && kb[s.Key].wasPressedThisFrame) { Press(s); break; }
        }

        /// <summary>After every visible change of the world: which buttons are on the bar, which can be pressed, the open screen.</summary>
        public void Refresh(WorldState ws, bool busy)
        {
            _busy = busy;
            _dialog = ws.DialogOpen != 0;
            if (_hud == null) return;
            var rt = RuleRuntime.Create(_em, _world, _player);
            bool any = false;
            foreach (var s in _slots)
            {
                if (s.Go == null) continue;
                bool shown = rt.Eval(s.Def.When);
                if (s.Go.activeSelf != shown) s.Go.SetActive(shown);
                any |= shown;
                // a button with only events waits while time passes; screens can be looked at, but not during a dialogue
                bool can = !_dialog && (s.Def.Open != null || !busy);
                s.Button.interactable = can;
                if (s.Icon != null) s.Icon.color = can ? Color.white : new Color(1, 1, 1, 0.45f);
                if (!shown && _screens.Current != null && s.Def.Open == _screens.Current && !OtherButtonShows(s)) _screens.Close();
            }
            if (!any) _tooltip.gameObject.SetActive(false);
            if (_dialog) { _screens.Close(); _tooltip.gameObject.SetActive(false); }
            _screens.Refresh();
            Highlight();
        }

        bool OtherButtonShows(Slot except)
        {
            foreach (var s in _slots) if (s != except && s.Def.Open == except.Def.Open && s.Go != null && s.Go.activeSelf) return true;
            return false;
        }

        /// <summary>The button of the open screen or of an open panel is drawn lit.</summary>
        void Highlight()
        {
            foreach (var s in _slots)
            {
                if (s.Plate == null || s.Icon == null) continue;        // label buttons keep the look of the game's buttons
                string open = s.Def.Open;
                bool on = open != null && (s_Panels.TryGetValue(open, out var panel) ? DraggablePanel.IsOpen(panel) : _screens.Current == open);
                s.Plate.color = on ? new Color(0.95f, 0.78f, 0.35f, 0.9f) : UiKit.PanelColor;
            }
        }

        public void Dispose() { _icons.Clear(); _screens.Dispose(); }
    }

    /// <summary>Calls back when the pointer enters or leaves the object.</summary>
    public class HudHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Action Enter, Exit;
        public void OnPointerEnter(PointerEventData e) => Enter?.Invoke();
        public void OnPointerExit(PointerEventData e) => Exit?.Invoke();
        void OnDisable() => Exit?.Invoke();
    }

    /// <summary>
    /// Pictures read from plain files and owned here, not by the shared TextureCache (the map view trims it when the place
    /// changes). With mipmaps and the transparent edge bled, so pictures drawn much smaller than the file stay smooth.
    /// </summary>
    public sealed class OwnedTextures
    {
        readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();
        readonly bool _mipmaps, _bleed;

        /// <param name="bleed">Spread the edge colour into the transparent pixels: for small pictures (icons), it costs time on big ones.</param>
        public OwnedTextures(bool mipmaps, bool bleed) { _mipmaps = mipmaps; _bleed = bleed; }

        public Texture2D Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_cache.TryGetValue(path, out var tex)) return tex;
            tex = null;
            if (System.IO.File.Exists(path))
            {
                tex = ImageFile.Load(path, _mipmaps);
                if (tex != null && _mipmaps)
                {
                    if (_bleed)
                    {
                        var px = tex.GetPixels32();
                        ButtonSkin.BleedAlpha(px, tex.width, tex.height, 4);
                        tex.SetPixels32(px);
                    }
                    tex.filterMode = FilterMode.Trilinear;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.Apply(true, false);
                }
                else if (tex != null) tex.filterMode = FilterMode.Bilinear;
            }
            _cache[path] = tex;
            return tex;
        }

        public void Clear()
        {
            foreach (var t in _cache.Values) if (t != null) UiKit.Kill(t);
            _cache.Clear();
        }
    }
}
