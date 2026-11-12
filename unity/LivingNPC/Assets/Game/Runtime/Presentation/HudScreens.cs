using System;
using System.Collections.Generic;
using System.Text;
using LivingNPC.Content;
using LivingNPC.Simulation;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// The screens of the bar (hud.json): "status", the player's page (picture, name, text, stats), and "npcs", a list of the
    /// characters with the page of the chosen one (picture, name, description, details, stats, hints). One window over the
    /// middle of the game screen; it is drawn again when the world changes, keeping where the lists were scrolled.
    /// </summary>
    public class HudScreens
    {
        static readonly Color BarBack = new Color(1, 1, 1, 0.10f);
        static readonly Color Muted = new Color(0.54f, 0.58f, 0.65f);

        RectTransform _window, _body;
        Text _title;
        Action _close;
        readonly OwnedTextures _pictures = new OwnedTextures(true, false);

        EntityManager _em;
        Entity _world, _player;
        ContentDatabase _db;
        CompiledContent _compiled;
        readonly Dictionary<int, string> _statNames = new Dictionary<int, string>();
        readonly Dictionary<int, StatRule> _statRules = new Dictionary<int, StatRule>();
        readonly Dictionary<int, string> _activityNames = new Dictionary<int, string>();

        /// <summary>The open screen (status | npcs), null when closed.</summary>
        public string Current { get; private set; }
        /// <summary>Space left at the top for the bar, in pixels of a 1080 screen.</summary>
        public float Top = 80;

        int _npc;                                   // the chosen npc of the npcs screen
        ScrollRect _listScroll, _pageScroll;
        float _listPos = 1, _pagePos = 1;

        public void Build(Transform canvas, Action close)
        {
            _close = close;
            _window = UiKit.Panel(canvas, "HudScreen", new Vector2(0.235f, 0.215f), new Vector2(0.765f, 0.94f), new Color(0.04f, 0.05f, 0.08f, 0.96f));
            var head = UiKit.Panel(_window, "Head", new Vector2(0, 1), new Vector2(1, 1), new Color(0.1f, 0.12f, 0.18f, 0.95f));
            head.pivot = new Vector2(0.5f, 1);
            head.offsetMin = new Vector2(0, -52); head.offsetMax = Vector2.zero;
            _title = UiKit.Label(head, "Title", 28, TextAnchor.MiddleLeft, new Vector2(0.03f, 0), new Vector2(0.85f, 1));
            var x = UiKit.Button(head, "Close", "X", 24, () => _close?.Invoke());
            var xr = (RectTransform)x.transform;
            xr.anchorMin = new Vector2(1, 0.5f); xr.anchorMax = new Vector2(1, 0.5f);
            xr.pivot = new Vector2(1, 0.5f);
            xr.sizeDelta = new Vector2(44, 40); xr.anchoredPosition = new Vector2(-8, 0);
            _body = UiKit.Rect(_window, "Body", Vector2.zero, Vector2.one);
            _body.offsetMin = new Vector2(18, 16); _body.offsetMax = new Vector2(-18, -64);
            _window.gameObject.SetActive(false);
        }

        public void Bind(EntityManager em, Entity world, Entity player, ContentDatabase db, CompiledContent compiled)
        {
            _em = em; _world = world; _player = player; _db = db; _compiled = compiled;
            _pictures.Clear();
            _statNames.Clear(); _statRules.Clear(); _activityNames.Clear();
            foreach (var s in db.Rules.Stats) if (!string.IsNullOrEmpty(s.Id)) _statNames[Ids.Hash(s.Id)] = TextTable.Localize(s.Name) is { Length: > 0 } n ? n : s.Id;
            foreach (var r in compiled.StatRules) _statRules[r.Id] = r;
            foreach (var a in db.Rules.Activities) if (!string.IsNullOrEmpty(a.Id)) _activityNames[Ids.Hash(a.Id)] = TextTable.Localize(a.Name) is { Length: > 0 } n ? n : a.Id;
            if (Current != null) Draw();
        }

        public void Open(string screen)
        {
            if (Current != screen) { _listPos = _pagePos = 1; }
            Current = screen;
            _window.offsetMax = new Vector2(0, -Top);
            _window.gameObject.SetActive(true);
            _window.SetAsLastSibling();
            Draw();
        }

        public void Close()
        {
            if (_window == null) return;
            Current = null;
            _window.gameObject.SetActive(false);
            UiKit.Clear(_body);
            _pictures.Clear();          // the pictures are read again when a screen opens
        }

        /// <summary>The world changed: the open screen is drawn again.</summary>
        public void Refresh() { if (Current != null) Draw(); }

        public void Dispose() => _pictures.Clear();

        /// <summary>How many rows the list "List" or "Page" of the open screen has (0 when it is not there).</summary>
        public int Rows(string list)
        {
            var t = _body != null ? _body.Find(list + "/Content") : null;
            return t != null ? t.childCount : 0;
        }

        bool Ready => _compiled?.Hud != null && _em.World != null && _em.World.IsCreated && _em.Exists(_world);

        void Draw()
        {
            if (!Ready) return;
            if (_listScroll != null) _listPos = _listScroll.verticalNormalizedPosition;
            if (_pageScroll != null) _pagePos = _pageScroll.verticalNormalizedPosition;
            _listScroll = _pageScroll = null;
            UiKit.Clear(_body);
            var hud = _compiled.Hud;
            if (Current == "status") { _title.text = $"<color=#f2c75a>{TextTable.Get(hud.StatusTitle)}</color>"; DrawStatus(hud); }
            else if (Current == "npcs") { _title.text = $"<color=#f2c75a>{TextTable.Get(hud.NpcsTitle)}</color>"; DrawNpcs(hud); }
            // the content of a scroll list has its height only after the layout: put the lists back where they were then
            Canvas.ForceUpdateCanvases();
            if (_listScroll != null) _listScroll.verticalNormalizedPosition = _listPos;
            if (_pageScroll != null) _pageScroll.verticalNormalizedPosition = _pagePos;
        }

        // ------------------------------------------------------------------ status: the player

        void DrawStatus(CompiledHud hud)
        {
            var pic = _pictures.Get(hud.StatusImage);
            float left = 0;
            if (pic != null) { Picture(_body, pic, new Vector2(0, 0), new Vector2(0.3f, 1)); left = 0.33f; }
            var page = UiKit.Scroll(_body, "Page", new Vector2(left, 0), Vector2.one, out _pageScroll, 8);
            string name = TextTable.Get(hud.StatusName);
            if (name.Length > 0) Line(page, $"<b>{name}</b>", 34);
            string text = _compiled.Format(TextTable.Get(hud.StatusText), 0);
            if (text.Length > 0) Line(page, text, 22);
            Heading(page, "Stats");
            var stats = _em.GetBuffer<StatValue>(_player, true);
            if (!StatRows(page, hud.StatusStats, stats)) Line(page, "<color=#8a93a6>none</color>", 20);
        }

        // ------------------------------------------------------------------ npcs: list + page of the chosen one

        void DrawNpcs(CompiledHud hud)
        {
            var rt = RuleRuntime.Create(_em, _world, _player);
            var listed = new List<CompiledNpc>();
            foreach (var n in _compiled.Npcs) if (rt.Eval(n.Profile.When)) listed.Add(n);
            if (listed.Count == 0)
            {
                var none = UiKit.Label(_body, "Nobody", 24, TextAnchor.MiddleCenter, Vector2.zero, Vector2.one);
                none.text = "<color=#8a93a6>You don't know anybody yet.</color>";
                return;
            }
            if (!listed.Exists(n => n.Id == _npc)) { _npc = listed[0].Id; _pagePos = 1; }

            var list = UiKit.Scroll(_body, "List", new Vector2(0, 0), new Vector2(0.28f, 1), out _listScroll, 6);
            foreach (var n in listed) ListRow(list, n, n.Id == _npc);

            var npc = listed.Find(n => n.Id == _npc);
            DrawNpcPage(hud, npc, rt);
        }

        /// <summary>A row of the list: small picture and name; the chosen one is lit.</summary>
        void ListRow(Transform list, CompiledNpc n, bool chosen)
        {
            int id = n.Id;
            var b = UiKit.Button(list, "Npc_" + id, "", 22, () => { if (_npc != id) { _npc = id; _pagePos = 1; Draw(); } },
                chosen ? new Color(0.95f, 0.78f, 0.35f, 0.9f) : UiKit.ButtonColor);
            b.gameObject.AddComponent<LayoutElement>().minHeight = 64;
            var label = b.GetComponentInChildren<Text>();
            var lrt = (RectTransform)label.transform;
            var tex = _pictures.Get(n.Profile.Image);
            if (tex != null)
            {
                var box = UiKit.Rect(b.transform, "Thumb", new Vector2(0, 0), new Vector2(0, 1));
                box.offsetMin = new Vector2(6, 4); box.offsetMax = new Vector2(54, -4);
                Thumb(box, tex);
                lrt.offsetMin = new Vector2(62, 0);
            }
            else lrt.offsetMin = new Vector2(12, 0);
            label.alignment = TextAnchor.MiddleLeft;
            label.resizeTextForBestFit = false;
            string name = TextTable.Get(n.NameText);
            // a skinned button keeps its own text colour: only the chosen row is written dark over the gold
            label.text = chosen && UiKit.Skin == null ? $"<color=#1a1408>{name}</color>" : name;
        }

        void DrawNpcPage(CompiledHud hud, CompiledNpc npc, RuleRuntime rt)
        {
            var page = UiKit.Scroll(_body, "Page", new Vector2(0.31f, 0), Vector2.one, out _pageScroll, 8);
            var entity = NpcEntity(npc.Id);

            // the picture beside the name, the details and the description
            var head = UiKit.Rect(page, "Head", Vector2.zero, Vector2.one);
            var hl = head.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 18; hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            var tex = _pictures.Get(npc.Profile.Image);
            if (tex != null)
            {
                var box = UiKit.Rect(head, "Picture", Vector2.zero, Vector2.one);
                var le = box.gameObject.AddComponent<LayoutElement>();
                le.preferredWidth = le.minWidth = 200; le.preferredHeight = le.minHeight = 300;
                Picture(box, tex, Vector2.zero, Vector2.one, top: true);
            }
            var info = UiKit.Label(head, "Info", 22, TextAnchor.UpperLeft, Vector2.zero, Vector2.one);
            info.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            info.verticalOverflow = VerticalWrapMode.Overflow;
            info.text = NpcInfoText(hud, npc, entity);

            if (entity != Entity.Null && hud.NpcStats.Count > 0)
            {
                Heading(page, "Stats");
                StatRows(page, hud.NpcStats, _em.GetBuffer<StatValue>(entity, true));
            }

            var hints = new List<string>();
            foreach (var (text, when) in npc.Profile.Hints)
                if (rt.Eval(when)) hints.Add(_compiled.Format(TextTable.Get(text), npc.Id));
            if (npc.Profile.Hints.Count > 0)
            {
                Heading(page, "Hints");
                if (hints.Count == 0) Line(page, "<color=#8a93a6>Nothing yet: get to know them better.</color>", 20);
                foreach (var h in hints) Line(page, $"<color=#f2c75a>•</color> {h}", 22);
            }
        }

        /// <summary>Name, then the details hud.json asks for (age, activity, where, relation, goal), then the description.</summary>
        string NpcInfoText(CompiledHud hud, CompiledNpc npc, Entity e)
        {
            var sb = new StringBuilder();
            sb.Append("<size=34><b>").Append(TextTable.Get(npc.NameText)).Append("</b></size>");
            string full = _compiled.NameOf(npc.Id, "full");
            if (full.Length > 0 && full != TextTable.Get(npc.NameText)) sb.Append("\n<color=#8a93a6>").Append(full).Append("</color>");
            var d = hud.NpcDetails;
            if (e != Entity.Null)
            {
                if (d.Contains("age") && _em.HasComponent<NpcInfo>(e) && _em.GetComponentData<NpcInfo>(e).Age > 0)
                    Detail(sb, "Age", _em.GetComponentData<NpcInfo>(e).Age.ToString());
                var loc = _em.GetComponentData<NpcLocation>(e);
                if (d.Contains("activity"))
                    Detail(sb, "Doing", loc.Activity != 0 ? (_activityNames.TryGetValue(loc.Activity, out var a) ? a : Ids.Name(loc.Activity)) : loc.Node == 0 ? "walking" : "nothing in particular");
                if (d.Contains("where"))
                    Detail(sb, "Where", loc.Node == 0 ? "on the way" : _db.Maps.TryGetValue(Ids.Name(loc.Node), out var map) ? TextTable.Localize(map.Title) : Ids.Name(loc.Node));
                if (d.Contains("relation") && _em.HasBuffer<RelationValue>(e))
                {
                    float rel = 0;
                    foreach (var r in _em.GetBuffer<RelationValue>(e, true)) if (r.Other == 0) { rel = r.Value; break; }
                    Detail(sb, "Towards you", $"{RelationWord(rel)} <color=#8a93a6>({rel:+0;-0;0})</color>");
                }
                if (d.Contains("goal") && _em.HasComponent<NpcAuto>(e))
                {
                    int goal = _em.GetComponentData<NpcAuto>(e).Goal;
                    if (goal != 0) Detail(sb, "Wants", _compiled.GoalNames.TryGetValue(goal, out var g) ? g : Ids.Name(goal));
                }
            }
            string desc = _compiled.Format(TextTable.Get(npc.Profile.DescriptionText), npc.Id);
            if (desc.Length > 0) sb.Append("\n\n").Append(desc);
            return sb.ToString();
        }

        static void Detail(StringBuilder sb, string label, string value) => sb.Append("\n<color=#8a93a6>").Append(label).Append(":</color> ").Append(value);

        static string RelationWord(float v) =>
            v <= -50 ? "<color=#e06c6c>hostile</color>" : v < -10 ? "<color=#e0a06c>cold</color>" : v <= 10 ? "neutral" : v < 50 ? "<color=#a8d88f>friendly</color>" : "<color=#7bd88f>close</color>";

        Entity NpcEntity(int id)
        {
            var idx = _em.GetBuffer<EntityRef>(_world, true);
            for (int i = 0; i < idx.Length; i++) if (idx[i].Id == id) return idx[i].Entity;
            return Entity.Null;
        }

        // ------------------------------------------------------------------ pieces

        /// <summary>One bar per stat (name, filled part, value / max); false when none of the stats is there.</summary>
        bool StatRows(Transform parent, List<int> which, DynamicBuffer<StatValue> values)
        {
            bool any = false;
            foreach (int stat in which)
            {
                float? v = null;
                for (int i = 0; i < values.Length; i++) if (values[i].Stat == stat) { v = values[i].Value; break; }
                if (v == null || !_statRules.TryGetValue(stat, out var rule)) continue;
                any = true;
                var row = UiKit.Rect(parent, "Stat_" + Ids.Name(stat), Vector2.zero, Vector2.one);
                var le = row.gameObject.AddComponent<LayoutElement>();
                le.minHeight = le.preferredHeight = 34;
                var name = UiKit.Label(row, "Name", 22, TextAnchor.MiddleLeft, new Vector2(0, 0), new Vector2(0.34f, 1));
                name.text = _statNames.TryGetValue(stat, out var n) ? n : Ids.Name(stat);
                var back = UiKit.Panel(row, "Bar", new Vector2(0.35f, 0.22f), new Vector2(0.80f, 0.78f), BarBack);
                back.GetComponent<Image>().raycastTarget = false;
                float f = rule.Max > rule.Min ? Mathf.Clamp01((v.Value - rule.Min) / (rule.Max - rule.Min)) : 0;
                var fill = UiKit.Panel(back, "Fill", Vector2.zero, new Vector2(f, 1), UiKit.AccentColor);
                fill.GetComponent<Image>().raycastTarget = false;
                var val = UiKit.Label(row, "Value", 20, TextAnchor.MiddleRight, new Vector2(0.81f, 0), new Vector2(1, 1));
                val.text = $"{v.Value:0} <color=#8a93a6>/ {rule.Max:0}</color>";
            }
            return any;
        }

        static void Heading(Transform parent, string text)
        {
            var t = Line(parent, $"<color=#f2c75a>{text}</color>", 24);
            t.GetComponent<LayoutElement>().minHeight = 44;
            t.alignment = TextAnchor.LowerLeft;
        }

        /// <summary>A line of text as tall as it needs (wrapped to the width of the list).</summary>
        static Text Line(Transform parent, string text, int size)
        {
            var t = UiKit.Label(parent, "Line", size, TextAnchor.UpperLeft, Vector2.zero, Vector2.one);
            t.text = text;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.gameObject.AddComponent<LayoutElement>();
            return t;
        }

        /// <summary>A square cut of the picture: from the top of a tall one (the face of a standing character), the middle of a wide one.</summary>
        static void Thumb(Transform parent, Texture2D tex)
        {
            var raw = UiKit.Rect(parent, "Thumb", Vector2.zero, Vector2.one).gameObject.AddComponent<RawImage>();
            raw.texture = tex;
            raw.raycastTarget = false;
            float aspect = (float)tex.width / tex.height;
            if (aspect < 1)
            {
                float w = 0.7f, h = w * aspect;
                raw.uvRect = new Rect((1 - w) / 2, Mathf.Max(0, 1 - h - 0.02f), w, h);
            }
            else raw.uvRect = new Rect((1 - 1 / aspect) / 2, 0, 1 / aspect, 1);
        }

        /// <summary>A picture fitted inside the rect without stretching; <paramref name="top"/> sticks it to the top (a face stays visible).</summary>
        static RawImage Picture(Transform parent, Texture2D tex, Vector2 min, Vector2 max, bool top = false)
        {
            var box = UiKit.Rect(parent, "Frame", min, max);
            var rt = UiKit.Rect(box, "Picture", Vector2.zero, Vector2.one);
            if (top) { rt.pivot = new Vector2(0.5f, 1); }
            var fit = rt.gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fit.aspectRatio = (float)tex.width / tex.height;
            var raw = rt.gameObject.AddComponent<RawImage>();
            raw.texture = tex;
            raw.raycastTarget = false;
            return raw;
        }
    }
}
