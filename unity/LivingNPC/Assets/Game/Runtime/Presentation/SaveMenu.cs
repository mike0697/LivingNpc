using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LivingNPC.Simulation;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// The save / load screen (Esc in game, "Load" on the title): a row of tabs, a 3x2 grid of slots with the picture
    /// of the screen at the moment of saving, pages A (automatic saves, only loadable) and 1-9. Drawn with IMGUI like the
    /// title screen; what saving, loading and leaving do is up to the host (<see cref="GameBootstrap"/>).
    /// </summary>
    public class SaveMenu
    {
        public enum Tab { Save, Load, Settings }

        // ---- set by the host
        /// <summary>A button with the pictures of title.json: (rect, label, highlighted) -> clicked.</summary>
        public Func<Rect, string, bool, bool> Button;
        /// <summary>In game: Save, Load, Settings, Main menu, Quit. On the title: Load and Settings.</summary>
        public bool InGame;
        /// <summary>Why the game cannot be saved right now (dialog, journey), null when it can.</summary>
        public Func<string> SaveBlocked;
        /// <summary>Writes the game into a slot: null when done, else what went wrong.</summary>
        public Func<string, string> Save;
        public Action<JObject> Load;
        public Action Close, MainMenu, Quit;
        /// <summary>Picture drawn behind the menu (cropped to the screen), or null for the plain dark one.</summary>
        public Texture Background;

        public bool IsOpen { get; private set; }
        public Tab Current { get; private set; }

        const int Columns = 3, Rows = 2, PerPage = Columns * Rows, Pages = 9;
        /// <summary>0 = page A (automatic saves), 1-9 = the player's pages.</summary>
        int _page = 1;

        // a question waiting for "Yes" / "No"
        string _ask;
        Action _onYes;
        string _note;
        float _noteUntil;

        class SlotInfo
        {
            public bool Exists;
            public string Title, When, Place, Problem;
            public Texture2D Thumb;
            public JObject Save;
        }
        readonly Dictionary<string, SlotInfo> _slots = new Dictionary<string, SlotInfo>();

        GUIStyle _pagerStyle, _pageStyle, _captionStyle, _smallStyle, _emptyStyle, _boxStyle, _linkStyle;
        static readonly Color Shade = new Color(0.01f, 0.04f, 0.08f, 0.96f);
        static readonly Color EmptySlot = new Color(0.04f, 0.27f, 0.36f, 1f);
        static readonly Color EmptyStripe = new Color(0.3f, 0.47f, 0.53f, 1f);
        static readonly Color Accent = new Color(0.1f, 0.62f, 0.86f, 1f);

        public void Open(Tab tab)
        {
            IsOpen = true;
            Current = tab;
            _ask = null; _onYes = null; _note = null;
            Invalidate();
            // loading from the title: start on the page of the newest save
            if (tab == Tab.Load && !InGame) foreach (var path in SaveGame.AllSaves()) { _page = PageOf(Path.GetFileNameWithoutExtension(path)); break; }
        }

        static int PageOf(string slot)
        {
            if (slot == SaveGame.AutoSlot || slot == SaveGame.QuickSlot) return 0;
            int dash = slot.IndexOf('-');
            return dash > 0 && int.TryParse(slot.Substring(0, dash), out int p) && p >= 1 && p <= Pages ? p : 1;
        }

        public void Hide()
        {
            GameSettings.SaveIfChanged();
            IsOpen = false;
            _ask = null; _onYes = null;
            Invalidate();
        }

        /// <summary>Esc while the menu is up: first closes a question, then the menu.</summary>
        public void Back()
        {
            if (_ask != null) { _ask = null; _onYes = null; return; }
            Close?.Invoke();
        }

        /// <summary>Forgets what was read from disk (and frees the pictures): read again on the next draw.</summary>
        void Invalidate()
        {
            foreach (var s in _slots.Values) if (s.Thumb != null) UiKit.Kill(s.Thumb);
            _slots.Clear();
        }

        static string SlotId(int page, int index)
            => page == 0 ? (index == 0 ? SaveGame.AutoSlot : index == 1 ? SaveGame.QuickSlot : null) : $"{page}-{index + 1}";

        SlotInfo Info(string slot)
        {
            if (_slots.TryGetValue(slot, out var info)) return info;
            info = new SlotInfo();
            _slots[slot] = info;
            var path = SaveGame.SlotPath(slot);
            info.Exists = File.Exists(path) || File.Exists(path + ".bak");
            if (!info.Exists) return info;

            info.When = FormatDate(File.Exists(path) ? File.GetLastWriteTime(path) : File.GetLastWriteTime(path + ".bak"));
            info.Save = SaveGame.Read(path, out var problem);
            if (info.Save == null) info.Problem = problem ?? "not readable";
            else if (info.Save["meta"] is JObject meta)
            {
                int now = meta.Value<int?>("now") ?? 0;
                int dow = meta.Value<int?>("startDayOfWeek") ?? 0;
                info.Title = GameTime.Format(now, dow);
                var place = meta["place"];
                info.Place = place != null && place.Type != JTokenType.Null ? TextTable.Localize(place) : meta.Value<string>("node");
            }

            var thumb = SaveGame.ThumbPath(slot);
            if (File.Exists(thumb))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = Path.GetFileName(thumb), wrapMode = TextureWrapMode.Clamp };
                if (tex.LoadImage(File.ReadAllBytes(thumb), true)) info.Thumb = tex; else UiKit.Kill(tex);
            }
            return info;
        }

        static string FormatDate(DateTime t)
        {
            return t.ToString("ddd, dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);     // "Tue, 03 Nov 2026, 23:45"
        }

        void Say(string text) { _note = text; _noteUntil = Time.unscaledTime + 3f; }

        void Ask(string question, Action onYes) { _ask = question; _onYes = onYes; }

        // ------------------------------------------------------------------ drawing

        void MakeStyles(float u)
        {
            _pageStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            _captionStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, clipping = TextClipping.Clip, wordWrap = false };
            _smallStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, clipping = TextClipping.Clip, wordWrap = false };
            _emptyStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _boxStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _linkStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperRight };
            _pageStyle.fontSize = Mathf.RoundToInt(44 * u);
            _pageStyle.normal.textColor = Accent;
            _captionStyle.fontSize = Mathf.RoundToInt(22 * u);
            _smallStyle.fontSize = Mathf.RoundToInt(18 * u);
            _smallStyle.normal.textColor = new Color(0.75f, 0.8f, 0.86f);
            _emptyStyle.fontSize = Mathf.RoundToInt(22 * u);
            _boxStyle.fontSize = Mathf.RoundToInt(28 * u);
            _linkStyle.fontSize = Mathf.RoundToInt(22 * u);
            _linkStyle.normal.textColor = new Color(0.6f, 0.63f, 0.68f);
            _linkStyle.hover.textColor = new Color(1f, 0.55f, 0.55f);
        }

        public void Draw()
        {
            if (!IsOpen) return;
            float u = Screen.height / 1080f;
            MakeStyles(u);

            Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, 1));
            if (Background != null) GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Background, ScaleMode.ScaleAndCrop);
            Fill(new Rect(0, 0, Screen.width, Screen.height), Background != null ? new Color(Shade.r, Shade.g, Shade.b, 0.85f) : Shade);

            bool modal = _ask != null;
            GUI.enabled = !modal;
            DrawTabs(u);
            if (Current == Tab.Settings) DrawSettings(u);
            else { DrawGrid(u); DrawPager(u); }
            GUI.enabled = true;

            if (_note != null && Time.unscaledTime < _noteUntil)
                GUI.Label(new Rect(0, Screen.height - 70 * u, Screen.width, 40 * u), _note, _emptyStyle);
            if (modal) DrawQuestion(u);
        }

        void DrawTabs(float u)
        {
            var tabs = new List<(string label, bool on, Action click)> { ("Back", false, () => Close?.Invoke()) };
            if (InGame) tabs.Add(("Save", Current == Tab.Save, () => Switch(Tab.Save)));
            tabs.Add(("Load", Current == Tab.Load, () => Switch(Tab.Load)));
            tabs.Add(("Settings", Current == Tab.Settings, () => Switch(Tab.Settings)));
            if (InGame)
            {
                tabs.Add(("Main menu", false, () => Ask("Return to the main menu?\nThe game is saved in the autosave.", () => MainMenu?.Invoke())));
                tabs.Add(("Quit", false, () => Ask("Quit the game?\nThe game is saved in the autosave.", () => Quit?.Invoke())));
            }
            float margin = 70 * u, gap = 12 * u, h = 80 * u;
            float w = Mathf.Min(380 * u, (Screen.width - 2 * margin - gap * (tabs.Count - 1)) / tabs.Count);
            float x = (Screen.width - (w * tabs.Count + gap * (tabs.Count - 1))) / 2f;
            foreach (var t in tabs)
            {
                if (Button(new Rect(x, 40 * u, w, h), t.label, t.on)) t.click();
                x += w + gap;
            }
        }

        void Switch(Tab tab)
        {
            if (Current == Tab.Settings) GameSettings.SaveIfChanged();
            Current = tab;
        }

        // ------------------------------------------------------------------ settings

        GUIStyle _rowStyle, _valueStyle, _hintStyle;

        void DrawSettings(float u)
        {
            _rowStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
            _valueStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight };
            _hintStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, wordWrap = true };
            _rowStyle.fontSize = Mathf.RoundToInt(28 * u);
            _valueStyle.fontSize = Mathf.RoundToInt(26 * u);
            _hintStyle.fontSize = Mathf.RoundToInt(18 * u);
            _hintStyle.normal.textColor = new Color(0.6f, 0.63f, 0.68f);

            GUI.Label(new Rect(0, 150 * u, Screen.width, 70 * u), "Settings", _pageStyle);

            float w = 1100 * u, x = (Screen.width - w) / 2f, y = 280 * u, rowH = 90 * u;
            float labelW = 330 * u, valueW = 110 * u, sliderX = x + labelW + 20 * u, sliderW = w - labelW - valueW - 60 * u;

            void Volume(string label, float value, Action<float> set)
            {
                GUI.Label(new Rect(x, y, labelW, rowH), label, _rowStyle);
                float v = Slider(new Rect(sliderX, y + rowH / 2f - 20 * u, sliderW, 40 * u), value, u);
                if (!Mathf.Approximately(v, value)) set(v);
                GUI.Label(new Rect(x + w - valueW, y, valueW, rowH), $"{Mathf.RoundToInt(v * 100)}%", _valueStyle);
                y += rowH;
            }
            Volume("Master volume", GameSettings.Master, v => GameSettings.Master = v);
            Volume("Music", GameSettings.Music, v => GameSettings.Music = v);
            Volume("Ambience", GameSettings.Ambient, v => GameSettings.Ambient = v);
            Volume("Sound effects", GameSettings.Effects, v => GameSettings.Effects = v);

            y += 20 * u;
            GUI.Label(new Rect(x, y, labelW, rowH), "Display", _rowStyle);
            float bw = 260 * u, bh = 64 * u, by = y + (rowH - bh) / 2f;
            bool full = GameSettings.Fullscreen;
            if (Button(new Rect(sliderX, by, bw, bh), "Fullscreen", full)) GameSettings.Fullscreen = true;
            if (Button(new Rect(sliderX + bw + 20 * u, by, bw, bh), "Windowed", !full)) GameSettings.Fullscreen = false;
            if (Application.isEditor)
                GUI.Label(new Rect(sliderX + 2 * bw + 50 * u, y, w - (sliderX - x) - 2 * bw - 50 * u, rowH), "no effect in the Editor: applies to the built game", _hintStyle);
            y += rowH + 50 * u;

            float rw = 400 * u, rx = (Screen.width - 2 * rw - 30 * u) / 2f;
            if (Button(new Rect(rx, y, rw, bh), "Reset volumes", false))
                Ask("Set the volumes back to their defaults?", () => { GameSettings.ResetDefaults(); Say("Volumes reset"); });
            if (Button(new Rect(rx + rw + 30 * u, y, rw, bh), "Reset panels", false))
                Ask("Put Present, Journal and Backpack back in place, open?", () =>
                {
                    GameSettings.ResetPanels();
                    GameSettings.SaveIfChanged();
                    DraggablePanel.ReloadAll();
                    Say("Panels reset");
                });
            GUI.Label(new Rect(x, y + bh + 20 * u, w, 40 * u), "Drag the title bar of Present, Journal and Backpack to move them.", _hintStyle);
        }

        /// <summary>A horizontal slider 0-1 drawn at the menu's size (Unity's own is a few pixels high): click or drag anywhere on it.</summary>
        static float Slider(Rect r, float value, float u)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var e = Event.current;
            var hit = new Rect(r.x - 14 * u, r.y - 10 * u, r.width + 28 * u, r.height + 20 * u);
            float At(Vector2 p) => Mathf.Clamp01((p.x - r.x) / r.width);
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (GUI.enabled && e.button == 0 && hit.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        value = At(e.mousePosition);
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id) { value = At(e.mousePosition); e.Use(); }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); }
                    break;
                case EventType.Repaint:
                {
                    float th = 8 * u, cy = r.center.y;
                    var track = new Rect(r.x, cy - th / 2f, r.width, th);
                    Fill(track, new Color(1, 1, 1, 0.18f));
                    Fill(new Rect(track.x, track.y, track.width * value, th), GUI.enabled ? Accent : new Color(0.4f, 0.45f, 0.5f));
                    bool hot = GUIUtility.hotControl == id || (GUI.enabled && hit.Contains(e.mousePosition));
                    float k = (hot ? 30 : 24) * u;
                    Fill(new Rect(r.x + r.width * value - k / 2f, cy - k / 2f, k, k), hot ? Color.white : new Color(0.85f, 0.88f, 0.92f));
                    break;
                }
            }
            return value;
        }

        // ------------------------------------------------------------------ slots

        void DrawGrid(float u)
        {
            string blocked = Current == Tab.Save ? SaveBlocked?.Invoke() : null;
            string heading = _page == 0 ? "Automatic saves" : $"Page {_page}";
            GUI.Label(new Rect(0, 150 * u, Screen.width, 70 * u), heading, _pageStyle);
            string sub = null;
            if (Current == Tab.Save && blocked != null) sub = $"You cannot save now: {blocked}";
            else if (Current == Tab.Save && _page == 0) sub = "These can only be loaded";
            if (sub != null) GUI.Label(new Rect(0, 215 * u, Screen.width, 36 * u), sub, _emptyStyle);

            float tw = 400 * u, th = 225 * u, gx = 50 * u, rowH = th + 115 * u;
            float x0 = (Screen.width - (Columns * tw + (Columns - 1) * gx)) / 2f, y0 = 265 * u;
            for (int i = 0; i < PerPage; i++)
            {
                string slot = SlotId(_page, i);
                if (slot == null) continue;
                var r = new Rect(x0 + (i % Columns) * (tw + gx), y0 + (i / Columns) * rowH, tw, th);
                DrawSlot(r, slot, i, blocked, u);
            }
        }

        void DrawSlot(Rect r, string slot, int index, string blocked, float u)
        {
            var info = Info(slot);
            bool canClick = Current == Tab.Save
                ? _page != 0 && blocked == null
                : info.Exists && info.Save != null;

            // picture, or the empty slot
            if (info.Thumb != null) GUI.DrawTexture(r, info.Thumb, ScaleMode.ScaleAndCrop);
            else
            {
                Fill(r, EmptySlot);
                if (Event.current.type == EventType.Repaint) DrawStripe(r);
                if (info.Exists) GUI.Label(r, info.Problem != null ? "Cannot be loaded" : "No picture", _emptyStyle);
            }

            bool hover = GUI.enabled && canClick && r.Contains(Event.current.mousePosition);
            if (!canClick && info.Exists) Fill(r, new Color(0, 0, 0, 0.35f));
            if (hover) Frame(r, Accent, 4 * u);

            var old = GUI.enabled;
            GUI.enabled = old && canClick;
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) Click(slot, info);
            GUI.enabled = old;

            // caption: day and hour of the game + place, then the real date; "Elimina" on the right
            float cy = r.yMax + 8 * u, lw = r.width - 110 * u;
            if (_page == 0)
            {
                var strip = new Rect(r.x, r.y, r.width, 36 * u);
                Fill(strip, new Color(0, 0, 0, 0.55f));
                GUI.Label(new Rect(strip.x + 10 * u, strip.y + 3 * u, strip.width, strip.height), index == 0 ? "Autosave" : "Quick save (F6)", _captionStyle);
            }
            if (!info.Exists)
            {
                GUI.Label(new Rect(r.x + 8 * u, cy, lw, 30 * u), "Empty slot", _captionStyle);
                return;
            }
            if (info.Problem != null)
            {
                GUI.Label(new Rect(r.x, cy, lw, 30 * u), info.When, _captionStyle);
                GUI.Label(new Rect(r.x, cy + 32 * u, r.width, 60 * u), info.Problem, _smallStyle);
            }
            else
            {
                GUI.Label(new Rect(r.x, cy, lw, 30 * u), info.Title ?? info.When, _captionStyle);
                GUI.Label(new Rect(r.x, cy + 32 * u, r.width, 26 * u), string.IsNullOrEmpty(info.Place) ? info.When : $"{info.Place}  ·  {info.When}", _smallStyle);
            }
            if (GUI.Button(new Rect(r.xMax - 110 * u, cy, 110 * u, 30 * u), "Delete", _linkStyle))
                Ask($"Delete this save?\n{info.Title ?? ""}  {info.When}", () =>
                {
                    try { SaveGame.Delete(slot); Say("Save deleted"); }
                    catch (Exception e) { Debug.LogException(e); Say("Delete failed: " + e.Message); }
                    Invalidate();
                });
        }

        void Click(string slot, SlotInfo info)
        {
            if (Current == Tab.Save)
            {
                void DoSave()
                {
                    var error = Save?.Invoke(slot);
                    Say(error == null ? "Game saved" : "Save failed: " + error);
                    Invalidate();
                }
                if (info.Exists) Ask("Overwrite this save?", DoSave);
                else DoSave();
                return;
            }
            var save = info.Save;
            if (InGame) Ask("Load this save?\nUnsaved progress will be lost.", () => Load?.Invoke(save));
            else Load?.Invoke(save);
        }

        void DrawPager(float u)
        {
            float s = 60 * u, gap = 10 * u, y = 950 * u;
            int count = Pages + 3;      // < A 1..9 >
            float x = (Screen.width - (count * s + (count - 1) * gap)) / 2f;
            _pagerStyle ??= new GUIStyle(_pageStyle);
            var style = _pagerStyle;
            style.fontSize = Mathf.RoundToInt(34 * u);
            for (int i = 0; i < count; i++)
            {
                var r = new Rect(x + i * (s + gap), y, s, s);
                string label; int target;
                if (i == 0) { label = "<"; target = (_page + Pages) % (Pages + 1); }
                else if (i == count - 1) { label = ">"; target = (_page + 1) % (Pages + 1); }
                else { target = i - 1; label = target == 0 ? "A" : target.ToString(); }
                bool on = i > 0 && i < count - 1 && target == _page;
                style.normal.textColor = on ? Color.white : new Color(0.55f, 0.58f, 0.63f);
                style.hover.textColor = Accent;
                if (on && Event.current.type == EventType.Repaint) Fill(new Rect(r.x + s * 0.2f, r.yMax - 4 * u, s * 0.6f, 3 * u), Accent);
                if (GUI.Button(r, label, style) && target != _page) { _page = target; Invalidate(); }
            }
        }

        void DrawQuestion(float u)
        {
            Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, 0.6f));
            float w = 760 * u, h = 320 * u;
            var box = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            Fill(box, new Color(0.03f, 0.07f, 0.12f, 0.98f));
            Frame(box, Accent, 2 * u);
            GUI.Label(new Rect(box.x + 30 * u, box.y + 20 * u, w - 60 * u, h - 130 * u), _ask, _boxStyle);
            float bw = 220 * u, bh = 70 * u, by = box.yMax - bh - 30 * u;
            if (Button(new Rect(box.center.x - bw - 15 * u, by, bw, bh), "Yes", false))
            {
                var yes = _onYes;
                _ask = null; _onYes = null;
                yes?.Invoke();
            }
            else if (Button(new Rect(box.center.x + 15 * u, by, bw, bh), "No", false)) { _ask = null; _onYes = null; }
        }

        // ------------------------------------------------------------------ shapes

        static void Fill(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        static void Frame(Rect r, Color c, float t)
        {
            Fill(new Rect(r.x - t, r.y - t, r.width + 2 * t, t), c);
            Fill(new Rect(r.x - t, r.yMax, r.width + 2 * t, t), c);
            Fill(new Rect(r.x - t, r.y, t, r.height), c);
            Fill(new Rect(r.xMax, r.y, t, r.height), c);
        }

        static Texture2D s_Stripe;

        /// <summary>The lighter diagonal band of an empty slot (top-right corner).</summary>
        static void DrawStripe(Rect r)
        {
            if (s_Stripe == null)
            {
                const int n = 256;
                s_Stripe = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
                var px = new Color32[n * n];
                var c = (Color32)EmptyStripe;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        // a smooth edge: the share of the pixel past the line, over about one pixel
                        float a = Mathf.Clamp01(x * 0.55f + y * 0.45f - n * 0.75f + 0.5f);
                        px[y * n + x] = new Color32(c.r, c.g, c.b, (byte)(a * 255));
                    }
                s_Stripe.SetPixels32(px);
                s_Stripe.Apply();
            }
            GUI.DrawTexture(r, s_Stripe, ScaleMode.StretchToFill);
        }
    }
}
