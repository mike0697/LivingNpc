using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LivingNPC.Content
{
    /// <summary>
    /// Discovers content packs and merges them in priority order. A "pack" is just a folder:
    ///
    ///   pack.json                      (optional manifest)
    ///   project.json                   (world: name, startNode, editor graph positions)
    ///   nodes/ID/node.json + png       (maps, written by the map editor)
    ///   rules.json                     (clock, stats, activities, day parts)
    ///   hud.json                       (the bar of pictures at the top of the game screen)
    ///   actions.json                   (the menu of actions on a character)
    ///   npcs/*.json schedules/*.json quests/*.json events/*.json templates/*.json lines/*.json
    ///
    /// Later packs override earlier ones by id. A file can also carry "$op": "merge" (deep-merge into the existing
    /// definition: ideal for LLM patches) or "$op": "delete".
    /// </summary>
    /// <summary>What the main menu shows for a world (title.json): background image, music, whether to write the title.</summary>
    public class TitleScreen
    {
        public string ImagePath;
        public string MusicPath;
        public float Volume = 1;
        public bool DrawTitle = true;
        public bool MusicOn = true;
        /// <summary>Pictures of every button of the game ("buttons") and of the main menu's ("menuButtons", else the same).</summary>
        public ButtonImages Buttons, MenuButtons;
        public string Source;
        public List<string> Problems = new List<string>();
    }

    /// <summary>A button drawn with pictures: normal, and hover / pressed / selected (absolute paths); border = 9-slice edge in pixels.</summary>
    public class ButtonImages
    {
        public string Normal;
        public string Hover;
        public int Border;
        /// <summary>Colour of the button's words ("textColor": "#4a2516"), null = white.</summary>
        public Color? TextColor;
    }

    public static class ContentLoader
    {
        static readonly JsonLoadSettings s_Load = new JsonLoadSettings
        {
            CommentHandling = CommentHandling.Ignore,
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace,
            LineInfoHandling = LineInfoHandling.Load,
        };

        static readonly JsonMergeSettings s_MergeReplaceArrays = new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace };

        class Raw { public JObject Obj; public string Source; public string Dir; public string PackDir; }

        const string WorldPrefKey = "LivingNPC.World";

        /// <summary>Names a built game looks for beside its .exe: a folder of worlds, or one world.</summary>
        static readonly string[] s_BesideNames = { "World", "Worlds", "world", "worlds" };

        /// <summary>
        /// The folder of the game itself: beside the .exe in a build (dataPath is &lt;game&gt;/&lt;name&gt;_Data), the
        /// repository root in the editor.
        /// </summary>
        public static string GameDir()
        {
#if UNITY_EDITOR
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
#else
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
#endif
        }

        /// <summary>A world is a folder of packs: some subfolder of it holds pack.json, project.json or nodes/.</summary>
        public static bool IsWorld(string dir)
        {
            if (!Directory.Exists(dir)) return false;
            return Directory.GetDirectories(dir).Any(p => File.Exists(Path.Combine(p, "pack.json")) || File.Exists(Path.Combine(p, "project.json")) || Directory.Exists(Path.Combine(p, "nodes")));
        }

        /// <summary>
        /// Where the worlds are: env LIVINGNPC_WORLDS; in the editor &lt;repo&gt;/worlds; in a build a "World" or "Worlds"
        /// folder beside the .exe, then (only if it exists) the old place in the user's AppData. The folder holds one
        /// subfolder per world, or is itself a world (a game shipped with its one world).
        /// </summary>
        public static string WorldsDir()
        {
            var env = Environment.GetEnvironmentVariable("LIVINGNPC_WORLDS");
            if (!string.IsNullOrEmpty(env)) return env;
#if UNITY_EDITOR
            return Path.Combine(GameDir(), "worlds");
#else
            foreach (var name in s_BesideNames)
            {
                var beside = Path.Combine(GameDir(), name);
                if (Directory.Exists(beside)) return beside;
            }
            var old = Path.Combine(Application.persistentDataPath, "Worlds");
            return Directory.Exists(old) && Directory.GetDirectories(old).Length > 0 ? old : Path.Combine(GameDir(), "World");
#endif
        }

        /// <summary>The worlds found (folder names). One name when the worlds folder is itself a world.</summary>
        public static List<string> ListWorlds()
        {
            var dir = WorldsDir();
            if (!Directory.Exists(dir)) return new List<string>();
            if (IsWorld(dir)) return new List<string> { Path.GetFileName(dir.TrimEnd('/', '\\')) };
            return Directory.GetDirectories(dir).Where(IsWorld).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// The title screen of a world, from title.json in one of its packs (the base pack first, beside project.json):
        /// { "music": "file in music/", "musicOn": true, "image": "file in images/", "drawTitle": true, "volume": 1,
        ///   "buttons": { "normal": …, "hover": …, "border": 0 }, "menuButtons": { … } }.
        /// Paths come back absolute; a missing file is left null and said in <see cref="TitleScreen.Problems"/>.
        /// </summary>
        public static TitleScreen LoadTitleScreen(string world)
        {
            var ts = new TitleScreen();
            if (string.IsNullOrEmpty(world)) return ts;
            var dir = WorldDir(world);
            if (!Directory.Exists(dir)) return ts;
            var packs = Directory.GetDirectories(dir).OrderBy(p => Path.GetFileName(p).Equals("base", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var pack in packs)
            {
                var file = Path.Combine(pack, "title.json");
                if (!File.Exists(file)) continue;
                ts.Source = file;
                JObject o;
                try { o = JObject.Parse(File.ReadAllText(file)); }
                catch (Exception e) { ts.Problems.Add($"title.json is not valid JSON: {e.Message}"); return ts; }
                ts.DrawTitle = (bool?)o["drawTitle"] ?? true;
                ts.MusicOn = (bool?)o["musicOn"] ?? true;
                ts.Volume = Mathf.Clamp01((float?)o["volume"] ?? 1f);
                // "musicOn": false keeps the menu silent, the file named in "music" stays for when it is switched on
                if (ts.MusicOn) ts.MusicPath = Resolve(pack, "music", (string)o["music"], new[] { ".wav", ".ogg", ".mp3" }, "the music", ts.Problems);
                ts.ImagePath = Resolve(pack, "images", (string)o["image"], Presentation.ImageFile.Extensions, "the image", ts.Problems);
                ts.Buttons = ReadButtons(pack, o["buttons"], "buttons", ts.Problems);
                ts.MenuButtons = ReadButtons(pack, o["menuButtons"], "menuButtons", ts.Problems) ?? ts.Buttons;
                return ts;
            }
            return ts;
        }

        /// <summary>
        /// { "normal": "file in images/", "hover": "file in images/", "border": 16, "textColor": "#4a2516" }: null when missing or without a usable
        /// normal picture; a missing hover picture uses the normal one.
        /// </summary>
        static ButtonImages ReadButtons(string pack, JToken token, string key, List<string> problems)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JObject o)) { problems.Add($"title.json: \"{key}\" must be {{ \"normal\": …, \"hover\": … }}"); return null; }
            var exts = Presentation.ImageFile.Extensions;
            var normal = Resolve(pack, "images", (string)o["normal"], exts, $"the image {key}.normal", problems);
            if (normal == null)
            {
                if (string.IsNullOrWhiteSpace((string)o["normal"])) problems.Add($"title.json: \"{key}\" has no \"normal\" image");
                return null;
            }
            var hover = Resolve(pack, "images", (string)o["hover"], exts, $"the image {key}.hover", problems) ?? normal;
            Color? text = null;
            var hex = (string)o["textColor"];
            if (!string.IsNullOrWhiteSpace(hex))
            {
                if (ColorUtility.TryParseHtmlString(hex.Trim(), out var c)) text = c;
                else problems.Add($"title.json: \"{key}.textColor\" \"{hex}\" is not a colour (write for example \"#4a2516\")");
            }
            return new ButtonImages { Normal = normal, Hover = hover, Border = Math.Max(0, (int?)o["border"] ?? 0), TextColor = text };
        }

        /// <summary>A file named in title.json: &lt;pack&gt;/&lt;folder&gt;/name, else &lt;pack&gt;/name. Null (and a problem) when it is not there.</summary>
        static string Resolve(string pack, string folder, string name, string[] exts, string what, List<string> problems)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim().Replace('\\', '/');
            if (name.Split('/').Any(p => p == "..")) { problems.Add($"title.json: {what} \"{name}\" cannot point outside the pack"); return null; }
            if (!exts.Contains(Path.GetExtension(name).ToLowerInvariant())) { problems.Add($"title.json: {what} \"{name}\" must be {string.Join(", ", exts)}"); return null; }
            foreach (var candidate in new[] { Path.Combine(pack, folder, name), Path.Combine(pack, name) })
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            problems.Add($"title.json: cannot find {what} \"{name}\" in {folder}/");
            return null;
        }

        /// <summary>The folder of a world found by <see cref="ListWorlds"/>.</summary>
        public static string WorldDir(string world)
        {
            var dir = WorldsDir();
            return IsWorld(dir) ? dir : Path.Combine(dir, world);
        }

        /// <summary>
        /// The title of a world's game: "name" of the project.json of its packs (the base pack first, as the map editor
        /// writes it), else the folder name.
        /// </summary>
        public static string WorldTitle(string world)
        {
            if (string.IsNullOrEmpty(world)) return "";
            var dir = WorldDir(world);
            if (Directory.Exists(dir))
            {
                var packs = Directory.GetDirectories(dir).OrderBy(p => Path.GetFileName(p).Equals("base", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
                foreach (var p in packs)
                {
                    var file = Path.Combine(p, "project.json");
                    if (!File.Exists(file)) continue;
                    try
                    {
                        var name = (string)JObject.Parse(File.ReadAllText(file))["name"];
                        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
                    }
                    catch (Exception) { /* a broken project.json is reported when the world loads */ }
                }
            }
            return world;
        }

        /// <summary>Env LIVINGNPC_WORLD, else the saved choice, else the first world found. Null if there are none.</summary>
        public static string SelectedWorld
        {
            get
            {
                var worlds = ListWorlds();
                var env = Environment.GetEnvironmentVariable("LIVINGNPC_WORLD");
                if (!string.IsNullOrEmpty(env)) return env;
                var saved = PlayerPrefs.GetString(WorldPrefKey, "");
                if (worlds.Contains(saved)) return saved;
                return worlds.Count > 0 ? worlds[0] : null;
            }
            set { PlayerPrefs.SetString(WorldPrefKey, value ?? ""); PlayerPrefs.Save(); }
        }

        /// <summary>Folders scanned for packs, in order. The first one is the shipped (read-only) core; then the chosen world.</summary>
        public static List<(string path, int defaultPriority)> DefaultRoots()
        {
            var roots = new List<(string, int)>
            {
                (Path.Combine(Application.streamingAssetsPath, "Content"), 0),
            };
            var world = SelectedWorld;
            if (!string.IsNullOrEmpty(world)) roots.Add((WorldDir(world), 100));
            roots.Add((Path.Combine(Application.persistentDataPath, "Mods"), 100));
#if UNITY_EDITOR
            // Editor convenience: <project>/Mods is scanned too, and is easy to edit with any tool/LLM.
            roots.Add((Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Mods")), 100));
#endif
            var extra = Environment.GetEnvironmentVariable("LIVINGNPC_MODS");
            if (!string.IsNullOrEmpty(extra)) roots.Add((extra, 200));
            return roots;
        }

        public static ContentDatabase Load(List<(string path, int defaultPriority)> roots = null)
        {
            var db = new ContentDatabase();
            roots ??= DefaultRoots();

            var packs = DiscoverPacks(roots, db.Report);
            var maps = new Dictionary<string, Raw>();
            var mapOrder = new List<string>();
            var npcs = new Dictionary<string, Raw>();
            var schedules = new Dictionary<string, Raw>();
            var quests = new Dictionary<string, Raw>();
            var events = new Dictionary<string, Raw>();
            var items = new Dictionary<string, Raw>();
            var templates = new Dictionary<string, Raw>();
            var lines = new Dictionary<string, Raw>();
            var graph = new Dictionary<string, (float x, float y)>();
            var rules = new JObject();
            JObject hud = null, actions = null;

            foreach (var pack in packs)
            {
                db.Packs.Add(pack);
                db.Report.Info(pack.Id, $"loaded pack '{pack.Name}' v{pack.Version} priority {pack.Priority} ({pack.Dir})");
                LoadWorld(pack, db, graph);
                LoadMaps(pack, db.Report, maps, mapOrder);
                var rulesFile = Path.Combine(pack.Dir, "rules.json");
                if (File.Exists(rulesFile) && TryRead(rulesFile, db.Report, out var rt) && rt is JObject ro) MergeRules(rules, ro);
                var hudFile = Path.Combine(pack.Dir, "hud.json");
                if (File.Exists(hudFile) && TryRead(hudFile, db.Report, out var ht))
                {
                    if (ht is JObject ho) { MergeHud(hud ??= new JObject(), ho, pack.Dir); db.HudSource = hudFile; }
                    else db.Report.Error(hudFile, "expected a JSON object { … }");
                }
                var actionsFile = Path.Combine(pack.Dir, "actions.json");
                if (File.Exists(actionsFile) && TryRead(actionsFile, db.Report, out var at))
                {
                    if (at is JObject ao) { MergeById(actions ??= new JObject(), ao, "actions", pack.Dir); db.ActionsSource = actionsFile; }
                    else db.Report.Error(actionsFile, "expected a JSON object { \"actions\": [ … ] }");
                }
                LoadCategory(pack, "npcs", db.Report, npcs);
                LoadCategory(pack, "schedules", db.Report, schedules);
                LoadCategory(pack, "quests", db.Report, quests);
                LoadCategory(pack, "events", db.Report, events);
                LoadCategory(pack, "items", db.Report, items);
                LoadCategory(pack, "templates", db.Report, templates);
                LoadCategory(pack, "lines", db.Report, lines);
            }

            db.Rules = Deserialize<RulesDef>(rules, "rules.json", db.Report) ?? new RulesDef();
            if (string.IsNullOrEmpty(db.StartNode)) db.StartNode = db.Rules.StartNode;
            if (hud != null) db.Hud = Deserialize<HudDef>(hud, db.HudSource, db.Report);
            if (actions != null) db.Actions = Deserialize<ActionsDef>(actions, db.ActionsSource, db.Report);

            foreach (var id in mapOrder)
            {
                if (!maps.TryGetValue(id, out var raw)) continue; // deleted by a later pack
                var def = Deserialize<MapNodeDef>(raw.Obj, raw.Source, db.Report);
                if (def == null) continue;
                def.Id = id; def.Dir = raw.Dir; def.Source = raw.Source;
                if (graph.TryGetValue(id, out var g)) { def.GraphX = g.x; def.GraphY = g.y; }
                db.MapList.Add(def); db.Maps[id] = def;
            }
            foreach (var kv in npcs) { var d = Deserialize<NpcDef>(kv.Value.Obj, kv.Value.Source, db.Report); if (d != null) { d.Id = kv.Key; d.Source = kv.Value.Source; d.PackDir = kv.Value.PackDir; db.NpcList.Add(d); db.Npcs[d.Id] = d; } }
            foreach (var kv in schedules) { var d = Deserialize<ScheduleDef>(kv.Value.Obj, kv.Value.Source, db.Report); if (d != null) { d.Id = kv.Key; d.Source = kv.Value.Source; db.Schedules[d.Id] = d; } }
            foreach (var kv in quests) { var d = Deserialize<QuestDef>(kv.Value.Obj, kv.Value.Source, db.Report); if (d != null) { d.Id = kv.Key; d.Source = kv.Value.Source; d.PackDir = kv.Value.PackDir; db.QuestList.Add(d); } }
            foreach (var kv in items) { var d = Deserialize<ItemDef>(kv.Value.Obj, kv.Value.Source, db.Report); if (d != null) { d.Id = kv.Key; d.Source = kv.Value.Source; db.ItemList.Add(d); db.Items[d.Id] = d; } }
            foreach (var kv in events) { var d = Deserialize<EventDef>(kv.Value.Obj, kv.Value.Source, db.Report); if (d != null) { d.Id = kv.Key; d.Source = kv.Value.Source; d.PackDir = kv.Value.PackDir; db.EventList.Add(d); } }
            foreach (var kv in templates) { var d = Deserialize<TemplateDef>(kv.Value.Obj, kv.Value.Source, db.Report); if (d != null) { d.Id = kv.Key; d.Source = kv.Value.Source; d.PackDir = kv.Value.PackDir; db.TemplateList.Add(d); } }
            foreach (var kv in lines) { var d = Deserialize<LinesDef>(kv.Value.Obj, kv.Value.Source, db.Report); if (d != null) { d.Id = kv.Key; d.Source = kv.Value.Source; db.LinesList.Add(d); } }
            return db;
        }

        // ---------------------------------------------------------------- packs

        static List<PackManifest> DiscoverPacks(List<(string path, int defaultPriority)> roots, ValidationReport rep)
        {
            var found = new List<PackManifest>();
            foreach (var (root, defPrio) in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(dir);
                    var manifest = new PackManifest { Id = name, Name = name, Priority = defPrio };
                    var mf = Path.Combine(dir, "pack.json");
                    if (File.Exists(mf) && TryRead(mf, rep, out var tok) && tok is JObject o)
                    {
                        try { manifest = o.ToObject<PackManifest>(); }
                        catch (Exception e) { rep.Error(mf, "invalid pack.json: " + e.Message); continue; }
                        if (string.IsNullOrEmpty(manifest.Id)) manifest.Id = name;
                        if (o["priority"] == null) manifest.Priority = defPrio;
                    }
                    manifest.Dir = dir;
                    if (!manifest.Enabled) { rep.Info(manifest.Id, "pack disabled"); continue; }
                    if (found.Any(p => p.Id == manifest.Id)) { rep.Warn(manifest.Id, $"duplicate pack id; '{dir}' ignored"); continue; }
                    found.Add(manifest);
                }
            }
            foreach (var p in found)
                foreach (var dep in p.DependsOn)
                    if (found.All(x => x.Id != dep)) rep.Warn(p.Id, $"depends on missing pack '{dep}'");

            // priority, then id; a pack is moved after its dependencies
            var ordered = found.OrderBy(p => p.Priority).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
            for (int pass = 0; pass < ordered.Count; pass++)
            {
                bool moved = false;
                for (int i = 0; i < ordered.Count && !moved; i++)
                    foreach (var dep in ordered[i].DependsOn)
                    {
                        int j = ordered.FindIndex(x => x.Id == dep);
                        if (j > i) { var p = ordered[i]; ordered.RemoveAt(i); ordered.Insert(j, p); moved = true; break; }
                    }
                if (!moved) break;
            }
            return ordered;
        }

        static void LoadWorld(PackManifest pack, ContentDatabase db, Dictionary<string, (float x, float y)> graph)
        {
            var file = Path.Combine(pack.Dir, "project.json");
            if (!File.Exists(file) || !TryRead(file, db.Report, out var tok) || !(tok is JObject o)) return;
            var name = (string)o["name"]; if (!string.IsNullOrEmpty(name)) db.WorldName = name;
            var start = (string)o["startNode"]; if (!string.IsNullOrEmpty(start)) db.StartNode = start;
            if (o["nodes"] is JArray nodes)
                foreach (var n in nodes.OfType<JObject>())
                {
                    var id = (string)n["id"]; var g = n["graph"];
                    if (id != null && g != null) graph[id] = ((float)(g["x"] ?? 0), (float)(g["y"] ?? 0));
                }
        }

        static void LoadMaps(PackManifest pack, ValidationReport rep, Dictionary<string, Raw> maps, List<string> order)
        {
            var nodesDir = Path.Combine(pack.Dir, "nodes");
            if (!Directory.Exists(nodesDir)) return;
            foreach (var dir in Directory.GetDirectories(nodesDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(dir, "node.json");
                if (!File.Exists(file) || !TryRead(file, rep, out var tok) || !(tok is JObject o)) continue;
                var id = (string)o["id"] ?? Path.GetFileName(dir);
                o["id"] = id;
                var op = ((string)o["$op"] ?? "replace").ToLowerInvariant();
                if (op == "delete") { maps.Remove(id); continue; }
                if (op == "merge" && maps.TryGetValue(id, out var prev))
                {
                    prev.Obj.Merge(o, s_MergeReplaceArrays);
                    prev.Source = file;
                    continue;
                }
                if (!maps.ContainsKey(id)) order.Add(id);
                maps[id] = new Raw { Obj = o, Source = file, Dir = dir };
            }
        }

        static void LoadCategory(PackManifest pack, string folder, ValidationReport rep, Dictionary<string, Raw> target)
        {
            var dir = Path.Combine(pack.Dir, folder);
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryRead(file, rep, out var tok)) continue;
                IEnumerable<JToken> items = tok is JArray arr ? (IEnumerable<JToken>)arr : new[] { tok };
                foreach (var item in items)
                {
                    if (!(item is JObject o)) { rep.Error(file, "expected a JSON object"); continue; }
                    var id = (string)o["id"];
                    if (string.IsNullOrEmpty(id)) { rep.Error(file, "object without 'id'"); continue; }
                    var op = ((string)o["$op"] ?? "replace").ToLowerInvariant();
                    if (op == "delete") target.Remove(id);
                    else if (op == "merge" && target.TryGetValue(id, out var prev))
                    {
                        prev.Obj.Merge(o, s_MergeReplaceArrays);
                        prev.Source = file;
                    }
                    else target[id] = new Raw { Obj = o, Source = file, Dir = dir, PackDir = pack.Dir };
                }
            }
        }

        // ---------------------------------------------------------------- helpers

        static readonly string[] s_MergeByIdArrays = { "stats", "activities", "dayParts", "goals" };

        static void MergeRules(JObject acc, JObject next)
        {
            foreach (var p in next.Properties())
            {
                var existing = acc.GetValue(p.Name, StringComparison.OrdinalIgnoreCase);
                bool byId = s_MergeByIdArrays.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase));
                if (byId && existing is JArray ea && p.Value is JArray na)
                {
                    foreach (var item in na.OfType<JObject>())
                    {
                        var id = (string)item["id"];
                        var idx = ea.ToList().FindIndex(x => (string)x["id"] == id);
                        if (idx >= 0) ea[idx] = item.DeepClone(); else ea.Add(item.DeepClone());
                    }
                }
                else acc[p.Name] = p.Value.DeepClone();
            }
        }

        /// <summary>
        /// hud.json of a later pack over the earlier ones: "buttons" by id (an entry with "$op": "delete" removes it), "status"
        /// and "npcs" deep-merged, the rest replaced. Pictures belong to the pack that names them: every button and the status
        /// screen remember it in "__packDir".
        /// </summary>
        static void MergeHud(JObject acc, JObject next, string packDir)
        {
            foreach (var p in next.Properties())
            {
                string name = p.Name.ToLowerInvariant();
                var existing = acc.GetValue(p.Name, StringComparison.OrdinalIgnoreCase);
                if (name == "buttons" && p.Value is JArray) MergeById(acc, new JObject { [p.Name] = p.Value }, p.Name, packDir);
                else if ((name == "status" || name == "npcs") && p.Value is JObject no)
                {
                    var copy = (JObject)no.DeepClone();
                    if (name == "status" && copy["image"] != null) copy["__packDir"] = packDir;
                    if (existing is JObject eo) eo.Merge(copy, s_MergeReplaceArrays); else acc[p.Name] = copy;
                }
                else acc[p.Name] = p.Value.DeepClone();
            }
        }

        /// <summary>
        /// The list <paramref name="key"/> of a later pack over the earlier one, by id: an entry replaces the one with its id,
        /// "$op": "delete" removes it, new ones go at the end. Each entry remembers its pack ("__packDir") for its pictures.
        /// The other properties of <paramref name="next"/> replace the earlier ones.
        /// </summary>
        static void MergeById(JObject acc, JObject next, string key, string packDir)
        {
            foreach (var p in next.Properties())
            {
                if (!string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) || !(p.Value is JArray na)) { acc[p.Name] = p.Value.DeepClone(); continue; }
                var existing = acc.GetValue(p.Name, StringComparison.OrdinalIgnoreCase);
                var list = existing as JArray ?? new JArray();
                foreach (var item in na.OfType<JObject>())
                {
                    var id = (string)item["id"];
                    int idx = id == null ? -1 : list.ToList().FindIndex(x => (string)x["id"] == id);
                    if (string.Equals((string)item["$op"], "delete", StringComparison.OrdinalIgnoreCase)) { if (idx >= 0) list.RemoveAt(idx); continue; }
                    var copy = (JObject)item.DeepClone();
                    copy["__packDir"] = packDir;
                    if (idx >= 0) list[idx] = copy; else list.Add(copy);
                }
                if (existing != null) acc.Remove(((JProperty)existing.Parent).Name);
                acc[p.Name] = list;
            }
        }

        static bool TryRead(string file, ValidationReport rep, out JToken token)
        {
            token = null;
            try
            {
                var text = File.ReadAllText(file).TrimStart('﻿');
                token = JToken.Parse(text, s_Load);
                return true;
            }
            catch (Exception e)
            {
                rep.Error(file, "cannot parse JSON: " + e.Message);
                return false;
            }
        }

        static T Deserialize<T>(JObject o, string source, ValidationReport rep) where T : class
        {
            try { return o.ToObject<T>(); }
            catch (Exception e)
            {
                rep.Error(source, $"invalid {typeof(T).Name}: {e.Message}");
                return null;
            }
        }
    }
}
