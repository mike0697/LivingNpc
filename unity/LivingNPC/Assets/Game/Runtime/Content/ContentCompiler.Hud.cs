using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace LivingNPC.Content
{
    /// <summary>A picture of the bar, ready for the view.</summary>
    public class CompiledHudButton
    {
        public string Id;
        /// <summary>Hashed id: what the button sends with CommandType.Button (events with "trigger": "button").</summary>
        public int Hash;
        /// <summary>Absolute path of the picture, null = a button with the label.</summary>
        public string Icon;
        public int LabelText;
        /// <summary>The screen or panel it opens: <see cref="CompiledHud.Screens"/>, null = none (only its events).</summary>
        public string Open;
        /// <summary>Name of the key that presses it as UnityEngine.InputSystem.Key spells it ("C", "Digit1", "F1"), null = none.</summary>
        public string Key;
        /// <summary>Root of the condition that shows it, -1 = always.</summary>
        public int When = -1;
    }

    /// <summary>The bar of pictures (hud.json) and the settings of its screens.</summary>
    public class CompiledHud
    {
        /// <summary>The screens and panels a button can open.</summary>
        public static readonly string[] Screens = { "status", "npcs", "journal", "backpack", "present" };
        /// <summary>What the npcs screen can say besides name, picture, description, stats and hints.</summary>
        public static readonly string[] Details = { "age", "activity", "where", "relation", "goal" };

        /// <summary>0 left, 1 centre, 2 right.</summary>
        public int Align = 1;
        public float Size = 64;
        public List<CompiledHudButton> Buttons = new List<CompiledHudButton>();

        public int StatusTitle, StatusName, StatusText;
        public string StatusImage;
        /// <summary>Stat ids shown by the status screen, in order.</summary>
        public List<int> StatusStats = new List<int>();

        public int NpcsTitle;
        public List<int> NpcStats = new List<int>();
        public HashSet<string> NpcDetails = new HashSet<string>();
    }

    /// <summary>The npc's page in the npcs screen.</summary>
    public class CompiledNpcProfile
    {
        /// <summary>Absolute path of the picture, null = none.</summary>
        public string Image;
        /// <summary>Listed only while this holds; -1 = always.</summary>
        public int When = -1;
        public int DescriptionText;
        public List<(int text, int when)> Hints = new List<(int, int)>();
    }

    public partial class CompiledContent
    {
        /// <summary>The bar of pictures, null when the world has no hud.json.</summary>
        public CompiledHud Hud;
    }

    public partial class CompiledNpc
    {
        public CompiledNpcProfile Profile = new CompiledNpcProfile();
    }

    // hud.json: the bar of pictures at the top of the game screen and its screens; the npcs' "profile" (their page there).
    public partial class ContentCompiler
    {
        readonly HashSet<int> _hudButtons = new HashSet<int>();
        readonly HashSet<int> _pressedButtons = new HashSet<int>();     // buttons some "button" event listens to

        /// <summary>Keys the game already uses (F5 reload, F6 save, F9 load, F12 debug, Esc, Space, Enter) are refused.</summary>
        static readonly Regex s_Key = new Regex(@"^(?:[A-Za-z]|[0-9]|F(?:[1-4]|7|8|10|11))$");

        void CompileHud(CompiledContent result)
        {
            var def = _db.Hud;
            if (def == null) return;
            string src = _db.HudSource ?? "hud.json";
            var hud = new CompiledHud();

            switch ((def.Align ?? "center").Trim().ToLowerInvariant())
            {
                case "left": hud.Align = 0; break;
                case "center": case "centre": hud.Align = 1; break;
                case "right": hud.Align = 2; break;
                default: _rep.Warn(src, $"align '{def.Align}': write left, center or right (using center)"); break;
            }
            hud.Size = def.Size;
            if (hud.Size < 24 || hud.Size > 160) { _rep.Warn(src, $"size {def.Size}: must be between 24 and 160 pixels (using 64)"); hud.Size = 64; }

            foreach (var b in def.Buttons ?? new List<HudButtonDef>())
            {
                if (b == null) continue;
                if (string.IsNullOrWhiteSpace(b.Id)) { _rep.Error(src, "a button of the bar has no 'id'"); continue; }
                string at = $"{src}#{b.Id}";
                var cb = new CompiledHudButton { Id = b.Id, Hash = Ids.Hash("hud:" + b.Id) };
                cb.LabelText = TextTable.Register($"hud:{b.Id}:label", b.Label ?? new JValue(b.Id));
                if (!string.IsNullOrWhiteSpace(b.Icon))
                {
                    cb.Icon = ImagePath(b.PackDir, b.Icon.Trim(), out var err);
                    if (cb.Icon == null) _rep.Warn(at, $"icon: {err} (the button shows its label)");
                }
                if (!string.IsNullOrWhiteSpace(b.Open))
                {
                    string open = b.Open.Trim().ToLowerInvariant();
                    if (CompiledHud.Screens.Contains(open)) cb.Open = open;
                    else if (open == "map") _rep.Warn(at, "open 'map': the map screen does not exist yet; the button only runs its events");
                    else _rep.Error(at, $"open '{b.Open}': unknown screen ({string.Join(" | ", CompiledHud.Screens)})");
                }
                if (!string.IsNullOrWhiteSpace(b.Key))
                {
                    string key = b.Key.Trim();
                    if (!s_Key.IsMatch(key)) _rep.Error(at, $"key '{b.Key}': use a letter, a digit or F1-F4, F7, F8, F10, F11 (the other keys belong to the game)");
                    else if (hud.Buttons.Any(o => string.Equals(o.Key, KeyName(key), StringComparison.Ordinal))) _rep.Warn(at, $"key '{b.Key}' is already used by another button");
                    else cb.Key = KeyName(key);
                }
                if (b.When != null && b.When.Type != JTokenType.Null) cb.When = CompileCond(b.When, at + ".when");
                hud.Buttons.Add(cb);
                _hudButtons.Add(cb.Hash);
            }
            if (hud.Buttons.Count == 0) _rep.Warn(src, "the bar has no buttons");

            var st = def.Status ?? new HudStatusDef();
            hud.StatusTitle = TextTable.Register("hud:status:title", st.Title ?? new JValue("My status"));
            hud.StatusName = TextTable.Register("hud:status:name", st.Name);
            hud.StatusText = TextTable.Register("hud:status:text", st.Text);
            if (!string.IsNullOrWhiteSpace(st.Image))
            {
                hud.StatusImage = ImagePath(st.PackDir, st.Image.Trim(), out var err);
                if (hud.StatusImage == null) _rep.Warn(src, $"status.image: {err}");
            }
            hud.StatusStats = HudStats(st.Stats, src, "status.stats", result);

            var np = def.Npcs ?? new HudNpcsDef();
            hud.NpcsTitle = TextTable.Register("hud:npcs:title", np.Title ?? new JValue("Characters"));
            hud.NpcStats = HudStats(np.Stats, src, "npcs.stats", result);
            foreach (var d in np.Show ?? new List<string> { "age", "relation" })
            {
                string k = (d ?? "").Trim().ToLowerInvariant();
                if (CompiledHud.Details.Contains(k)) hud.NpcDetails.Add(k);
                else _rep.Warn(src, $"npcs.show '{d}': unknown ({string.Join(" | ", CompiledHud.Details)})");
            }
            result.Hud = hud;
        }

        /// <summary>"c" -> "C", "1" -> "Digit1", "f1" -> "F1": the names of UnityEngine.InputSystem.Key.</summary>
        static string KeyName(string key) => char.IsDigit(key[0]) ? "Digit" + key : key.ToUpperInvariant();

        /// <summary>The stats a screen shows: the listed ones that exist, in order; none listed = all of rules.json.</summary>
        List<int> HudStats(List<string> ids, string src, string what, CompiledContent result)
        {
            if (ids == null) return result.StatRules.Select(s => s.Id).ToList();
            var list = new List<int>();
            foreach (var id in ids)
            {
                int h = Ids.Hash(id ?? "");
                if (!_statIds.Contains(h)) { _rep.Warn(src, $"{what}: unknown stat '{id}'"); continue; }
                if (!list.Contains(h)) list.Add(h);
            }
            return list;
        }

        /// <summary>The npc's page: its picture (profile.image, else its talk "default" picture, else its map one), when it is listed, its hints.</summary>
        CompiledNpcProfile CompileProfile(NpcDef def, CompiledNpc npc)
        {
            var res = new CompiledNpcProfile();
            res.DescriptionText = TextTable.Register($"npc:{def.Id}:description", def.Description);
            var p = def.Profile;
            if (p != null && !string.IsNullOrWhiteSpace(p.Image))
            {
                res.Image = ImagePath(def.PackDir, p.Image.Trim(), out var err);
                if (res.Image == null) _rep.Warn(def.Source, $"npc '{def.Id}': profile.image: {err}");
            }
            if (res.Image == null)
            {
                npc.Images.Talk.TryGetValue(Ids.Hash("default"), out var talk);
                res.Image = talk?.Path ?? npc.Images.Default?.Path;
            }
            if (p == null) return res;
            string at = $"{def.Source}#{def.Id}.profile";
            if (p.When != null && p.When.Type != JTokenType.Null) res.When = CompileCond(p.When, at + ".when");
            for (int i = 0; i < (p.Hints?.Count ?? 0); i++)
            {
                var h = p.Hints[i];
                if (h == null || h.Type == JTokenType.Null) continue;
                JToken text = h;
                int when = -1;
                // { "text": …, "when": … } is a hint with a condition; any other object is a text in several languages
                if (h is JObject o && o["text"] != null)
                {
                    text = o["text"];
                    if (o["when"] != null && o["when"].Type != JTokenType.Null) when = CompileCond(o["when"], $"{at}.hints[{i}].when");
                }
                res.Hints.Add((TextTable.Register($"npc:{def.Id}:hint:{i}", text), when));
            }
            return res;
        }

        /// <summary>After the events: a button that opens nothing and that no event listens to does nothing.</summary>
        void CheckHudButtons(CompiledContent result)
        {
            if (result.Hud == null) return;
            foreach (var b in result.Hud.Buttons)
                if (b.Open == null && !_pressedButtons.Contains(b.Hash))
                    _rep.Warn(_db.HudSource ?? "hud.json", $"button '{b.Id}' opens nothing and no event has trigger 'button' with button '{b.Id}': it does nothing");
        }
    }
}
