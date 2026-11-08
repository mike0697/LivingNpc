using Unity.Entities;

namespace LivingNPC.Simulation
{
    // ------------------------------------------------------------------ enums (stored as bytes in components)

    public enum QuestStatus : byte { NotStarted = 0, Active = 1, Completed = 2, Failed = 3 }
    public enum MsgKind : byte { Info = 0, Event = 1, QuestStarted = 2, QuestUpdated = 3, QuestCompleted = 4, QuestFailed = 5, Travel = 6, Talk = 7 }
    public enum CommandType : byte { Travel = 1, Wait = 2, Choose = 3, Talk = 4, Advance = 5, Sleep = 6 }

    // ------------------------------------------------------------------ singleton entity: "the world"
    // One entity carries everything global; systems fetch it with SystemAPI.GetSingletonEntity<WorldState>().

    public struct WorldState : IComponentData
    {
        public byte Dirty;          // something changed outside the clock: re-evaluate rules once
        public byte Woke;           // pulse: the player has just woken up, not yet processed by rules
        public int TalkNpc;         // npc id the player just talked to (0 none)
        public byte DialogOpen;
        public int DialogEvent;
        public int DialogText;
        public int DialogNpc;
        public int DialogWho;       // text id of a free speaker name (script lines), 0 none
        public int DialogEventIdx;  // index into RuleBlob.Events of the open dialog
        public int DialogStep;      // absolute index into RuleBlob.Script of the line on screen, -1 before the first line
        public uint Version;        // bumped on every visible change; the UI polls this
        public byte DebugUsed;      // the debug console changed this game at least once (kept in the saves)
    }

    public struct ClockState : IComponentData
    {
        public int Now;             // minutes since day 0
        public int Pending;         // minutes still to simulate (travel / wait / advanceTime)
        public int TickMinutes;     // simulation resolution
        public int StartDayOfWeek;
        public byte Ticked;         // true during the frame in which a tick was simulated
        public int TickSerial;
        public int LastStep;        // minutes simulated by the last tick (<= TickMinutes)
        public byte Sleeping;       // the pending minutes are a sleep: dialogues wait until the player wakes up
    }

    public struct NavGraphRef : IComponentData { public Unity.Entities.BlobAssetReference<NavGraphBlob> Value; }
    public struct RuleRef : IComponentData { public Unity.Entities.BlobAssetReference<RuleBlob> Value; }

    // buffers on the world entity -------------------------------------------------------------

    public struct FlagValue : IBufferElementData { public int Key; public float Value; }
    /// <summary>
    /// index == QuestRule index. StartedAt = minute the quest began; StageAt = minute the current stage began, or the quest
    /// ended once it is completed / failed. While the quest is active both feed the "elapsed" condition.
    /// </summary>
    public struct QuestState : IBufferElementData { public byte Status; public int Stage; public int StageAt; public int StartedAt; }
    public struct EventState : IBufferElementData { public int Count; public int LastAt; }                      // index == EventRule index
    public struct GameMessage : IBufferElementData { public int TextId; public MsgKind Kind; public int At; public int Arg; }
    public struct GameCommand : IBufferElementData { public CommandType Type; public int A; public int B; }
    public struct DialogChoice : IBufferElementData { public int ChoiceIndex; public int TextId; }
    /// <summary>An image a Show step of the open dialogue put on screen (Image: index into CompiledContent.Images). Cleared when the dialogue ends.</summary>
    public struct DialogImage : IBufferElementData { public int Key; public int Image; }
    public struct EntityRef : IBufferElementData { public int Id; public Entity Entity; }                       // sorted by Id (npc index)
    /// <summary>A sound effect to play once (index into CompiledContent.Sounds). Not saved: the view plays and clears these every frame.</summary>
    public struct SoundCue : IBufferElementData { public int Sound; }

    // ------------------------------------------------------------------ player

    public struct PlayerState : IComponentData
    {
        public int Node;            // 0 while travelling
        public int PendingNode;     // destination while travelling
        public int FromNode;
        public byte Arrived;        // pulse: arrival not yet processed by rules
    }

    // ------------------------------------------------------------------ npc

    public struct NpcTag : IComponentData { public int Id; public int NameText; }

    public struct NpcLocation : IComponentData
    {
        public int Node;            // current node id, 0 while moving between nodes
        public int NextNode;        // next hop while moving
        public int ArriveAt;        // absolute minute of arrival at NextNode
        public int Goal;            // where the schedule wants the npc to be
        public int HomeNode;        // fallback when no schedule block applies
        public int Activity;        // current activity id (0 none), only while at the goal
        public int OverrideNode;    // forced destination (quests/events)
        public int OverrideUntil;   // absolute minute
    }

    public struct StatValue : IBufferElementData { public int Stat; public float Value; }

    /// <summary>
    /// What an owner (the player or an NPC) holds: one entry per item, count always &gt; 0.
    /// Item is the same hashed id used everywhere else (Ids.Hash of the item id string).
    /// </summary>
    public struct InventorySlot : IBufferElementData { public int Item; public int Count; }

    /// <summary>
    /// Compiled schedule entry. DayMask bit0 = Monday. Blocks crossing midnight are split at compile time.
    /// Place != 0: the block was written as "$place"; the node is looked up in the npc's <see cref="NpcPlace"/> table
    /// when the schedule is evaluated (so setPlace moves every block that uses it), Node is only the fallback.
    /// Tag != 0: the "id" of the block (or of the shared schedule it came from), used by addRoutine / removeRoutine.
    /// </summary>
    public struct ScheduleBlock : IBufferElementData
    {
        public byte DayMask;
        public short Start;
        public short End;
        public int Node;
        public int Activity;
        public short Priority;
        public int Place;
        public int Tag;
    }

    /// <summary>A named place of an npc ("home", "work"...): hashed name -> node id. Changed at runtime by the setPlace effect.</summary>
    public struct NpcPlace : IBufferElementData { public int Key; public int Node; }
}
