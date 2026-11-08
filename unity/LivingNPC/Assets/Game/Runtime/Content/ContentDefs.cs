using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LivingNPC.Content
{
    // Plain data classes mirroring the external JSON files (property names are matched case-insensitively).
    // Unknown fields are ignored, missing fields get defaults: friendly to hand/LLM-authored content.
    // Conditions, effects and localized texts stay as JToken and are compiled by ContentCompiler.

    public class PackManifest
    {
        public string Id;
        public string Name;
        public string Author;
        public string Version = "1.0";
        public int Priority = 100;
        public bool Enabled = true;
        public List<string> DependsOn = new List<string>();
        public string Description;

        [JsonIgnore] public string Dir;
    }

    public class LinkDef
    {
        public string Id;
        public string Target;
        public JToken Label;
        public float X, Y, Width = 18, Height = 6.5f; // X, Y = centre (as in the map editor)
        public int? Minutes;
        public JToken Requires;
        public JToken LockedText;
        /// <summary>Initial state: shown on the map / can be walked through. Changed at runtime by showLink, hideLink, openLink, closeLink.</summary>
        public bool Visible = true;
        public bool Open = true;
    }

    public class MapNodeDef
    {
        public string Id;
        public JToken Title;
        public JToken Text;
        public Dictionary<string, string> Backgrounds = new Dictionary<string, string>();
        public List<LinkDef> Links = new List<LinkDef>();
        public List<string> Tags = new List<string>();

        [JsonIgnore] public string Dir;      // absolute directory containing the images
        [JsonIgnore] public string Source;
        public float GraphX, GraphY;         // editor position (from project.json), kept for tools
    }

    public class NpcDef
    {
        public string Id;
        /// <summary>The name the game shows. Omitted: first name and last name.</summary>
        public JToken Name;
        public JToken FirstName;
        public JToken LastName;
        public int? Age;
        /// <summary>"m" (male), "f" (female), "x" (other); used by the {slot:a|b|c} forms of the texts.</summary>
        public string Sex;
        public JToken Description;
        /// <summary>Picks automatic quests (templates/) by itself when it is free.</summary>
        public bool AutoQuests;
        /// <summary>What it wants: an id of rules.json "goals". Templates weigh it.</summary>
        public string Goal;
        /// <summary>What it thinks of the others at the start: npc id or "player" -> -100..100.</summary>
        public Dictionary<string, float> Relations = new Dictionary<string, float>();
        /// <summary>Named places: "home", "work", "school"... usable in schedules as "$home", "$work".</summary>
        public Dictionary<string, string> Places = new Dictionary<string, string>();
        /// <summary>Schedule template ids and/or inline blocks.</summary>
        public List<JToken> Routine = new List<JToken>();
        public Dictionary<string, float> Stats = new Dictionary<string, float>();
        /// <summary>Starting inventory: item id -> count.</summary>
        public Dictionary<string, int> Inventory = new Dictionary<string, int>();
        public List<string> Tags = new List<string>();
        /// <summary>Pictures of the npc on the map and while it speaks. Optional: without them the npc only appears in the list.</summary>
        public NpcImagesDef Images;
        [JsonIgnore] public string Source;
        [JsonIgnore] public string PackDir;     // pictures live in <PackDir>/images
    }

    /// <summary>
    /// A picture placed on the map area, positions and sizes in percent like links (origin top-left; height omitted = keep the
    /// file's proportions). In a per-map override every unset field falls back to the picture it overrides.
    /// </summary>
    public class ImageDef
    {
        public string File;
        public float? X, Y, Width, Height;
        /// <summary>Per-map overrides: node id -> position / size (and optionally another file) used on that map only.</summary>
        public Dictionary<string, ImageDef> Nodes;
    }

    public class NpcImagesDef
    {
        /// <summary>Shown wherever the npc is, unless a picture for what it is doing applies.</summary>
        public ImageDef Default;
        /// <summary>activity id -> picture while the npc does that activity ("sleep", "work"...).</summary>
        public Dictionary<string, ImageDef> Activity = new Dictionary<string, ImageDef>();
        /// <summary>pose name -> picture while the npc speaks to the player; "default" is used when a line names no pose.</summary>
        public Dictionary<string, ImageDef> Talk = new Dictionary<string, ImageDef>();
    }

    /// <summary>An item definition (static data). What each owner holds is runtime state: see InventorySlot.</summary>
    public class ItemDef
    {
        public string Id;
        public JToken Name;
        public JToken Description;
        /// <summary>misc | consumable | equipment | key | quest</summary>
        public string Type = "misc";
        /// <summary>Base price; shops and trade will scale it.</summary>
        public int Value;
        public List<string> Tags = new List<string>();
        [JsonIgnore] public string Source;
    }

    public class BlockDef
    {
        /// <summary>Optional name of the block, to replace or remove it later with the addRoutine / removeRoutine effects.</summary>
        public string Id;
        public JToken Days;          // "weekdays" | "mon" | ["mon","wed"] ; default all
        public string From = "00:00";
        public string To = "24:00";
        public string At;            // node id or "$place"
        public string Activity;
        public int Priority = 0;
    }

    public class ScheduleDef
    {
        public string Id;
        public List<BlockDef> Blocks = new List<BlockDef>();
        [JsonIgnore] public string Source;
    }

    public class StageDef
    {
        public string Id;
        public JToken Text;          // journal text
        public JToken Complete;      // condition
        public JToken Fail;          // condition
        public JToken OnStart;       // effects, when the stage begins
        public JToken OnComplete;    // effects
        public JToken OnFail;        // effects
        public string Next;          // stage id; empty = next in list; "end" = finish quest
    }

    public class QuestDef
    {
        public string Id;
        public JToken Title;
        public JToken Summary;
        public string Giver;
        public JToken AutoStart;     // condition: quest starts by itself when it becomes true
        public JToken OnStart;       // effects
        public JToken OnComplete;    // effects
        public List<StageDef> Stages = new List<StageDef>();
        [JsonIgnore] public string Source;
        [JsonIgnore] public string PackDir;     // images used by showImage effects live in <PackDir>/images
    }

    /// <summary>
    /// An automatic quest: a quest an npc picks and runs by itself (templates/&lt;id&gt;.json). Conditions and effects can name
    /// "self" (the npc) and the slots; texts can say {self}, {slot}, {slot.first}, {slot:his|her}.
    /// </summary>
    public class TemplateDef
    {
        public string Id;
        public JToken Title;
        /// <summary>Who can pick it (condition on self).</summary>
        public JToken When;
        public WeightDef Weight;
        public List<SlotDef> Slots = new List<SlotDef>();
        /// <summary>Days before the npc picks another quest; omitted = rules.json autoQuests.pauseDays.</summary>
        public float? PauseDays;
        /// <summary>group name -> variants [{ "when": cond, "lines": [text, ...] }] (or a single list of lines).</summary>
        public Dictionary<string, JToken> Texts = new Dictionary<string, JToken>();
        public JToken OnStart;
        public JToken OnComplete;
        public JToken OnFail;
        public List<StageDef> Stages = new List<StageDef>();
        [JsonIgnore] public string Source;
        [JsonIgnore] public string PackDir;
    }

    /// <summary>Score of a template: base + stat × k + k for the npc's goal + relation to a slot × k. A score of 0 or less is not picked.</summary>
    public class WeightDef
    {
        public float Base = 10;
        public Dictionary<string, float> Stats = new Dictionary<string, float>();
        public Dictionary<string, float> Goals = new Dictionary<string, float>();
        public Dictionary<string, float> Relations = new Dictionary<string, float>();
    }

    /// <summary>npc | place | node | item (see Simulation.SlotRule).</summary>
    public class SlotDef
    {
        public string Id;
        public string Type = "npc";
        public JToken When;
        public bool Free;
        public string Of;
        public string Place;
        public List<string> Nodes;
        public List<string> Items;
    }

    /// <summary>Shared lines (lines/&lt;id&gt;.json) the templates say with tell(id).</summary>
    public class LinesDef
    {
        public string Id;
        public JToken Variants;
        [JsonIgnore] public string Source;
    }

    public class GoalDef
    {
        public string Id;
        public JToken Name;
    }

    public class AutoQuestsDef
    {
        /// <summary>How often (minutes) a free npc looks for a quest to do.</summary>
        public int CheckMinutes = 60;
        /// <summary>At most this many automatic quests at the same time in the world.</summary>
        public int MaxActive = 3;
        public float PauseDays = 1;
    }

    public class ChoiceDef
    {
        public JToken Text;
        public JToken When;
        public JToken Effects;
    }

    /// <summary>
    /// A line step of an event script (the other steps are effects, "if" and "choices": see ContentCompiler.CompileSteps).
    /// A step may combine commands; they run in the order hide, show, sound, say.
    /// say: text of the line (click / key advances). who: npc id or free speaker name.
    /// show: key of an image (needs file/x/y/width); hide: key of an image shown earlier; sound: a sound effect (file in sfx/).
    /// x, y, width, height are percentages of the map area, like links; height omitted = keep the file's proportions.
    /// </summary>
    public class ScriptStepDef
    {
        public JToken Say;
        public JToken Who;
        public string Show;
        public string Hide;
        /// <summary>say only: pose of the speaking npc (a key of its images.talk).</summary>
        public string Pose;
        public string File;
        public float X, Y;
        public float Width = 30;
        public float? Height;
        /// <summary>show only: stacking order (0 above the map ... 30 above the interface); default 40.</summary>
        public int? Layer;
        /// <summary>A sound effect played once when the script reaches this step: file inside the pack's sfx/ folder.</summary>
        public string Sound;
        /// <summary>sound only: 0 to 1, default 1.</summary>
        public float? Volume;
    }

    public class EventDef
    {
        public string Id;
        /// <summary>enterNode | tick | talk | wake (the player has just woken up; node = where)</summary>
        public string Trigger = "enterNode";
        public string Node;
        public string Npc;
        public JToken When;
        public bool Once;
        public int CooldownMinutes;
        public JToken Text;
        public bool Popup;
        public JToken Effects;
        public List<ChoiceDef> Choices = new List<ChoiceDef>();
        public int Priority;
        /// <summary>
        /// The commands of the event in the order they run: lines, images, sounds, effects ({"do": ...}), conditions
        /// ({"if", "then", "else"}) and choices ({"choices": [{"text", "when", "then"}]}), nested as deep as needed.
        /// "effects" run before it, and the old-style "choices" after it.
        /// </summary>
        public JToken Script;
        [JsonIgnore] public string Source;
        [JsonIgnore] public string PackDir;
    }

    public class StatDef
    {
        public string Id;
        public JToken Name;
        public float Min = 0, Max = 100, Baseline = 50;
        public float DriftPerHour;       // fraction/hour pulled toward Baseline
        public float ConstantPerHour;    // linear change per hour
    }

    public class ActivityDef
    {
        public string Id;
        public JToken Name;
        public Dictionary<string, float> Effects = new Dictionary<string, float>(); // stat -> per hour
    }

    public class DayPartDef
    {
        public string Id;
        public string From = "00:00";
        public string To = "24:00";
        public string Background = "day";    // key in node.backgrounds
    }

    /// <summary>
    /// Going to bed: the "Sleep" button appears in every node tagged "bed" (node.json: "tags": ["bed"]) from <see cref="From"/>
    /// until <see cref="WakeAt"/>, and sleeping skips the time up to the next WakeAt. The world keeps living (routines, rules),
    /// but no dialogue can interrupt the sleep. Effects run on waking, before the "wake" events.
    /// </summary>
    public class SleepDef
    {
        public string From = "20:00";
        public string WakeAt = "07:00";
        public JToken Effects;
    }

    public class RulesDef
    {
        public int TickMinutes = 5;
        public int StartMinute = 8 * 60;
        public int StartDayOfWeek = 0;
        public int DefaultLinkMinutes = 10;
        public string Language = "en";
        public string StartNode;
        /// <summary>Starting inventory of the player: item id -> count.</summary>
        public Dictionary<string, int> PlayerInventory = new Dictionary<string, int>();
        public List<StatDef> Stats = new List<StatDef>();
        public List<ActivityDef> Activities = new List<ActivityDef>();
        public List<DayPartDef> DayParts = new List<DayPartDef>();
        public SleepDef Sleep = new SleepDef();
        public List<GoalDef> Goals = new List<GoalDef>();
        public AutoQuestsDef AutoQuests = new AutoQuestsDef();
    }
}
