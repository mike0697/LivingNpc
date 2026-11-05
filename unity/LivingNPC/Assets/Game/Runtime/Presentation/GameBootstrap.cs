using System;
using System.Collections.Generic;
using System.IO;
using LivingNPC.Content;
using LivingNPC.Simulation;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// Entry point. Created automatically after the first scene loads, so the project needs no scene setup.
    /// Load order: JSON packs -> validation/compile -> entities -> view.
    /// F5 reloads the content from disk and keeps the game in progress (edit a quest/map with any editor or LLM,
    /// press F5, see it); F6 saves, F9 loads the quick save; a new day writes the autosave.
    /// </summary>
    public class GameBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (FindFirstObjectByType<GameBootstrap>() != null) return;
            DontDestroyOnLoad(new GameObject("LivingNPCGame", typeof(GameBootstrap)));
        }

        GameView _view;
        CompiledContent _compiled;
        bool _loaded;
        string _error;
        string _status;
        float _statusUntil;
        int _autoDay;

        public static string ReportPath => Path.Combine(Application.persistentDataPath, "content_report.txt");

        // ---- main menu: the title of the selected world's game; "Load world" picks another world
        bool _inMenu = true;
        bool _pickWorld;
        string _menuWorld;
        List<string> _worlds = new List<string>();
        readonly Dictionary<string, string> _titles = new Dictionary<string, string>();
        Vector2 _menuScroll;
        GUIStyle _titleStyle, _buttonStyle, _labelStyle, _smallStyle, _noteStyle;
        TitleScreen _title = new TitleScreen();
        /// <summary>Pictures of the main menu's buttons (title.json "menuButtons", else "buttons"), or null.</summary>
        ButtonSkin _menuSkin;
        const int MenuMusicChannel = AudioPlayer.MenuMusicChannel;

        // ---- save / load screen (Esc in game, "Load" on the title)
        readonly SaveMenu _saveMenu = new SaveMenu();
        /// <summary>The screen as it was when Esc was pressed: the picture of the saves made from the menu.</summary>
        Texture2D _shot;
        /// <summary>A screen capture is waiting for the end of the frame: the hints are not drawn, so they stay out of the picture.</summary>
        bool _shooting;
        /// <summary>The selected world has at least one save (read when the title screen is shown, not every frame).</summary>
        bool _anySave;

        void Start()
        {
            GameSettings.Apply();
            _view = new GameView(transform);
            SetupSaveMenu();
            // LIVINGNPC_WORLD set (dev / automation): skip the menu and start that world straight away.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIVINGNPC_WORLD")))
            {
                _menuWorld = ContentLoader.SelectedWorld;
                LoadTitle();            // the button pictures of title.json apply to the game too
                StartGame(null);
                return;
            }
            _view.SetVisible(false);
            RefreshMenu();
        }

        /// <summary>Reads again the worlds on disk and their titles (the menu draws every frame, so they are kept).</summary>
        void RefreshMenu()
        {
            _worlds = ContentLoader.ListWorlds();
            _titles.Clear();
            foreach (var w in _worlds) _titles[w] = ContentLoader.WorldTitle(w);
            _menuWorld = ContentLoader.SelectedWorld;
            if (_menuWorld != null && !_worlds.Contains(_menuWorld)) _menuWorld = _worlds.Count > 0 ? _worlds[0] : null;
            ApplyTitleScreen();
        }

        /// <summary>Reads title.json of the menu's world: the pictures of every button of the game and of the menu's.</summary>
        void LoadTitle()
        {
            _title = ContentLoader.LoadTitleScreen(_menuWorld);
            foreach (var p in _title.Problems) Debug.LogWarning("[LivingNPC] " + p);
            UiKit.SetSkin(ButtonSkin.Load(_title.Buttons));
            _menuSkin?.Dispose();
            _menuSkin = ButtonSkin.Load(_title.MenuButtons);
            _buttonStyle = null;        // made again with the new pictures
        }

        /// <summary>
        /// The menu button style: Unity's plain button, or with the pictures of title.json a style with no background
        /// of its own (<see cref="MenuButton"/> draws the picture) and the words in the skin's colour.
        /// </summary>
        GUIStyle MakeMenuButtonStyle()
        {
            if (_menuSkin == null) return new GUIStyle(GUI.skin.button);
            // GUIStyle.none draws nothing of its own (a copy of the button style keeps Unity's grey box)
            var s = new GUIStyle(GUIStyle.none) { alignment = TextAnchor.MiddleCenter, font = GUI.skin.button.font, clipping = TextClipping.Clip };
            foreach (var state in new[] { s.normal, s.focused, s.hover, s.active, s.onNormal, s.onFocused, s.onHover, s.onActive })
                state.textColor = _menuSkin.TextColor;
            return s;
        }

        /// <summary>A menu button: with the pictures of title.json, the normal or hover one, then the clickable words.</summary>
        bool MenuButton(Rect r, string label, bool highlight = false)
        {
            if (_menuSkin != null && Event.current.type == EventType.Repaint)
            {
                bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
                var old = GUI.color;
                if (!GUI.enabled) GUI.color = new Color(old.r * 0.7f, old.g * 0.7f, old.b * 0.7f, old.a * 0.6f);
                DrawSliced(r, hover || highlight ? _menuSkin.HoverTexture : _menuSkin.NormalTexture, _menuSkin.Border);
                GUI.color = old;
            }
            return GUI.Button(r, label, _buttonStyle);
        }

        /// <summary>
        /// A picture over <paramref name="r"/> as the game's buttons draw it (3-slice): scaled to the button's height as a
        /// whole, so its bottom edge stays at the bottom; the left and right <paramref name="border"/> pixels keep their
        /// shape and the middle stretches sideways.
        /// </summary>
        static void DrawSliced(Rect r, Texture2D tex, int border)
        {
            if (tex == null) return;
            border = Mathf.Min(border, tex.width / 2 - 1);
            if (border <= 0) { GUI.DrawTexture(r, tex, ScaleMode.StretchToFill); return; }
            float d = Mathf.Min(border * r.height / tex.height, r.width / 2f);
            float bu = border / (float)tex.width;
            // whole pixels: IMGUI snaps each piece on its own, and two edges in the middle of a pixel leave a 1 px gap
            // between them, a thin line of whatever is behind the button
            float x0 = Mathf.Round(r.x), x3 = Mathf.Round(r.xMax), y = Mathf.Round(r.y), h = Mathf.Round(r.yMax) - y;
            float x1 = Mathf.Round(r.x + d), x2 = Mathf.Max(x1, Mathf.Round(r.xMax - d));
            // the middle first and 1 px wider on each side, under the ends: no seam even if a driver rounds differently
            if (x2 > x1) GUI.DrawTextureWithTexCoords(new Rect(x1 - 1, y, x2 - x1 + 2, h), tex, new Rect(bu, 0, 1 - 2 * bu, 1));
            if (x1 > x0) GUI.DrawTextureWithTexCoords(new Rect(x0, y, x1 - x0, h), tex, new Rect(0, 0, bu, 1));
            if (x3 > x2) GUI.DrawTextureWithTexCoords(new Rect(x2, y, x3 - x2, h), tex, new Rect(1 - bu, 0, bu, 1));
        }

        /// <summary>Reads title.json of the menu's world and starts its music (crossfading from another world's).</summary>
        void ApplyTitleScreen()
        {
            LoadTitle();
            _anySave = SaveGame.AllSaves().Count > 0;
            _view.Audio.SetLoop(MenuMusicChannel, _title.MusicPath == null ? null
                : new CompiledSound { Path = _title.MusicPath, Volume = _title.Volume, Fade = CompiledSound.DefaultFade });
        }

        string TitleOf(string world) => world != null && _titles.TryGetValue(world, out var t) ? t : world ?? "";

        /// <summary>Starts the selected world: from a save, or a fresh game when <paramref name="save"/> is null.</summary>
        void StartGame(JObject save)
        {
            _view.Audio.SetLoop(MenuMusicChannel, null);
            Reload(save);
            if (!_loaded) { _inMenu = true; _view.SetVisible(false); ApplyTitleScreen(); return; }
            _inMenu = false; _view.SetVisible(true);
        }

        void BackToMenu()
        {
            if (_saveMenu.IsOpen) CloseGameMenu();
            SaveTo(SaveGame.AutoSlot, "Autosave", _shot, out _);
            _loaded = false; _inMenu = true; _pickWorld = false; _error = null;
            _view.SetVisible(false);
            RefreshMenu();
        }

        /// <summary>The newest save of the selected world that can be loaded (any slot), or null.</summary>
        JObject LatestSave(out string problem)
        {
            problem = "no saves";
            foreach (var path in SaveGame.AllSaves())
            {
                var save = SaveGame.Read(path, out var why);
                if (save != null) { problem = null; return save; }
                problem = why;
            }
            return null;
        }

        // ------------------------------------------------------------------ save / load screen

        void SetupSaveMenu()
        {
            _saveMenu.Button = (r, label, on) => MenuButton(r, label, on);
            _saveMenu.SaveBlocked = () =>
                !_loaded ? "no game in progress"
                : SaveGame.CanSave(Em, _worldEntity, _playerEntity, out var why) ? null : why;
            _saveMenu.Save = slot => SaveTo(slot, null, _shot, out var error) ? null : error;
            _saveMenu.Load = save =>
            {
                if (_saveMenu.InGame)
                {
                    CloseGameMenu();
                    Reload(save);
                    if (_loaded) Say("Game loaded");
                }
                else { _saveMenu.Hide(); StartGame(save); }
            };
            _saveMenu.Close = () =>
            {
                if (_saveMenu.InGame) CloseGameMenu();
                else { _saveMenu.Hide(); _anySave = SaveGame.AllSaves().Count > 0; }     // saves may have been deleted
            };
            _saveMenu.MainMenu = BackToMenu;
            _saveMenu.Quit = () => { SaveTo(SaveGame.AutoSlot, "Autosave", _shot, out _); Quit(); };
        }

        /// <summary>"Load" / "Settings" on the title: the same screen as in game, over the title's picture.</summary>
        void OpenTitleMenu(SaveMenu.Tab tab)
        {
            _error = null;
            _saveMenu.InGame = false;
            _saveMenu.Background = TextureCache.Get(_title.ImagePath);
            _saveMenu.Open(tab);
        }

        /// <summary>Esc in game: takes the picture of the screen, then pauses the game under the menu.</summary>
        void OpenGameMenu()
        {
            StartCoroutine(Shoot(shot =>
            {
                if (_inMenu || !_loaded) { if (shot != null) Destroy(shot); return; }
                if (_shot != null) Destroy(_shot);
                _shot = shot;
                Pause(true);
                _view.SetInteractive(false);
                _saveMenu.InGame = true;
                _saveMenu.Background = null;
                _saveMenu.Open(SaveGame.CanSave(Em, _worldEntity, _playerEntity, out _) ? SaveMenu.Tab.Save : SaveMenu.Tab.Load);
            }));
        }

        void CloseGameMenu()
        {
            _saveMenu.Hide();
            Pause(false);
            _view.SetInteractive(true);
        }

        /// <summary>Stops the simulation (clock, journeys, events) while the menu is up; the music keeps playing.</summary>
        static void Pause(bool paused)
        {
            var group = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<SimulationSystemGroup>();
            if (group != null) group.Enabled = !paused;
        }

        /// <summary>Waits for the end of the frame (when the screen is drawn, without the hints) and hands over a small copy of it, or null.</summary>
        System.Collections.IEnumerator Shoot(Action<Texture2D> then)
        {
            _shooting = true;
            yield return new WaitForEndOfFrame();
            Texture2D shot = null;
            try { shot = CaptureThumb(384); }
            catch (Exception e) { Debug.LogException(e); }
            _shooting = false;
            then(shot);
        }

        /// <summary>The screen shrunk to <paramref name="width"/> pixels (average of the pixels each one covers, on the CPU so colours stay exact).</summary>
        static Texture2D CaptureThumb(int width)
        {
            var full = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                int sw = full.width, sh = full.height;
                int w = Mathf.Min(width, sw), h = Mathf.Max(1, Mathf.RoundToInt(w * sh / (float)sw));
                var src = full.GetPixels32();
                var dst = new Color32[w * h];
                for (int y = 0; y < h; y++)
                {
                    int y0 = y * sh / h, y1 = Mathf.Max(y0 + 1, (y + 1) * sh / h);
                    for (int x = 0; x < w; x++)
                    {
                        int x0 = x * sw / w, x1 = Mathf.Max(x0 + 1, (x + 1) * sw / w);
                        int r = 0, g = 0, b = 0, n = 0;
                        for (int yy = y0; yy < y1; yy++)
                            for (int xx = x0, i = yy * sw + x0; xx < x1; xx++, i++) { r += src[i].r; g += src[i].g; b += src[i].b; n++; }
                        dst[y * w + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                    }
                }
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.SetPixels32(dst);
                tex.Apply();
                return tex;
            }
            finally { Destroy(full); }
        }

        void DrawMenu()
        {
            float u = Screen.height / 1080f;
            _titleStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _buttonStyle ??= MakeMenuButtonStyle();
            _labelStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            _titleStyle.fontSize = Mathf.RoundToInt(64 * u);
            _buttonStyle.fontSize = Mathf.RoundToInt(28 * u);
            _labelStyle.fontSize = Mathf.RoundToInt(22 * u);

            var old = GUI.color;
            GUI.color = new Color(0.04f, 0.05f, 0.08f, 1f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = old;
            // the background of title.json fills the screen (cropped, never stretched)
            var bg = _worlds.Count > 0 ? TextureCache.Get(_title.ImagePath) : null;
            if (bg != null) GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), bg, ScaleMode.ScaleAndCrop);

            _smallStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            _smallStyle.fontSize = Mathf.RoundToInt(18 * u);
            _noteStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerLeft, wordWrap = true };
            _noteStyle.fontSize = Mathf.RoundToInt(16 * u);

            float w = 560 * u, bh = 64 * u, gap = 14 * u;
            float x = (Screen.width - w) / 2f, y = 140 * u;

            if (_worlds.Count == 0)
            {
                GUI.Label(new Rect(0, y, Screen.width, 90 * u), "No world", _titleStyle);
                y += 130 * u;
                GUI.Label(new Rect(x - 300 * u, y, w + 600 * u, 140 * u),
                    $"Put the world folder next to the game, named \"World\":\n{Path.Combine(ContentLoader.GameDir(), "World")}\n(or \"Worlds\" with several worlds inside)", _labelStyle);
                y += 160 * u;
                if (MenuButton(new Rect(x, y, w, bh), "Search again")) RefreshMenu();
                y += bh + gap;
                if (MenuButton(new Rect(x, y, w, bh), "Quit")) Quit();
                if (_error != null) GUI.Label(new Rect(20, 20, 1200, 60), _error);
                return;
            }

            if (_pickWorld) { DrawWorldPicker(u, x, y, w, bh, gap); return; }

            // the title screen of the selected world's game; "drawTitle": false when the image already has the title in it
            if (_title.DrawTitle) ShadowLabel(new Rect(0, y, Screen.width, 90 * u), TitleOf(_menuWorld), _titleStyle, 3 * u);
            y += 160 * u;
            if (_title.Problems.Count > 0)
                GUI.Label(new Rect(16 * u, Screen.height - 120 * u, Screen.width * 0.6f, 110 * u), string.Join("\n", _title.Problems), _noteStyle);

            if (ContentLoader.SelectedWorld != _menuWorld) ContentLoader.SelectedWorld = _menuWorld;      // saves and content below refer to this world
            GUI.enabled = _anySave;
            if (MenuButton(new Rect(x, y, w, bh), "Continue"))
            {
                var save = LatestSave(out var problem);
                if (save == null) _error = "Load: " + problem; else StartGame(save);
            }
            y += bh + gap;
            if (MenuButton(new Rect(x, y, w, bh), "Load")) OpenTitleMenu(SaveMenu.Tab.Load);
            GUI.enabled = true;
            y += bh + gap;
            if (MenuButton(new Rect(x, y, w, bh), "New game")) StartGame(null);
            y += bh + gap;
            if (_worlds.Count > 1)
            {
                if (MenuButton(new Rect(x, y, w, bh), "Load world")) { RefreshMenu(); _pickWorld = true; _menuScroll = Vector2.zero; }
                y += bh + gap;
            }
            if (MenuButton(new Rect(x, y, w, bh), "Settings")) OpenTitleMenu(SaveMenu.Tab.Settings);
            y += bh + gap;
            if (MenuButton(new Rect(x, y, w, bh), "Quit")) Quit();

            if (_error != null) GUI.Label(new Rect(20, 20, 1200, 60), _error);
        }

        /// <summary>A label with a dark shadow, readable over any background picture.</summary>
        static void ShadowLabel(Rect r, string text, GUIStyle style, float offset)
        {
            var color = style.normal.textColor;
            style.normal.textColor = new Color(0, 0, 0, 0.75f);
            GUI.Label(new Rect(r.x + offset, r.y + offset, r.width, r.height), text, style);
            style.normal.textColor = color;
            GUI.Label(r, text, style);
        }

        /// <summary>"Load world": the worlds with their game titles; choosing one goes back to its title screen.</summary>
        void DrawWorldPicker(float u, float x, float y, float w, float bh, float gap)
        {
            GUI.Label(new Rect(0, y, Screen.width, 90 * u), "Load world", _titleStyle);
            y += 130 * u;
            float rowH = bh + 22 * u;
            float listH = Mathf.Min(_worlds.Count, 5) * (rowH + gap);
            _menuScroll = GUI.BeginScrollView(new Rect(x, y, w, listH), _menuScroll, new Rect(0, 0, w - 20 * u, _worlds.Count * (rowH + gap)));
            for (int i = 0; i < _worlds.Count; i++)
            {
                var world = _worlds[i];
                bool sel = world == _menuWorld;
                var prev = GUI.backgroundColor;
                if (sel && _menuSkin == null) GUI.backgroundColor = UiKit.AccentColor;
                var r = new Rect(0, i * (rowH + gap), w - 20 * u, rowH);
                if (MenuButton(r, "", sel))
                {
                    // a fresh start on the other world: nothing of the old one is loaded while the menu is up
                    ContentLoader.SelectedWorld = world;
                    _menuWorld = world;
                    _pickWorld = false;
                    _error = null;
                    ApplyTitleScreen();
                }
                GUI.backgroundColor = prev;
                // the words of a row in the colour of the button pictures (white without them)
                var ink = _menuSkin != null ? _menuSkin.TextColor : Color.white;
                var oldLabel = _labelStyle.normal.textColor; var oldSmall = _smallStyle.normal.textColor;
                _labelStyle.normal.textColor = ink; _smallStyle.normal.textColor = ink;
                GUI.Label(new Rect(r.x, r.y + 6 * u, r.width, bh * 0.6f), TitleOf(world), _labelStyle);
                GUI.Label(new Rect(r.x, r.y + bh * 0.6f + 4 * u, r.width, 26 * u), $"folder: {world}", _smallStyle);
                _labelStyle.normal.textColor = oldLabel; _smallStyle.normal.textColor = oldSmall;
            }
            GUI.EndScrollView();
            y += listH + 24 * u;
            if (MenuButton(new Rect(x, y, w, bh), "Back")) _pickWorld = false;
        }

        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        Entity _worldEntity, _playerEntity;

        void Say(string text)
        {
            _status = text; _statusUntil = Time.unscaledTime + 4f;
            Debug.Log("[LivingNPC] " + text);
        }

        EntityManager Em => World.DefaultGameObjectInjectionWorld.EntityManager;

        /// <summary>
        /// Writes the current game into a slot, with <paramref name="shot"/> as its picture (none: the old one is removed).
        /// Refuses while a dialog or a journey is in progress. With a <paramref name="label"/> the outcome is shown in game.
        /// </summary>
        bool SaveTo(string slot, string label, Texture2D shot, out string error)
        {
            error = null;
            if (!_loaded) { error = "no game in progress"; return false; }
            var em = Em;
            if (!SaveGame.CanSave(em, _worldEntity, _playerEntity, out var why))
            {
                error = why;
                if (label != null) Say($"{label}: not possible now, {why}");
                return false;
            }
            try
            {
                var ctx = new SaveContext(em, _worldEntity, _playerEntity, _compiled);
                SaveGame.Write(SaveGame.SlotPath(slot), SaveGame.Capture(ctx));
                if (label != null) Say($"{label} ({slot})");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                error = e.Message;
                if (label != null) Say($"{label}: error, {e.Message}");
                return false;
            }
            // the picture is a nicety: failing to write it never fails the save
            try
            {
                var thumb = SaveGame.ThumbPath(slot);
                if (shot != null) File.WriteAllBytes(thumb, shot.EncodeToPNG());
                else if (File.Exists(thumb)) File.Delete(thumb);
            }
            catch (Exception e) { Debug.LogWarning($"[LivingNPC] picture of the save not written: {e.Message}"); }
            return true;
        }

        /// <summary>Saves into a slot with a picture of the screen as it is now (taken at the end of this frame).</summary>
        void SaveWithShot(string slot, string label, Action<bool> done = null)
        {
            StartCoroutine(Shoot(shot =>
            {
                bool ok = SaveTo(slot, label, shot, out _);
                if (shot != null) Destroy(shot);
                done?.Invoke(ok);
            }));
        }

        void QuickLoad()
        {
            var save = SaveGame.Read(SaveGame.SlotPath(SaveGame.QuickSlot), out var problem);
            if (save == null) { Say("Load: " + problem); return; }
            Reload(save);
            if (_loaded) Say("Game loaded");
        }

        /// <summary>
        /// Rebuilds the world from the content on disk. With <paramref name="restore"/> the world is then put back
        /// into that saved state; F5 passes a snapshot of the running game so editing content never loses progress.
        /// </summary>
        void Reload(JObject restore)
        {
            _loaded = false; _error = null;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) { _error = "ECS default world not available"; return; }
            var em = world.EntityManager;

            var db = ContentLoader.Load();
            var compiled = ContentCompiler.Compile(db);
            db.Report.Save(ReportPath);
            Debug.Log($"[LivingNPC] {db.Report.ErrorCount} content error(s), {db.Report.WarningCount} warning(s). Report: {ReportPath}");
            foreach (var e in db.Report.Entries)
                if (e.Severity == Severity.Error) Debug.LogError($"[LivingNPC] {e.Source}: {e.Message}");

            if (!compiled.Valid) { compiled.Dispose(); _error = "Invalid content, see " + ReportPath; return; }

            em.DestroyEntity(em.UniversalQuery);     // wipe the previous game (hot reload)
            _compiled?.Dispose();                    // old blobs are unreferenced now
            TextureCache.Clear();
            _compiled = compiled;
            var h = WorldBuilder.Build(em, compiled);
            _worldEntity = h.World; _playerEntity = h.Player;

            SaveGame.ClearCarried();                 // a new game must not inherit what the last loaded save carried
            if (restore != null)
            {
                var ctx = new SaveContext(em, h.World, h.Player, compiled);
                try { SaveGame.Apply(ctx, restore); }
                catch (SaveException e) { Debug.LogError($"[LivingNPC] save not applied: {e.Message}"); _status = "Save not applicable: " + e.Message; _statusUntil = Time.unscaledTime + 6f; }
                foreach (var w in ctx.Warnings) Debug.LogWarning($"[LivingNPC] save: {w}");
            }

            _view.Bind(em, h.World, h.Player, db, compiled);
            _autoDay = GameTime.DayOf(em.GetComponentData<ClockState>(h.World).Now);
            _loaded = true;
        }

        /// <summary>A snapshot of the running game, or null when it cannot be saved right now (dialog open, travelling).</summary>
        JObject Snapshot()
        {
            if (!_loaded || !SaveGame.CanSave(Em, _worldEntity, _playerEntity, out _)) return null;
            try { return SaveGame.Capture(new SaveContext(Em, _worldEntity, _playerEntity, _compiled)); }
            catch (Exception e) { Debug.LogException(e); return null; }
        }

        void OnDestroy() { _compiled?.Dispose(); _view?.Audio.Dispose(); _saveMenu.Hide(); if (_shot != null) Destroy(_shot); }

        void Update()
        {
            var kb = Keyboard.current;
            _view?.UpdateAudio();
            bool esc = kb != null && kb.escapeKey.wasPressedThisFrame;
            if (_saveMenu.IsOpen) { if (esc) _saveMenu.Back(); return; }     // the game is paused under the menu
            if (_inMenu) return;
            if (esc) { if (!_shooting && _loaded) OpenGameMenu(); else if (!_loaded) BackToMenu(); return; }
            if (_shooting) return;                                            // the menu opens at the end of this frame
            if (kb != null && kb.f5Key.wasPressedThisFrame)
            {
                var keep = Snapshot();
                if (keep == null && _loaded) Say("Content reloaded, but the game restarts from scratch (dialogue or journey in progress)");
                Reload(keep);
            }
            if (kb != null && kb.f6Key.wasPressedThisFrame) SaveWithShot(SaveGame.QuickSlot, "Game saved");
            if (kb != null && kb.f9Key.wasPressedThisFrame) QuickLoad();

            if (_loaded)
            {
                int day = GameTime.DayOf(Em.GetComponentData<ClockState>(_worldEntity).Now);
                // retried each frame until the world is at rest
                if (day > _autoDay && SaveGame.CanSave(Em, _worldEntity, _playerEntity, out _))
                    SaveWithShot(SaveGame.AutoSlot, "Autosave", ok => { if (ok) _autoDay = Mathf.Max(_autoDay, day); });
                _view.Tick();
            }
        }

        void OnGUI()
        {
            if (_saveMenu.IsOpen) { _saveMenu.Draw(); return; }
            if (_inMenu) { DrawMenu(); return; }
            if (_shooting) return;                                            // the picture of the save shows the game, not the hints
            if (_error != null) GUI.Label(new Rect(20, 20, 1200, 60), _error);
            else GUI.Label(new Rect(8, Screen.height - 22, 700, 20), "F5: reload content   F6: save   F9: load   Esc: menu");
            if (_status != null && Time.unscaledTime < _statusUntil) GUI.Label(new Rect(8, Screen.height - 44, 1200, 20), _status);
        }
    }
}
