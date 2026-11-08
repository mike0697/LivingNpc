using Unity.Entities;

namespace LivingNPC.Simulation
{
    // Immutable, Burst-readable data compiled from the JSON content. Rebuilt on content reload.

    public struct NavEdge
    {
        public int To;          // dense node index
        public int Minutes;
        public int LinkId;
        public int Cond;        // condition root or -1
        public int LockedText;
        public byte Visible, Open;          // initial state, overridden by the flags below
        public int VisibleKey, OpenKey;     // flag keys of the runtime state (see Ids.LinkVisibleKey)
    }

    public struct NavGraphBlob
    {
        public BlobArray<int> NodeIds;      // sorted ascending
        public BlobArray<int> EdgeStart;    // CSR, length n+1
        public BlobArray<NavEdge> Edges;
        public BlobArray<int> NextHop;      // n*n dense index of first hop, -1 unreachable
        public BlobArray<int> Dist;         // n*n minutes, int.MaxValue unreachable

        public int Count => NodeIds.Length;

        public int IndexOf(int id)
        {
            int lo = 0, hi = NodeIds.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int v = NodeIds[mid];
                if (v == id) return mid;
                if (v < id) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        public int Distance(int fromId, int toId)
        {
            int a = IndexOf(fromId), b = IndexOf(toId);
            if (a < 0 || b < 0) return int.MaxValue;
            return Dist[a * NodeIds.Length + b];
        }

        /// <summary>Id of the next node on the shortest path, 0 if unreachable / already there.</summary>
        public int NextHopId(int fromId, int toId, out int minutes)
        {
            minutes = 0;
            int a = IndexOf(fromId), b = IndexOf(toId);
            if (a < 0 || b < 0 || a == b) return 0;
            int hop = NextHop[a * NodeIds.Length + b];
            if (hop < 0) return 0;
            for (int e = EdgeStart[a]; e < EdgeStart[a + 1]; e++)
                if (Edges[e].To == hop) { minutes = Edges[e].Minutes; break; }
            return NodeIds[hop];
        }
    }

    // ------------------------------------------------------------------ rules: conditions / effects / quests / events

    public enum CondType : byte { Always, All, Any, Not, Time, Day, Date, PlayerAt, NpcAt, NpcWithPlayer, Stat, Flag, Quest, Activity, HasItem, Elapsed, After, Relation, Goal, Age, Sex, Chance, Doing, Did }
    public enum ItemKind : byte { Misc = 0, Consumable = 1, Equipment = 2, Key = 3, Quest = 4 }
    public enum CmpOp : byte { Eq, Ne, Lt, Le, Gt, Ge }
    public enum FxType : byte { Message, SetFlag, AddFlag, StartQuest, AdvanceQuest, CompleteQuest, FailQuest, ModStat, SetStat, MoveNpc, AdvanceTime, TeleportPlayer, ShowLink, HideLink, OpenLink, CloseLink, GiveItem, TakeItem, ShowImage, HideImage, SetPlace, AddRoutine, RemoveRoutine, SetRoutine, AdvanceTimeTo, PlaySound, StartLoop, StopLoop, Tell, ModRelation, SetRelation, SetGoal, SetAutoQuests, StartAuto, StopAuto }
    public enum TriggerType : byte { EnterNode, Tick, Talk, Wake }
    public enum StepKind : byte { Say, Show, Hide, Sound, Fx, If, Jump, Choice }

    /// <summary>
    /// Condition trees are flattened: children always have a lower index than their parent and every subtree is a
    /// contiguous range [Lo..root], so evaluation is an iterative loop (no recursion, Burst friendly).
    /// </summary>
    public struct CondNode
    {
        public CondType Type;
        public CmpOp Op;
        public int A, B, C;
        public float F;
        public int ChildStart, ChildCount;   // into RuleBlob.ChildRefs
        public int Lo;
    }

    public struct FxNode
    {
        public FxType Type;
        public int A, B;
        public int C;
        public float F;
    }

    /// <summary>Blocks an addRoutine / setRoutine effect puts into an npc: a range of RuleBlob.RoutineBlocks.</summary>
    public struct RoutineSet { public int Start, Count; }

    public struct ItemRule { public int Id; public ItemKind Kind; public int Value; public int NameText, DescText; }

    public struct StatRule { public int Id; public float Min, Max, Baseline, DriftPerHour, ConstantPerHour; }
    public struct ActivityFx { public int Activity; public int Stat; public float PerHour; }

    public struct StageRule
    {
        public int Id, TextId;
        public int CompleteCond, FailCond;
        public int StartFxStart, StartFxCount;      // run when the stage begins
        public int FxStart, FxCount;
        public int FailFxStart, FailFxCount;
        public int Next;                    // stage index, -1 = end of quest
    }

    public struct QuestRule
    {
        public int Id, TitleText;
        public int AutoStartCond;
        public int StartFxStart, StartFxCount;
        public int CompleteFxStart, CompleteFxCount;
        public int StageStart, StageCount;
    }

    /// <summary>
    /// One command of an event's program, run in order by RuleRuntime.Run. Jumps only go forward, so a program always ends.
    /// Say: TextId/WhoNpc/WhoText/Pose, waits for a click. Show: Key + Image. Hide: Key. Sound: Sound.
    /// Fx: effects [Start, Start + Count). If: when Cond is false, goes to Target. Jump: goes to Target.
    /// Choice: the options are ChoiceRules [Start, Start + Count), each going to its Target; with no option available it goes to Target.
    /// Targets are absolute indexes into RuleBlob.Script; the end of the event's program means "close".
    /// </summary>
    public struct ScriptStep
    {
        public StepKind Kind;
        public int TextId, WhoNpc, WhoText;
        public int Key, Image;
        public int Sound;       // Sound: index into CompiledContent.Sounds, played once when the program reaches it
        public int Pose;        // Say: hashed pose name of the speaker's images.talk (0 = default)
        public int Cond, Target;
        public int Start, Count;
    }

    /// <summary>An option of a Choice step: shown when Cond holds; picking it goes on at Target (absolute index into RuleBlob.Script).</summary>
    public struct ChoiceRule { public int TextId; public int Cond; public int Target; }

    public struct EventRule
    {
        public int Id;
        public TriggerType Trigger;
        public int TriggerNode, TriggerNpc;
        public int Cond;
        public byte Once;
        public byte HasDialog;      // the program has a line or a choice: the event waits while another dialogue is open or the player sleeps
        public int CooldownMin;
        public int TextId;          // written to the log when the event runs
        public int ScriptStart, ScriptCount;
        public int NpcForDialog;
    }

    // ------------------------------------------------------------------ automatic quests (templates an npc picks and runs by itself)

    /// <summary>
    /// The arguments of a template's conditions and effects that name its own characters, places and items: "self" (slot 0,
    /// the npc that runs it) and the template's slots (1..). They are stored in place of the id and replaced at run time by
    /// the values of the running instance (RuleRuntime.Bind). Ids.Hash never returns a value of this range.
    /// </summary>
    public static class BindRef
    {
        public const int Base = int.MinValue;
        /// <summary>At most this many values: self + 6 slots (FixedList32Bytes&lt;int&gt; holds 7).</summary>
        public const int Max = 7;
        /// <summary>What an argument bound to nothing becomes: no npc, place or item has this id.</summary>
        public const int None = Base + 15;
        public const int Reserved = 16;
        public static int Of(int slot) => Base + slot;
        public static bool Is(int v) => v >= Base && v < Base + Max;
        public static int Slot(int v) => v - Base;
    }

    public enum SlotKind : byte { Npc, Place, Node, Item }

    /// <summary>
    /// A slot of a template, filled when an npc picks it. Npc / Node / Item: a random candidate (Node and Item from the list
    /// [ListStart, +ListCount) of RuleBlob.ListValues, all of them when the list is empty) for which Cond holds, with the
    /// candidate bound to the slot itself ("it"). Npc: never the npc itself or another slot; Free: not busy with a quest of
    /// its own nor bound to someone else's. Place: the named place Place of the npc bound to Of.
    /// </summary>
    public struct SlotRule
    {
        public int Id;
        public SlotKind Kind;
        public byte Free;
        public int Cond;
        public int Of, Place;
        public int ListStart, ListCount;
    }

    public enum WeightKind : byte { Stat, Goal, Relation }

    /// <summary>One term of a template's score: stat of self × K, K when self has the goal Key, relation self → slot Key × K.</summary>
    public struct WeightTerm { public WeightKind Kind; public int Key; public float K; }

    public struct TemplateRule
    {
        public int Id, TitleText;
        public int Cond;                    // who can pick it (self bound), -1 = anyone
        public float WeightBase;
        public int WeightStart, WeightCount;
        public int SlotStart, SlotCount;
        public int StartFxStart, StartFxCount;
        public int CompleteFxStart, CompleteFxCount;
        public int FailFxStart, FailFxCount;
        public int StageStart, StageCount;  // into RuleBlob.Stages
        public int PauseMin;                // pause after it ends; -1 = rules.json autoQuests.pauseDays
        public int TalkGroup;               // text group said when the player talks to the npc while it runs this, -1 none
    }

    /// <summary>A group of lines (a template's "texts" entry or a shared "lines" file): the first variant whose condition holds says one of its lines.</summary>
    public struct TextGroupRule { public int Id; public int VariantStart, VariantCount; }
    public struct TextVariantRule { public int Cond; public int LineStart, LineCount; }

    public struct RuleBlob
    {
        public BlobArray<CondNode> Conds;
        public BlobArray<int> ChildRefs;
        public BlobArray<FxNode> Fx;
        public BlobArray<StatRule> Stats;
        public BlobArray<ActivityFx> ActivityEffects;
        public BlobArray<QuestRule> Quests;
        public BlobArray<StageRule> Stages;
        public BlobArray<EventRule> Events;     // sorted by priority, highest first
        public BlobArray<ChoiceRule> Choices;
        public BlobArray<ScriptStep> Script;
        public BlobArray<ItemRule> Items;       // order of ContentDatabase.ItemList
        public BlobArray<ScheduleBlock> RoutineBlocks;      // blocks of routine-changing effects
        public BlobArray<RoutineSet> RoutineSets;           // FxNode.B of addRoutine / setRoutine indexes this
        public int DefaultTalkText;

        // going to bed (rules.json "sleep"): minute-of-day window, waking minute, effects applied on waking, nodes with a bed
        public int SleepFrom, SleepWake;
        public int SleepFxStart, SleepFxCount;
        public BlobArray<int> BedNodes;         // node ids, sorted ascending

        // automatic quests
        public BlobArray<TemplateRule> Templates;
        public BlobArray<SlotRule> Slots;
        public BlobArray<WeightTerm> Weights;
        public BlobArray<int> ListValues;
        public BlobArray<TextGroupRule> TextGroups;
        public BlobArray<TextVariantRule> TextVariants;
        public BlobArray<int> TextLines;
        public int AutoCheckMinutes;            // how often free npcs look for a quest to do
        public int AutoMaxActive;               // at most this many running at once in the whole world
        public int AutoPauseMin;                // default pause after a quest ends
    }
}
