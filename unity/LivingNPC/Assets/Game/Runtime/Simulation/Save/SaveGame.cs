using System;
using System.Collections.Generic;
using System.IO;
using LivingNPC.Content;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using UnityEngine;

namespace LivingNPC.Simulation
{
    /// <summary>What a save section can reach: the live world plus the compiled content it was built from.</summary>
    public class SaveContext
    {
        public EntityManager Em;
        public Entity World, Player;
        public CompiledContent Content;
        public RuleRuntime Rt;
        /// <summary>Non-fatal problems found while loading (an id the content no longer has, a stage that was renamed...).</summary>
        public readonly List<string> Warnings = new List<string>();

        public SaveContext(EntityManager em, Entity world, Entity player, CompiledContent content)
        {
            Em = em; World = world; Player = player; Content = content;
            Rt = RuleRuntime.Create(em, world, player);
        }

        public void Warn(string message) => Warnings.Add(message);
    }

    /// <summary>
    /// One independent part of a save (clock, flags, quests, a future economy...). A new game system adds its own
    /// section class and registers it in <see cref="SaveGame.Sections"/>; nothing else changes.
    /// Rules every section follows:
    ///  - data refers to content by string id, never by the hashed int or a list index;
    ///  - Apply runs on a world that was just built from content, so anything missing from the data keeps its
    ///    content default; an id the content no longer has is skipped with a warning, never an exception;
    ///  - old saves are not kept loadable: when the layout of a section changes, raise <see cref="SaveGame.FormatVersion"/>,
    ///    and saves of another format are refused with a clear message.
    /// </summary>
    public interface ISaveSection
    {
        string Id { get; }
        JToken Capture(SaveContext ctx);
        void Apply(SaveContext ctx, JToken data);
    }

    public class SaveException : Exception { public SaveException(string message) : base(message) { } }

    /// <summary>Ids as they are written in a save: the content string, or "#123" for a hash this session cannot name.</summary>
    public static class SaveIds
    {
        public static string Name(int id) => id == 0 ? "" : Ids.TryName(id, out var n) ? n : "#" + id;

        public static int Id(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            if (name[0] == '#' && int.TryParse(name.Substring(1), out var raw)) return raw;
            return Ids.Hash(name);
        }
    }

    /// <summary>
    /// Whole-game save: an envelope with a summary and a set of <see cref="ISaveSection"/>s, written as JSON.
    /// Sections the running game does not know (a newer build, a removed system) are kept and written back, so
    /// loading and saving with an older build never destroys them.
    /// </summary>
    public static class SaveGame
    {
        public const string FormatName = "livingnpc-save";
        /// <summary>
        /// 2: sections without their own version, items with their owner, links and message keys by id.
        /// 3: automatic quests, relations and memory of the npcs, relations of the player, the values of a log line ("bind").
        /// </summary>
        public const int FormatVersion = 3;
        public const string QuickSlot = "quick", AutoSlot = "auto";

        /// <summary>Applied in this order: later sections may rely on earlier ones (npcs need the clock).</summary>
        public static readonly List<ISaveSection> Sections = new List<ISaveSection>
        {
            new ClockSection(), new FlagsSection(), new LinksSection(), new ImagesSection(), new SoundsSection(), new QuestsSection(),
            new EventsSection(), new MessagesSection(), new PlayerSection(), new NpcsSection(), new DebugSection(),
        };

        /// <summary>Sections of the loaded save that this build does not know: written back on the next save.</summary>
        static readonly Dictionary<string, JToken> s_Carried = new Dictionary<string, JToken>();

        /// <summary>Forgets the sections carried from the last loaded save. Call before starting a game that is not that save (new game, another world).</summary>
        public static void ClearCarried() => s_Carried.Clear();

        /// <summary>Saves are kept per world, so two stories never see each other's slots.</summary>
        public static string Folder => Path.Combine(Application.persistentDataPath, "saves", LivingNPC.Content.ContentLoader.SelectedWorld ?? "");

        public static string SlotPath(string slot)
        {
            var clean = new System.Text.StringBuilder();
            foreach (var ch in slot ?? "") clean.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            return Path.Combine(Folder, (clean.Length == 0 ? "slot" : clean.ToString()) + ".json");
        }

        /// <summary>The picture of the screen at the moment of saving, next to the save (never inside it).</summary>
        public static string ThumbPath(string slot) => Path.ChangeExtension(SlotPath(slot), ".png");

        /// <summary>Removes a slot: the save, its previous copy and its picture.</summary>
        public static void Delete(string slot)
        {
            var path = SlotPath(slot);
            foreach (var f in new[] { path, path + ".bak", path + ".tmp", ThumbPath(slot) })
                if (File.Exists(f)) File.Delete(f);
        }

        /// <summary>Every save file of the selected world (not the .bak copies), newest first.</summary>
        public static List<string> AllSaves()
        {
            var list = new List<string>();
            if (Directory.Exists(Folder)) list.AddRange(Directory.GetFiles(Folder, "*.json"));
            list.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            return list;
        }

        /// <summary>Saving is only allowed when the world is at rest: no dialog, no travel or waiting in progress.</summary>
        public static bool CanSave(EntityManager em, Entity world, Entity player, out string why)
        {
            why = null;
            var ws = em.GetComponentData<WorldState>(world);
            var clock = em.GetComponentData<ClockState>(world);
            var p = em.GetComponentData<PlayerState>(player);
            if (ws.DialogOpen != 0) why = "a dialogue is open";
            else if (clock.Pending > 0 || p.Node == 0 || p.PendingNode != 0) why = "time is passing (journey or wait)";
            return why == null;
        }

        public static JObject Capture(SaveContext ctx)
        {
            var sections = new JObject();
            foreach (var kv in s_Carried) sections[kv.Key] = kv.Value.DeepClone();
            foreach (var s in Sections)
            {
                try { sections[s.Id] = s.Capture(ctx); }
                catch (Exception e) { throw new SaveException($"section '{s.Id}': {e.Message}"); }   // never write a save with a hole in it
            }
            return new JObject
            {
                ["format"] = FormatName,
                ["version"] = FormatVersion,
                ["savedAt"] = DateTime.UtcNow.ToString("o"),
                ["meta"] = Meta(ctx),
                ["sections"] = sections,
            };
        }

        /// <summary>A summary for listing slots without reading the sections. Language neutral: the menu formats "now" itself.</summary>
        static JObject Meta(SaveContext ctx)
        {
            var p = ctx.Em.GetComponentData<PlayerState>(ctx.Player);
            int active = 0, done = 0;
            foreach (var q in ctx.Em.GetBuffer<QuestState>(ctx.World, true))
            {
                if (q.Status == (byte)QuestStatus.Active) active++;
                else if (q.Status == (byte)QuestStatus.Completed) done++;
            }
            var packs = new JArray();
            if (ctx.Content?.Db != null) foreach (var pk in ctx.Content.Db.Packs) packs.Add(pk.Id);
            var clock = ctx.Em.GetComponentData<ClockState>(ctx.World);
            string node = SaveIds.Name(p.Node != 0 ? p.Node : p.PendingNode);
            // the title of the place as the content writes it (a string or {"it": ..., "en": ...}): the menu localizes it
            JToken place = null;
            if (ctx.Content?.Db != null && ctx.Content.Db.Maps.TryGetValue(node, out var map)) place = map.Title?.DeepClone();
            return new JObject
            {
                ["now"] = clock.Now,
                ["startDayOfWeek"] = clock.StartDayOfWeek,
                ["node"] = node,
                ["place"] = place,
                ["questsActive"] = active,
                ["questsCompleted"] = done,
                ["packs"] = packs,
                ["debugUsed"] = ctx.Em.GetComponentData<WorldState>(ctx.World).DebugUsed != 0,
            };
        }

        /// <summary>Puts a save into a world just built from content. Problems that leave the rest usable become ctx.Warnings.</summary>
        public static void Apply(SaveContext ctx, JObject save)
        {
            Validate(save);
            var sections = save["sections"] as JObject ?? new JObject();
            s_Carried.Clear();
            var known = new HashSet<string>();
            foreach (var s in Sections)
            {
                known.Add(s.Id);
                var data = sections[s.Id];
                if (data == null || data.Type == JTokenType.Null) continue;     // absent section: the content defaults stay
                try { s.Apply(ctx, data); }
                catch (Exception e) { ctx.Warn($"section '{s.Id}': {e.Message}"); Debug.LogException(e); }
            }
            foreach (var kv in sections)
            {
                if (known.Contains(kv.Key)) continue;
                s_Carried[kv.Key] = kv.Value.DeepClone();
                ctx.Warn($"unknown section '{kv.Key}': kept in the file without being used");
            }

            // what a load must not repeat or leave dangling
            var clock = ctx.Em.GetComponentData<ClockState>(ctx.World);
            clock.Pending = 0; clock.Ticked = 0; clock.LastStep = 0; clock.Sleeping = 0;
            ctx.Em.SetComponentData(ctx.World, clock);
            var p = ctx.Em.GetComponentData<PlayerState>(ctx.Player);
            p.PendingNode = 0; p.Arrived = 0;               // arriving again would re-fire the enter-node events of this place
            ctx.Em.SetComponentData(ctx.Player, p);
            ctx.Rt.Touch();
        }

        static void Validate(JObject save)
        {
            if (save == null) throw new SaveException("empty save");
            if (save.Value<string>("format") != FormatName) throw new SaveException("not a LivingNPC save");
            int version = save["version"]?.Value<int>() ?? 0;
            if (version < 1) throw new SaveException("save version missing");
            if (version < FormatVersion) throw new SaveException($"save from an older version of the game ({version}): it can no longer be loaded, start a new game");
            if (version > FormatVersion) throw new SaveException($"save from a newer version of the game ({version}, this game reads up to {FormatVersion})");
        }

        /// <summary>Writes through a temp file and keeps the previous save as .bak, so a crash never leaves the only copy corrupt.</summary>
        public static void Write(string path, JObject save)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, save.ToString(Formatting.Indented));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
        }

        /// <summary>Null (with the reason in <paramref name="problem"/>) when there is nothing usable; an unreadable file falls back to its .bak.</summary>
        public static JObject Read(string path, out string problem)
        {
            problem = null;
            if (!File.Exists(path) && !File.Exists(path + ".bak")) { problem = "no save: " + path; return null; }
            foreach (var candidate in new[] { path, path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    var o = JObject.Parse(File.ReadAllText(candidate));
                    Validate(o);
                    if (candidate != path) Debug.LogWarning($"[LivingNPC] {path} is unreadable, loaded the previous save instead ({candidate})");
                    return o;
                }
                catch (SaveException e) { problem = e.Message; return null; }          // readable but not loadable: an older copy would hide it
                catch (Exception e) { problem = $"{Path.GetFileName(candidate)}: {e.Message}"; }
            }
            return null;
        }
    }
}
