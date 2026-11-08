using System;
using System.Collections.Generic;
using LivingNPC.Content;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace LivingNPC.Simulation
{
    // The sections of a save, one per kind of state. See ISaveSection for the rules they follow.
    // Output is sorted by id so two saves of the same state are identical and diff cleanly; fields that hold their
    // default (nothing, 0, the content's value) are left out.

    static class SaveJson
    {
        public static JObject Obj(JToken t) => t as JObject ?? new JObject();
        public static JArray Arr(JToken t) => t as JArray ?? new JArray();
        public static int Int(JToken o, string key, int def = 0) { var v = o?[key]; return v == null || v.Type == JTokenType.Null ? def : v.Value<int>(); }
        public static float Float(JToken o, string key, float def = 0) { var v = o?[key]; return v == null || v.Type == JTokenType.Null ? def : v.Value<float>(); }
        public static string Str(JToken o, string key) { var v = o?[key]; return v == null || v.Type == JTokenType.Null ? null : v.Value<string>(); }

        /// <summary>Enum names as written in a save: "questStarted", "active". Reading ignores case, so older "QuestStarted" still parses.</summary>
        public static string Camel(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

        public static bool Near(float a, float b) => Math.Abs(a - b) < 0.001f;

        public static JObject Sorted(SortedDictionary<string, JToken> items)
        {
            var o = new JObject();
            foreach (var kv in items) o[kv.Key] = kv.Value;
            return o;
        }

        /// <summary>
        /// Path of a pack file from one of <paramref name="folders"/> on ("images", "sfx"...), with or without that folder:
        /// the absolute path differs between machines.
        /// </summary>
        public static string PackPath(string path, bool keepFolder, params string[] folders)
        {
            path = (path ?? "").Replace('\\', '/');
            int best = -1;
            string hit = null;
            foreach (var f in folders)
            {
                int i = path.LastIndexOf("/" + f + "/", StringComparison.OrdinalIgnoreCase);
                if (i > best) { best = i; hit = f; }
            }
            return best < 0 ? path : path.Substring(best + 1 + (keepFolder ? 0 : hit.Length + 1));
        }

        /// <summary>The id of a node that exists in the current content, else 0.</summary>
        public static int KnownNode(SaveContext c, string name)
        {
            int id = SaveIds.Id(name);
            return id != 0 && c.Rt.Nav.Value.IndexOf(id) >= 0 ? id : 0;
        }


        public static JObject Stats(SaveContext c, Entity owner)
        {
            var items = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var s in c.Em.GetBuffer<StatValue>(owner, true)) items[SaveIds.Name(s.Stat)] = s.Value;
            return Sorted(items);
        }

        /// <summary>Sets the stats the owner has; a stat the content no longer declares is ignored.</summary>
        public static void ApplyStats(SaveContext c, Entity owner, JToken data)
        {
            var buf = c.Em.GetBuffer<StatValue>(owner);
            foreach (var p in Obj(data).Properties())
            {
                int stat = SaveIds.Id(p.Name);
                for (int i = 0; i < buf.Length; i++)
                    if (buf[i].Stat == stat) { buf[i] = new StatValue { Stat = stat, Value = p.Value.Value<float>() }; break; }
            }
        }

        /// <summary>What the owner holds, item id -> count.</summary>
        /// <summary>What the owner thinks of the others: {"npc_id": 20, "player": -5}.</summary>
        public static JObject Relations(SaveContext c, Entity owner)
        {
            var items = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            if (c.Em.HasBuffer<RelationValue>(owner))
                foreach (var r in c.Em.GetBuffer<RelationValue>(owner, true)) items[r.Other == 0 ? "player" : SaveIds.Name(r.Other)] = r.Value;
            return Sorted(items);
        }

        /// <summary>Replaces the relations of the owner; one with a character that no longer exists is skipped.</summary>
        public static void ApplyRelations(SaveContext c, Entity owner, JToken data, string who)
        {
            if (!c.Em.HasBuffer<RelationValue>(owner)) return;
            var buf = c.Em.GetBuffer<RelationValue>(owner);
            buf.Clear();
            foreach (var p in Obj(data).Properties())
            {
                int other = p.Name == "player" ? 0 : SaveIds.Id(p.Name);
                if (other != 0 && c.Rt.FindNpc(other) == Entity.Null) { c.Warn($"{who}: relation with '{p.Name}', who no longer exists: skipped"); continue; }
                buf.Add(new RelationValue { Other = other, Value = p.Value.Value<float>() });
            }
        }

        /// <summary>A list of ids (characters, places, items) by name.</summary>
        public static JArray Names(in Unity.Collections.FixedList32Bytes<int> ids)
        {
            var arr = new JArray();
            for (int i = 0; i < ids.Length; i++) arr.Add(SaveIds.Name(ids[i]));
            return arr;
        }

        public static Unity.Collections.FixedList32Bytes<int> Ids32(JToken data)
        {
            var list = new Unity.Collections.FixedList32Bytes<int>();
            foreach (var v in Arr(data)) if (list.Length < list.Capacity) list.Add(SaveIds.Id(v.Value<string>()));
            return list;
        }

        public static JObject Items(SaveContext c, Entity owner)
        {
            var items = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var s in c.Em.GetBuffer<InventorySlot>(owner, true)) items[SaveIds.Name(s.Item)] = s.Count;
            return Sorted(items);
        }

        /// <summary>
        /// Replaces what the owner holds. An item the content no longer has is kept by its id (so it is written back on
        /// the next save, and comes back if the pack that defines it returns).
        /// </summary>
        public static void ApplyItems(SaveContext c, Entity owner, JToken data)
        {
            var buf = c.Em.GetBuffer<InventorySlot>(owner);
            buf.Clear();
            foreach (var p in Obj(data).Properties())
            {
                int count = p.Value.Value<int>();
                if (count > 0) buf.Add(new InventorySlot { Item = SaveIds.Id(p.Name), Count = count });
            }
        }
    }

    // ---------------------------------------------------------------- clock
    class ClockSection : ISaveSection
    {
        public string Id => "clock";

        public JToken Capture(SaveContext c) => new JObject { ["now"] = c.Em.GetComponentData<ClockState>(c.World).Now };

        public void Apply(SaveContext c, JToken data)
        {
            var clock = c.Em.GetComponentData<ClockState>(c.World);
            clock.Now = SaveJson.Int(data, "now", clock.Now);
            c.Em.SetComponentData(c.World, clock);
        }
    }

    // ---------------------------------------------------------------- flags (the author's setFlag / addFlag keys)
    /// <summary>
    /// The flags effects set by name. The flags the engine uses for its own state are saved by their own sections:
    /// links (LinksSection), overlay images (ImagesSection) and looping sounds (SoundsSection).
    /// </summary>
    class FlagsSection : ISaveSection
    {
        public string Id => "flags";

        public JToken Capture(SaveContext c)
        {
            var own = LinksSection.Keys(c);
            own.UnionWith(c.Content.OverlayKeys);
            own.UnionWith(c.Content.SoundKeys);
            var items = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var f in c.Em.GetBuffer<FlagValue>(c.World, true))
                if (f.Value != 0 && !own.Contains(f.Key)) items[SaveIds.Name(f.Key)] = f.Value;
            return SaveJson.Sorted(items);
        }

        public void Apply(SaveContext c, JToken data)
        {
            var buf = c.Em.GetBuffer<FlagValue>(c.World);
            buf.Clear();
            foreach (var p in SaveJson.Obj(data).Properties())
            {
                float v = p.Value.Value<float>();
                if (v != 0) buf.Add(new FlagValue { Key = SaveIds.Id(p.Name), Value = v });
            }
        }
    }

    // ---------------------------------------------------------------- links (showLink / hideLink / openLink / closeLink)
    /// <summary>
    /// The links an effect has forced shown / hidden or open / closed: {"link_id": {"visible": false, "open": true}}.
    /// A field that is absent keeps the value written in the map. Must be applied after FlagsSection, which clears the flags.
    /// </summary>
    class LinksSection : ISaveSection
    {
        public string Id => "links";

        /// <summary>Flag keys of every link state (visible and open) the map has.</summary>
        public static HashSet<int> Keys(SaveContext c)
        {
            ref var nav = ref c.Rt.Nav.Value;
            var keys = new HashSet<int>();
            for (int i = 0; i < nav.Edges.Length; i++) { keys.Add(nav.Edges[i].VisibleKey); keys.Add(nav.Edges[i].OpenKey); }
            return keys;
        }

        static void CaptureForced(SaveContext c, int key, string field, JObject o)
        {
            float v = c.Rt.GetFlag(key);         // 1 forced on, 2 forced off (see RuleRuntime.LinkState)
            if (v == 1) o[field] = true;
            else if (v == 2) o[field] = false;
        }

        public JToken Capture(SaveContext c)
        {
            ref var nav = ref c.Rt.Nav.Value;
            var items = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            for (int i = 0; i < nav.Edges.Length; i++)
            {
                var e = nav.Edges[i];
                var o = new JObject();
                CaptureForced(c, e.VisibleKey, "visible", o);
                CaptureForced(c, e.OpenKey, "open", o);
                if (o.Count > 0) items[SaveIds.Name(e.LinkId)] = o;
            }
            return SaveJson.Sorted(items);
        }

        public void Apply(SaveContext c, JToken data)
        {
            var keys = Keys(c);
            foreach (var p in SaveJson.Obj(data).Properties())
            {
                int visible = Ids.LinkVisibleKey(p.Name), open = Ids.LinkOpenKey(p.Name);
                if (!keys.Contains(visible)) { c.Warn($"link '{p.Name}' no longer exists: skipped"); continue; }
                if (p.Value["visible"] is JValue v) c.Rt.SetFlag(visible, (bool)v ? 1 : 2);
                if (p.Value["open"] is JValue o) c.Rt.SetFlag(open, (bool)o ? 1 : 2);
            }
        }
    }

    // ---------------------------------------------------------------- overlay images (showImage / hideImage)
    /// <summary>
    /// The images on screen: {"image:key:shown": {"file": "x.png", "x": 10, "y": 20}}. The file finds the image again; the
    /// position only picks between images that use the same file, so the author can move or resize an image freely.
    /// </summary>
    class ImagesSection : ISaveSection
    {
        public string Id => "images";

        static string Rel(string path) => SaveJson.PackPath(path, false, "images");

        public JToken Capture(SaveContext c)
        {
            var items = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            foreach (int key in c.Content.OverlayKeys)
            {
                int idx = (int)c.Rt.GetFlag(key) - 1;
                if (idx < 0 || idx >= c.Content.Images.Count) continue;
                var img = c.Content.Images[idx];
                items[SaveIds.Name(key)] = new JObject { ["file"] = Rel(img.Path), ["x"] = img.X, ["y"] = img.Y };
            }
            return SaveJson.Sorted(items);
        }

        public void Apply(SaveContext c, JToken data)
        {
            var valid = new HashSet<int>(c.Content.OverlayKeys);
            foreach (var p in SaveJson.Obj(data).Properties())
            {
                int key = SaveIds.Id(p.Name);
                if (!valid.Contains(key)) { c.Warn($"image '{p.Name}': no effect shows it any more"); continue; }
                string file = SaveJson.Str(p.Value, "file");
                float x = SaveJson.Float(p.Value, "x"), y = SaveJson.Float(p.Value, "y");
                int found = -1;
                float bestDist = float.MaxValue;
                for (int i = 0; i < c.Content.Images.Count; i++)
                {
                    var img = c.Content.Images[i];
                    if (Rel(img.Path) != file) continue;
                    float d = Math.Abs(img.X - x) + Math.Abs(img.Y - y);
                    if (d < bestDist) { bestDist = d; found = i; }
                }
                if (found < 0) { c.Warn($"image '{p.Name}' ({file}): no longer exists"); continue; }
                c.Rt.SetFlag(key, found + 1);
            }
        }
    }

    // ---------------------------------------------------------------- looping sounds (startMusic / startAmbient and their stops)
    /// <summary>The music and the ambient sounds that are playing. One-shot sound effects are not state and are not saved.</summary>
    class SoundsSection : ISaveSection
    {
        public string Id => "sounds";

        static string Rel(string path) => SaveJson.PackPath(path, true, "sfx", "music");

        public JToken Capture(SaveContext c)
        {
            var items = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            foreach (int key in c.Content.SoundKeys)
            {
                int idx = (int)c.Rt.GetFlag(key) - 1;
                if (idx < 0 || idx >= c.Content.Sounds.Count) continue;
                var snd = c.Content.Sounds[idx];
                items[SaveIds.Name(key)] = new JObject { ["file"] = Rel(snd.Path), ["volume"] = snd.Volume, ["fade"] = snd.Fade };
            }
            return SaveJson.Sorted(items);
        }

        public void Apply(SaveContext c, JToken data)
        {
            var valid = new HashSet<int>(c.Content.SoundKeys);
            foreach (var p in SaveJson.Obj(data).Properties())
            {
                int key = SaveIds.Id(p.Name);
                if (!valid.Contains(key)) { c.Warn($"sound '{p.Name}': no effect starts it any more"); continue; }
                string file = SaveJson.Str(p.Value, "file");
                float volume = SaveJson.Float(p.Value, "volume", 1), fade = SaveJson.Float(p.Value, "fade");
                int found = -1, sameFile = -1;
                for (int i = 0; i < c.Content.Sounds.Count && found < 0; i++)
                {
                    var snd = c.Content.Sounds[i];
                    if (Rel(snd.Path) != file) continue;
                    if (SaveJson.Near(snd.Volume, volume) && SaveJson.Near(snd.Fade, fade)) found = i;
                    else if (sameFile < 0) sameFile = i;       // the author changed the volume or fade since: the file is what matters
                }
                if (found < 0) found = sameFile;
                if (found < 0) { c.Warn($"sound '{p.Name}' ({file}): no longer exists"); continue; }
                c.Rt.SetFlag(key, found + 1);
            }
        }
    }

    // ---------------------------------------------------------------- quests
    /// <summary>
    /// {"quest_id": {"status": "active", "stage": "stage_id", "startedAt": 600, "stageAt": 720}}; quests not started are left out.
    /// stageAt is when the current stage began, or when the quest ended.
    /// </summary>
    class QuestsSection : ISaveSection
    {
        public string Id => "quests";

        public JToken Capture(SaveContext c)
        {
            ref var rules = ref c.Rt.Rules.Value;
            var items = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            var states = c.Em.GetBuffer<QuestState>(c.World, true);
            for (int i = 0; i < states.Length && i < rules.Quests.Length; i++)
            {
                var st = states[i];
                if (st.Status == (byte)QuestStatus.NotStarted) continue;
                var q = rules.Quests[i];
                var o = new JObject { ["status"] = SaveJson.Camel(((QuestStatus)st.Status).ToString()) };
                if (q.StageCount > 0 && st.Stage < q.StageCount) o["stage"] = SaveIds.Name(rules.Stages[q.StageStart + st.Stage].Id);
                o["startedAt"] = st.StartedAt;
                o["stageAt"] = st.StageAt;
                items[SaveIds.Name(q.Id)] = o;
            }
            return SaveJson.Sorted(items);
        }

        public void Apply(SaveContext c, JToken data)
        {
            ref var rules = ref c.Rt.Rules.Value;
            var index = new Dictionary<string, int>();
            for (int i = 0; i < rules.Quests.Length; i++) index[SaveIds.Name(rules.Quests[i].Id)] = i;
            var states = c.Em.GetBuffer<QuestState>(c.World);
            foreach (var p in SaveJson.Obj(data).Properties())
            {
                if (!index.TryGetValue(p.Name, out int qi) || qi >= states.Length) { c.Warn($"quest '{p.Name}' no longer exists: skipped"); continue; }
                var o = p.Value;
                if (!Enum.TryParse<QuestStatus>(SaveJson.Str(o, "status"), true, out var status)) { c.Warn($"quest '{p.Name}': stato sconosciuto '{SaveJson.Str(o, "status")}'"); continue; }
                var q = rules.Quests[qi];
                string stageName = SaveJson.Str(o, "stage");
                int stage = 0;
                bool found = string.IsNullOrEmpty(stageName);
                for (int s = 0; s < q.StageCount && !found; s++)
                    if (SaveIds.Name(rules.Stages[q.StageStart + s].Id) == stageName) { stage = s; found = true; }
                if (!found && status == QuestStatus.Active) c.Warn($"quest '{p.Name}': stage '{stageName}' no longer exists, restarting from the first");
                states[qi] = new QuestState { Status = (byte)status, Stage = stage, StageAt = SaveJson.Int(o, "stageAt"), StartedAt = SaveJson.Int(o, "startedAt") };
            }
        }
    }

    // ---------------------------------------------------------------- events (once / cooldown memory)
    class EventsSection : ISaveSection
    {
        public string Id => "events";

        public JToken Capture(SaveContext c)
        {
            ref var rules = ref c.Rt.Rules.Value;
            var items = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            var states = c.Em.GetBuffer<EventState>(c.World, true);
            for (int i = 0; i < states.Length && i < rules.Events.Length; i++)
                if (states[i].Count > 0) items[SaveIds.Name(rules.Events[i].Id)] = new JObject { ["count"] = states[i].Count, ["lastAt"] = states[i].LastAt };
            return SaveJson.Sorted(items);
        }

        public void Apply(SaveContext c, JToken data)
        {
            ref var rules = ref c.Rt.Rules.Value;
            var index = new Dictionary<string, int>();
            for (int i = 0; i < rules.Events.Length; i++) index[SaveIds.Name(rules.Events[i].Id)] = i;
            var states = c.Em.GetBuffer<EventState>(c.World);
            foreach (var p in SaveJson.Obj(data).Properties())
            {
                if (!index.TryGetValue(p.Name, out int ei) || ei >= states.Length) { c.Warn($"event '{p.Name}' no longer exists: skipped"); continue; }
                states[ei] = new EventState { Count = SaveJson.Int(p.Value, "count"), LastAt = SaveJson.Int(p.Value, "lastAt") };
            }
        }
    }

    // ---------------------------------------------------------------- debug ({"used": true} once the debug console changed the game)
    /// <summary>
    /// Whether the debug console changed this game. It stays true for good, through saves and loads: a bug report from such
    /// a game may come from the change rather than from the content.
    /// </summary>
    class DebugSection : ISaveSection
    {
        public string Id => "debug";

        public JToken Capture(SaveContext c) => new JObject { ["used"] = c.Em.GetComponentData<WorldState>(c.World).DebugUsed != 0 };

        public void Apply(SaveContext c, JToken data)
        {
            var ws = c.Em.GetComponentData<WorldState>(c.World);
            ws.DebugUsed = (byte)(SaveJson.Obj(data).Value<bool?>("used") == true ? 1 : 0);
            c.Em.SetComponentData(c.World, ws);
        }
    }

    // ---------------------------------------------------------------- message log
    /// <summary>
    /// The log, oldest first: [{"at": 605, "kind": "questUpdated", "text": "quest:q_id:stage:stage_id", "npc": "lia"}].
    /// "text" is the TextTable key, so the log follows the language and the author's rewording; "npc" only when the message
    /// names one.
    /// </summary>
    class MessagesSection : ISaveSection
    {
        public string Id => "messages";

        static string TextKey(int id)
        {
            string name = SaveIds.Name(id);
            return name.StartsWith(TextTable.IdPrefix, StringComparison.Ordinal) ? name.Substring(TextTable.IdPrefix.Length) : name;
        }

        static int TextId(string key) => string.IsNullOrEmpty(key) ? 0 : key[0] == '#' ? SaveIds.Id(key) : TextTable.IdOf(key);

        public JToken Capture(SaveContext c)
        {
            var arr = new JArray();
            foreach (var m in c.Em.GetBuffer<GameMessage>(c.World, true))
            {
                var o = new JObject { ["at"] = m.At, ["kind"] = SaveJson.Camel(m.Kind.ToString()), ["text"] = TextKey(m.TextId) };
                if (m.Arg != 0) o["npc"] = SaveIds.Name(m.Arg);     // Arg is the npc that {npc} in the text stands for
                if (m.Bind.Length > 0) o["bind"] = SaveJson.Names(m.Bind);      // what {self} and the slots of an automatic quest stand for
                arr.Add(o);
            }
            return arr;
        }

        public void Apply(SaveContext c, JToken data)
        {
            var buf = c.Em.GetBuffer<GameMessage>(c.World);
            buf.Clear();
            int dropped = 0;
            foreach (var m in SaveJson.Arr(data))
            {
                int text = TextId(SaveJson.Str(m, "text"));
                if (text != 0 && string.IsNullOrEmpty(TextTable.Get(text))) { dropped++; continue; }
                Enum.TryParse<MsgKind>(SaveJson.Str(m, "kind"), true, out var kind);
                buf.Add(new GameMessage { TextId = text, Kind = kind, At = SaveJson.Int(m, "at"), Arg = SaveIds.Id(SaveJson.Str(m, "npc")), Bind = SaveJson.Ids32(m["bind"]) });
            }
            if (dropped > 0) c.Warn($"log: {dropped} message(s) with a text that no longer exists");
        }
    }

    // ---------------------------------------------------------------- player
    /// <summary>{"node": "...", "stats": {...}, "items": {...}}.</summary>
    class PlayerSection : ISaveSection
    {
        public string Id => "player";

        public JToken Capture(SaveContext c)
        {
            var p = c.Em.GetComponentData<PlayerState>(c.Player);
            return new JObject
            {
                ["node"] = SaveIds.Name(p.Node != 0 ? p.Node : p.PendingNode),
                ["stats"] = SaveJson.Stats(c, c.Player),
                ["items"] = SaveJson.Items(c, c.Player),        // always written: an empty inventory is not the starting one
                ["relations"] = SaveJson.Relations(c, c.Player),
            };
        }

        public void Apply(SaveContext c, JToken data)
        {
            var p = c.Em.GetComponentData<PlayerState>(c.Player);
            string name = SaveJson.Str(data, "node");
            int node = SaveJson.KnownNode(c, name);
            if (node != 0) { p.Node = node; p.FromNode = node; }
            else c.Warn($"player: place '{name}' no longer exists, staying at the starting place");
            c.Em.SetComponentData(c.Player, p);
            SaveJson.ApplyStats(c, c.Player, data?["stats"]);
            if (data?["items"] is JObject items) SaveJson.ApplyItems(c, c.Player, items);
            if (data?["relations"] is JObject relations) SaveJson.ApplyRelations(c, c.Player, relations, "player");
        }
    }

    // ---------------------------------------------------------------- npcs
    /// <summary>
    /// Where each npc is and what it is doing, plus its stats:
    /// {"lia": {"node": "square", "activity": "work", "override": {"node": "inn", "until": 900}, "stats": {...}}}.
    /// While travelling "node" is absent and "next" / "arriveAt" say where to; "goal" only when it is not where the npc is
    /// or is heading. The places, routine and items an npc was built with come from content and are not saved; only what
    /// the game changed is: "places" (the places that now point somewhere else than the content says), "routine" (the
    /// whole block list, when it differs from the content's) and "items" (when they differ from the starting ones).
    /// An npc without those fields keeps the content's. So an author's later change to the routine of an npc whose
    /// routine was already changed in a save does not reach that save.
    /// </summary>
    class NpcsSection : ISaveSection
    {
        public string Id => "npcs";

        public JToken Capture(SaveContext c)
        {
            var content = new Dictionary<int, CompiledNpc>();
            foreach (var n in c.Content.Npcs) content[n.Id] = n;
            var items = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var r in c.Em.GetBuffer<EntityRef>(c.World, true))
            {
                var l = c.Em.GetComponentData<NpcLocation>(r.Entity);
                var o = new JObject();
                if (l.Node != 0) o["node"] = SaveIds.Name(l.Node);
                else { o["next"] = SaveIds.Name(l.NextNode); o["arriveAt"] = l.ArriveAt; }
                if (l.Goal != 0 && l.Goal != (l.Node != 0 ? l.Node : l.NextNode)) o["goal"] = SaveIds.Name(l.Goal);
                if (l.Activity != 0) o["activity"] = SaveIds.Name(l.Activity);
                if (l.OverrideNode != 0) o["override"] = new JObject { ["node"] = SaveIds.Name(l.OverrideNode), ["until"] = l.OverrideUntil };
                o["stats"] = SaveJson.Stats(c, r.Entity);
                content.TryGetValue(r.Id, out var cn);
                CaptureContentChanges(c, r.Entity, cn, o);
                CaptureAuto(c, r.Entity, cn, o);
                items[SaveIds.Name(r.Id)] = o;
            }
            return SaveJson.Sorted(items);
        }

        static void CaptureContentChanges(SaveContext c, Entity npc, CompiledNpc content, JObject o)
        {
            var places = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var p in c.Em.GetBuffer<NpcPlace>(npc, true))
            {
                bool same = false;
                if (content != null) foreach (var cp in content.Places) if (cp.Key == p.Key && cp.Node == p.Node) { same = true; break; }
                if (!same) places[SaveIds.Name(p.Key)] = SaveIds.Name(p.Node);
            }
            if (places.Count > 0) o["places"] = SaveJson.Sorted(places);

            if (content == null || !SameItems(c.Em.GetBuffer<InventorySlot>(npc, true), content.Items)) o["items"] = SaveJson.Items(c, npc);

            var blocks = c.Em.GetBuffer<ScheduleBlock>(npc, true);
            bool unchanged = content != null && blocks.Length == content.Blocks.Count;
            for (int i = 0; unchanged && i < blocks.Length; i++) unchanged = SameBlock(blocks[i], content.Blocks[i]);
            if (unchanged) return;
            var arr = new JArray();
            foreach (var b in blocks)
                arr.Add(new JObject
                {
                    ["days"] = (int)b.DayMask, ["from"] = (int)b.Start, ["to"] = (int)b.End,      // minutes since midnight; days: bit 0 = Monday
                    ["node"] = SaveIds.Name(b.Node), ["place"] = SaveIds.Name(b.Place),
                    ["activity"] = SaveIds.Name(b.Activity), ["priority"] = (int)b.Priority, ["tag"] = SaveIds.Name(b.Tag),
                });
            o["routine"] = arr;
        }

        /// <summary>
        /// The automatic quests of the npc: "relations" (always), "memory" (when it has any) and "auto": what differs from the
        /// content (enabled, goal), the pause ("nextAt") and the running quest ("template", "stage", "bind"...).
        /// </summary>
        static void CaptureAuto(SaveContext c, Entity npc, CompiledNpc content, JObject o)
        {
            o["relations"] = SaveJson.Relations(c, npc);
            if (c.Em.HasBuffer<NpcMemory>(npc))
            {
                var mem = new JArray();
                foreach (var m in c.Em.GetBuffer<NpcMemory>(npc, true))
                {
                    var mo = new JObject { ["template"] = SaveIds.Name(m.Template), ["outcome"] = m.Outcome, ["at"] = m.At };
                    if (m.Other != 0) mo["other"] = SaveIds.Name(m.Other);
                    mem.Add(mo);
                }
                if (mem.Count > 0) o["memory"] = mem;
            }
            if (!c.Em.HasComponent<NpcAuto>(npc)) return;
            var a = c.Em.GetComponentData<NpcAuto>(npc);
            var ao = new JObject();
            if (content == null || (a.Enabled != 0) != content.AutoQuests) ao["enabled"] = a.Enabled != 0;
            if (content == null || a.Goal != content.Goal) ao["goal"] = SaveIds.Name(a.Goal);
            if (a.NextAt != 0) ao["nextAt"] = a.NextAt;
            ref var rules = ref c.Rt.Rules.Value;
            if (a.Template > 0 && a.Template <= rules.Templates.Length)
            {
                var t = rules.Templates[a.Template - 1];
                ao["template"] = SaveIds.Name(t.Id);
                if (a.Stage < t.StageCount) ao["stage"] = SaveIds.Name(rules.Stages[t.StageStart + a.Stage].Id);
                ao["stageAt"] = a.StageAt;
                ao["startedAt"] = a.StartedAt;
                ao["roll"] = a.Roll;
                ao["seed"] = a.Seed;
                ao["bind"] = SaveJson.Names(a.Bind);
            }
            if (ao.Count > 0) o["auto"] = ao;
        }

        static void ApplyAuto(SaveContext c, Entity npc, string name, JToken o)
        {
            if (o["relations"] is JObject relations) SaveJson.ApplyRelations(c, npc, relations, $"character '{name}'");
            if (c.Em.HasBuffer<NpcMemory>(npc) && o["memory"] is JArray memory)
            {
                var mem = c.Em.GetBuffer<NpcMemory>(npc);
                mem.Clear();
                foreach (var m in memory)
                    mem.Add(new NpcMemory { Template = SaveIds.Id(SaveJson.Str(m, "template")), Outcome = (byte)SaveJson.Int(m, "outcome"), At = SaveJson.Int(m, "at"), Other = SaveIds.Id(SaveJson.Str(m, "other")) });
            }
            if (!c.Em.HasComponent<NpcAuto>(npc) || !(o["auto"] is JObject ao)) return;
            var a = c.Em.GetComponentData<NpcAuto>(npc);
            if (ao["enabled"] is JValue en) a.Enabled = (byte)((bool)en ? 1 : 0);
            if (ao["goal"] != null)
            {
                string goal = SaveJson.Str(ao, "goal");
                a.Goal = string.IsNullOrEmpty(goal) ? 0 : SaveIds.Id(goal);
                if (a.Goal != 0 && !c.Content.GoalNames.ContainsKey(a.Goal)) { c.Warn($"character '{name}': goal '{goal}' no longer exists"); a.Goal = 0; }
            }
            a.NextAt = SaveJson.Int(ao, "nextAt");
            string template = SaveJson.Str(ao, "template");
            if (!string.IsNullOrEmpty(template))
            {
                ref var rules = ref c.Rt.Rules.Value;
                int ti = -1, tid = SaveIds.Id(template);
                for (int i = 0; i < rules.Templates.Length; i++) if (rules.Templates[i].Id == tid) { ti = i; break; }
                if (ti < 0) c.Warn($"character '{name}': automatic quest '{template}' no longer exists: dropped");
                else
                {
                    var t = rules.Templates[ti];
                    string stageName = SaveJson.Str(ao, "stage");
                    int stage = -1;
                    for (int s = 0; s < t.StageCount; s++) if (SaveIds.Name(rules.Stages[t.StageStart + s].Id) == stageName) { stage = s; break; }
                    var bind = SaveJson.Ids32(ao["bind"]);
                    if (stage < 0) c.Warn($"character '{name}': automatic quest '{template}': stage '{stageName}' no longer exists: dropped");
                    else if (bind.Length != t.SlotCount + 1) c.Warn($"character '{name}': automatic quest '{template}': its slots changed: dropped");
                    else
                    {
                        a.Template = ti + 1; a.Stage = stage; a.Bind = bind;
                        a.StageAt = SaveJson.Int(ao, "stageAt"); a.StartedAt = SaveJson.Int(ao, "startedAt");
                        a.Roll = SaveJson.Int(ao, "roll"); a.Seed = ao["seed"]?.Value<uint>() ?? 1u;
                    }
                }
            }
            c.Em.SetComponentData(npc, a);
        }

        /// <summary>Same items and counts, in any order (both hold one entry per item).</summary>
        static bool SameItems(DynamicBuffer<InventorySlot> held, List<InventorySlot> start)
        {
            if (held.Length != start.Count) return false;
            foreach (var s in held)
            {
                bool found = false;
                foreach (var t in start) if (t.Item == s.Item && t.Count == s.Count) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        static bool SameBlock(in ScheduleBlock a, in ScheduleBlock b) =>
            a.DayMask == b.DayMask && a.Start == b.Start && a.End == b.End && a.Node == b.Node && a.Activity == b.Activity
            && a.Priority == b.Priority && a.Place == b.Place && a.Tag == b.Tag;

        /// <summary>Puts back the places, routine and items the game had changed at runtime (absent fields: the content's stay).</summary>
        static void ApplyContentChanges(SaveContext c, Entity npc, string name, JToken o)
        {
            int homeKey = Ids.Hash(Ids.HomePlace);
            var places = c.Em.GetBuffer<NpcPlace>(npc);
            foreach (var p in SaveJson.Obj(o["places"]).Properties())
            {
                int key = SaveIds.Id(p.Name), node = SaveJson.KnownNode(c, p.Value.Value<string>());
                if (node == 0) { c.Warn($"character '{name}': place '{p.Name}' points to '{p.Value}', which no longer exists"); continue; }
                bool found = false;
                for (int i = 0; i < places.Length && !found; i++)
                    if (places[i].Key == key) { places[i] = new NpcPlace { Key = key, Node = node }; found = true; }
                if (!found) places.Add(new NpcPlace { Key = key, Node = node });
                if (key == homeKey)
                {
                    var loc = c.Em.GetComponentData<NpcLocation>(npc);
                    loc.HomeNode = node;
                    c.Em.SetComponentData(npc, loc);
                }
            }

            if (o["items"] is JObject items) SaveJson.ApplyItems(c, npc, items);

            if (!(o["routine"] is JArray routine)) return;
            var blocks = c.Em.GetBuffer<ScheduleBlock>(npc);
            blocks.Clear();
            foreach (var b in routine)
            {
                int place = SaveIds.Id(SaveJson.Str(b, "place"));
                int node = SaveJson.KnownNode(c, SaveJson.Str(b, "node"));
                if (node == 0 && place == 0) { c.Warn($"character '{name}': a routine block points to a place that no longer exists: skipped"); continue; }
                blocks.Add(new ScheduleBlock
                {
                    DayMask = (byte)SaveJson.Int(b, "days", 0x7F), Start = (short)SaveJson.Int(b, "from"), End = (short)SaveJson.Int(b, "to", 1440),
                    Node = node, Place = place, Activity = SaveIds.Id(SaveJson.Str(b, "activity")),
                    Priority = (short)SaveJson.Int(b, "priority"), Tag = SaveIds.Id(SaveJson.Str(b, "tag")),
                });
            }
        }

        public void Apply(SaveContext c, JToken data)
        {
            var restored = new HashSet<int>();
            int unknown = 0;
            foreach (var p in SaveJson.Obj(data).Properties())
            {
                int id = SaveIds.Id(p.Name);
                var e = c.Rt.FindNpc(id);
                if (e == Entity.Null) { unknown++; continue; }
                var o = p.Value;
                ApplyContentChanges(c, e, p.Name, o);
                SaveJson.ApplyStats(c, e, o["stats"]);
                ApplyAuto(c, e, p.Name, o);
                int node = SaveJson.KnownNode(c, SaveJson.Str(o, "node"));
                int next = SaveJson.KnownNode(c, SaveJson.Str(o, "next"));
                if (node == 0 && next == 0) continue;        // not placeable (a map was removed): resolved from the routine below
                int goal = SaveJson.KnownNode(c, SaveJson.Str(o, "goal"));
                var ov = o["override"];
                var loc = c.Em.GetComponentData<NpcLocation>(e);
                loc.Node = node;
                loc.NextNode = node == 0 ? next : 0;
                loc.ArriveAt = node == 0 ? SaveJson.Int(o, "arriveAt") : 0;
                loc.Goal = goal != 0 ? goal : (node != 0 ? node : next);
                loc.Activity = SaveIds.Id(SaveJson.Str(o, "activity"));
                loc.OverrideNode = SaveJson.KnownNode(c, SaveJson.Str(ov, "node"));
                loc.OverrideUntil = loc.OverrideNode != 0 ? SaveJson.Int(ov, "until") : 0;
                c.Em.SetComponentData(e, loc);
                restored.Add(id);
            }
            if (unknown > 0) c.Warn($"{unknown} character(s) of the save no longer exist: skipped");

            // npcs the save does not place (new content, or a map that was removed): put them where their routine says they are now
            var clock = c.Em.GetComponentData<ClockState>(c.World);
            foreach (var r in c.Em.GetBuffer<EntityRef>(c.World, true))
            {
                if (restored.Contains(r.Id)) continue;
                var loc = c.Em.GetComponentData<NpcLocation>(r.Entity);
                NpcLogic.Resolve(c.Em.GetBuffer<ScheduleBlock>(r.Entity, true), c.Em.GetBuffer<NpcPlace>(r.Entity, true), clock.Now, clock.StartDayOfWeek, loc.HomeNode, out int node, out int act);
                loc.Node = node; loc.Goal = node; loc.NextNode = 0; loc.ArriveAt = 0; loc.Activity = act;
                loc.OverrideNode = 0; loc.OverrideUntil = 0;
                c.Em.SetComponentData(r.Entity, loc);
            }
        }
    }
}
