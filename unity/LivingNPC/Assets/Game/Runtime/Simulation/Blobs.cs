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

    public enum CondType : byte { Always, All, Any, Not, Time, Day, Date, PlayerAt, NpcAt, NpcWithPlayer, Stat, Flag, Quest, Activity, HasItem, Elapsed, After }
    public enum ItemKind : byte { Misc = 0, Consumable = 1, Equipment = 2, Key = 3, Quest = 4 }
    public enum CmpOp : byte { Eq, Ne, Lt, Le, Gt, Ge }
    public enum FxType : byte { Message, SetFlag, AddFlag, StartQuest, AdvanceQuest, CompleteQuest, FailQuest, ModStat, SetStat, MoveNpc, AdvanceTime, TeleportPlayer, ShowLink, HideLink, OpenLink, CloseLink, GiveItem, TakeItem, ShowImage, HideImage, SetPlace, AddRoutine, RemoveRoutine, SetRoutine, AdvanceTimeTo, PlaySound, StartLoop, StopLoop }
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
    }
}
