using System;
using System.Collections.Generic;
using System.Linq;
using LivingNPC.Simulation;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace LivingNPC.Content
{
    /// <summary>An npc picture ready for the view: position and size as percent of the map area.</summary>
    public class NpcImage
    {
        public string Path;
        public float X, Y, Width;
        public float? Height;
        public Dictionary<int, NpcImage> Maps = new Dictionary<int, NpcImage>();   // node id -> fully resolved override

        public NpcImage For(int node) => Maps.TryGetValue(node, out var m) ? m : this;
    }

    public class CompiledNpcImages
    {
        public NpcImage Default;
        public Dictionary<int, NpcImage> Activity = new Dictionary<int, NpcImage>();
        public Dictionary<int, NpcImage> Talk = new Dictionary<int, NpcImage>();

        /// <summary>The picture of the npc standing on <paramref name="node"/> and doing <paramref name="activity"/>, or null.</summary>
        public NpcImage OnMap(int activity, int node)
        {
            var img = activity != 0 && Activity.TryGetValue(activity, out var a) ? a : Default;
            return img?.For(node);
        }

        /// <summary>The picture while the npc speaks: the named pose, else "default", else null.</summary>
        public NpcImage Speaking(int pose, int node)
        {
            NpcImage img = null;
            if (pose != 0) Talk.TryGetValue(pose, out img);
            if (img == null) Talk.TryGetValue(Ids.Hash("default"), out img);
            return img?.For(node);
        }
    }

    public partial class CompiledNpc
    {
        public CompiledNpcImages Images = new CompiledNpcImages();
        public NpcDef Def;
        public int Id, NameText, Home;
        public int FirstNameText, LastNameText;     // 0 when not written
        public int Age;
        public byte Sex;                            // 0 not said, 1 male, 2 female, 3 other
        public bool AutoQuests;
        public int Goal;
        public List<RelationValue> Relations = new List<RelationValue>();
        public List<ScheduleBlock> Blocks = new List<ScheduleBlock>();
        /// <summary>The npc's named places as the content defines them (setPlace changes them at runtime).</summary>
        public List<NpcPlace> Places = new List<NpcPlace>();
        public List<StatValue> Stats = new List<StatValue>();
        public List<InventorySlot> Items = new List<InventorySlot>();
    }

    /// <summary>An image shown by a dialogue script (managed side: paths and layout are read by the view).</summary>
    public class CompiledImage
    {
        public string Path;
        public float X, Y, Width;
        public float? Height;
        /// <summary>
        /// Stacking order: the image sits above the element with the next lower number. 0 = map picture, 10 = npc pictures on the
        /// map, 20 = links, 30 = interface; 30 and more is above the interface (the default, as before layers existed).
        /// </summary>
        public int Layer = DefaultLayer;
        public const int DefaultLayer = 40;
    }

    /// <summary>A sound file with its playback settings (managed side: the view loads the file and plays it).</summary>
    public class CompiledSound
    {
        public string Path;
        public float Volume = 1;
        /// <summary>Loops only: seconds to fade in when it starts, and to fade out when it is stopped or replaced.</summary>
        public float Fade;
        public const float DefaultFade = 1.5f;
    }

    /// <summary>Everything the world builder needs: blobs for Burst + per-entity spawn data.</summary>
    public partial class CompiledContent
    {
        public ContentDatabase Db;
        public BlobAssetReference<NavGraphBlob> Nav;
        public BlobAssetReference<RuleBlob> Rules;
        public List<CompiledNpc> Npcs = new List<CompiledNpc>();
        public int StartNode;
        public int QuestCount, EventCount, TemplateCount;
        /// <summary>Goal id -> name as the player reads it (rules.json "goals").</summary>
        public Dictionary<int, string> GoalNames = new Dictionary<int, string>();
        public List<StatRule> StatRules = new List<StatRule>();
        public List<InventorySlot> PlayerItems = new List<InventorySlot>();
        public List<ItemRule> ItemRules = new List<ItemRule>();
        public List<CompiledImage> Images = new List<CompiledImage>();
        /// <summary>Flag keys (Ids.ImageKey) of every image a showImage effect can put on screen.</summary>
        public List<int> OverlayKeys = new List<int>();
        public List<CompiledSound> Sounds = new List<CompiledSound>();
        /// <summary>Flag keys (Ids.MusicKey / Ids.AmbientKey) of every looping sound a startMusic / startAmbient effect can play.</summary>
        public List<int> SoundKeys = new List<int>();
        public bool Valid;
        /// <summary>The compiler that built this content, kept to compile the debug console's effects with the same checks.</summary>
        internal ContentCompiler Compiler;

        /// <summary>
        /// Compiles one effect written while the game runs (the debug console) with the checks of the content. Only the
        /// effects of <see cref="ContentCompiler.ConsoleEffects"/>: the others need texts, pictures, sounds or routine
        /// blocks that must be in the rule blob built at load.
        /// </summary>
        public bool TryCompileConsoleEffect(JObject effect, out FxNode fx, out string error)
        {
            fx = default;
            if (Compiler == null) { error = "no content loaded"; return false; }
            return Compiler.TryCompileConsoleEffect(effect, out fx, out error);
        }

        /// <summary>Releases the blobs. Call when the ECS world built from this content is destroyed/reloaded.</summary>
        public void Dispose()
        {
            if (Nav.IsCreated) Nav.Dispose();
            if (Rules.IsCreated) Rules.Dispose();
        }
    }

    /// <summary>
    /// Validates and converts the authoring model (strings, JSON trees) into the runtime model
    /// (int ids, flat arrays, blobs). Every problem is reported with its source file; broken elements are
    /// skipped (or neutralised: a broken condition becomes "never") so one bad file never breaks the game.
    /// </summary>
    public partial class ContentCompiler
    {
        readonly ContentDatabase _db;
        readonly ValidationReport _rep;

        readonly List<CondNode> _conds = new List<CondNode>();
        readonly List<int> _childRefs = new List<int>();
        readonly List<FxNode> _fx = new List<FxNode>();
        readonly List<ChoiceRule> _choices = new List<ChoiceRule>();
        readonly List<StageRule> _stages = new List<StageRule>();
        readonly List<QuestRule> _quests = new List<QuestRule>();
        readonly List<EventRule> _events = new List<EventRule>();
        readonly List<ScriptStep> _script = new List<ScriptStep>();
        readonly List<CompiledImage> _images = new List<CompiledImage>();
        readonly HashSet<int> _overlayKeys = new HashSet<int>();
        readonly List<CompiledSound> _sounds = new List<CompiledSound>();
        readonly Dictionary<string, int> _soundIndex = new Dictionary<string, int>();      // identical sounds share one entry
        readonly HashSet<int> _soundKeys = new HashSet<int>();                              // loop channels some start effect uses
        readonly List<(string src, int key, string what)> _loopStops = new List<(string, int, string)>();
        readonly Dictionary<int, HashSet<int>> _npcPoses = new Dictionary<int, HashSet<int>>();   // npc id -> hashed talk pose names
        readonly List<(string src, string key)> _hides = new List<(string, string)>();
        string _curQuest;       // id of the quest whose conditions are being compiled (null in events): the default quest of "elapsed"
        string _fxPackDir;      // pack folder of the quest / event whose effects are being compiled
        string _fxSrc;          // where those effects come from, for messages
        readonly List<ActivityFx> _activityFx = new List<ActivityFx>();
        readonly List<ItemRule> _items = new List<ItemRule>();
        readonly HashSet<int> _itemIds = new HashSet<int>();
        readonly List<ScheduleBlock> _routineBlocks = new List<ScheduleBlock>();
        readonly List<RoutineSet> _routineSets = new List<RoutineSet>();
        readonly HashSet<(int npc, int place)> _placeSetters = new HashSet<(int, int)>();        // places some setPlace effect defines
        readonly List<(string npc, string place, string src)> _pendingPlaces = new List<(string, string, string)>();   // "$place" used by a routine effect before any definition

        int _sleepFrom = 20 * 60, _sleepWake = 7 * 60, _sleepFxStart, _sleepFxCount;
        readonly List<int> _beds = new List<int>();

        readonly HashSet<int> _nodeIds = new HashSet<int>();
        readonly HashSet<string> _linkIds = new HashSet<string>();
        readonly HashSet<int> _npcIds = new HashSet<int>();
        readonly HashSet<int> _statIds = new HashSet<int>();
        readonly Dictionary<string, int> _questIndex = new Dictionary<string, int>();
        readonly Dictionary<string, Dictionary<string, int>> _stageIndex = new Dictionary<string, Dictionary<string, int>>();

        const int MaxTreeNodes = 400;

        ContentCompiler(ContentDatabase db) { _db = db; _rep = db.Report; }

        public static CompiledContent Compile(ContentDatabase db)
        {
            TextTable.Reset();
            Ids.Reset();
            var c = new ContentCompiler(db);
            return c.Run();
        }

        CompiledContent Run()
        {
            var result = new CompiledContent { Db = _db, Compiler = this };
            TextTable.Language = _db.Rules.Language ?? "en";

            if (_db.MapList.Count == 0)
            {
                _rep.Error("content", "no maps found: at least one node is required (nodes/<id>/node.json)");
                return result;
            }

            foreach (var m in _db.MapList) _nodeIds.Add(Ids.Hash(m.Id));
            foreach (var n in _db.NpcList) _npcIds.Add(Ids.Hash(n.Id));

            // --- start node
            if (string.IsNullOrEmpty(_db.StartNode) || !_db.Maps.ContainsKey(_db.StartNode))
            {
                var fallback = _db.MapList[0].Id;
                _rep.Error("project.json", $"startNode '{_db.StartNode}' not found; using '{fallback}'");
                _db.StartNode = fallback;
            }
            result.StartNode = Ids.Hash(_db.StartNode);

            CompileStats(result);
            foreach (var kv in _db.Rules.Activities) { Ids.Hash(kv.Id); }
            CompileGoals(result);
            CompileItems(result);      // before npcs, quests and events: they refer to items
            IndexTemplates();          // conditions and effects refer to templates and to shared lines by id

            // quests/stages are indexed before any condition is compiled (conditions refer to them)
            for (int i = 0; i < _db.QuestList.Count; i++)
            {
                var q = _db.QuestList[i];
                _questIndex[q.Id] = i;
                var map = new Dictionary<string, int>();
                for (int s = 0; s < q.Stages.Count; s++)
                    if (!string.IsNullOrEmpty(q.Stages[s].Id)) map[q.Stages[s].Id] = s;
                _stageIndex[q.Id] = map;
            }

            result.Nav = CompileNav();
            CompileNpcs(result);
            CompileHud(result);        // after the npcs, before the events: "button" events name its buttons
            CompileLines();
            CompileQuests();
            CompileEvents();
            CheckHudButtons(result);
            CompileTemplates();
            CompileAutoRules();
            CompileSleep();
            foreach (var (npc, place, src) in _pendingPlaces)
                if (!_placeSetters.Contains((Ids.Hash(npc), Ids.Hash(place))))
                    _rep.Error(src, $"routine effect for npc '{npc}': place '${place}' is not in npc.places and no setPlace effect sets it");
            foreach (var (src, key) in _hides)
                if (!_overlayKeys.Contains(Ids.ImageKey(key))) _rep.Warn(src, $"hideImage '{key}': no showImage effect uses this image key");
            foreach (var (src, key, what) in _loopStops)
                if (!_soundKeys.Contains(key)) _rep.Warn(src, $"{what}: no start effect plays it");
            result.Images = _images;
            result.OverlayKeys = _overlayKeys.ToList();
            result.Sounds = _sounds;
            result.SoundKeys = _soundKeys.ToList();
            result.Rules = BuildRuleBlob(result);
            result.QuestCount = _quests.Count;
            result.EventCount = _events.Count;
            result.TemplateCount = _templates.Count;

            foreach (var col in Ids.Collisions) _rep.Error("ids", col);
            result.Valid = result.Nav.IsCreated && result.Rules.IsCreated;
            return result;
        }

        // ------------------------------------------------------------------ stats & activities

        void CompileStats(CompiledContent result)
        {
            var stats = _db.Rules.Stats;
            if (stats.Count == 0)
            {
                _rep.Warn("rules.json", "no stats defined; adding default 'happiness'");
                stats.Add(new StatDef { Id = "happiness", Min = 0, Max = 100, Baseline = 60, DriftPerHour = 0.05f });
            }
            foreach (var s in stats)
            {
                if (string.IsNullOrEmpty(s.Id)) { _rep.Error("rules.json", "stat without id"); continue; }
                int id = Ids.Hash(s.Id);
                _statIds.Add(id);
                result.StatRules.Add(new StatRule { Id = id, Min = s.Min, Max = s.Max, Baseline = s.Baseline, DriftPerHour = s.DriftPerHour, ConstantPerHour = s.ConstantPerHour });
            }
            foreach (var a in _db.Rules.Activities)
            {
                if (string.IsNullOrEmpty(a.Id)) { _rep.Error("rules.json", "activity without id"); continue; }
                foreach (var kv in a.Effects)
                {
                    int stat = Ids.Hash(kv.Key);
                    if (!_statIds.Contains(stat)) { _rep.Warn("rules.json", $"activity '{a.Id}' refers to unknown stat '{kv.Key}'"); continue; }
                    _activityFx.Add(new ActivityFx { Activity = Ids.Hash(a.Id), Stat = stat, PerHour = kv.Value });
                }
            }
        }

        // ------------------------------------------------------------------ items

        void CompileItems(CompiledContent result)
        {
            foreach (var def in _db.ItemList)
            {
                var rule = new ItemRule { Id = Ids.Hash(def.Id), Value = Math.Max(0, def.Value) };
                switch ((def.Type ?? "misc").ToLowerInvariant())
                {
                    case "misc": rule.Kind = ItemKind.Misc; break;
                    case "consumable": rule.Kind = ItemKind.Consumable; break;
                    case "equipment": rule.Kind = ItemKind.Equipment; break;
                    case "key": rule.Kind = ItemKind.Key; break;
                    case "quest": rule.Kind = ItemKind.Quest; break;
                    default:
                        _rep.Warn(def.Source, $"item '{def.Id}': unknown type '{def.Type}' (misc | consumable | equipment | key | quest); using misc");
                        break;
                }
                if (def.Value < 0) _rep.Warn(def.Source, $"item '{def.Id}': negative value, using 0");
                rule.NameText = TextTable.Register($"item:{def.Id}:name", def.Name ?? new JValue(def.Id));
                rule.DescText = TextTable.Register($"item:{def.Id}:desc", def.Description);
                _itemIds.Add(rule.Id);
                _items.Add(rule);
            }
            result.ItemRules = _items;

            result.PlayerItems = CompileHeld(_db.Rules.PlayerInventory, "rules.json", "player");
        }

        /// <summary>Starting inventory (item id -> count) to slots; unknown items and bad counts are reported and skipped.</summary>
        List<InventorySlot> CompileHeld(Dictionary<string, int> held, string source, string owner)
        {
            var slots = new List<InventorySlot>();
            if (held == null) return slots;
            foreach (var kv in held)
            {
                int item = Ids.Hash(kv.Key);
                if (!_itemIds.Contains(item)) { _rep.Error(source, $"{owner}: inventory refers to unknown item '{kv.Key}'"); continue; }
                if (kv.Value <= 0) { _rep.Warn(source, $"{owner}: inventory count of '{kv.Key}' must be positive; skipped"); continue; }
                slots.Add(new InventorySlot { Item = item, Count = kv.Value });
            }
            return slots;
        }

        // ------------------------------------------------------------------ navigation graph

        BlobAssetReference<NavGraphBlob> CompileNav()
        {
            var maps = _db.MapList.OrderBy(m => Ids.Hash(m.Id)).ToList();
            int n = maps.Count;
            var index = new Dictionary<string, int>();
            for (int i = 0; i < n; i++) index[maps[i].Id] = i;

            var edges = new List<NavEdge>();
            var edgeStart = new int[n + 1];
            var seenLinkIds = new HashSet<string>();
            for (int i = 0; i < n; i++)
            {
                edgeStart[i] = edges.Count;
                var m = maps[i];
                for (int li = 0; li < m.Links.Count; li++)
                {
                    var l = m.Links[li];
                    if (string.IsNullOrEmpty(l.Target) || !index.TryGetValue(l.Target, out var to))
                    {
                        _rep.Error(m.Source, $"map '{m.Id}': link '{l.Id}' targets unknown node '{l.Target}'");
                        continue;
                    }
                    if (string.IsNullOrEmpty(l.Id)) l.Id = $"{m.Id}_to_{l.Target}";
                    if (!seenLinkIds.Add(l.Id)) _rep.Warn(m.Source, $"duplicate link id '{l.Id}'");
                    _linkIds.Add(l.Id);
                    int minutes = Math.Max(1, l.Minutes ?? _db.Rules.DefaultLinkMinutes);
                    int cond = l.Requires != null ? CompileCond(l.Requires, m.Source + "#" + l.Id) : -1;
                    int locked = l.LockedText != null ? TextTable.Register($"link:{l.Id}:locked", l.LockedText) : 0;
                    edges.Add(new NavEdge
                    {
                        To = to, Minutes = minutes, LinkId = Ids.Hash(l.Id), Cond = cond, LockedText = locked,
                        Visible = (byte)(l.Visible ? 1 : 0), Open = (byte)(l.Open ? 1 : 0),
                        VisibleKey = Ids.LinkVisibleKey(l.Id), OpenKey = Ids.LinkOpenKey(l.Id),
                    });
                }
            }
            edgeStart[n] = edges.Count;

            // all-pairs shortest paths (Dijkstra from every node)
            var dist = new int[n * n];
            var next = new int[n * n];
            for (int src = 0; src < n; src++)
            {
                var d = new int[n]; var first = new int[n];
                for (int i = 0; i < n; i++) { d[i] = int.MaxValue; first[i] = -1; }
                d[src] = 0;
                var pq = new SortedSet<(int dist, int node)> { (0, src) };
                while (pq.Count > 0)
                {
                    var (cd, u) = pq.Min; pq.Remove(pq.Min);
                    if (cd > d[u]) continue;
                    for (int e = edgeStart[u]; e < edgeStart[u + 1]; e++)
                    {
                        var ed = edges[e];
                        int nd = cd + ed.Minutes;
                        if (nd < d[ed.To])
                        {
                            d[ed.To] = nd;
                            first[ed.To] = u == src ? ed.To : first[u];
                            pq.Add((nd, ed.To));
                        }
                    }
                }
                for (int i = 0; i < n; i++) { dist[src * n + i] = d[i]; next[src * n + i] = first[i]; }
            }

            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<NavGraphBlob>();
            var ids = builder.Allocate(ref root.NodeIds, n);
            for (int i = 0; i < n; i++) ids[i] = Ids.Hash(maps[i].Id);
            var es = builder.Allocate(ref root.EdgeStart, n + 1);
            for (int i = 0; i <= n; i++) es[i] = edgeStart[i];
            var ea = builder.Allocate(ref root.Edges, edges.Count);
            for (int i = 0; i < edges.Count; i++) ea[i] = edges[i];
            var nh = builder.Allocate(ref root.NextHop, n * n);
            var ds = builder.Allocate(ref root.Dist, n * n);
            for (int i = 0; i < n * n; i++) { nh[i] = next[i]; ds[i] = dist[i]; }
            var blob = builder.CreateBlobAssetReference<NavGraphBlob>(Allocator.Persistent);
            builder.Dispose();

            // reachability sanity check from the start node
            int s0 = index[_db.StartNode];
            var unreachable = maps.Where((m, i) => dist[s0 * n + i] == int.MaxValue).Select(m => m.Id).ToList();
            if (unreachable.Count > 0) _rep.Warn("project.json", "maps unreachable from the start node: " + string.Join(", ", unreachable));
            return blob;
        }

        // ------------------------------------------------------------------ npcs

        void CompileNpcs(CompiledContent result)
        {
            foreach (var def in _db.NpcList)
            {
                var npc = new CompiledNpc { Def = def, Id = Ids.Hash(def.Id) };
                npc.NameText = TextTable.Register($"npc:{def.Id}:name", def.Name ?? DefaultName(def));
                CompileNpcIdentity(def, npc);

                foreach (var kv in def.Places)
                {
                    if (!_db.Maps.ContainsKey(kv.Value)) _rep.Error(def.Source, $"npc '{def.Id}': place '{kv.Key}' refers to unknown node '{kv.Value}'");
                    else npc.Places.Add(new NpcPlace { Key = Ids.Hash(kv.Key), Node = Ids.Hash(kv.Value) });
                }
                if (def.Places.TryGetValue(Ids.HomePlace, out var home) && _db.Maps.ContainsKey(home)) npc.Home = Ids.Hash(home);

                foreach (var token in def.Routine) AddRoutineEntry(def, token, null, false, npc.Blocks);

                if (npc.Home == 0)
                {
                    npc.Home = npc.Blocks.Count > 0 ? npc.Blocks[0].Node : result.StartNode;
                    _rep.Warn(def.Source, $"npc '{def.Id}': no places.home, using fallback node '{Ids.Name(npc.Home)}'");
                }

                foreach (var s in result.StatRules)
                {
                    float v = s.Baseline;
                    foreach (var kv in def.Stats) if (Ids.Hash(kv.Key) == s.Id) v = kv.Value;
                    npc.Stats.Add(new StatValue { Stat = s.Id, Value = v });
                }
                foreach (var kv in def.Stats)
                    if (!_statIds.Contains(Ids.Hash(kv.Key))) _rep.Warn(def.Source, $"npc '{def.Id}': unknown stat '{kv.Key}'");

                npc.Items = CompileHeld(def.Inventory, def.Source, $"npc '{def.Id}'");
                npc.Images = CompileNpcImages(def);
                npc.Profile = CompileProfile(def, npc);
                _npcPoses[npc.Id] = new HashSet<int>(npc.Images.Talk.Keys);

                result.Npcs.Add(npc);
            }
        }

        /// <summary>Resolves an image file inside <pack>/images; null plus a message when it cannot be used.</summary>
        static string ImagePath(string packDir, string file, out string error)
        {
            error = null;
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".gif") error = $"'{file}': GIF images are not supported yet (use png, jpg or webp)";
            else if (!s_ImageExt.Contains(ext)) error = $"'{file}': unsupported image type '{ext}' (use png, jpg or webp)";
            else
            {
                string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(packDir ?? "", "images", file));
                if (System.IO.File.Exists(path)) return path;
                error = $"image not found: {path}";
            }
            return null;
        }

        static readonly HashSet<string> s_SoundExt = new HashSet<string> { ".wav", ".ogg", ".mp3" };

        /// <summary>Resolves a sound file inside <pack>/<folder> (sfx or music); null plus a message when it cannot be used.</summary>
        static string SoundPath(string packDir, string folder, string file, out string error)
        {
            error = null;
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            if (!s_SoundExt.Contains(ext)) { error = $"'{file}': unsupported sound type '{ext}' (use wav, ogg or mp3)"; return null; }
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(packDir ?? "", folder));
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, file));
            if (!path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { error = $"'{file}': the sound must be inside {folder}/"; return null; }
            if (System.IO.File.Exists(path)) return path;
            error = $"sound not found: {path}";
            return null;
        }

        /// <summary>Index of the sound in the shared list (an identical file / volume / fade is stored once).</summary>
        int AddSound(string path, float volume, float fade)
        {
            string key = $"{path}|{volume:R}|{fade:R}";
            if (_soundIndex.TryGetValue(key, out int index)) return index;
            index = _sounds.Count;
            _sounds.Add(new CompiledSound { Path = path, Volume = volume, Fade = fade });
            _soundIndex[key] = index;
            return index;
        }

        static float Volume01(float? v, string what)
        {
            float volume = v ?? 1;
            if (volume < 0 || volume > 1) throw new CondException($"{what}: volume must be between 0 and 1");
            return volume;
        }

        /// <summary>A looping sound effect (startMusic / startAmbient): the flag of its channel holds which sound plays.</summary>
        FxNode CompileLoop(JObject o, string folder, int channel, string what)
        {
            string file = Req(o, "file");
            string path = SoundPath(_fxPackDir, folder, file, out var soundError) ?? throw new CondException(soundError);
            float fade = (float?)o["fade"] ?? CompiledSound.DefaultFade;
            if (fade < 0) throw new CondException($"{what}: fade must be 0 or more");
            int sound = AddSound(path, Volume01((float?)o["volume"], what), fade);
            _soundKeys.Add(channel);
            return new FxNode { Type = FxType.StartLoop, A = channel, B = sound };
        }

        CompiledNpcImages CompileNpcImages(NpcDef def)
        {
            var res = new CompiledNpcImages();
            var src = def.Images;
            if (src == null) return res;
            string at = def.Source;

            NpcImage Make(ImageDef d, string where)
            {
                if (d == null) return null;
                if (string.IsNullOrEmpty(d.File)) { _rep.Error(at, $"npc '{def.Id}': images.{where} needs a 'file'"); return null; }
                var path = ImagePath(def.PackDir, d.File, out var err);
                if (path == null) { _rep.Error(at, $"npc '{def.Id}': images.{where}: {err}"); return null; }
                var img = new NpcImage { Path = path, X = d.X ?? 0, Y = d.Y ?? 0, Width = d.Width ?? 30, Height = d.Height };
                if (d.Nodes == null) return img;
                foreach (var kv in d.Nodes)
                {
                    if (kv.Value == null) continue;
                    if (!_db.Maps.ContainsKey(kv.Key)) { _rep.Error(at, $"npc '{def.Id}': images.{where}.nodes: unknown node '{kv.Key}'"); continue; }
                    var o = kv.Value;
                    string opath = path;
                    if (!string.IsNullOrEmpty(o.File) && o.File != d.File)
                    {
                        opath = ImagePath(def.PackDir, o.File, out var oerr);
                        if (opath == null) { _rep.Error(at, $"npc '{def.Id}': images.{where}.nodes.{kv.Key}: {oerr}"); continue; }
                    }
                    img.Maps[Ids.Hash(kv.Key)] = new NpcImage { Path = opath, X = o.X ?? img.X, Y = o.Y ?? img.Y, Width = o.Width ?? img.Width, Height = o.Height ?? img.Height };
                }
                return img;
            }

            res.Default = Make(src.Default, "default");
            if (src.Activity != null)
                foreach (var kv in src.Activity)
                {
                    if (!_db.Rules.Activities.Any(a => a.Id == kv.Key))
                        _rep.Warn(at, $"npc '{def.Id}': images.activity '{kv.Key}' is not an activity declared in rules.json: it will never be shown");
                    var img = Make(kv.Value, "activity." + kv.Key);
                    if (img != null) res.Activity[Ids.Hash(kv.Key)] = img;
                }
            if (src.Talk != null)
                foreach (var kv in src.Talk)
                {
                    var img = Make(kv.Value, "talk." + kv.Key);
                    if (img != null) res.Talk[Ids.Hash(kv.Key)] = img;
                }
            return res;
        }

        /// <summary>
        /// One routine entry (a shared schedule id, {"schedule": id} or an inline block) compiled into <paramref name="dst"/>.
        /// <paramref name="idOverride"/> names every block (the "id" of an addRoutine effect); otherwise a block keeps its own
        /// "id" and blocks of a shared schedule are named after the schedule.
        /// </summary>
        void AddRoutineEntry(NpcDef def, JToken token, string idOverride, bool fromEffect, List<ScheduleBlock> dst)
        {
            string src = fromEffect ? _fxSrc : def.Source;
            var obj = token as JObject;
            if (token.Type == JTokenType.String || obj?["schedule"] != null)
            {
                string sid = token.Type == JTokenType.String ? token.Value<string>() : (string)obj["schedule"];
                string tag = idOverride ?? (string)obj?["id"] ?? sid;
                if (!_db.Schedules.TryGetValue(sid, out var sched)) { _rep.Error(src, $"npc '{def.Id}': unknown schedule '{sid}'"); return; }
                foreach (var b in sched.Blocks) AddBlock(def, b, "schedule " + sid, tag, dst, fromEffect);
            }
            else if (obj != null)
            {
                try { AddBlock(def, obj.ToObject<BlockDef>(), "inline block", idOverride, dst, fromEffect); }
                catch (Exception e) { _rep.Error(src, $"npc '{def.Id}': invalid inline block: {e.Message}"); }
            }
            else _rep.Error(src, $"npc '{def.Id}': routine entries must be schedule ids or block objects");
        }

        void AddBlock(NpcDef def, BlockDef b, string origin, string tag, List<ScheduleBlock> dst, bool fromEffect)
        {
            string where = $"npc '{def.Id}' / {origin}";
            string src = fromEffect ? _fxSrc : def.Source;
            if (b == null) return;
            if (!GameTime.TryParse(b.From, out var from) || !GameTime.TryParse(b.To, out var to))
            { _rep.Error(src, $"{where}: bad time range '{b.From}'-'{b.To}' (use HH:mm)"); return; }
            if (from == to) { _rep.Warn(src, $"{where}: empty range {b.From}-{b.To}"); return; }

            int mask = ParseDays(b.Days, src, where);
            if (mask == 0) return;

            string at = b.At;
            if (string.IsNullOrEmpty(at)) { _rep.Error(src, $"{where}: block without 'at'"); return; }
            int placeKey = 0, node = 0;
            if (at[0] == '$')
            {
                // The node is looked up in the npc's places every time the schedule is read, so setPlace moves the block.
                // A place the npc does not have yet is fine if some setPlace effect gives it one (checked at the end of the compile).
                string name = at.Substring(1);
                placeKey = Ids.Hash(name);
                if (def.Places.TryGetValue(name, out var resolved)) at = resolved;
                else { _pendingPlaces.Add((def.Id, name, src)); at = null; }
            }
            if (at != null)
            {
                if (!_db.Maps.ContainsKey(at)) { _rep.Error(src, $"{where}: unknown node '{at}'"); return; }
                node = Ids.Hash(at);
            }

            int act = string.IsNullOrEmpty(b.Activity) ? 0 : Ids.Hash(b.Activity);
            if (act != 0 && !_db.Rules.Activities.Any(a => a.Id == b.Activity))
                _rep.Warn(src, $"{where}: activity '{b.Activity}' is not declared in rules.json (it will have no stat effects)");

            int tagId = !string.IsNullOrEmpty(b.Id) ? Ids.Hash(b.Id) : tag != null ? Ids.Hash(tag) : 0;
            var block = new ScheduleBlock { DayMask = (byte)mask, Start = (short)from, End = (short)to, Node = node, Activity = act, Priority = (short)b.Priority, Place = placeKey, Tag = tagId };
            if (to > from) dst.Add(block);
            else
            {   // crosses midnight: split; the tail belongs to the following day
                block.End = 1440;
                dst.Add(block);
                if (to > 0)
                {
                    block.DayMask = (byte)GameTime.RotateMaskNextDay(mask);
                    block.Start = 0; block.End = (short)to;
                    dst.Add(block);
                }
            }
        }

        int ParseDays(JToken t, string src, string where)
        {
            if (t == null || t.Type == JTokenType.Null) return 0x7F;
            var names = t.Type == JTokenType.Array ? t.Select(x => x.ToString()) : new[] { t.ToString() };
            int mask = 0;
            foreach (var n in names)
            {
                if (!GameTime.TryParseDay(n, out var m)) { _rep.Error(src, $"{where}: unknown day '{n}' (mon..sun, weekdays, weekend, all)"); return 0; }
                mask |= m;
            }
            return mask;
        }

        // ------------------------------------------------------------------ quests

        void CompileQuests()
        {
            for (int qi = 0; qi < _db.QuestList.Count; qi++)
            {
                var q = _db.QuestList[qi];
                string src = q.Source;
                _fxPackDir = q.PackDir;
                _curQuest = q.Id;
                var rule = new QuestRule { Id = Ids.Hash(q.Id), AutoStartCond = -1 };
                rule.TitleText = TextTable.Register($"quest:{q.Id}:title", q.Title ?? new JValue(q.Id));
                if (q.Stages.Count == 0) _rep.Error(src, $"quest '{q.Id}' has no stages");
                if (!string.IsNullOrEmpty(q.Giver) && !_db.Npcs.ContainsKey(q.Giver)) _rep.Warn(src, $"quest '{q.Id}': giver '{q.Giver}' is not a character");
                if (q.BusyGiver)
                {
                    if (string.IsNullOrEmpty(q.Giver)) _rep.Error(src, $"quest '{q.Id}': busyGiver needs a 'giver'");
                    else if (_db.Npcs.ContainsKey(q.Giver)) rule.BusyNpc = Ids.Hash(q.Giver);
                }
                if (q.AutoStart != null) rule.AutoStartCond = CompileCond(q.AutoStart, $"{src}#{q.Id}.autoStart");
                (rule.StartFxStart, rule.StartFxCount) = CompileEffects(q.OnStart, $"{src}#{q.Id}.onStart", $"quest:{q.Id}:start");
                (rule.CompleteFxStart, rule.CompleteFxCount) = CompileEffects(q.OnComplete, $"{src}#{q.Id}.onComplete", $"quest:{q.Id}:done");

                rule.StageStart = _stages.Count;
                for (int si = 0; si < q.Stages.Count; si++)
                {
                    var s = q.Stages[si];
                    string ssrc = $"{src}#{q.Id}.stages[{si}]";
                    // text keys use the stage id, not its index: the message log of a save keeps them, and stages get reordered
                    string stageId = string.IsNullOrEmpty(s.Id) ? "stage" + si : s.Id;
                    var sr = new StageRule { Id = Ids.Hash(stageId), CompleteCond = -1, FailCond = -1, Next = si + 1 < q.Stages.Count ? si + 1 : -1 };
                    sr.TextId = TextTable.Register($"quest:{q.Id}:stage:{stageId}", s.Text ?? new JValue(""));
                    if (s.Complete != null) sr.CompleteCond = CompileCond(s.Complete, ssrc + ".complete");
                    if (s.Fail != null) sr.FailCond = CompileCond(s.Fail, ssrc + ".fail");
                    (sr.StartFxStart, sr.StartFxCount) = CompileEffects(s.OnStart, ssrc + ".onStart", $"quest:{q.Id}:stage:{stageId}:start");
                    (sr.FxStart, sr.FxCount) = CompileEffects(s.OnComplete, ssrc + ".onComplete", $"quest:{q.Id}:stage:{stageId}:done");
                    (sr.FailFxStart, sr.FailFxCount) = CompileEffects(s.OnFail, ssrc + ".onFail", $"quest:{q.Id}:stage:{stageId}:fail");
                    if (!string.IsNullOrEmpty(s.Next))
                    {
                        if (s.Next == "end") sr.Next = -1;
                        else if (_stageIndex[q.Id].TryGetValue(s.Next, out var ni)) sr.Next = ni;
                        else _rep.Error(ssrc, $"next stage '{s.Next}' not found");
                    }
                    if (s.Complete == null && s.Fail == null)
                        _rep.Info(ssrc, "stage has no 'complete' condition: it will only advance through an advanceQuest effect");
                    _stages.Add(sr);
                }
                rule.StageCount = q.Stages.Count;
                _quests.Add(rule);
            }
            _curQuest = null;
        }

        // ------------------------------------------------------------------ events

        void CompileEvents()
        {
            var ordered = _db.EventList.Select((e, i) => (e, i)).OrderByDescending(x => x.e.Priority).ThenBy(x => x.i).Select(x => x.e);
            foreach (var e in ordered)
            {
                string src = $"{e.Source}#{e.Id}";
                _fxPackDir = e.PackDir;
                var r = new EventRule { Id = Ids.Hash(e.Id), Cond = -1, Once = (byte)(e.Once ? 1 : 0), CooldownMin = e.CooldownMinutes };
                switch ((e.Trigger ?? "").ToLowerInvariant())
                {
                    case "enternode": case "enter": r.Trigger = TriggerType.EnterNode; break;
                    case "tick": case "time": r.Trigger = TriggerType.Tick; break;
                    case "talk": r.Trigger = TriggerType.Talk; break;
                    case "wake": r.Trigger = TriggerType.Wake; break;
                    case "button": r.Trigger = TriggerType.Button; break;
                    default: _rep.Error(src, $"unknown trigger '{e.Trigger}' (enterNode | tick | talk | wake | button)"); continue;
                }
                if (r.Trigger == TriggerType.Button)
                {
                    if (string.IsNullOrWhiteSpace(e.Button)) { _rep.Error(src, "button events need a 'button' (the id of a hud.json button)"); continue; }
                    r.TriggerButton = Ids.Hash("hud:" + e.Button.Trim());
                    if (!_hudButtons.Contains(r.TriggerButton)) _rep.Warn(src, $"button '{e.Button}' is not a button of the bar (hud.json): the event never runs");
                    _pressedButtons.Add(r.TriggerButton);
                }
                else if (!string.IsNullOrWhiteSpace(e.Button)) _rep.Warn(src, $"'button' is used only with trigger 'button' (this event has trigger '{e.Trigger}')");
                if (!string.IsNullOrEmpty(e.Node))
                {
                    if (!_db.Maps.ContainsKey(e.Node)) { _rep.Error(src, $"unknown node '{e.Node}'"); continue; }
                    r.TriggerNode = Ids.Hash(e.Node);
                }
                if (!string.IsNullOrEmpty(e.Npc))
                {
                    if (!_db.Npcs.ContainsKey(e.Npc)) { _rep.Error(src, $"unknown npc '{e.Npc}'"); continue; }
                    r.TriggerNpc = Ids.Hash(e.Npc);
                }
                if (r.Trigger == TriggerType.Talk && r.TriggerNpc == 0) { _rep.Error(src, "talk events need an 'npc'"); continue; }
                r.NpcForDialog = r.TriggerNpc;
                if (e.When != null) r.Cond = CompileCond(e.When, src + ".when");
                r.TextId = TextTable.Register($"event:{e.Id}:text", e.Text);
                (r.ScriptStart, r.ScriptCount) = CompileProgram(e, r, src, out bool hasDialog);
                r.HasDialog = (byte)(hasDialog ? 1 : 0);
                _events.Add(r);
            }
        }

        // ------------------------------------------------------------------ sleep

        /// <summary>rules.json "sleep" (window, waking time, effects on waking) and the nodes tagged "bed".</summary>
        void CompileSleep()
        {
            var s = _db.Rules.Sleep ?? new SleepDef();
            if (GameTime.TryParse(s.From, out var from)) _sleepFrom = from % GameTime.MinutesPerDay;
            else _rep.Error("rules.json", $"sleep.from '{s.From}' is not a time (HH:mm); using 20:00");
            if (GameTime.TryParse(s.WakeAt, out var wake)) _sleepWake = wake % GameTime.MinutesPerDay;
            else _rep.Error("rules.json", $"sleep.wakeAt '{s.WakeAt}' is not a time (HH:mm); using 07:00");

            (_sleepFxStart, _sleepFxCount) = CompileEffects(s.Effects, "rules.json#sleep.effects", "sleep");

            foreach (var m in _db.MapList)
                if (m.Tags != null && m.Tags.Any(t => string.Equals(t, "bed", StringComparison.OrdinalIgnoreCase)))
                    _beds.Add(Ids.Hash(m.Id));
            _beds.Sort();
            if (_beds.Count == 0) _rep.Info("rules.json", "no node has the tag 'bed' (node.json: \"tags\": [\"bed\"]): the player cannot sleep");
        }

        static readonly HashSet<string> s_ImageExt = new HashSet<string>(Presentation.ImageFile.Extensions);

        static bool IsSet(JToken t) => t != null && t.Type != JTokenType.Null;

        readonly HashSet<int> _shownInEvent = new HashSet<int>();      // image keys some Show step of the current event uses

        /// <summary>
        /// The event's program, in the order it runs: its "effects", then the event text as a line (popup or old-style choices
        /// without a script), then the "script" steps, then the old-style "choices" as a final choice.
        /// <paramref name="hasDialog"/>: the program has a line or a choice, so it opens the dialogue when it reaches one.
        /// </summary>
        (int start, int count) CompileProgram(EventDef e, EventRule r, string src, out bool hasDialog)
        {
            int start = _script.Count;
            _shownInEvent.Clear();
            var (fxStart, fxCount) = CompileEffects(e.Effects, src + ".effects", $"event:{e.Id}");
            if (fxCount > 0) Emit(new ScriptStep { Kind = StepKind.Fx, Start = fxStart, Count = fxCount });

            var steps = Steps(e.Script, src + ".script");
            if (steps.Count == 0 && (e.Popup || e.Choices.Count > 0))
                Emit(new ScriptStep { Kind = StepKind.Say, TextId = r.TextId, WhoNpc = r.NpcForDialog });
            CompileSteps(e, steps, src + ".script", $"event:{e.Id}:script");

            if (e.Choices.Count > 0)
            {
                var options = new JArray();
                foreach (var c in e.Choices)
                {
                    var then = c.Effects as JArray ?? (IsSet(c.Effects) ? new JArray(c.Effects) : new JArray());
                    var o = new JObject { ["then"] = then };
                    if (IsSet(c.Text)) o["text"] = c.Text;
                    if (IsSet(c.When)) o["when"] = c.When;
                    options.Add(o);
                }
                CompileChoice(e, options, src, $"event:{e.Id}");
            }

            hasDialog = false;
            for (int i = start; i < _script.Count; i++)
                if (_script[i].Kind == StepKind.Say || _script[i].Kind == StepKind.Choice) hasDialog = true;
            return (start, _script.Count - start);
        }

        int Emit(ScriptStep step) { _script.Add(step); return _script.Count - 1; }

        void SetTarget(int step, int target) { var s = _script[step]; s.Target = target; _script[step] = s; }

        /// <summary>A list of steps; nothing is an empty list.</summary>
        JArray Steps(JToken t, string at)
        {
            if (!IsSet(t)) return new JArray();
            if (t is JArray a) return a;
            _rep.Error(at, "expected a list of steps");
            return new JArray();
        }

        /// <summary>
        /// The steps of a script, appended to the program in order. A step is one of: an effect ({"do": ...}), a condition
        /// ({"if": cond, "then": [...], "else": [...]}), a choice ({"choices": [{"text", "when", "then": [...]}]}) or a line
        /// with images and sounds (say / who / pose / show / hide / sound, see ScriptStepDef).
        /// </summary>
        void CompileSteps(EventDef e, JArray steps, string at, string key)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                string sat = $"{at}[{i}]", skey = $"{key}:{i}";
                if (!(steps[i] is JObject o)) { _rep.Error(sat, "a step must be an object"); continue; }
                if (o.ContainsKey("do"))
                {
                    _fxSrc = sat;
                    try { var fx = CompileEffect(o, skey); Emit(new ScriptStep { Kind = StepKind.Fx, Start = _fx.Count, Count = 1 }); _fx.Add(fx); }
                    catch (CondException ex) { _rep.Error(sat, ex.Message); }
                }
                else if (o.ContainsKey("if"))
                {
                    int ifStep = Emit(new ScriptStep { Kind = StepKind.If, Cond = CompileCond(o["if"], sat + ".if") });
                    CompileSteps(e, Steps(o["then"], sat + ".then"), sat + ".then", skey + ":then");
                    var other = Steps(o["else"], sat + ".else");
                    if (other.Count > 0)
                    {
                        int jump = Emit(new ScriptStep { Kind = StepKind.Jump });
                        SetTarget(ifStep, _script.Count);
                        CompileSteps(e, other, sat + ".else", skey + ":else");
                        SetTarget(jump, _script.Count);
                    }
                    else SetTarget(ifStep, _script.Count);
                }
                else if (o.ContainsKey("choices"))
                {
                    if (o["choices"] is JArray options && options.Count > 0) CompileChoice(e, options, sat, skey);
                    else _rep.Error(sat, "'choices' needs a list of options");
                }
                else
                {
                    ScriptStepDef d;
                    try { d = o.ToObject<ScriptStepDef>(); }
                    catch (Exception ex) { _rep.Error(sat, ex.Message); continue; }
                    CompileLine(e, d, sat, skey);
                }
            }
        }

        /// <summary>A choice: one ChoiceRule per option, each option's steps after it, and a jump past the others at its end.</summary>
        void CompileChoice(EventDef e, JArray options, string at, string key)
        {
            int first = _choices.Count;
            int step = Emit(new ScriptStep { Kind = StepKind.Choice, Start = first, Count = options.Count });
            for (int k = 0; k < options.Count; k++) _choices.Add(new ChoiceRule { Cond = -1 });
            var jumps = new List<int>();
            for (int k = 0; k < options.Count; k++)
            {
                string oat = $"{at}.choices[{k}]", okey = $"{key}:choice:{k}";
                var opt = options[k] as JObject;
                if (opt == null) { _rep.Error(oat, "an option must be an object"); opt = new JObject(); }
                var cr = new ChoiceRule { Cond = -1, Target = _script.Count, TextId = TextTable.Register(okey, IsSet(opt["text"]) ? opt["text"] : new JValue("...")) };
                if (IsSet(opt["when"])) cr.Cond = CompileCond(opt["when"], oat + ".when");
                _choices[first + k] = cr;
                CompileSteps(e, Steps(opt["then"], oat + ".then"), oat + ".then", okey);
                if (k < options.Count - 1) jumps.Add(Emit(new ScriptStep { Kind = StepKind.Jump }));
            }
            foreach (int j in jumps) SetTarget(j, _script.Count);
            SetTarget(step, _script.Count);         // no option available: go on after the choice
        }

        /// <summary>A line step: its commands run in the order hide, show, sound, say.</summary>
        void CompileLine(EventDef e, ScriptStepDef d, string at, string key)
        {
            bool any = false;
            if (!string.IsNullOrEmpty(d.Hide))
            {
                any = true;
                int hideKey = Ids.Hash(d.Hide);
                if (!_shownInEvent.Contains(hideKey)) _rep.Warn(at, $"hide '{d.Hide}': no earlier step shows an image with that key");
                Emit(new ScriptStep { Kind = StepKind.Hide, Key = hideKey });
            }
            if (!string.IsNullOrEmpty(d.Show))
            {
                any = true;
                if (string.IsNullOrEmpty(d.File)) { _rep.Error(at, $"show '{d.Show}' needs a 'file'"); }
                else
                {
                    string ext = System.IO.Path.GetExtension(d.File).ToLowerInvariant();
                    string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(e.PackDir ?? "", "images", d.File));
                    if (ext == ".gif") _rep.Error(at, $"'{d.File}': GIF images are not supported yet (use png, jpg or webp)");
                    else if (!s_ImageExt.Contains(ext)) _rep.Error(at, $"'{d.File}': unsupported image type '{ext}' (use png, jpg or webp)");
                    else if (!System.IO.File.Exists(path)) _rep.Error(at, $"image not found: {path}");
                    else
                    {
                        int showKey = Ids.Hash(d.Show);
                        int layer = d.Layer ?? CompiledImage.DefaultLayer;
                        if (layer < 0) { _rep.Error(at, $"show '{d.Show}': layer must be 0 or more"); layer = 0; }
                        _shownInEvent.Add(showKey);
                        Emit(new ScriptStep { Kind = StepKind.Show, Key = showKey, Image = _images.Count });
                        _images.Add(new CompiledImage { Path = path, X = d.X, Y = d.Y, Width = d.Width, Height = d.Height, Layer = layer });
                    }
                }
            }
            if (!string.IsNullOrEmpty(d.Sound))
            {
                any = true;
                string path = SoundPath(e.PackDir, "sfx", d.Sound, out var soundError);
                if (path == null) _rep.Error(at, soundError);
                else
                {
                    float volume = d.Volume ?? 1;
                    if (volume < 0 || volume > 1) { _rep.Error(at, $"sound '{d.Sound}': volume must be between 0 and 1"); volume = Math.Max(0, Math.Min(1, volume)); }
                    Emit(new ScriptStep { Kind = StepKind.Sound, Sound = AddSound(path, volume, 0) });
                }
            }
            if (IsSet(d.Say))
            {
                any = true;
                var step = new ScriptStep { Kind = StepKind.Say, TextId = TextTable.Register(key, d.Say) };
                if (IsSet(d.Who))
                {
                    if (d.Who.Type == JTokenType.String && _db.Npcs.ContainsKey((string)d.Who)) step.WhoNpc = Ids.Hash((string)d.Who);
                    else step.WhoText = TextTable.Register(key + ":who", d.Who);
                }
                if (!string.IsNullOrEmpty(d.Pose))
                {
                    step.Pose = Ids.Hash(d.Pose);
                    if (step.WhoNpc != 0 && (!_npcPoses.TryGetValue(step.WhoNpc, out var poses) || !poses.Contains(step.Pose)))
                        _rep.Warn(at, $"pose '{d.Pose}': npc '{d.Who}' has no images.talk.{d.Pose}; its default talk picture is used");
                }
                Emit(step);
            }
            if (!any) _rep.Error(at, "step needs one of: do, if, choices, say, show, hide, sound");
        }

        // ------------------------------------------------------------------ conditions

        int Never() => AddCond(new CondNode { Type = CondType.Not, ChildStart = AddRefs(new[] { AddCond(new CondNode { Type = CondType.Always }) }), ChildCount = 1 }, true);

        int AddCond(CondNode n, bool computeLo = false)
        {
            int idx = _conds.Count;
            if (n.Lo == 0 && !computeLo) n.Lo = idx;
            if (computeLo)
            {
                int lo = idx;
                for (int i = 0; i < n.ChildCount; i++) lo = Math.Min(lo, _conds[_childRefs[n.ChildStart + i]].Lo);
                n.Lo = lo;
            }
            _conds.Add(n);
            return idx;
        }

        int AddRefs(IList<int> refs)
        {
            int start = _childRefs.Count;
            foreach (var r in refs) _childRefs.Add(r);
            return start;
        }

        /// <summary>Compiles a condition JSON tree. Returns the root index. Invalid input becomes a "never" condition.</summary>
        int CompileCond(JToken t, string src)
        {
            int before = _conds.Count;
            int root;
            try { root = CompileCondInner(t, src); }
            catch (CondException e)
            {
                _rep.Error(src, "invalid condition: " + e.Message);
                root = Never();
            }
            if (_conds.Count - before > MaxTreeNodes)
            {
                _rep.Error(src, $"condition tree too large (>{MaxTreeNodes} nodes)");
                root = Never();
            }
            return root;
        }

        class CondException : Exception { public CondException(string m) : base(m) { } }

        int Group(CondType type, IEnumerable<JToken> children, string src)
        {
            var refs = children.Select(c => CompileCondInner(c, src)).ToList();
            return AddCond(new CondNode { Type = type, ChildStart = AddRefs(refs), ChildCount = refs.Count }, true);
        }

        int CompileCondInner(JToken t, string src)
        {
            if (t is JArray arr) return Group(CondType.All, arr, src);
            if (!(t is JObject o)) throw new CondException("expected object or array");

            if (o["all"] is JArray all) return Group(CondType.All, all, src);
            if (o["any"] is JArray any) return Group(CondType.Any, any, src);
            if (o["not"] != null) return Group(CondType.Not, new[] { o["not"] }, src);

            string type = ((string)o["type"] ?? "").ToLowerInvariant();
            switch (type)
            {
                case "always": return AddCond(new CondNode { Type = CondType.Always });
                case "time":
                    {
                        if (!GameTime.TryParse((string)o["from"], out var f) || !GameTime.TryParse((string)o["to"], out var to))
                            throw new CondException("time needs 'from' and 'to' as HH:mm");
                        return AddCond(new CondNode { Type = CondType.Time, A = f, B = to });
                    }
                case "day":
                    {
                        var mask = ParseDays(o["days"], src, "condition");
                        if (mask == 0) throw new CondException("day needs 'days'");
                        return AddCond(new CondNode { Type = CondType.Day, A = mask });
                    }
                case "date":
                    return AddCond(new CondNode { Type = CondType.Date, A = (int?)o["fromDay"] ?? 0, B = (int?)o["toDay"] ?? int.MaxValue });
                case "after":
                    {
                        // from an absolute moment on: "day" counts from 0 like "date" (0 = first day), "time" defaults to midnight
                        if (o["day"] == null) throw new CondException("after needs 'day'");
                        int day = (int?)o["day"] ?? 0;
                        if (day < 0) throw new CondException("after: 'day' cannot be negative");
                        int minute = 0;
                        if (o["time"] != null && !GameTime.TryParse((string)o["time"], out minute)) throw new CondException("after: 'time' must be HH:mm");
                        return AddCond(new CondNode { Type = CondType.After, A = day * GameTime.MinutesPerDay + minute });
                    }
                case "elapsed":
                    {
                        // time passed since the stage / the quest began; true only while that quest is running
                        int mode;
                        switch (((string)o["since"] ?? "stage").ToLowerInvariant())
                        {
                            case "stage": mode = 0; break;
                            case "quest": mode = 1; break;
                            default: throw new CondException("elapsed: 'since' must be 'stage' or 'quest'");
                        }
                        string qid = (string)o["quest"];
                        double total = ((double?)o["days"] ?? 0) * GameTime.MinutesPerDay + ((double?)o["hours"] ?? 0) * 60 + ((double?)o["minutes"] ?? 0);
                        if (total <= 0) throw new CondException("elapsed needs a duration (days, hours and/or minutes)");
                        int minutes = (int)Math.Min(int.MaxValue, Math.Round(total));
                        // in the stages of an automatic quest: since its own stage / start (A = -1)
                        if (string.IsNullOrEmpty(qid) && _tpl != null && _tpl.InStage) return AddCond(new CondNode { Type = CondType.Elapsed, A = -1, B = mode, C = minutes });
                        if (string.IsNullOrEmpty(qid)) qid = _curQuest ?? throw new CondException("elapsed outside a quest needs 'quest'");
                        if (!_questIndex.TryGetValue(qid, out var qi)) throw new CondException($"unknown quest '{qid}'");
                        return AddCond(new CondNode { Type = CondType.Elapsed, A = qi, B = mode, C = minutes });
                    }
                case "playerat":
                    return AddCond(new CondNode { Type = CondType.PlayerAt, A = NodeRef(o, "node") });
                case "npcat":
                    {
                        int npc = NpcRef(o, "npc");
                        if (o["node"] == null) return AddCond(new CondNode { Type = CondType.NpcWithPlayer, A = npc });
                        return AddCond(new CondNode { Type = CondType.NpcAt, A = npc, B = NodeRef(o, "node") });
                    }
                case "npcwithplayer":
                    return AddCond(new CondNode { Type = CondType.NpcWithPlayer, A = NpcRef(o, "npc") });
                case "stat":
                    return AddCond(new CondNode { Type = CondType.Stat, A = TargetRef(o, "target"), B = StatRef(o, "stat"), Op = ParseOp((string)o["op"]), F = (float?)o["value"] ?? 0 });
                case "flag":
                    return AddCond(new CondNode { Type = CondType.Flag, A = Ids.Hash(Req(o, "key")), Op = ParseOp((string)o["op"]), F = (float?)o["value"] ?? 1 });
                case "quest":
                    {
                        string qid = Req(o, "quest");
                        if (!_questIndex.TryGetValue(qid, out var qi)) throw new CondException($"unknown quest '{qid}'");
                        int status = -1;
                        var st = ((string)o["status"] ?? "").ToLowerInvariant();
                        switch (st)
                        {
                            case "": break;
                            case "notstarted": status = 0; break;
                            case "active": status = 1; break;
                            case "completed": case "done": status = 2; break;
                            case "failed": status = 3; break;
                            default: throw new CondException($"unknown quest status '{st}'");
                        }
                        int stage = -1;
                        var sid = (string)o["stage"];
                        if (sid != null && !_stageIndex[qid].TryGetValue(sid, out stage)) throw new CondException($"quest '{qid}' has no stage '{sid}'");
                        return AddCond(new CondNode { Type = CondType.Quest, A = qi, B = status, C = stage });
                    }
                case "activity":
                    return AddCond(new CondNode { Type = CondType.Activity, A = NpcRef(o, "npc"), B = Ids.Hash(Req(o, "activity")) });
                case "hasitem":
                    return AddCond(new CondNode { Type = CondType.HasItem, A = TargetRef(o, "target"), B = ItemRef(o), Op = ParseOp((string)o["op"]), F = (float?)o["count"] ?? 1 });
                case "relation":
                    return AddCond(new CondNode { Type = CondType.Relation, A = TargetRef(o, "from"), B = TargetRef(o, "to"), Op = ParseOp((string)o["op"]), F = (float?)o["value"] ?? 0 });
                case "goal":
                    return AddCond(new CondNode { Type = CondType.Goal, A = NpcRef(o, "npc"), B = string.IsNullOrEmpty((string)o["goal"]) ? 0 : GoalRef((string)o["goal"]) });
                case "age":
                    return AddCond(new CondNode { Type = CondType.Age, A = NpcRef(o, "npc"), Op = ParseOp((string)o["op"]), F = (float?)o["value"] ?? 0 });
                case "sex":
                    {
                        byte sex = ParseSex((string)o["sex"]);
                        if (sex == 0) throw new CondException("sex must be m, f or x");
                        return AddCond(new CondNode { Type = CondType.Sex, A = NpcRef(o, "npc"), B = sex });
                    }
                case "chance":
                    {
                        // the roll of the stage (0-99, drawn when the stage begins) is below the percent: stable for the whole stage
                        if (_tpl == null || !_tpl.InStage) throw new CondException("chance is only for the stages of an automatic quest (template)");
                        float p = (float?)o["percent"] ?? throw new CondException("chance needs 'percent'");
                        return AddCond(new CondNode { Type = CondType.Chance, A = (int)Math.Round(Math.Max(0, Math.Min(100, p))) });
                    }
                case "doing":
                    {
                        int npc = NpcRef(o, "npc");
                        int ti = -1, stage = -1;
                        string tid = (string)o["template"];
                        if (!string.IsNullOrEmpty(tid) && !_templateIndex.TryGetValue(tid, out ti)) throw new CondException($"unknown template '{tid}'");
                        string sid = (string)o["stage"];
                        if (sid != null)
                        {
                            if (ti < 0) throw new CondException("doing: 'stage' needs 'template'");
                            if (!_templateStageIndex[tid].TryGetValue(sid, out stage)) throw new CondException($"template '{tid}' has no stage '{sid}'");
                        }
                        return AddCond(new CondNode { Type = CondType.Doing, A = npc, B = ti + 1, C = stage });
                    }
                case "busy":
                    return AddCond(new CondNode { Type = CondType.Busy, A = NpcRef(o, "npc") });
                case "did":
                    {
                        int npc = NpcRef(o, "npc");
                        int ti = -1;
                        string tid = (string)o["template"];
                        if (!string.IsNullOrEmpty(tid) && !_templateIndex.TryGetValue(tid, out ti)) throw new CondException($"unknown template '{tid}'");
                        int outcome;
                        switch (((string)o["status"] ?? "").ToLowerInvariant())
                        {
                            case "": case "any": outcome = 0; break;
                            case "completed": case "done": outcome = (int)QuestStatus.Completed; break;
                            case "failed": outcome = (int)QuestStatus.Failed; break;
                            case "stopped": outcome = RuleRuntime.AutoStopped; break;
                            default: throw new CondException($"did: unknown status '{o["status"]}' (completed, failed, stopped)");
                        }
                        float days = (float?)o["days"] ?? 0;
                        if (days < 0) throw new CondException("did: 'days' cannot be negative");
                        return AddCond(new CondNode { Type = CondType.Did, A = npc, B = ti + 1, C = outcome, F = days });
                    }
                case "":
                    throw new CondException("missing 'type' (or all/any/not)");
                default:
                    throw new CondException($"unknown condition type '{type}'");
            }
        }

        static string Req(JObject o, string key)
        {
            var v = (string)o[key];
            if (string.IsNullOrEmpty(v)) throw new CondException($"missing '{key}'");
            return v;
        }

        /// <summary>8 hex digits that change when the JSON value changes (FNV-1a; not registered in Ids, it is not an id).</summary>
        static string ContentHash(JToken t)
        {
            uint h = 2166136261;
            foreach (char ch in t.ToString(Newtonsoft.Json.Formatting.None)) { h ^= ch; h *= 16777619; }
            return h.ToString("x8");
        }

        int NodeRef(JObject o, string key)
        {
            var id = Req(o, key);
            if (TryBound(id, SlotKind.Node, out int bound)) return bound;
            if (!_db.Maps.ContainsKey(id)) throw new CondException($"unknown node '{id}'");
            return Ids.Hash(id);
        }

        int NpcRef(JObject o, string key)
        {
            var id = Req(o, key);
            if (TryBound(id, SlotKind.Npc, out int bound)) return bound;
            if (!_db.Npcs.ContainsKey(id)) throw new CondException($"unknown npc '{id}'");
            return Ids.Hash(id);
        }

        int TargetRef(JObject o, string key)
        {
            var id = (string)o[key];
            if (string.IsNullOrEmpty(id) || id == "player") return 0;
            if (TryBound(id, SlotKind.Npc, out int bound)) return bound;
            if (!_db.Npcs.ContainsKey(id)) throw new CondException($"unknown npc '{id}'");
            return Ids.Hash(id);
        }

        int StatRef(JObject o, string key)
        {
            var id = Req(o, key);
            int h = Ids.Hash(id);
            if (!_statIds.Contains(h)) throw new CondException($"unknown stat '{id}' (declare it in rules.json)");
            return h;
        }

        int ItemRef(JObject o)
        {
            var id = Req(o, "item");
            if (TryBound(id, SlotKind.Item, out int bound)) return bound;
            int h = Ids.Hash(id);
            if (!_itemIds.Contains(h)) throw new CondException($"unknown item '{id}'");
            return h;
        }

        static CmpOp ParseOp(string s)
        {
            switch ((s ?? ">=").Trim())
            {
                case "==": case "=": case "eq": return CmpOp.Eq;
                case "!=": case "ne": return CmpOp.Ne;
                case "<": case "lt": return CmpOp.Lt;
                case "<=": case "le": return CmpOp.Le;
                case ">": case "gt": return CmpOp.Gt;
                case ">=": case "ge": return CmpOp.Ge;
                default: throw new CondException($"unknown comparison operator '{s}'");
            }
        }

        // ------------------------------------------------------------------ effects

        (int start, int count) CompileEffects(JToken t, string src, string textKey)
        {
            int start = _fx.Count;
            if (t == null || t.Type == JTokenType.Null) return (start, 0);
            _fxSrc = src;
            var items = t is JArray a ? a.ToList() : new List<JToken> { t };
            var local = new List<FxNode>();
            int n = 0;
            foreach (var item in items)
            {
                try { local.Add(CompileEffect(item, textKey)); }
                catch (CondException e) { _rep.Error(src, $"effect #{n}: {e.Message}"); }
                n++;
            }
            _fx.AddRange(local);
            return (start, local.Count);
        }

        /// <summary>The effects the debug console can run (JSON "do" names): the ones made only of ids and numbers.</summary>
        public static readonly string[] ConsoleEffects =
        {
            "setFlag", "addFlag", "startQuest", "advanceQuest", "completeQuest", "failQuest", "modStat", "setStat",
            "giveItem", "takeItem", "moveNpc", "setPlace", "removeRoutine", "advanceTime", "advanceTimeTo", "teleportPlayer",
            "showLink", "hideLink", "openLink", "closeLink", "modRelation", "setRelation", "setGoal", "setAutoQuests",
            "startAuto", "stopAuto",
        };

        internal bool TryCompileConsoleEffect(JObject o, out FxNode fx, out string error)
        {
            fx = default; error = null;
            string d = (string)o["do"] ?? "";
            if (Array.FindIndex(ConsoleEffects, e => string.Equals(e, d, StringComparison.OrdinalIgnoreCase)) < 0)
            {
                error = $"'{d}' cannot be run from the console";
                return false;
            }
            try { fx = CompileEffect(o, "console"); return true; }
            catch (CondException e) { error = e.Message; }
            // a value of the wrong type: (float?)o["value"] on a word, (int?)o["count"] on "abc"...
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is ArgumentException || e is OverflowException) { error = e.Message; }
            return false;
        }

        FxNode CompileEffect(JToken t, string textKey)
        {
            if (!(t is JObject o)) throw new CondException("expected object");
            string d = ((string)o["do"] ?? "").ToLowerInvariant();
            switch (d)
            {
                case "message":
                    {
                        // keyed by the text itself, not by the effect's position: the message log of a save keeps the key
                        var text = o["text"] ?? throw new CondException("message needs 'text'");
                        return new FxNode { Type = FxType.Message, A = TextTable.Register($"{textKey}:msg:{ContentHash(text)}", BindTexts(text, _fxSrc)) };
                    }
                case "setflag": return new FxNode { Type = FxType.SetFlag, A = Ids.Hash(Req(o, "key")), F = (float?)o["value"] ?? 1 };
                case "addflag": return new FxNode { Type = FxType.AddFlag, A = Ids.Hash(Req(o, "key")), F = (float?)o["value"] ?? 1 };
                case "startquest": return new FxNode { Type = FxType.StartQuest, A = QuestRef(o) };
                case "advancequest": return new FxNode { Type = FxType.AdvanceQuest, A = QuestRef(o) };
                case "completequest": return new FxNode { Type = FxType.CompleteQuest, A = QuestRef(o) };
                case "failquest": return new FxNode { Type = FxType.FailQuest, A = QuestRef(o) };
                case "modstat": return new FxNode { Type = FxType.ModStat, A = TargetRef(o, "target"), B = StatRef(o, "stat"), F = (float?)o["delta"] ?? 0 };
                case "setstat": return new FxNode { Type = FxType.SetStat, A = TargetRef(o, "target"), B = StatRef(o, "stat"), F = (float?)o["value"] ?? 0 };
                case "movenpc": return new FxNode { Type = FxType.MoveNpc, A = NpcRef(o, "npc"), B = NodeRef(o, "node"), F = (float?)o["forMinutes"] ?? 60 };
                case "setplace":
                    {
                        // changes a named place of the npc ("home", "work"...): every routine block written as "$place" follows
                        int npc = NpcRef(o, "npc");
                        string place = Req(o, "place").TrimStart('$');
                        int node = NodeRef(o, "node");
                        _placeSetters.Add((npc, Ids.Hash(place)));
                        return new FxNode { Type = FxType.SetPlace, A = npc, B = Ids.Hash(place), C = node, F = place == Ids.HomePlace ? 1 : 0 };
                    }
                case "addroutine": return CompileRoutineEffect(o, FxType.AddRoutine);
                case "setroutine": return CompileRoutineEffect(o, FxType.SetRoutine);
                case "removeroutine": return new FxNode { Type = FxType.RemoveRoutine, A = NpcRef(o, "npc"), B = Ids.Hash(Req(o, "id")) };
                case "advancetime": return new FxNode { Type = FxType.AdvanceTime, B = Math.Max(0, (int?)o["minutes"] ?? 0) };
                case "advancetimeto":
                    {
                        // skips the time up to the next occurrence of "HH:mm" (nothing if it is exactly that time)
                        if (!GameTime.TryParse((string)o["time"], out var at)) throw new CondException("advanceTimeTo needs 'time' as HH:mm");
                        return new FxNode { Type = FxType.AdvanceTimeTo, B = at % GameTime.MinutesPerDay };
                    }
                case "teleportplayer": return new FxNode { Type = FxType.TeleportPlayer, A = NodeRef(o, "node") };
                case "showlink": return new FxNode { Type = FxType.ShowLink, A = Ids.LinkVisibleKey(LinkRef(o)) };
                case "hidelink": return new FxNode { Type = FxType.HideLink, A = Ids.LinkVisibleKey(LinkRef(o)) };
                case "openlink": return new FxNode { Type = FxType.OpenLink, A = Ids.LinkOpenKey(LinkRef(o)) };
                case "closelink": return new FxNode { Type = FxType.CloseLink, A = Ids.LinkOpenKey(LinkRef(o)) };
                case "showimage":
                    {
                        // an image that stays on screen until a hideImage effect with the same key removes it
                        string key = Req(o, "image");
                        string file = Req(o, "file");
                        string path = ImagePath(_fxPackDir, file, out var imageError) ?? throw new CondException(imageError);
                        int layer = (int?)o["layer"] ?? CompiledImage.DefaultLayer;
                        if (layer < 0) throw new CondException("showImage: layer must be 0 or more");
                        int image = _images.Count;
                        _images.Add(new CompiledImage { Path = path, X = (float?)o["x"] ?? 0, Y = (float?)o["y"] ?? 0, Width = (float?)o["width"] ?? 30, Height = (float?)o["height"], Layer = layer });
                        int flagKey = Ids.ImageKey(key);
                        _overlayKeys.Add(flagKey);
                        return new FxNode { Type = FxType.ShowImage, A = flagKey, B = image };
                    }
                case "hideimage":
                    {
                        string key = Req(o, "image");
                        _hides.Add((_fxSrc, key));
                        return new FxNode { Type = FxType.HideImage, A = Ids.ImageKey(key) };
                    }
                case "playsound":
                    {
                        // a sound effect heard once (<pack>/sfx)
                        string file = Req(o, "file");
                        string path = SoundPath(_fxPackDir, "sfx", file, out var soundError) ?? throw new CondException(soundError);
                        return new FxNode { Type = FxType.PlaySound, A = AddSound(path, Volume01((float?)o["volume"], "playSound"), 0) };
                    }
                case "startmusic": return CompileLoop(o, "music", Ids.MusicKey(), "startMusic");        // replaces the music that plays now
                case "stopmusic":
                    _loopStops.Add((_fxSrc, Ids.MusicKey(), "stopMusic"));
                    return new FxNode { Type = FxType.StopLoop, A = Ids.MusicKey() };
                case "startambient": return CompileLoop(o, "sfx", Ids.AmbientKey(Req(o, "sound")), "startAmbient");     // layers over music and other ambients; the same name replaces its file
                case "stopambient":
                    {
                        string name = Req(o, "sound");
                        _loopStops.Add((_fxSrc, Ids.AmbientKey(name), $"stopAmbient '{name}'"));
                        return new FxNode { Type = FxType.StopLoop, A = Ids.AmbientKey(name) };
                    }
                case "tell": return CompileTell(o);
                case "modrelation": return new FxNode { Type = FxType.ModRelation, A = TargetRef(o, "from"), B = TargetRef(o, "to"), F = (float?)o["delta"] ?? 0 };
                case "setrelation": return new FxNode { Type = FxType.SetRelation, A = TargetRef(o, "from"), B = TargetRef(o, "to"), F = (float?)o["value"] ?? 0 };
                case "setgoal":
                    {
                        string goal = (string)o["goal"];
                        return new FxNode { Type = FxType.SetGoal, A = NpcRef(o, "npc"), B = string.IsNullOrEmpty(goal) || goal == "none" ? 0 : GoalRef(goal) };
                    }
                case "setautoquests": return new FxNode { Type = FxType.SetAutoQuests, A = NpcRef(o, "npc"), B = ((bool?)o["on"] ?? true) ? 1 : 0 };
                case "startauto":
                    {
                        int npc = NpcRef(o, "npc");
                        string tid = Req(o, "template");
                        if (!_templateIndex.TryGetValue(tid, out int ti)) throw new CondException($"unknown template '{tid}'");
                        return new FxNode { Type = FxType.StartAuto, A = npc, B = ti };
                    }
                case "stopauto": return new FxNode { Type = FxType.StopAuto, A = NpcRef(o, "npc") };
                case "giveitem": return new FxNode { Type = FxType.GiveItem, A = TargetRef(o, "target"), B = ItemRef(o), F = PositiveCount(o) };
                case "takeitem": return new FxNode { Type = FxType.TakeItem, A = TargetRef(o, "target"), B = ItemRef(o), F = PositiveCount(o) };
                case "": throw new CondException("missing 'do'");
                default: throw new CondException($"unknown effect '{d}'");
            }
        }

        /// <summary>
        /// addRoutine: { npc, id?, block | blocks | schedule }: adds blocks to the npc's routine; blocks that share an id
        /// with existing ones replace them (adding the same id again edits it).
        /// setRoutine: { npc, routine: [...] }: replaces the whole routine (same entries as npc.routine).
        /// </summary>
        FxNode CompileRoutineEffect(JObject o, FxType type)
        {
            int npcId = NpcRef(o, "npc");
            if (BindRef.Is(npcId)) throw new CondException((type == FxType.SetRoutine ? "setRoutine" : "addRoutine") + " needs the id of a character, not self or a slot");
            var def = _db.Npcs[(string)o["npc"]];
            string id = (string)o["id"];
            var tokens = new List<JToken>();
            if (type == FxType.SetRoutine)
            {
                var r = o["routine"] ?? throw new CondException("setRoutine needs 'routine'");
                if (r is JArray ra) tokens.AddRange(ra); else tokens.Add(r);
            }
            else
            {
                if (o["routine"] is JArray rb) tokens.AddRange(rb);      // same entries as npc.routine (what the quest editor writes)
                else if (o["routine"] != null) tokens.Add(o["routine"]);
                if (o["schedule"] != null) tokens.Add(o["schedule"]);
                if (o["block"] != null) tokens.Add(o["block"]);
                if (o["blocks"] is JArray ba) tokens.AddRange(ba);
                if (tokens.Count == 0) throw new CondException("addRoutine needs 'routine', 'block', 'blocks' or 'schedule'");
            }

            var blocks = new List<ScheduleBlock>();
            int errorsBefore = _rep.ErrorCount;
            foreach (var token in tokens) AddRoutineEntry(def, token, id, true, blocks);
            if (tokens.Count > 0 && (blocks.Count == 0 || _rep.ErrorCount > errorsBefore))
                throw new CondException($"the routine for npc '{def.Id}' has invalid entries (see the messages above)");

            int set = _routineSets.Count;
            _routineSets.Add(new RoutineSet { Start = _routineBlocks.Count, Count = blocks.Count });
            _routineBlocks.AddRange(blocks);
            return new FxNode { Type = type, A = npcId, B = set };
        }

        static float PositiveCount(JObject o)
        {
            int n = (int?)o["count"] ?? 1;
            if (n <= 0) throw new CondException("'count' must be positive");
            return n;
        }

        string LinkRef(JObject o)
        {
            var id = Req(o, "link");
            if (!_linkIds.Contains(id)) throw new CondException($"unknown link '{id}'");
            return id;
        }

        int QuestRef(JObject o)
        {
            var id = Req(o, "quest");
            if (!_questIndex.TryGetValue(id, out var qi)) throw new CondException($"unknown quest '{id}'");
            return qi;
        }

        // ------------------------------------------------------------------ blob

        BlobAssetReference<RuleBlob> BuildRuleBlob(CompiledContent result)
        {
            var b = new BlobBuilder(Allocator.Temp);
            ref var root = ref b.ConstructRoot<RuleBlob>();
            Fill(b, ref root.Conds, _conds);
            Fill(b, ref root.ChildRefs, _childRefs);
            Fill(b, ref root.Fx, _fx);
            Fill(b, ref root.Stats, result.StatRules);
            Fill(b, ref root.ActivityEffects, _activityFx);
            Fill(b, ref root.Quests, _quests);
            Fill(b, ref root.Stages, _stages);
            Fill(b, ref root.Events, _events);
            Fill(b, ref root.Choices, _choices);
            Fill(b, ref root.Script, _script);
            Fill(b, ref root.Items, _items);
            Fill(b, ref root.RoutineBlocks, _routineBlocks);
            Fill(b, ref root.RoutineSets, _routineSets);
            Fill(b, ref root.BedNodes, _beds);
            Fill(b, ref root.Templates, _templates);
            Fill(b, ref root.Slots, _slots);
            Fill(b, ref root.Weights, _weights);
            Fill(b, ref root.ListValues, _listValues);
            Fill(b, ref root.TextGroups, _textGroups);
            Fill(b, ref root.TextVariants, _textVariants);
            Fill(b, ref root.TextLines, _textLines);
            root.AutoCheckMinutes = _autoCheck; root.AutoMaxActive = _autoMax; root.AutoPauseMin = _autoPause;
            root.SleepFrom = _sleepFrom; root.SleepWake = _sleepWake;
            root.SleepFxStart = _sleepFxStart; root.SleepFxCount = _sleepFxCount;
            root.DefaultTalkText = TextTable.Register("sys:talk_default", new JValue("{npc} has nothing to say right now."));
            var blob = b.CreateBlobAssetReference<RuleBlob>(Allocator.Persistent);
            b.Dispose();
            return blob;
        }

        static void Fill<T>(BlobBuilder b, ref BlobArray<T> dst, List<T> src) where T : unmanaged
        {
            var arr = b.Allocate(ref dst, src.Count);
            for (int i = 0; i < src.Count; i++) arr[i] = src[i];
        }
    }
}
