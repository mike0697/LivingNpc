using System.Collections.Generic;
using System.Text;
using LivingNPC.Content;
using LivingNPC.Simulation;
using Unity.Entities;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// Thin, replaceable view: reads ECS state, writes GameCommands. Swap it for any other UI (UI Toolkit,
    /// 3D, a headless LLM player) without touching the simulation.
    /// </summary>
    public class GameView
    {
        EntityManager _em;
        Entity _world, _player;
        ContentDatabase _db;
        CompiledContent _compiled;

        RectTransform _inventoryList;
        RectTransform _mapRect, _linkLayer, _npcList, _questList, _dialogPanel, _dialogChoices, _imageLayer, _overlayLayer, _npcLayer, _portraitLayer, _catcher;
        RawImage _mapImage;
        // one container per stacking slot (index = Slot(layer)): showImage effects and dialogue show steps; slot 3 is _overlayLayer / _imageLayer
        readonly RectTransform[] _overlaySlots = new RectTransform[4], _dialogSlots = new RectTransform[4];
        readonly List<AspectRatioFitter> _slotAspects = new List<AspectRatioFitter>();
        AspectRatioFitter _aspect, _imageAspect, _overlayAspect, _portraitAspect;
        Text _topText, _placeText, _logText, _dialogText, _dialogHint;
        Button _sleepButton;
        readonly List<Button> _lockables = new List<Button>();
        readonly List<(GameObject go, LinkDef def)> _linkButtons = new List<(GameObject, LinkDef)>();

        uint _lastVersion = uint.MaxValue;
        string _currentMapId;
        string _currentBgPath;
        bool _dialogOpen;
        float _lastAdvance;
        string _shownImages = "";                          // signature of the images currently on screen
        readonly List<string> _shownPaths = new List<string>();
        string _shownOverlays = "";                        // same for the images put on screen by showImage effects
        readonly List<string> _overlayPaths = new List<string>();
        string _shownPortrait = "";                        // signature of the talk picture on screen
        string _portraitPath;
        readonly List<string> _npcPaths = new List<string>();                  // textures of the npc pictures on the map
        readonly Dictionary<int, CompiledNpc> _npcById = new Dictionary<int, CompiledNpc>();
        Dictionary<int, string> _statNames = new Dictionary<int, string>();

        GameObject _canvasGo;
        readonly AudioPlayer _audio;

        /// <summary>The sound player, for tools and tests (the game only talks to it through this view).</summary>
        public AudioPlayer Audio => _audio;

        public GameView(Transform root) { Build(root); _audio = new AudioPlayer(root); }

        /// <summary>Hides/shows the whole game UI (the main menu is drawn on top of nothing). Hiding also fades the music and ambient sounds out.</summary>
        public void SetVisible(bool visible)
        {
            if (_canvasGo != null) _canvasGo.SetActive(visible);
            if (!visible) _audio.StopAll(0.6f);
        }

        bool _interactive = true;

        /// <summary>False while a menu is drawn over the game: clicks and keys no longer reach the map, links, panels and dialogue.</summary>
        public void SetInteractive(bool interactive)
        {
            _interactive = interactive;      // the keys too: typing in the debug console must not advance a dialogue
            if (_canvasGo != null) _canvasGo.GetComponent<GraphicRaycaster>().enabled = interactive;
        }

        /// <summary>Sound fades and file loading; runs every frame, also while the main menu is up.</summary>
        public void UpdateAudio() => _audio.Update(Time.unscaledDeltaTime);

        // ------------------------------------------------------------------ layout

        void Build(Transform root)
        {
            var canvasGo = new GameObject("LivingNPCCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(root, false);
            _canvasGo = canvasGo;
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            if (Object.FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                es.transform.SetParent(root, false);
            }

            var bg = UiKit.Panel(canvasGo.transform, "Backdrop", Vector2.zero, Vector2.one, Color.black);

            _mapRect = UiKit.Rect(canvasGo.transform, "Map", Vector2.zero, Vector2.one);
            _aspect = _mapRect.gameObject.AddComponent<AspectRatioFitter>();
            _aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _aspect.aspectRatio = 16f / 9f;
            _mapImage = _mapRect.gameObject.AddComponent<RawImage>();
            _mapImage.color = Color.white;
            // image layers (see CompiledImage.Layer): 0 above the map | 10 above the npc pictures | 20 above the links | 30+ above the interface
            _overlaySlots[0] = UiKit.Rect(_mapRect, "OverlayImages_0", Vector2.zero, Vector2.one);
            _dialogSlots[0] = UiKit.Rect(_mapRect, "DialogImages_0", Vector2.zero, Vector2.one);
            _npcLayer = UiKit.Rect(_mapRect, "NpcImages", Vector2.zero, Vector2.one);   // npc pictures: above the map, below the link buttons
            _overlaySlots[1] = UiKit.Rect(_mapRect, "OverlayImages_10", Vector2.zero, Vector2.one);
            _dialogSlots[1] = UiKit.Rect(_mapRect, "DialogImages_10", Vector2.zero, Vector2.one);
            _linkLayer = UiKit.Rect(_mapRect, "Links", Vector2.zero, Vector2.one);
            // above the links, below the rest of the interface (created before the bars and panels, so they draw over it)
            foreach (var slots in new[] { _overlaySlots, _dialogSlots })
            {
                slots[2] = UiKit.Rect(canvasGo.transform, (slots == _overlaySlots ? "OverlayImages_20" : "DialogImages_20"), Vector2.zero, Vector2.one);
                var fit = slots[2].gameObject.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fit.aspectRatio = 16f / 9f;
                _slotAspects.Add(fit);
            }

            // top bar
            var top = UiKit.Panel(canvasGo.transform, "TopBar", new Vector2(0, 0.94f), Vector2.one);
            _topText = UiKit.Label(top, "Time", 30, TextAnchor.MiddleLeft, new Vector2(0.01f, 0), new Vector2(0.34f, 1));
            _placeText = UiKit.Label(top, "Place", 30, TextAnchor.MiddleCenter, new Vector2(0.34f, 0), new Vector2(0.58f, 1));
            _sleepButton = AddTopButton(top, "Sleep", 22, 0.59f, 0.70f, () => Send(CommandType.Sleep));
            _sleepButton.gameObject.SetActive(false);
            AddWaitButton(top, "Wait 30m", 30, 0.71f);
            AddWaitButton(top, "1h", 60, 0.83f);
            AddWaitButton(top, "4h", 240, 0.90f);

            // left: place description + npcs
            var left = UiKit.Panel(canvasGo.transform, "Left", new Vector2(0.005f, 0.30f), new Vector2(0.22f, 0.93f));
            _npcList = UiKit.Rect(left, "NpcList", new Vector2(0.03f, 0.02f), new Vector2(0.97f, 0.92f));
            var v = _npcList.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 6; v.childControlHeight = true; v.childControlWidth = true; v.childForceExpandHeight = false; v.childAlignment = TextAnchor.UpperCenter;

            // right: journal
            var right = UiKit.Panel(canvasGo.transform, "Right", new Vector2(0.78f, 0.30f), new Vector2(0.995f, 0.93f));
            _questList = UiKit.Rect(right, "Quests", new Vector2(0.04f, 0.02f), new Vector2(0.96f, 0.92f));
            var qv = _questList.gameObject.AddComponent<VerticalLayoutGroup>();
            qv.spacing = 8; qv.childControlHeight = true; qv.childControlWidth = true; qv.childForceExpandHeight = false;

            // left-bottom: player inventory
            var bag = UiKit.Panel(canvasGo.transform, "Inventory", new Vector2(0.005f, 0.02f), new Vector2(0.22f, 0.29f));
            _inventoryList = UiKit.Rect(bag, "Items", new Vector2(0.04f, 0.03f), new Vector2(0.96f, 0.80f));
            var iv = _inventoryList.gameObject.AddComponent<VerticalLayoutGroup>();
            iv.spacing = 4; iv.childControlHeight = true; iv.childControlWidth = true; iv.childForceExpandHeight = false;
            DraggablePanel.Make(bag, _inventoryList, "Backpack", new Vector2(0.005f, 0.24f), new Vector2(0.115f, 0.29f));

            DraggablePanel.Make(left, _npcList, "Present", new Vector2(0.005f, 0.88f), new Vector2(0.115f, 0.93f));
            DraggablePanel.Make(right, _questList, "Journal", new Vector2(0.885f, 0.88f), new Vector2(0.995f, 0.93f));

            // bottom: log
            var bottom = UiKit.Panel(canvasGo.transform, "Log", new Vector2(0.23f, 0.0f), new Vector2(0.77f, 0.2f));
            _logText = UiKit.Label(bottom, "LogText", 22, TextAnchor.LowerLeft, new Vector2(0.015f, 0.03f), new Vector2(0.985f, 0.97f));

            // images from showImage effects (quests, events): same rect as the map, they stay until hidden
            _overlayLayer = UiKit.Rect(canvasGo.transform, "OverlayImages", Vector2.zero, Vector2.one);
            _overlayAspect = _overlayLayer.gameObject.AddComponent<AspectRatioFitter>();
            _overlayAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _overlayAspect.aspectRatio = 16f / 9f;
            _overlaySlots[3] = _overlayLayer;

            // talk picture of the npc that is speaking: same rect as the map, above the quest images
            _portraitLayer = UiKit.Rect(canvasGo.transform, "PortraitImages", Vector2.zero, Vector2.one);
            _portraitAspect = _portraitLayer.gameObject.AddComponent<AspectRatioFitter>();
            _portraitAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _portraitAspect.aspectRatio = 16f / 9f;

            // dialogue images: same rect as the map, above every panel and link, below the text box
            _imageLayer = UiKit.Rect(canvasGo.transform, "DialogImages", Vector2.zero, Vector2.one);
            _imageAspect = _imageLayer.gameObject.AddComponent<AspectRatioFitter>();
            _imageAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _imageAspect.aspectRatio = 16f / 9f;
            _dialogSlots[3] = _imageLayer;

            // click anywhere outside the text box = next line (invisible, only active while a dialogue is open)
            _catcher = UiKit.Panel(canvasGo.transform, "DialogClick", Vector2.zero, Vector2.one, new Color(0, 0, 0, 0));
            AddClick(_catcher.gameObject, Advance);
            _catcher.gameObject.SetActive(false);

            // dialog: visual-novel style box at the bottom
            _dialogPanel = UiKit.Panel(canvasGo.transform, "Dialog", new Vector2(0.2f, 0.02f), new Vector2(0.8f, 0.40f), new Color(0.04f, 0.05f, 0.08f, 0.95f));
            AddClick(_dialogPanel.gameObject, Advance);
            _dialogText = UiKit.Label(_dialogPanel, "Text", 28, TextAnchor.UpperLeft, new Vector2(0.04f, 0.5f), new Vector2(0.96f, 0.95f));
            _dialogHint = UiKit.Label(_dialogPanel, "Hint", 28, TextAnchor.LowerRight, new Vector2(0.88f, 0.03f), new Vector2(0.98f, 0.2f));
            _dialogHint.text = "<color=#f2c75a>\u25BC</color>";
            _dialogChoices = UiKit.Rect(_dialogPanel, "Choices", new Vector2(0.04f, 0.04f), new Vector2(0.96f, 0.47f));
            var dv = _dialogChoices.gameObject.AddComponent<VerticalLayoutGroup>();
            dv.spacing = 8; dv.childControlHeight = true; dv.childControlWidth = true; dv.childForceExpandHeight = false;
            _dialogPanel.gameObject.SetActive(false);
        }

        /// <summary>Makes a graphic (an Image on <paramref name="go"/>) clickable without any hover/press tint.</summary>
        static void AddClick(GameObject go, UnityEngine.Events.UnityAction onClick)
        {
            var b = go.AddComponent<Button>();
            b.transition = Selectable.Transition.None;
            b.targetGraphic = go.GetComponent<Image>();
            b.onClick.AddListener(onClick);
        }

        void AddWaitButton(Transform parent, string label, int minutes, float x)
            => AddTopButton(parent, label, 22, x, x + (label.Length > 5 ? 0.115f : 0.065f), () => Send(CommandType.Wait, minutes));

        /// <summary>A button of the top bar (greyed out while time is passing or a dialogue is open).</summary>
        Button AddTopButton(Transform parent, string label, int size, float xMin, float xMax, UnityEngine.Events.UnityAction onClick)
        {
            var b = UiKit.Button(parent, "Top_" + label, label, size, onClick);
            var rt = (RectTransform)b.transform;
            rt.anchorMin = new Vector2(xMin, 0.1f); rt.anchorMax = new Vector2(xMax, 0.9f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            _lockables.Add(b);
            return b;
        }

        // ------------------------------------------------------------------ binding

        public void Bind(EntityManager em, Entity world, Entity player, ContentDatabase db, CompiledContent compiled)
        {
            _em = em; _world = world; _player = player; _db = db; _compiled = compiled;
            _lastVersion = uint.MaxValue;
            _currentMapId = null; _currentBgPath = null;
            _shownImages = null; ClearImages();
            _shownOverlays = null; ClearOverlays();
            _shownPortrait = null; ClearPortrait();
            _audio.Rebind();
            UiKit.Clear(_npcLayer); _npcPaths.Clear();
            _npcById.Clear();
            foreach (var n in compiled.Npcs) _npcById[n.Id] = n;
            _statNames.Clear();
            foreach (var s in db.Rules.Stats) _statNames[Ids.Hash(s.Id)] = TextTable.Localize(s.Name) is { Length: > 0 } n ? n : s.Id;
        }

        void Send(CommandType type, int a = 0, int b = 0)
            => _em.GetBuffer<GameCommand>(_world).Add(new GameCommand { Type = type, A = a, B = b });

        /// <summary>Next dialogue line. The short lock keeps a double click from skipping a line unread.</summary>
        void Advance()
        {
            if (Time.unscaledTime - _lastAdvance < 0.15f) return;
            _lastAdvance = Time.unscaledTime;
            Send(CommandType.Advance);
        }

        // ------------------------------------------------------------------ per-frame

        public void Tick()
        {
            if (_em.World == null || !_em.World.IsCreated || !_em.Exists(_world)) return;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (_dialogOpen && _interactive && kb != null && (kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame))
                Advance();

            PlayCues();
            var ws = _em.GetComponentData<WorldState>(_world);
            if (ws.Version == _lastVersion) return;
            _lastVersion = ws.Version;
            Refresh(ws);
        }

        // ------------------------------------------------------------------ sounds

        /// <summary>Plays the one-shot sounds the simulation queued (sound effects of events, quests and dialogue lines).</summary>
        void PlayCues()
        {
            if (!_em.HasBuffer<SoundCue>(_world)) return;
            var cues = _em.GetBuffer<SoundCue>(_world);
            if (cues.Length == 0) return;
            foreach (var cue in cues)
                if (cue.Sound >= 0 && cue.Sound < _compiled.Sounds.Count) _audio.PlayOnce(_compiled.Sounds[cue.Sound]);
            cues.Clear();
        }

        /// <summary>
        /// The music and ambient layers are state, like overlay images: the flag of each channel (Ids.MusicKey / Ids.AmbientKey)
        /// is the index of the sound that plays there + 1, 0 when it is off. They keep playing across dialogues and map changes,
        /// and come back from a save.
        /// </summary>
        void RefreshSounds()
        {
            _audio.KeepOnly(_compiled.SoundKeys, 0.6f);
            if (_compiled.SoundKeys.Count == 0) return;
            var flags = _em.GetBuffer<FlagValue>(_world, true);
            foreach (int key in _compiled.SoundKeys)
            {
                CompiledSound sound = null;
                for (int i = 0; i < flags.Length; i++)
                {
                    if (flags[i].Key != key) continue;
                    int idx = (int)flags[i].Value - 1;
                    if (idx >= 0 && idx < _compiled.Sounds.Count) sound = _compiled.Sounds[idx];
                    break;
                }
                _audio.SetLoop(key, sound);
            }
        }

        void Refresh(WorldState ws)
        {
            var clock = _em.GetComponentData<ClockState>(_world);
            var player = _em.GetComponentData<PlayerState>(_player);
            bool busy = clock.Pending > 0 || ws.DialogOpen != 0;
            _lockables.RemoveAll(b => b == null);   // link buttons of the previous map were destroyed
            foreach (var b in _lockables) b.interactable = !busy;

            _topText.text = GameTime.Format(clock.Now, clock.StartDayOfWeek)
                + (clock.Pending > 0 ? $"  <color=#f2c75a>{(clock.Sleeping != 0 ? "zZz" : "…")}{clock.Pending} min</color>" : "");
            _sleepButton.gameObject.SetActive(RuleRuntime.CanSleep(_em.GetComponentData<RuleRef>(_world).Value, player.Node, clock.Now, out _));

            if (player.Node != 0) ShowNode(Ids.Name(player.Node), clock, busy);
            else _placeText.text = "On the way…";

            RefreshNpcs(player, busy);
            RefreshNpcImages(player, busy, ws);
            RefreshQuests();
            RefreshInventory();
            RefreshLog();
            RefreshOverlays();
            RefreshSounds();
            RefreshDialog(ws);
            RefreshPortrait(ws, player);
        }

        void RefreshInventory()
        {
            UiKit.Clear(_inventoryList);
            var rules = _em.GetComponentData<RuleRef>(_world).Value;
            var slots = _em.GetBuffer<InventorySlot>(_player, true);
            if (slots.Length == 0)
            {
                var empty = UiKit.Label(_inventoryList, "Empty", 20, TextAnchor.UpperLeft, Vector2.zero, Vector2.one);
                empty.text = "<color=#8a93a6>empty</color>";
                empty.gameObject.AddComponent<LayoutElement>().minHeight = 26;
                return;
            }
            for (int i = 0; i < slots.Length; i++)
            {
                string name = null;
                for (int k = 0; k < rules.Value.Items.Length; k++)
                    if (rules.Value.Items[k].Id == slots[i].Item) { name = TextTable.Get(rules.Value.Items[k].NameText); break; }
                // an item this session does not know (e.g. from a removed mod) is shown greyed out
                string line = name != null ? name : $"<color=#8a93a6>{Ids.Name(slots[i].Item)}</color>";
                var t = UiKit.Label(_inventoryList, "Item" + i, 22, TextAnchor.MiddleLeft, Vector2.zero, Vector2.one);
                t.text = $"{line} <color=#f2c75a>x{slots[i].Count}</color>";
                t.gameObject.AddComponent<LayoutElement>().minHeight = 28;
            }
        }

        string DayPartBackground(int minuteOfDay)
        {
            var parts = _db.Rules.DayParts;
            if (parts.Count == 0)
            {
                if (minuteOfDay >= 6 * 60 && minuteOfDay < 16 * 60) return "day";
                if (minuteOfDay >= 16 * 60 && minuteOfDay < 19 * 60) return "afternoon";
                if (minuteOfDay >= 19 * 60 && minuteOfDay < 23 * 60) return "night_light";
                return "night";
            }
            foreach (var p in parts)
            {
                if (!GameTime.TryParse(p.From, out var f) || !GameTime.TryParse(p.To, out var t)) continue;
                if (f < t ? (minuteOfDay >= f && minuteOfDay < t) : (minuteOfDay >= f || minuteOfDay < t)) return p.Background;
            }
            return "day";
        }

        void ShowNode(string nodeId, ClockState clock, bool busy)
        {
            if (!_db.Maps.TryGetValue(nodeId, out var map)) return;
            _placeText.text = TextTable.Localize(map.Title);

            string key = DayPartBackground(GameTime.MinuteOfDay(clock.Now));
            string file = null;
            if (!map.Backgrounds.TryGetValue(key, out file) && !map.Backgrounds.TryGetValue("day", out file))
                foreach (var kv in map.Backgrounds) { file = kv.Value; break; }
            string path = file == null ? null : System.IO.Path.Combine(map.Dir, file);

            if (path != _currentBgPath)
            {
                var tex = TextureCache.Get(path);
                _mapImage.texture = tex;
                _mapImage.color = tex != null ? Color.white : new Color(0.1f, 0.1f, 0.12f);
                if (tex != null)
                {
                    _portraitAspect.aspectRatio = _overlayAspect.aspectRatio = _imageAspect.aspectRatio = _aspect.aspectRatio = (float)tex.width / tex.height;
                    foreach (var fit in _slotAspects) fit.aspectRatio = _aspect.aspectRatio;
                }
                _currentBgPath = path;
                var keep = new List<string>(_shownPaths) { path };
                keep.AddRange(_overlayPaths);
                keep.AddRange(_npcPaths);
                if (_portraitPath != null) keep.Add(_portraitPath);
                TextureCache.Trim(keep);
            }

            if (_currentMapId != nodeId)
            {
                _currentMapId = nodeId;
                UiKit.Clear(_linkLayer);
                _linkButtons.Clear();
                foreach (var l in map.Links)
                {
                    if (!_db.Maps.ContainsKey(l.Target)) continue;
                    int linkId = Ids.Hash(l.Id);
                    string label = TextTable.Localize(l.Label);
                    var b = UiKit.Button(_linkLayer, "Link_" + l.Id, string.IsNullOrEmpty(label) ? l.Target : label, 26, () => Send(CommandType.Travel, linkId),
                        new Color(0.1f, 0.12f, 0.18f, 0.72f));
                    var rt = (RectTransform)b.transform;
                    // like the map editor: x / y are the CENTRE of the button, in % of the 16:9 scene
                    rt.anchorMin = new Vector2((l.X - l.Width / 2f) / 100f, 1f - (l.Y + l.Height / 2f) / 100f);
                    rt.anchorMax = new Vector2((l.X + l.Width / 2f) / 100f, 1f - (l.Y - l.Height / 2f) / 100f);
                    rt.offsetMin = rt.offsetMax = Vector2.zero;
                    _lockables.Add(b);
                    _linkButtons.Add((b.gameObject, l));
                }
            }
            // links can be shown / hidden at any time by effects, so visibility is re-applied on every refresh
            var flags = _em.GetBuffer<FlagValue>(_world, true);
            foreach (var (go, def) in _linkButtons)
                if (go != null) go.SetActive(FlagState(flags, Ids.LinkVisibleKey(def.Id), def.Visible));
            _lockables.RemoveAll(b => b == null);
            foreach (var b in _lockables) b.interactable = !busy;
        }

        static bool FlagState(DynamicBuffer<FlagValue> flags, int key, bool initial)
        {
            for (int i = 0; i < flags.Length; i++)
                if (flags[i].Key == key) return flags[i].Value == 1 ? true : flags[i].Value == 2 ? false : initial;
            return initial;
        }

        string NpcName(int npcId)
        {
            foreach (var n in _compiled.Npcs) if (n.Id == npcId) return TextTable.Get(n.NameText);
            return Ids.Name(npcId);
        }

        void RefreshNpcs(PlayerState player, bool busy)
        {
            UiKit.Clear(_npcList);
            if (player.Node == 0) return;
            var idx = _em.GetBuffer<EntityRef>(_world, true);
            var sb = new StringBuilder();
            for (int i = 0; i < idx.Length; i++)
            {
                var e = idx[i].Entity;
                var loc = _em.GetComponentData<NpcLocation>(e);
                if (loc.Node != player.Node) continue;
                int id = idx[i].Id;
                var stats = _em.GetBuffer<StatValue>(e, true);
                sb.Clear();
                sb.Append(NpcName(id));
                for (int s = 0; s < stats.Length; s++)
                    if (_statNames.TryGetValue(stats[s].Stat, out var sn)) sb.Append($"\n<size=16>{sn}: {stats[s].Value:0}</size>");
                if (loc.Activity != 0) sb.Append($"\n<size=15><i>{Ids.Name(loc.Activity)}</i></size>");
                var b = UiKit.Button(_npcList, "Npc_" + id, sb.ToString(), 22, () => Send(CommandType.Talk, id));
                b.interactable = !busy;
                b.gameObject.AddComponent<LayoutElement>().minHeight = 88;
                var t = b.GetComponentInChildren<Text>();
                t.resizeTextForBestFit = false;
            }
        }

        // ------------------------------------------------------------------ npc pictures

        /// <summary>The npc whose line is on screen: the line's speaker, else the npc of the event. 0 for the narrator or a free name.</summary>
        static int SpeakerNpc(WorldState ws) => ws.DialogOpen != 0 && ws.DialogWho == 0 ? ws.DialogNpc : 0;

        /// <summary>Pose named by the current script line (hashed), 0 when none or outside a script.</summary>
        int CurrentPose(WorldState ws)
        {
            if (ws.DialogOpen == 0 || ws.DialogStep < 0) return 0;
            var rules = _em.GetComponentData<RuleRef>(_world).Value;
            return rules.Value.Script[ws.DialogStep].Pose;
        }

        /// <summary>
        /// Pictures of the npcs standing here, drawn on the map and clickable (= talk), besides the list on the left.
        /// Which picture: the one for the activity the npc is doing, else its default one; either can be overridden per map.
        /// An npc whose talk picture is on screen is left off the map while it speaks.
        /// </summary>
        void RefreshNpcImages(PlayerState player, bool busy, WorldState ws)
        {
            UiKit.Clear(_npcLayer);
            _npcPaths.Clear();
            if (player.Node == 0) return;
            int speaker = SpeakerNpc(ws), pose = CurrentPose(ws);
            var idx = _em.GetBuffer<EntityRef>(_world, true);
            for (int i = 0; i < idx.Length; i++)
            {
                int id = idx[i].Id;
                if (!_npcById.TryGetValue(id, out var npc)) continue;
                var loc = _em.GetComponentData<NpcLocation>(idx[i].Entity);
                if (loc.Node != player.Node) continue;
                if (id == speaker && npc.Images.Speaking(pose, player.Node) != null) continue;
                var img = npc.Images.OnMap(loc.Activity, player.Node);
                if (img == null) continue;
                var raw = PlaceImage(_npcLayer, _aspect.aspectRatio, img.Path, img.X, img.Y, img.Width, img.Height, "Npc_" + id);
                if (raw == null) continue;
                _npcPaths.Add(img.Path);
                raw.raycastTarget = true;
                raw.gameObject.AddComponent<AlphaHit>();       // only the character is clickable, not its transparent box
                var b = raw.gameObject.AddComponent<Button>();
                b.transition = Selectable.Transition.None;
                b.targetGraphic = raw;
                b.interactable = !busy;
                int npcId = id;
                b.onClick.AddListener(() => Send(CommandType.Talk, npcId));
            }
        }

        void ClearPortrait()
        {
            if (_shownPortrait == "") return;
            UiKit.Clear(_portraitLayer);
            _shownPortrait = "";
            _portraitPath = null;
        }

        /// <summary>While an npc speaks, its talk picture (the pose named by the line, else "default") is shown on the map area.</summary>
        void RefreshPortrait(WorldState ws, PlayerState player)
        {
            int speaker = SpeakerNpc(ws);
            NpcImage img = null;
            if (speaker != 0 && _npcById.TryGetValue(speaker, out var npc)) img = npc.Images.Speaking(CurrentPose(ws), player.Node);
            string sig = img == null ? "" : $"{img.Path}|{img.X}|{img.Y}|{img.Width}|{img.Height}";
            if (sig == _shownPortrait) return;
            ClearPortrait();
            if (img == null) return;
            _shownPortrait = sig;
            if (PlaceImage(_portraitLayer, _portraitAspect.aspectRatio, img.Path, img.X, img.Y, img.Width, img.Height, "Talk_" + speaker) != null) _portraitPath = img.Path;
        }

        void RefreshQuests()
        {
            UiKit.Clear(_questList);
            var states = _em.GetBuffer<QuestState>(_world, true);
            var rules = _em.GetComponentData<RuleRef>(_world).Value;
            for (int i = 0; i < states.Length; i++)
            {
                var st = states[i];
                if (st.Status == (byte)QuestStatus.NotStarted) continue;
                ref var q = ref rules.Value.Quests[i];
                string title = TextTable.Get(q.TitleText);
                string body;
                switch ((QuestStatus)st.Status)
                {
                    case QuestStatus.Active: body = $"<color=#f2c75a>{title}</color>\n<size=18>{TextTable.Get(rules.Value.Stages[q.StageStart + st.Stage].TextId)}</size>"; break;
                    case QuestStatus.Completed: body = $"<color=#7bd88f>[done] {title}</color>"; break;
                    default: body = $"<color=#e06c6c>[failed] {title}</color>"; break;
                }
                var t = UiKit.Label(_questList, "Q" + i, 22, TextAnchor.UpperLeft, Vector2.zero, Vector2.one);
                t.text = body;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                t.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }
        }

        void RefreshLog()
        {
            var msgs = _em.GetBuffer<GameMessage>(_world, true);
            var clock = _em.GetComponentData<ClockState>(_world);
            var sb = new StringBuilder();
            for (int i = Mathf.Max(0, msgs.Length - 6); i < msgs.Length; i++)
            {
                var m = msgs[i];
                string text = TextTable.Get(m.TextId).Replace("{npc}", NpcName(m.Arg));
                string color = m.Kind switch
                {
                    MsgKind.QuestStarted => "#f2c75a",
                    MsgKind.QuestUpdated => "#f2e6b0",
                    MsgKind.QuestCompleted => "#7bd88f",
                    MsgKind.QuestFailed => "#e06c6c",
                    _ => "#e8e8e8",
                };
                string prefix = m.Kind switch
                {
                    MsgKind.QuestStarted => "New quest: ",
                    MsgKind.QuestCompleted => "Quest completed: ",
                    MsgKind.QuestFailed => "Quest failed: ",
                    _ => "",
                };
                int mod = GameTime.MinuteOfDay(m.At);
                sb.AppendLine($"<color=#8a93a6>{GameTime.Clock(mod)}</color> <color={color}>{prefix}{text}</color>");
            }
            _logText.text = sb.ToString();
        }

        void RefreshDialog(WorldState ws)
        {
            bool open = ws.DialogOpen != 0;
            _dialogOpen = open;
            _dialogPanel.gameObject.SetActive(open);
            _catcher.gameObject.SetActive(open);
            if (!open) { ClearImages(); return; }

            string speaker = "";
            if (ws.DialogWho != 0) speaker = $"<color=#f2c75a>{TextTable.Get(ws.DialogWho)}</color>\n";
            else if (ws.DialogNpc != 0) speaker = $"<color=#f2c75a>{NpcName(ws.DialogNpc)}</color>\n";
            _dialogText.text = speaker + TextTable.Get(ws.DialogText).Replace("{npc}", NpcName(ws.DialogNpc));

            UiKit.Clear(_dialogChoices);
            var choices = _em.GetBuffer<DialogChoice>(_world, true);
            for (int i = 0; i < choices.Length; i++)
            {
                int idx = choices[i].ChoiceIndex;
                string label = idx < 0 ? "OK" : TextTable.Get(choices[i].TextId);
                var b = UiKit.Button(_dialogChoices, "Choice" + i, label, 24, () => Send(CommandType.Choose, idx));
                b.gameObject.AddComponent<LayoutElement>().minHeight = 56;
            }

            _dialogHint.gameObject.SetActive(choices.Length == 0);
            ShowImages();
        }

        void ClearImages()
        {
            if (_shownImages == "") return;
            foreach (var slot in _dialogSlots) UiKit.Clear(slot);
            _shownImages = "";
            _shownPaths.Clear();
        }

        /// <summary>The images the Show steps of the open dialogue put on screen (the DialogImage buffer), redrawn only when they change.</summary>
        void ShowImages()
        {
            var active = new List<(int key, int image)>();
            foreach (var d in _em.GetBuffer<DialogImage>(_world, true)) active.Add((d.Key, d.Image));

            var sb = new StringBuilder();
            foreach (var a in active) sb.Append(a.image).Append(',');
            string sig = sb.ToString();
            if (sig == _shownImages) return;

            ClearImages();
            _shownImages = sig;
            foreach (int image in ByLayer(active.ConvertAll(a => a.image)))
                if (AddImage(_dialogSlots, _compiled.Images[image], image)) _shownPaths.Add(_compiled.Images[image].Path);
        }

        /// <summary>Which container an image goes to: below 10 above the map, 10-19 above the npc pictures, 20-29 above the links, 30+ above the interface.</summary>
        static int Slot(int layer) => layer < 10 ? 0 : layer < 20 ? 1 : layer < 30 ? 2 : 3;

        /// <summary>Image indexes ordered by layer (a higher layer is drawn later, on top); equal layers keep their order.</summary>
        List<int> ByLayer(List<int> images)
        {
            var sorted = new List<(int layer, int pos, int image)>();
            for (int i = 0; i < images.Count; i++) sorted.Add((_compiled.Images[images[i]].Layer, i, images[i]));
            sorted.Sort((a, b) => a.layer != b.layer ? a.layer.CompareTo(b.layer) : a.pos.CompareTo(b.pos));
            return sorted.ConvertAll(s => s.image);
        }

        /// <summary>Places one image in the slot container of its layer using the percentage layout of the content. False if the file cannot be loaded.</summary>
        bool AddImage(RectTransform[] slots, CompiledImage ci, int index)
            => PlaceImage(slots[Slot(ci.Layer)], _aspect.aspectRatio, ci.Path, ci.X, ci.Y, ci.Width, ci.Height, "Img_" + index) != null;

        /// <summary>Percentage layout: x / y from the top-left corner, width of the layer, height omitted = keep the file's proportions. Null if the file cannot be loaded.</summary>
        RawImage PlaceImage(RectTransform layer, float layerAspect, string path, float x, float y, float width, float? height, string name)
        {
            var tex = TextureCache.Get(path);
            if (tex == null) return null;
            float hPct = height ?? width * tex.height / tex.width * layerAspect;
            var rt = UiKit.Rect(layer, name, Vector2.zero, Vector2.one);
            rt.anchorMin = new Vector2(x / 100f, 1f - (y + hPct) / 100f);
            rt.anchorMax = new Vector2((x + width) / 100f, 1f - y / 100f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            var raw = rt.gameObject.AddComponent<RawImage>();
            raw.texture = tex;
            raw.raycastTarget = false;
            return raw;
        }

        void ClearOverlays()
        {
            if (_shownOverlays == "") return;
            foreach (var slot in _overlaySlots) UiKit.Clear(slot);
            _shownOverlays = "";
            _overlayPaths.Clear();
        }

        /// <summary>
        /// Images put on screen by showImage effects. Their state is the flag Ids.ImageKey(key): image index + 1 while
        /// shown, 0 once hidden by hideImage. Unlike dialogue images they survive across dialogues and map changes.
        /// </summary>
        void RefreshOverlays()
        {
            var keys = _compiled.OverlayKeys;
            if (keys.Count == 0) { ClearOverlays(); return; }
            var flags = _em.GetBuffer<FlagValue>(_world, true);
            var active = new List<int>();
            for (int i = 0; i < flags.Length; i++)
            {
                if (flags[i].Value < 1 || !keys.Contains(flags[i].Key)) continue;
                int image = (int)flags[i].Value - 1;
                if (image >= 0 && image < _compiled.Images.Count) active.Add(image);
            }
            string sig = string.Join(",", active);
            if (sig == _shownOverlays) return;

            ClearOverlays();
            _shownOverlays = sig;
            foreach (int image in ByLayer(active))
                if (AddImage(_overlaySlots, _compiled.Images[image], image)) _overlayPaths.Add(_compiled.Images[image].Path);
        }
    }
}
