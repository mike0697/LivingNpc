using System.Collections.Generic;

namespace LivingNPC.Content
{
    /// <summary>
    /// Merged, typed view of every enabled content pack (managed side, read-only after loading).
    /// This is the "authoring model". ContentCompiler turns it into the ECS-friendly runtime model.
    /// </summary>
    public class ContentDatabase
    {
        public readonly List<PackManifest> Packs = new List<PackManifest>();
        public string WorldName = "";
        public string StartNode;

        // insertion-ordered so that the build is deterministic
        public readonly List<MapNodeDef> MapList = new List<MapNodeDef>();
        public readonly Dictionary<string, MapNodeDef> Maps = new Dictionary<string, MapNodeDef>();
        public readonly List<NpcDef> NpcList = new List<NpcDef>();
        public readonly Dictionary<string, NpcDef> Npcs = new Dictionary<string, NpcDef>();
        public readonly Dictionary<string, ScheduleDef> Schedules = new Dictionary<string, ScheduleDef>();
        public readonly List<QuestDef> QuestList = new List<QuestDef>();
        public readonly List<EventDef> EventList = new List<EventDef>();
        public readonly List<TemplateDef> TemplateList = new List<TemplateDef>();
        public readonly List<LinesDef> LinesList = new List<LinesDef>();
        public readonly List<ItemDef> ItemList = new List<ItemDef>();
        public readonly Dictionary<string, ItemDef> Items = new Dictionary<string, ItemDef>();
        public RulesDef Rules = new RulesDef();
        /// <summary>The bar of pictures (hud.json), null when no pack has one: the game shows no bar.</summary>
        public HudDef Hud;
        /// <summary>The last hud.json read (for the content report).</summary>
        public string HudSource;

        public readonly ValidationReport Report = new ValidationReport();
    }
}
