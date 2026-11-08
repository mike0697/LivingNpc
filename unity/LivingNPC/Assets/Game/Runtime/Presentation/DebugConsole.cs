using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LivingNPC.Content;
using LivingNPC.Simulation;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// The debug console: one command per line, written as in the quests of the Studio
    /// (<c>modStat(energy, 20, target=elena)</c>), compiled with the same checks as the content and run through
    /// <see cref="RuleRuntime"/>, so rules, quests and events react as they would to a quest's effect.
    /// Beyond the effects: <c>target=all</c> / <c>target=tag:guard</c> for every npc or the npcs with a tag, and the
    /// commands <c>where</c>, <c>run</c>, <c>help</c>, <c>clear</c>. No UI here: the panel draws it, tests drive it.
    /// </summary>
    public sealed class DebugConsole
    {
        public enum Kind : byte { Input, Ok, Error, Info }
        public readonly struct Entry
        {
            public readonly string Text; public readonly Kind Kind;
            public Entry(string text, Kind kind) { Text = text; Kind = kind; }
        }

        const int MaxLog = 200, MaxHistory = 50;
        readonly List<Entry> _log = new List<Entry>();
        readonly List<string> _history = new List<string>();
        public IReadOnlyList<Entry> Log => _log;
        public IReadOnlyList<string> History => _history;
        /// <summary>Bumped on every new line of the log, so the panel knows when to scroll down.</summary>
        public int LogVersion { get; private set; }

        EntityManager _em;
        Entity _world, _player;
        CompiledContent _compiled;

        /// <summary>
        /// Arguments written without their name, in order, and the ones only accepted by name, for every effect of
        /// <see cref="ContentCompiler.ConsoleEffects"/>. A copy of the "pos" parameters of studio/lib/specs.js, kept equal to
        /// it by studio/tests/console.mjs (keep one entry per line in this form).
        /// </summary>
        static readonly Dictionary<string, (string[] positional, string[] named)> s_Params = new Dictionary<string, (string[], string[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["setFlag"] = (new[] { "key", "value" }, new string[] { }),
            ["addFlag"] = (new[] { "key", "value" }, new string[] { }),
            ["startQuest"] = (new[] { "quest" }, new string[] { }),
            ["advanceQuest"] = (new[] { "quest" }, new string[] { }),
            ["completeQuest"] = (new[] { "quest" }, new string[] { }),
            ["failQuest"] = (new[] { "quest" }, new string[] { }),
            ["modStat"] = (new[] { "stat", "delta" }, new[] { "target" }),
            ["setStat"] = (new[] { "stat", "value" }, new[] { "target" }),
            ["giveItem"] = (new[] { "item", "count" }, new[] { "target" }),
            ["takeItem"] = (new[] { "item", "count" }, new[] { "target" }),
            ["moveNpc"] = (new[] { "npc", "node" }, new[] { "forMinutes" }),
            ["setPlace"] = (new[] { "npc", "place", "node" }, new string[] { }),
            ["removeRoutine"] = (new[] { "npc", "id" }, new string[] { }),
            ["advanceTime"] = (new[] { "minutes" }, new string[] { }),
            ["advanceTimeTo"] = (new[] { "time" }, new string[] { }),
            ["teleportPlayer"] = (new[] { "node" }, new string[] { }),
            ["showLink"] = (new[] { "link" }, new string[] { }),
            ["hideLink"] = (new[] { "link" }, new string[] { }),
            ["openLink"] = (new[] { "link" }, new string[] { }),
            ["closeLink"] = (new[] { "link" }, new string[] { }),
        };

        /// <summary>The console's own commands, for help.</summary>
        static readonly (string usage, string what)[] s_Own =
        {
            ("where(npc)", "where an npc is and what it does; where() for the player"),
            ("run(event)", "runs an event now, as if its trigger had fired (its condition is not checked)"),
            ("resetQuest(quest)", "puts a quest back to not started, to play it again (flags and items it gave stay)"),
            ("resetEvent(event)", "forgets that an event ran: a once event can run again, a cooldown ends"),
            ("help / help(command)", "this list / the arguments of a command"),
            ("clear", "empties this log"),
        };

        sealed class ConsoleError : Exception { public ConsoleError(string m) : base(m) { } }

        public void Bind(EntityManager em, Entity world, Entity player, CompiledContent compiled)
        {
            _em = em; _world = world; _player = player; _compiled = compiled;
        }

        bool Alive => _compiled != null && _compiled.Rules.IsCreated && _em.Exists(_world) && _em.Exists(_player);

        // ------------------------------------------------------------------ running a line

        /// <summary>Runs one line; the outcome goes to the log. True when the command did what it says.</summary>
        public bool Execute(string line)
        {
            line = (line ?? "").Trim();
            if (line.Length == 0) return false;
            if (_history.Count == 0 || _history[_history.Count - 1] != line)
            {
                _history.Add(line);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
            }
            Write("> " + line, Kind.Input);
            if (!Alive) { Write("no game in progress", Kind.Error); return false; }
            try
            {
                Parse(line, out string name, out var args);
                switch (name.ToLowerInvariant())
                {
                    case "help": Help(args); return true;
                    case "clear": _log.Clear(); LogVersion++; return true;
                    case "where": Where(args); return true;
                    case "run": RunEvent(args); return true;
                    case "resetquest": ResetQuest(args); return true;
                    case "resetevent": ResetEvent(args); return true;
                    default: RunEffect(name, args); return true;
                }
            }
            catch (ConsoleError e) { Write(e.Message, Kind.Error); }
            catch (Exception e)
            {
                // never let a typo take the game down
                Write("error: " + e.Message, Kind.Error);
                UnityEngine.Debug.LogException(e);
            }
            return false;
        }

        void Write(string text, Kind kind)
        {
            _log.Add(new Entry(text, kind));
            if (_log.Count > MaxLog) _log.RemoveRange(0, _log.Count - MaxLog);
            LogVersion++;
        }

        RuleRuntime Rt => RuleRuntime.Create(_em, _world, _player);

        void MarkUsed()
        {
            var ws = _em.GetComponentData<WorldState>(_world);
            if (ws.DebugUsed != 0) return;
            ws.DebugUsed = 1;
            _em.SetComponentData(_world, ws);
        }

        // ------------------------------------------------------------------ effects

        void RunEffect(string name, List<(string key, JToken value)> args)
        {
            int k = Array.FindIndex(ContentCompiler.ConsoleEffects, e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));
            if (k < 0)
            {
                if (IsKnownElsewhere(name)) throw new ConsoleError($"'{name}' works in the quests but not here: it needs texts, pictures, sounds or routine blocks loaded with the content");
                throw new ConsoleError($"unknown command '{name}' (help lists them)");
            }
            string canonical = ContentCompiler.ConsoleEffects[k];
            var (positional, named) = s_Params[canonical];

            var o = new JObject { ["do"] = canonical };
            int next = 0;
            foreach (var (key, value) in args)
            {
                if (key == null)
                {
                    if (next >= positional.Length)
                        throw new ConsoleError($"{canonical}: too many values without a name ({Usage(canonical)})");
                    o[positional[next++]] = value;
                }
                else
                {
                    if (Array.IndexOf(positional, key) < 0 && Array.IndexOf(named, key) < 0)
                        throw new ConsoleError($"{canonical}: no argument '{key}' ({Usage(canonical)})");
                    o[key] = value;
                }
            }

            // one effect per target: target=all / target=tag:x stand for every matching npc
            var effects = new List<JObject>();
            string target = Array.IndexOf(named, "target") >= 0 ? (string)o["target"] : null;
            if (target != null && (target == "all" || target.StartsWith("tag:", StringComparison.Ordinal)))
            {
                string tag = target == "all" ? null : target.Substring(4);
                foreach (var n in _compiled.Db.NpcList)
                {
                    if (tag != null && (n.Tags == null || !n.Tags.Contains(tag))) continue;
                    var copy = (JObject)o.DeepClone();
                    copy["target"] = n.Id;
                    effects.Add(copy);
                }
                if (effects.Count == 0) throw new ConsoleError(tag == null ? "there are no npcs" : $"no npc has the tag '{tag}'");
            }
            else effects.Add(o);

            // everything is compiled before anything runs: a mistake changes nothing
            var fx = new List<FxNode>();
            foreach (var e in effects)
            {
                if (!_compiled.TryCompileConsoleEffect(e, out var node, out var error)) throw new ConsoleError($"{canonical}: {error}");
                fx.Add(node);
            }
            var rt = Rt;
            foreach (var f in fx) rt.ApplyOne(f);
            MarkUsed();
            for (int i = 0; i < fx.Count; i++) Write(Describe(fx[i], effects[i]), Kind.Ok);
        }

        /// <summary>What the effect left behind, read back from the world.</summary>
        string Describe(in FxNode fx, JObject o)
        {
            var rt = Rt;
            switch (fx.Type)
            {
                case FxType.ModStat:
                case FxType.SetStat:
                    return $"{Who(fx.A)}: {Ids.Name(fx.B)} = {rt.GetStat(fx.A, fx.B):0.##}";
                case FxType.GiveItem:
                case FxType.TakeItem:
                    return $"{Who(fx.A)}: {Ids.Name(fx.B)} × {rt.GetItemCount(fx.A, fx.B)}";
                case FxType.SetFlag:
                case FxType.AddFlag:
                    return $"{Ids.Name(fx.A)} = {rt.GetFlag(fx.A):0.##}";
                case FxType.StartQuest:
                case FxType.AdvanceQuest:
                case FxType.CompleteQuest:
                case FxType.FailQuest:
                    return QuestText(fx.A);
                case FxType.MoveNpc:
                    return $"{Who(fx.A)} goes to {Place(fx.B)} and stays there {fx.F:0} min";
                case FxType.AdvanceTime:
                case FxType.AdvanceTimeTo:
                    {
                        var clock = _em.GetComponentData<ClockState>(_world);
                        return clock.Pending > 0 ? $"time passes: {clock.Pending} min, the world lives them now" : "it is already that time";
                    }
                case FxType.TeleportPlayer: return $"player at {Place(fx.A)}";
                case FxType.SetPlace: return $"{Who(fx.A)}: {Ids.Name(fx.B)} → {Place(fx.C)}";
                default: return $"{(string)o["do"]}: done";
            }
        }

        string QuestText(int qi)
        {
            ref var rules = ref _compiled.Rules.Value;
            var st = _em.GetBuffer<QuestState>(_world, true)[qi];
            var q = rules.Quests[qi];
            string id = Ids.Name(q.Id);
            switch ((QuestStatus)st.Status)
            {
                case QuestStatus.Active: return $"{id}: active, stage {Ids.Name(rules.Stages[q.StageStart + st.Stage].Id)}";
                case QuestStatus.Completed: return $"{id}: completed";
                case QuestStatus.Failed: return $"{id}: failed";
                default: return $"{id}: not started";
            }
        }

        static bool IsKnownElsewhere(string name)
        {
            foreach (var n in new[] { "message", "showImage", "hideImage", "playSound", "startMusic", "stopMusic", "startAmbient", "stopAmbient", "addRoutine", "setRoutine" })
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ------------------------------------------------------------------ own commands

        void Help(List<(string key, JToken value)> args)
        {
            if (args.Count > 0)
            {
                string name = args[0].value.Type == JTokenType.String ? (string)args[0].value : args[0].value.ToString();
                foreach (var (usage, what) in s_Own)
                    if (usage.StartsWith(name + "(", StringComparison.OrdinalIgnoreCase) || string.Equals(usage, name, StringComparison.OrdinalIgnoreCase))
                    { Write($"{usage}: {what}", Kind.Info); return; }
                int k = Array.FindIndex(ContentCompiler.ConsoleEffects, e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));
                if (k < 0) throw new ConsoleError($"unknown command '{name}'");
                string canonical = ContentCompiler.ConsoleEffects[k];
                Write(Usage(canonical), Kind.Info);
                if (Array.IndexOf(s_Params[canonical].named, "target") >= 0)
                    Write("target: an npc id, empty = the player, all = every npc, tag:x = the npcs with tag x", Kind.Info);
                return;
            }
            Write("Commands, written as in the quests (ids without quotes, text in \"quotes\"):", Kind.Info);
            var sb = new StringBuilder();
            foreach (var e in ContentCompiler.ConsoleEffects) { if (sb.Length > 0) sb.Append(", "); sb.Append(e); }
            Write(sb.ToString(), Kind.Info);
            foreach (var (usage, what) in s_Own) Write($"{usage}: {what}", Kind.Info);
            Write("help(modStat) shows the arguments of one command. Changes made here are marked in the saves.", Kind.Info);
        }

        static string Usage(string canonical)
        {
            var (positional, named) = s_Params[canonical];
            var parts = new List<string>(positional);
            foreach (var n in named) parts.Add(n + "=");
            return $"{canonical}({string.Join(", ", parts)})";
        }

        void Where(List<(string key, JToken value)> args)
        {
            if (args.Count == 0)
            {
                var p = _em.GetComponentData<PlayerState>(_player);
                Write(p.Node != 0 ? $"player: {Place(p.Node)}" : $"player: travelling to {Place(p.PendingNode)}", Kind.Info);
                return;
            }
            string id = Id(args[0].value);
            if (!_compiled.Db.Npcs.ContainsKey(id)) throw new ConsoleError($"unknown npc '{id}'");
            int npc = Ids.Hash(id);
            Entity e = Entity.Null;
            foreach (var r in _em.GetBuffer<EntityRef>(_world, true)) if (r.Id == npc) { e = r.Entity; break; }
            if (e == Entity.Null) throw new ConsoleError($"npc '{id}' is not in the world");
            var loc = _em.GetComponentData<NpcLocation>(e);
            var clock = _em.GetComponentData<ClockState>(_world);
            string where = loc.Node != 0 ? Place(loc.Node) : $"travelling to {Place(loc.NextNode)}, arrives {GameTime.Clock(GameTime.MinuteOfDay(loc.ArriveAt))}";
            string doing = loc.Activity != 0 ? $", {Ids.Name(loc.Activity)}" : "";
            string forced = loc.OverrideUntil > clock.Now ? $", sent to {Place(loc.OverrideNode)} until {GameTime.Clock(GameTime.MinuteOfDay(loc.OverrideUntil))}" : "";
            Write($"{id}: {where}{doing}{forced}", Kind.Info);
        }

        void RunEvent(List<(string key, JToken value)> args)
        {
            if (args.Count != 1) throw new ConsoleError("run(event): one event id");
            string id = Id(args[0].value);
            ref var rules = ref _compiled.Rules.Value;
            int ei = -1;
            for (int i = 0; i < rules.Events.Length; i++) if (Ids.Name(rules.Events[i].Id) == id) { ei = i; break; }
            if (ei < 0) throw new ConsoleError($"unknown event '{id}'");

            var ws = _em.GetComponentData<WorldState>(_world);
            var clock = _em.GetComponentData<ClockState>(_world);
            if (ws.DialogOpen != 0) throw new ConsoleError("a dialogue is open: finish it first");
            if (clock.Pending > 0) throw new ConsoleError("time is passing: wait until it stops");

            // as the rule system does when the trigger fires: count it (once / cooldown), log its text, run its program
            var ev = rules.Events[ei];
            var states = _em.GetBuffer<EventState>(_world);
            var es = states[ei];
            states[ei] = new EventState { Count = es.Count + 1, LastAt = clock.Now };
            var rt = Rt;
            if (ev.TextId != 0) rt.PushMessage(ev.TextId, MsgKind.Event, ev.TriggerNpc);
            rt.RunEvent(ei);
            rt.Touch();
            MarkUsed();
            bool dialog = _em.GetComponentData<WorldState>(_world).DialogOpen != 0;
            Write(dialog ? $"{id} ran: close the panel to see its dialogue" : $"{id} ran", Kind.Ok);
        }

        void ResetQuest(List<(string key, JToken value)> args)
        {
            if (args.Count != 1) throw new ConsoleError("resetQuest(quest): one quest id");
            string id = Id(args[0].value);
            ref var rules = ref _compiled.Rules.Value;
            int qi = -1;
            for (int i = 0; i < rules.Quests.Length; i++) if (Ids.Name(rules.Quests[i].Id) == id) { qi = i; break; }
            if (qi < 0) throw new ConsoleError($"unknown quest '{id}'");

            // only the quest's own state: what its effects did to the world (flags, items, routines) is not undone
            var states = _em.GetBuffer<QuestState>(_world);
            states[qi] = new QuestState { Status = (byte)QuestStatus.NotStarted };
            Rt.Touch();
            MarkUsed();
            string note = rules.Quests[qi].AutoStartCond >= 0 ? " (it starts by itself again if its autoStart condition holds)" : "";
            Write($"{id}: not started{note}", Kind.Ok);
            Write("the events that offer it may be done (once) or waiting: resetEvent(event) frees them", Kind.Info);
        }

        void ResetEvent(List<(string key, JToken value)> args)
        {
            if (args.Count != 1) throw new ConsoleError("resetEvent(event): one event id");
            string id = Id(args[0].value);
            ref var rules = ref _compiled.Rules.Value;
            int ei = -1;
            for (int i = 0; i < rules.Events.Length; i++) if (Ids.Name(rules.Events[i].Id) == id) { ei = i; break; }
            if (ei < 0) throw new ConsoleError($"unknown event '{id}'");
            var states = _em.GetBuffer<EventState>(_world);
            states[ei] = default;
            Rt.Touch();
            MarkUsed();
            Write($"{id}: never ran, it can fire again", Kind.Ok);
        }

        // ------------------------------------------------------------------ Tab completion

        /// <summary>What the arguments of the console's own commands are, for completion.</summary>
        static readonly Dictionary<string, string> s_OwnArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["where"] = "npc", ["run"] = "event", ["resetEvent"] = "event", ["resetQuest"] = "quest", ["help"] = "command",
        };

        static readonly string[] s_OwnNames = { "where", "run", "resetQuest", "resetEvent", "help", "clear" };

        /// <summary>
        /// Completes the word at the end of <paramref name="text"/>: a command name, or an id of the kind the argument
        /// expects (npc, place, stat, item, quest, event, link, flag; for target also all and tag:x). With one match the
        /// word is completed; with several, as far as they agree, and <paramref name="options"/> lists them.
        /// </summary>
        public string Complete(string text, out List<string> options)
        {
            options = new List<string>();
            text ??= "";
            int start = text.Length;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_' || text[start - 1] == ':' || text[start - 1] == '-' || text[start - 1] == '.')) start--;
            string word = text.Substring(start), before = text.Substring(0, start);

            string kind = ArgumentKind(before);
            if (kind == null) return text;
            var matches = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Candidates(kind))
                if (c.StartsWith(word, StringComparison.OrdinalIgnoreCase)) matches.Add(c);
            if (matches.Count == 0) return text;
            options.AddRange(matches);
            if (options.Count == 1) return before + options[0];

            string common = options[0];
            foreach (var o in options)
            {
                int n = 0;
                while (n < common.Length && n < o.Length && char.ToLowerInvariant(common[n]) == char.ToLowerInvariant(o[n])) n++;
                common = common.Substring(0, n);
            }
            return common.Length > word.Length ? before + common : text;
        }

        /// <summary>The kind of word expected after <paramref name="before"/>: "command", a parameter name, or null.</summary>
        static string ArgumentKind(string before)
        {
            int open = before.IndexOf('(');
            string name;
            int index;
            string key = null;
            if (open < 0)
            {
                var words = before.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) return "command";
                if (!before.EndsWith(" ") && !before.EndsWith("\t")) return null;
                name = words[0];
                index = words.Length - 1;
            }
            else
            {
                name = before.Substring(0, open).Trim();
                string inside = before.Substring(open + 1);
                int depth = 0, commas = 0, last = 0;
                bool quoted = false;
                for (int i = 0; i < inside.Length; i++)
                {
                    char ch = inside[i];
                    if (quoted) { if (ch == '"') quoted = false; continue; }
                    if (ch == '"') quoted = true;
                    else if (ch == '[' || ch == '{') depth++;
                    else if (ch == ']' || ch == '}') depth--;
                    else if (ch == ',' && depth == 0) { commas++; last = i + 1; }
                }
                if (quoted) return null;
                string current = inside.Substring(last).Trim();
                if (current.EndsWith("="))
                {
                    key = current.Substring(0, current.Length - 1).Trim();
                    if (!IsIdent(key)) return null;
                }
                else if (current.Length > 0) return null;     // the start of the word is not where completion expects one
                index = commas;
            }

            if (s_OwnArgs.TryGetValue(name, out var own)) return key == null && index == 0 ? own : null;
            if (!s_Params.TryGetValue(name, out var p)) return null;
            if (key != null) return Array.IndexOf(p.named, key) >= 0 || Array.IndexOf(p.positional, key) >= 0 ? key : null;
            return index < p.positional.Length ? p.positional[index] : null;
        }

        IEnumerable<string> Candidates(string kind)
        {
            var db = _compiled?.Db;
            switch (kind)
            {
                case "command":
                    foreach (var e in ContentCompiler.ConsoleEffects) yield return e;
                    foreach (var o in s_OwnNames) yield return o;
                    yield break;
            }
            if (db == null) yield break;
            switch (kind)
            {
                case "npc":
                    foreach (var n in db.NpcList) yield return n.Id;
                    break;
                case "target":
                    foreach (var n in db.NpcList) yield return n.Id;
                    yield return "player";
                    yield return "all";
                    var tags = new HashSet<string>();
                    foreach (var n in db.NpcList) if (n.Tags != null) foreach (var t in n.Tags) tags.Add(t);
                    foreach (var t in tags) yield return "tag:" + t;
                    break;
                case "node":
                    foreach (var m in db.MapList) yield return m.Id;
                    break;
                case "link":
                    foreach (var m in db.MapList) foreach (var l in m.Links) if (!string.IsNullOrEmpty(l.Id)) yield return l.Id;
                    break;
                case "stat":
                    foreach (var s in db.Rules.Stats) yield return s.Id;
                    break;
                case "item":
                    foreach (var i in db.ItemList) yield return i.Id;
                    break;
                case "quest":
                    foreach (var q in db.QuestList) yield return q.Id;
                    break;
                case "event":
                    foreach (var e in db.EventList) yield return e.Id;
                    break;
                case "key":
                    // the flags the game has now (the author's, not the engine's own state)
                    if (!Alive) break;
                    foreach (var f in _em.GetBuffer<FlagValue>(_world, true).AsNativeArray().ToArray())
                    {
                        string name = Ids.Name(f.Key);
                        if (!name.StartsWith("link:") && !name.StartsWith("image:") && !name.StartsWith("sound:")) yield return name;
                    }
                    break;
            }
        }

        /// <summary>A line of information in the log (the panel lists the completion choices with it).</summary>
        public void Note(string text) => Write(text, Kind.Info);

        // ------------------------------------------------------------------ names

        string Who(int target)
        {
            if (target == 0) return "player";
            return Ids.Name(target);
        }

        string Place(int node)
        {
            if (node == 0) return "nowhere";
            string id = Ids.Name(node);
            if (_compiled.Db.Maps.TryGetValue(id, out var map))
            {
                string title = TextTable.Localize(map.Title);
                if (title.Length > 0 && title != id) return $"{title} ({id})";
            }
            return id;
        }

        static string Id(JToken value) => value.Type == JTokenType.String ? (string)value : value.ToString();

        // ------------------------------------------------------------------ parsing

        /// <summary>
        /// <c>name(a, b, key=value)</c>, or <c>name a b</c> for values without spaces. Values as in the Studio: numbers, true / false, "text in quotes",
        /// [JSON], {JSON}, or a bare word (an id).
        /// </summary>
        public static void Parse(string line, out string name, out List<(string key, JToken value)> args)
        {
            args = new List<(string, JToken)>();
            int open = line.IndexOf('(');
            if (open < 0)
            {
                // the short form for quick commands: "where elena", "run ev_x", "help modStat"
                var words = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                name = words.Length > 0 ? words[0] : "";
                if (!IsIdent(name)) throw new ConsoleError($"not a command: '{line}'");
                for (int i = 1; i < words.Length; i++) args.Add((null, Value(words[i])));
                return;
            }
            name = line.Substring(0, open).Trim();
            if (!IsIdent(name)) throw new ConsoleError($"not a command name: '{name}'");
            int close = line.LastIndexOf(')');
            if (close < open || line.Substring(close + 1).Trim().Length > 0) throw new ConsoleError("missing ')' at the end");
            foreach (var part in SplitTopLevel(line.Substring(open + 1, close - open - 1)))
            {
                string p = part.Trim();
                if (p.Length == 0) throw new ConsoleError("empty argument (two commas in a row?)");
                string key = null;
                int eq = KeyEnd(p);
                if (eq > 0) { key = p.Substring(0, eq).Trim(); p = p.Substring(eq + 1).Trim(); }
                if (p.Length == 0) throw new ConsoleError($"no value after '{key}='");
                args.Add((key, Value(p)));
            }
        }

        /// <summary>Index of the '=' of "key=value", or -1 when the argument has no name.</summary>
        static int KeyEnd(string p)
        {
            int i = 0;
            while (i < p.Length && (char.IsLetterOrDigit(p[i]) || p[i] == '_')) i++;
            int j = i;
            while (j < p.Length && p[j] == ' ') j++;
            return i > 0 && j < p.Length && p[j] == '=' && (j + 1 >= p.Length || p[j + 1] != '=') ? j : -1;
        }

        static JToken Value(string p)
        {
            if (p[0] == '"' || p[0] == '[' || p[0] == '{')
            {
                try { return JToken.Parse(p); }
                catch (Exception) { throw new ConsoleError($"not valid: {p}"); }
            }
            if (p == "true") return true;
            if (p == "false") return false;
            if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d == Math.Floor(d) && Math.Abs(d) < int.MaxValue ? new JValue((long)d) : new JValue(d);
            return p;     // a bare word: an id (also "12:30" for a time, "tag:guard" for a target)
        }

        /// <summary>Splits on the commas outside quotes, brackets and braces.</summary>
        static List<string> SplitTopLevel(string s)
        {
            var parts = new List<string>();
            if (s.Trim().Length == 0) return parts;
            int depth = 0, start = 0;
            bool quoted = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quoted) { if (c == '\\') i++; else if (c == '"') quoted = false; continue; }
                if (c == '"') quoted = true;
                else if (c == '[' || c == '{' || c == '(') depth++;
                else if (c == ']' || c == '}' || c == ')') depth--;
                else if (c == ',' && depth == 0) { parts.Add(s.Substring(start, i - start)); start = i + 1; }
            }
            if (quoted) throw new ConsoleError("a text is missing its closing quote");
            parts.Add(s.Substring(start));
            return parts;
        }

        static bool IsIdent(string s)
        {
            if (string.IsNullOrEmpty(s) || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (var c in s) if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }
    }
}
