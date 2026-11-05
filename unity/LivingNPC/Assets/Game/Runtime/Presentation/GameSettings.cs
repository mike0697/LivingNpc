using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// The player's preferences: volumes and screen mode. One file for the whole game (not per world, not in the saves),
    /// <c>&lt;persistentDataPath&gt;/settings.json</c>. A missing or broken file gives the defaults.
    /// </summary>
    public static class GameSettings
    {
        class Data
        {
            public float master = 1f, music = 0.8f, ambient = 0.8f, effects = 1f;
            public bool? fullscreen;
            /// <summary>The side panels of the game screen moved by the player, by title ("Presenti", "Diario", "Zaino").</summary>
            public Dictionary<string, PanelPlace> panels = new Dictionary<string, PanelPlace>();
        }

        /// <summary>How far a panel was dragged from its place (fractions of the screen, y upwards) and whether it is folded.</summary>
        class PanelPlace
        {
            public float x, y;
            public bool folded;
        }

        static Data s_Data;
        static bool s_Dirty;

        static Data D => s_Data ??= Read();

        public static string Path => System.IO.Path.Combine(Application.persistentDataPath, "settings.json");

        /// <summary>Volume of everything (0-1).</summary>
        public static float Master { get => D.master; set => Set(ref D.master, value); }
        /// <summary>Volume of the music, the game's and the title's (0-1).</summary>
        public static float Music { get => D.music; set => Set(ref D.music, value); }
        /// <summary>Volume of the ambient layers (0-1).</summary>
        public static float Ambient { get => D.ambient; set => Set(ref D.ambient, value); }
        /// <summary>Volume of the one-shot sound effects (0-1).</summary>
        public static float Effects { get => D.effects; set => Set(ref D.effects, value); }

        public static bool Fullscreen
        {
            get => D.fullscreen ?? Screen.fullScreen;
            set { if (D.fullscreen == value) return; D.fullscreen = value; s_Dirty = true; ApplyScreen(); }
        }

        static void Set(ref float field, float value)
        {
            value = Mathf.Clamp01(value);
            if (Mathf.Approximately(field, value)) return;
            field = value;
            s_Dirty = true;
            AudioListener.volume = D.master;
        }

        /// <summary>Puts the settings into effect (at start-up): the general volume and the screen mode.</summary>
        public static void Apply()
        {
            AudioListener.volume = D.master;
            if (D.fullscreen.HasValue) ApplyScreen();
        }

        static void ApplyScreen()
        {
            bool full = Fullscreen;
            if (full == Screen.fullScreen) return;
            if (full) Screen.SetResolution(Display.main.systemWidth, Display.main.systemHeight, FullScreenMode.FullScreenWindow);
            else
            {
                // a window a bit smaller than the display, so its borders stay on screen
                int h = Mathf.RoundToInt(Display.main.systemHeight * 0.8f), w = Mathf.RoundToInt(h * 16f / 9f);
                Screen.SetResolution(w, h, FullScreenMode.Windowed);
            }
        }

        /// <summary>Back to the default volumes; the screen mode and the places of the panels stay.</summary>
        public static void ResetDefaults()
        {
            var old = D;
            s_Data = new Data { fullscreen = old.fullscreen ?? Screen.fullScreen, panels = old.panels };
            s_Dirty = true;
            Apply();
        }

        // ------------------------------------------------------------------ panels of the game screen

        /// <summary>Offset of a panel from its default place (zero when never moved), and whether it was left folded.</summary>
        public static Vector2 PanelOffset(string key, out bool folded)
        {
            folded = false;
            if (D.panels == null || !D.panels.TryGetValue(key, out var p) || p == null) return Vector2.zero;
            folded = p.folded;
            return new Vector2(p.x, p.y);
        }

        public static void SetPanel(string key, Vector2 offset, bool folded)
        {
            D.panels ??= new Dictionary<string, PanelPlace>();
            D.panels.TryGetValue(key, out var p);
            if (p != null && Mathf.Approximately(p.x, offset.x) && Mathf.Approximately(p.y, offset.y) && p.folded == folded) return;
            D.panels[key] = new PanelPlace { x = offset.x, y = offset.y, folded = folded };
            s_Dirty = true;
        }

        /// <summary>Every panel back to its default place, open.</summary>
        public static void ResetPanels()
        {
            if (D.panels == null || D.panels.Count == 0) return;
            D.panels.Clear();
            s_Dirty = true;
        }

        /// <summary>Writes the file if something changed since the last write.</summary>
        public static void SaveIfChanged()
        {
            if (!s_Dirty || s_Data == null) return;
            s_Dirty = false;
            try { File.WriteAllText(Path, JsonConvert.SerializeObject(s_Data, Formatting.Indented)); }
            catch (Exception e) { Debug.LogWarning($"[LivingNPC] settings not written: {e.Message}"); }
        }

        static Data Read()
        {
            try
            {
                if (File.Exists(Path)) return JsonConvert.DeserializeObject<Data>(File.ReadAllText(Path)) ?? new Data();
            }
            catch (Exception e) { Debug.LogWarning($"[LivingNPC] settings.json not readable, defaults used: {e.Message}"); }
            return new Data();
        }
    }
}
