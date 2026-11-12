using System;
using System.Collections.Generic;
using LivingNPC.Content;
using LivingNPC.Simulation;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// The menu that opens beside the pointer when the player clicks a character (its picture on the map or its name in
    /// Present), with the actions of actions.json offered to it: the ones whose npcs / tags include it and whose "when"
    /// holds with self = the character. Choosing one sends CommandType.Action (and CommandType.Talk for "do": "talk").
    /// An action with sub-actions opens them in a panel beside its own (to the right, or to the left when there is no
    /// room), page by page with Next / Back, and Close; sub-actions can open further panels. A click outside, Esc, a
    /// dialogue, or the character walking away closes everything.
    /// </summary>
    public class ActionMenu
    {
        const float Width = 320, Gap = 6, RowHeight = 52;

        readonly Action<CommandType, int, int> _send;
        RectTransform _layer;
        readonly OwnedTextures _icons = new OwnedTextures(true, true);

        EntityManager _em;
        Entity _world, _player;
        CompiledContent _compiled;
        Vector2? _at;

        /// <summary>One panel: the first menu (Parent null) or the sub-actions of Parent, at a page.</summary>
        class Level
        {
            public CompiledAction Parent;
            public int Page;
            public RectTransform Panel, List;
            public Text Title;
            public readonly List<string> Shown = new List<string>();
            public readonly Dictionary<string, RectTransform> Rows = new Dictionary<string, RectTransform>();
        }
        readonly List<Level> _levels = new List<Level>();

        /// <summary>The character the menu is open on, 0 when closed.</summary>
        public int Npc { get; private set; }

        /// <summary>The ids of the actions on the first panel now.</summary>
        public List<string> Shown => _levels.Count > 0 ? _levels[0].Shown : s_None;
        static readonly List<string> s_None = new List<string>();

        /// <summary>How many panels are open (the menu, then one per sub-menu).</summary>
        public int Levels => _levels.Count;

        /// <summary>The ids of the actions on panel <paramref name="level"/> now (its current page).</summary>
        public List<string> ShownAt(int level) => level < _levels.Count ? _levels[level].Shown : s_None;

        /// <summary>The page panel <paramref name="level"/> shows (0 the first).</summary>
        public int PageAt(int level) => level < _levels.Count ? _levels[level].Page : 0;

        public ActionMenu(Action<CommandType, int, int> send) { _send = send; }

        /// <summary>Above the panels and the screens of the bar, below the dialogue (which closes it).</summary>
        public void Build(Transform canvas)
        {
            // a click anywhere outside the panels closes them (and does nothing else)
            _layer = UiKit.Panel(canvas, "ActionMenu", Vector2.zero, Vector2.one, new Color(0, 0, 0, 0));
            var catcher = _layer.gameObject.AddComponent<Button>();
            catcher.transition = Selectable.Transition.None;
            catcher.onClick.AddListener(Close);
            _layer.gameObject.SetActive(false);
        }

        public void Bind(EntityManager em, Entity world, Entity player, CompiledContent compiled)
        {
            _em = em; _world = world; _player = player; _compiled = compiled;
            Close();
            _icons.Clear();
        }

        /// <summary>
        /// Opens the menu on <paramref name="npc"/> at <paramref name="screenPos"/> (null = the pointer, else the middle of
        /// the screen). Returns false when the world has no actions.json: the caller talks instead.
        /// </summary>
        public bool Open(int npc, Vector2? screenPos = null)
        {
            if (_compiled?.Actions == null) return false;
            Close();
            Npc = npc;
            _at = screenPos ?? (UnityEngine.InputSystem.Pointer.current != null ? UnityEngine.InputSystem.Pointer.current.position.ReadValue() : (Vector2?)null);
            _layer.gameObject.SetActive(true);
            _layer.SetAsLastSibling();
            _levels.Add(NewLevel(null));
            Draw();
            return true;
        }

        public void Close()
        {
            Npc = 0;
            CloseFrom(0);
            if (_layer != null) _layer.gameObject.SetActive(false);
        }

        /// <summary>The world changed: closes the menu when a dialogue opened or the character is gone, else redraws it (conditions may have changed).</summary>
        public void Refresh(WorldState ws, bool busy)
        {
            if (Npc == 0) return;
            if (ws.DialogOpen != 0 || busy || !NpcHere(Npc)) { Close(); return; }
            Draw();
        }

        /// <summary>Chooses an action of any open panel by id, as a click would (tools and tests); false when it is not on screen.</summary>
        public bool Choose(string id)
        {
            for (int i = 0; i < _levels.Count; i++)
            {
                if (!_levels[i].Shown.Contains(id)) continue;
                var list = i == 0 ? _compiled.Actions : _levels[i].Parent.Children;
                Pick(i, list.Find(a => a.Id == id));
                return true;
            }
            return false;
        }

        /// <summary>Next / Back on panel <paramref name="level"/> (tools and tests); false when that page does not exist.</summary>
        public bool Turn(int level, int delta)
        {
            if (level >= _levels.Count) return false;
            var l = _levels[level];
            int pages = Pages(l, Available(level).Count);
            int page = l.Page + delta;
            if (page < 0 || page >= pages) return false;
            l.Page = page;
            CloseFrom(level + 1);
            Draw();
            return true;
        }

        /// <summary>Close on a sub-menu: it goes away with the ones it opened.</summary>
        public void CloseLevel(int level)
        {
            if (level <= 0) { Close(); return; }
            CloseFrom(level);
            Draw();
        }

        // ------------------------------------------------------------------ choosing

        void Pick(int level, CompiledAction a)
        {
            if (a == null) return;
            if (a.Children != null)
            {
                // the sub-actions in the next panel; choosing the same parent again closes it
                bool same = level + 1 < _levels.Count && _levels[level + 1].Parent == a;
                CloseFrom(level + 1);
                if (!same) _levels.Add(NewLevel(a));
                Draw();
                return;
            }
            int npc = Npc;
            Close();
            if (a.Talk) _send(CommandType.Talk, npc, 0);
            _send(CommandType.Action, npc, a.Hash);
        }

        void CloseFrom(int level)
        {
            for (int i = _levels.Count - 1; i >= level; i--)
            {
                if (_levels[i].Panel != null) UiKit.Kill(_levels[i].Panel.gameObject);
                _levels.RemoveAt(i);
            }
        }

        bool NpcHere(int npc)
        {
            if (_em.World == null || !_em.World.IsCreated || !_em.Exists(_world)) return false;
            var p = _em.GetComponentData<PlayerState>(_player);
            var idx = _em.GetBuffer<EntityRef>(_world, true);
            for (int i = 0; i < idx.Length; i++)
                if (idx[i].Id == npc) return p.Node != 0 && _em.GetComponentData<NpcLocation>(idx[i].Entity).Node == p.Node;
            return false;
        }

        // ------------------------------------------------------------------ what is offered

        /// <summary>An action is offered when its npcs / tags and "when" let it, and (with sub-actions) when one of them is offered.</summary>
        bool Offered(CompiledAction a, ref RuleRuntime rt)
        {
            if (!a.OfferedTo(Npc) || !rt.Eval(a.When)) return false;
            if (a.Children == null) return true;
            foreach (var c in a.Children) if (Offered(c, ref rt)) return true;
            return false;
        }

        List<CompiledAction> Available(int level)
        {
            var rt = RuleRuntime.Create(_em, _world, _player);
            rt.BindDialog(Npc);                 // "self" in the conditions is the character clicked
            var list = new List<CompiledAction>();
            foreach (var a in level == 0 ? _compiled.Actions : _levels[level].Parent.Children)
                if (Offered(a, ref rt)) list.Add(a);
            return list;
        }

        int PageSize(Level l) => l.Parent == null ? _compiled.ActionMenu?.PageSize ?? 0 : l.Parent.PageSize;

        int Pages(Level l, int count)
        {
            int size = PageSize(l);
            return size <= 0 || count == 0 ? 1 : (count + size - 1) / size;
        }

        // ------------------------------------------------------------------ drawing

        Level NewLevel(CompiledAction parent)
        {
            var l = new Level { Parent = parent };
            l.Panel = UiKit.Panel(_layer, parent == null ? "Menu" : "Menu_" + parent.Id, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Color(0.04f, 0.05f, 0.08f, 0.96f));
            l.Panel.pivot = new Vector2(0, 1);
            var v = l.Panel.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(10, 10, 8, 10); v.spacing = 6;
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandHeight = false;
            l.Panel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            l.Panel.sizeDelta = new Vector2(Width, 0);
            l.Title = UiKit.Label(l.Panel, "Title", 26, TextAnchor.MiddleLeft, Vector2.zero, Vector2.one);
            l.Title.gameObject.AddComponent<LayoutElement>().minHeight = 38;
            l.List = UiKit.Rect(l.Panel, "Actions", Vector2.zero, Vector2.one);
            var lv = l.List.gameObject.AddComponent<VerticalLayoutGroup>();
            lv.spacing = 6; lv.childControlWidth = true; lv.childControlHeight = true; lv.childForceExpandHeight = false;
            return l;
        }

        void Draw()
        {
            for (int i = 0; i < _levels.Count; i++)
            {
                var l = _levels[i];
                var items = Available(i);
                // a sub-menu whose actions are all gone (the world changed) closes
                if (i > 0 && items.Count == 0) { CloseFrom(i); break; }
                int pages = Pages(l, items.Count);
                l.Page = Mathf.Clamp(l.Page, 0, pages - 1);
                int size = PageSize(l);
                if (size > 0) items = items.GetRange(l.Page * size, Mathf.Min(size, items.Count - l.Page * size));

                UiKit.Clear(l.List);
                l.Shown.Clear(); l.Rows.Clear();
                string title = i == 0 ? _compiled.NameOf(Npc) : Label(l.Parent);
                l.Title.text = $"<color=#f2c75a>{title}</color>" + (pages > 1 ? $"  <color=#8a93a6><size=20>{l.Page + 1}/{pages}</size></color>" : "");
                var open = i + 1 < _levels.Count ? _levels[i + 1].Parent : null;
                foreach (var a in items)
                {
                    l.Shown.Add(a.Id);
                    l.Rows[a.Id] = Row(i, l, a, a == open);
                }
                if (items.Count == 0)
                {
                    var none = UiKit.Label(l.List, "Nothing", 22, TextAnchor.MiddleLeft, Vector2.zero, Vector2.one);
                    none.text = "<color=#8a93a6>Nothing to do now.</color>";
                    none.gameObject.AddComponent<LayoutElement>().minHeight = 40;
                }
                NavRow(i, l, pages);
                Place(i);
            }
        }

        string Label(CompiledAction a) => _compiled.Format(TextTable.Get(a.LabelText), Npc);      // {npc} = the character

        RectTransform Row(int level, Level l, CompiledAction a, bool open)
        {
            string label = Label(a);
            if (a.Children != null) label = open ? $"<b>{label}  ›</b>" : $"{label}  ›";
            var b = UiKit.Button(l.List, "Action_" + a.Id, label, 24, () => Pick(level, a));
            b.gameObject.AddComponent<LayoutElement>().minHeight = RowHeight;
            var text = b.GetComponentInChildren<Text>();
            text.alignment = TextAnchor.MiddleLeft;
            var lrt = (RectTransform)text.transform;
            var tex = _icons.Get(a.Icon);
            if (tex != null)
            {
                // a box on the left; the picture inside it keeps its proportions (the fitter fills its parent: the box)
                var box = UiKit.Rect(b.transform, "Icon", new Vector2(0, 0), new Vector2(0, 1));
                box.offsetMin = new Vector2(20, 8); box.offsetMax = new Vector2(56, -8);
                var pic = UiKit.Rect(box, "Picture", Vector2.zero, Vector2.one);
                var fit = pic.gameObject.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fit.aspectRatio = (float)tex.width / tex.height;
                var raw = pic.gameObject.AddComponent<RawImage>();
                raw.texture = tex;
                raw.raycastTarget = false;
                lrt.offsetMin = new Vector2(66, 0);
            }
            else lrt.offsetMin = new Vector2(32, 0);         // clear of the rounded end of a button picture
            lrt.offsetMax = new Vector2(-10, 0);
            return (RectTransform)b.transform;
        }

        /// <summary>Back / Next when there are several pages, and Close on a sub-menu.</summary>
        void NavRow(int level, Level l, int pages)
        {
            bool back = l.Page > 0, next = l.Page < pages - 1, close = level > 0;
            if (!back && !next && !close) return;
            var menu = _compiled.ActionMenu;
            var rowRt = UiKit.Rect(l.List, "Nav", Vector2.zero, Vector2.one);
            rowRt.gameObject.AddComponent<LayoutElement>().minHeight = 46;
            var h = rowRt.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 6; h.childControlWidth = true; h.childControlHeight = true; h.childForceExpandWidth = true; h.childForceExpandHeight = true;
            void Add(int text, string name, UnityEngine.Events.UnityAction act)
                => UiKit.Button(rowRt, name, TextTable.Get(text), 22, act).gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            if (back) Add(menu.BackText, "Back", () => Turn(level, -1));
            if (next) Add(menu.NextText, "Next", () => Turn(level, 1));
            if (close) Add(menu.CloseText, "Close", () => CloseLevel(level));
        }

        /// <summary>The first panel beside the pointer; a sub-menu beside its parent panel, level with the row that opened it. Always inside the screen.</summary>
        void Place(int level)
        {
            var panel = _levels[level].Panel;
            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
            var area = _layer.rect;
            var size = panel.rect.size;
            Vector2 local;
            if (level == 0)
            {
                local = Vector2.zero;
                var canvas = _layer.GetComponentInParent<Canvas>();
                var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                if (_at != null) RectTransformUtility.ScreenPointToLocalPointInRectangle(_layer, _at.Value, cam, out local);
                else local = new Vector2(-size.x / 2, size.y / 2);
                local += new Vector2(12, -12);
            }
            else
            {
                var parent = _levels[level - 1];
                var pPos = parent.Panel.anchoredPosition;
                float pWidth = parent.Panel.rect.width;
                float x = pPos.x + pWidth + Gap;
                if (x + size.x > area.xMax - 8) x = pPos.x - size.x - Gap;        // no room on the right: on the left
                float y = pPos.y;
                if (parent.Rows.TryGetValue(_levels[level].Parent.Id, out var row) && row != null)
                {
                    var corners = new Vector3[4];
                    row.GetWorldCorners(corners);
                    y = ((Vector2)_layer.InverseTransformPoint(corners[1])).y + 46;    // the title of the sub-menu beside the row
                }
                local = new Vector2(x, y);
            }
            local.x = Mathf.Clamp(local.x, area.xMin + 8, area.xMax - size.x - 8);
            local.y = Mathf.Clamp(local.y, area.yMin + size.y + 8, area.yMax - 8);
            panel.anchoredPosition = local;
        }

        public void Dispose() => _icons.Clear();
    }
}
