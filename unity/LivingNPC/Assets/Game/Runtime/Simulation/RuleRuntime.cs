using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace LivingNPC.Simulation
{
    /// <summary>
    /// Evaluates compiled conditions and applies compiled effects against the live ECS world.
    /// Pure struct + EntityManager access: usable from Burst-compiled ISystems on the main thread
    /// and from managed code (tests, tools) alike.
    /// </summary>
    public partial struct RuleRuntime
    {
        public EntityManager Em;
        public Entity World;
        public Entity Player;
        public BlobAssetReference<RuleBlob> Rules;
        public BlobAssetReference<NavGraphBlob> Nav;

        public static RuleRuntime Create(EntityManager em, Entity world, Entity player)
        {
            return new RuleRuntime
            {
                Em = em, World = world, Player = player,
                Rules = em.GetComponentData<RuleRef>(world).Value,
                Nav = em.GetComponentData<NavGraphRef>(world).Value,
            };
        }

        // ------------------------------------------------------------------ lookups

        public ClockState Clock => Em.GetComponentData<ClockState>(World);
        public PlayerState PlayerData => Em.GetComponentData<PlayerState>(Player);

        public Entity FindNpc(int id)
        {
            var idx = Em.GetBuffer<EntityRef>(World);
            int lo = 0, hi = idx.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int v = idx[mid].Id;
                if (v == id) return idx[mid].Entity;
                if (v < id) lo = mid + 1; else hi = mid - 1;
            }
            return Entity.Null;
        }

        Entity TargetEntity(int target) => target == 0 ? Player : FindNpc(target);

        public float GetStat(int target, int stat)
        {
            var e = TargetEntity(target);
            if (e == Entity.Null) return 0;
            var buf = Em.GetBuffer<StatValue>(e);
            for (int i = 0; i < buf.Length; i++) if (buf[i].Stat == stat) return buf[i].Value;
            return 0;
        }

        public void SetStat(int target, int stat, float value)
        {
            var e = TargetEntity(target);
            if (e == Entity.Null) return;
            ref var rules = ref Rules.Value;
            for (int i = 0; i < rules.Stats.Length; i++)
                if (rules.Stats[i].Id == stat) { value = math.clamp(value, rules.Stats[i].Min, rules.Stats[i].Max); break; }
            var buf = Em.GetBuffer<StatValue>(e);
            for (int i = 0; i < buf.Length; i++)
                if (buf[i].Stat == stat) { buf[i] = new StatValue { Stat = stat, Value = value }; Touch(); return; }
            buf.Add(new StatValue { Stat = stat, Value = value });
            Touch();
        }

        // ------------------------------------------------------------------ inventory

        /// <summary>How many of <paramref name="item"/> the target holds (target 0 = player, otherwise an npc id).</summary>
        public int GetItemCount(int target, int item)
        {
            var e = TargetEntity(target);
            if (e == Entity.Null || !Em.HasBuffer<InventorySlot>(e)) return 0;
            var buf = Em.GetBuffer<InventorySlot>(e);
            for (int i = 0; i < buf.Length; i++) if (buf[i].Item == item) return buf[i].Count;
            return 0;
        }

        /// <summary>
        /// Adds (delta &gt; 0) or removes (delta &lt; 0) items. Removal is clamped to what is held; empty entries
        /// are dropped. Returns the signed amount actually applied.
        /// </summary>
        public int AddItem(int target, int item, int delta)
        {
            var e = TargetEntity(target);
            if (e == Entity.Null || !Em.HasBuffer<InventorySlot>(e) || delta == 0) return 0;
            var buf = Em.GetBuffer<InventorySlot>(e);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].Item != item) continue;
                int applied = math.max(delta, -buf[i].Count);
                int count = buf[i].Count + applied;
                if (count <= 0) buf.RemoveAt(i); else buf[i] = new InventorySlot { Item = item, Count = count };
                if (applied != 0) Touch();
                return applied;
            }
            if (delta < 0) return 0;
            buf.Add(new InventorySlot { Item = item, Count = delta });
            Touch();
            return delta;
        }

        public float GetFlag(int key)
        {
            var buf = Em.GetBuffer<FlagValue>(World);
            for (int i = 0; i < buf.Length; i++) if (buf[i].Key == key) return buf[i].Value;
            return 0;
        }

        public void SetFlag(int key, float value)
        {
            var buf = Em.GetBuffer<FlagValue>(World);
            for (int i = 0; i < buf.Length; i++)
                if (buf[i].Key == key) { buf[i] = new FlagValue { Key = key, Value = value }; Touch(); return; }
            buf.Add(new FlagValue { Key = key, Value = value });
            Touch();
        }

        /// <summary>Runtime state of a link flag: 1 forced on, 2 forced off, otherwise the initial value from the content.</summary>
        bool LinkState(int key, byte initial)
        {
            float v = GetFlag(key);
            return v == 1 ? true : v == 2 ? false : initial != 0;
        }

        public bool LinkVisible(in NavEdge e) => LinkState(e.VisibleKey, e.Visible);
        public bool LinkOpen(in NavEdge e) => LinkState(e.OpenKey, e.Open);

        public void Touch()
        {
            var ws = Em.GetComponentData<WorldState>(World);
            ws.Version++;
            ws.Dirty = 1;
            Em.SetComponentData(World, ws);
        }

        public void PushMessage(int textId, MsgKind kind, int arg = 0)
        {
            var buf = Em.GetBuffer<GameMessage>(World);
            buf.Add(new GameMessage { TextId = textId, Kind = kind, At = Clock.Now, Arg = arg, Bind = Bind });
            if (buf.Length > 200) buf.RemoveRange(0, buf.Length - 150);
            Touch();
        }

        // ------------------------------------------------------------------ conditions

        static bool Compare(float a, CmpOp op, float b)
        {
            switch (op)
            {
                case CmpOp.Eq: return math.abs(a - b) < 0.0001f;
                case CmpOp.Ne: return math.abs(a - b) >= 0.0001f;
                case CmpOp.Lt: return a < b;
                case CmpOp.Le: return a <= b;
                case CmpOp.Gt: return a > b;
                default: return a >= b;
            }
        }

        /// <summary>Evaluates the condition tree rooted at <paramref name="root"/>. -1 means "no condition" (true).</summary>
        public bool Eval(int root)
        {
            if (root < 0) return true;
            var res = new FixedList512Bytes<byte>();
            return EvalTree(root, ref res);
        }

        /// <summary>
        /// Like <see cref="Eval"/>, and leaves in <paramref name="res"/> the result of every node of the tree: node i
        /// (from Conds[root].Lo to root) at res[i - Lo], 1 true / 0 false. The debug panel shows them branch by branch.
        /// </summary>
        public bool EvalTree(int root, ref FixedList512Bytes<byte> res)
        {
            ref var rules = ref Rules.Value;
            int lo = rules.Conds[root].Lo;
            int len = root - lo + 1;
            res.Length = len;

            var clock = Clock;
            var player = PlayerData;

            for (int i = lo; i <= root; i++)
            {
                ref var c = ref rules.Conds[i];
                bool r = false;
                switch (c.Type)
                {
                    case CondType.Always: r = true; break;
                    case CondType.All:
                        r = true;
                        for (int k = 0; k < c.ChildCount; k++) if (res[rules.ChildRefs[c.ChildStart + k] - lo] == 0) { r = false; break; }
                        break;
                    case CondType.Any:
                        for (int k = 0; k < c.ChildCount; k++) if (res[rules.ChildRefs[c.ChildStart + k] - lo] != 0) { r = true; break; }
                        break;
                    case CondType.Not:
                        r = c.ChildCount > 0 && res[rules.ChildRefs[c.ChildStart] - lo] == 0;
                        break;
                    case CondType.Time:
                        {
                            int m = clock.Now % GameTime.MinutesPerDay;
                            r = c.A == c.B || (c.A < c.B ? (m >= c.A && m < c.B) : (m >= c.A || m < c.B));
                            break;
                        }
                    case CondType.Day:
                        r = ((c.A >> GameTime.DayOfWeek(clock.Now, clock.StartDayOfWeek)) & 1) != 0;
                        break;
                    case CondType.Date:
                        {
                            int day = clock.Now / GameTime.MinutesPerDay;
                            r = day >= c.A && day <= c.B;
                            break;
                        }
                    case CondType.PlayerAt: r = player.Node == R(c.A); break;
                    case CondType.NpcAt:
                        {
                            var e = FindNpc(R(c.A));
                            r = e != Entity.Null && Em.GetComponentData<NpcLocation>(e).Node == R(c.B);
                            break;
                        }
                    case CondType.NpcWithPlayer:
                        {
                            var e = FindNpc(R(c.A));
                            r = e != Entity.Null && player.Node != 0 && Em.GetComponentData<NpcLocation>(e).Node == player.Node;
                            break;
                        }
                    case CondType.Stat: r = Compare(GetStat(R(c.A), c.B), c.Op, c.F); break;
                    case CondType.Flag: r = Compare(GetFlag(c.A), c.Op, c.F); break;
                    case CondType.HasItem: r = Compare(GetItemCount(R(c.A), R(c.B)), c.Op, c.F); break;
                    case CondType.Quest:
                        {
                            var qs = Em.GetBuffer<QuestState>(World)[c.A];
                            r = (c.B < 0 || qs.Status == c.B) && (c.C < 0 || (qs.Status == (byte)QuestStatus.Active && qs.Stage == c.C));
                            break;
                        }
                    case CondType.Elapsed:
                        {
                            // A = -1: the automatic quest being evaluated (its stage / its start)
                            if (c.A < 0) { r = Bind.Length > 0 && clock.Now - (c.B == 1 ? CtxStartedAt : CtxStageAt) >= c.C; break; }
                            // A = quest, B = 0 since the stage began / 1 since the quest began, C = minutes; only while the quest is running
                            var qs = Em.GetBuffer<QuestState>(World)[c.A];
                            r = qs.Status == (byte)QuestStatus.Active && clock.Now - (c.B == 1 ? qs.StartedAt : qs.StageAt) >= c.C;
                            break;
                        }
                    case CondType.After: r = clock.Now >= c.A; break;      // A = absolute minute
                    case CondType.Activity:
                        {
                            var e = FindNpc(R(c.A));
                            r = e != Entity.Null && Em.GetComponentData<NpcLocation>(e).Activity == c.B;
                            break;
                        }
                    case CondType.Relation: r = Compare(GetRelation(R(c.A), R(c.B)), c.Op, c.F); break;
                    case CondType.Goal:
                        {
                            var e = FindNpc(R(c.A));
                            if (e == Entity.Null || !Em.HasComponent<NpcAuto>(e)) break;
                            int goal = Em.GetComponentData<NpcAuto>(e).Goal;
                            r = c.B == 0 ? goal != 0 : goal == c.B;
                            break;
                        }
                    case CondType.Age:
                        {
                            var e = FindNpc(R(c.A));
                            r = e != Entity.Null && Em.HasComponent<NpcInfo>(e) && Compare(Em.GetComponentData<NpcInfo>(e).Age, c.Op, c.F);
                            break;
                        }
                    case CondType.Sex:
                        {
                            var e = FindNpc(R(c.A));
                            r = e != Entity.Null && Em.HasComponent<NpcInfo>(e) && Em.GetComponentData<NpcInfo>(e).Sex == c.B;
                            break;
                        }
                    case CondType.Chance: r = Bind.Length > 0 && CtxRoll < c.A; break;
                    case CondType.Doing:
                        {
                            var e = FindNpc(R(c.A));
                            if (e == Entity.Null || !Em.HasComponent<NpcAuto>(e)) break;
                            var a = Em.GetComponentData<NpcAuto>(e);
                            r = a.Template != 0 && (c.B == 0 || a.Template == c.B) && (c.C < 0 || a.Stage == c.C);
                            break;
                        }
                    case CondType.Did: r = Remembers(R(c.A), c.B, c.C, c.F, clock.Now); break;
                    case CondType.Busy: r = IsBusy(R(c.A)); break;
                }
                res[i - lo] = (byte)(r ? 1 : 0);
            }
            return res[len - 1] != 0;
        }

        // ------------------------------------------------------------------ effects

        public void Apply(int start, int count)
        {
            for (int i = 0; i < count; i++) ApplyOne(Rules.Value.Fx[start + i]);
        }

        /// <summary>Runs one effect. The compiled content's effects go through <see cref="Apply"/>; the debug console builds its own.</summary>
        public void ApplyOne(in FxNode fx)
        {
            switch (fx.Type)
            {
                case FxType.Message: PushMessage(fx.A, MsgKind.Event); break;
                case FxType.SetFlag: SetFlag(fx.A, fx.F); break;
                case FxType.AddFlag: SetFlag(fx.A, GetFlag(fx.A) + fx.F); break;
                case FxType.StartQuest: StartQuest(fx.A); break;
                case FxType.AdvanceQuest: AdvanceQuest(fx.A); break;
                case FxType.CompleteQuest: CompleteQuest(fx.A); break;
                case FxType.FailQuest: FailQuest(fx.A, false); break;
                case FxType.ModStat: SetStat(R(fx.A), fx.B, GetStat(R(fx.A), fx.B) + fx.F); break;
                case FxType.SetStat: SetStat(R(fx.A), fx.B, fx.F); break;
                case FxType.GiveItem: AddItem(R(fx.A), R(fx.B), (int)fx.F); break;
                case FxType.TakeItem: AddItem(R(fx.A), R(fx.B), -(int)fx.F); break;
                case FxType.MoveNpc:
                    {
                        var e = FindNpc(R(fx.A));
                        if (e == Entity.Null) break;
                        var loc = Em.GetComponentData<NpcLocation>(e);
                        loc.OverrideNode = R(fx.B);
                        loc.OverrideUntil = Clock.Now + (int)fx.F;
                        Em.SetComponentData(e, loc);
                        Touch();
                        break;
                    }
                case FxType.SetPlace: SetNpcPlace(R(fx.A), fx.B, R(fx.C), fx.F != 0); break;
                case FxType.AddRoutine: ApplyRoutineSet(fx.A, fx.B, false); break;
                case FxType.SetRoutine: ApplyRoutineSet(fx.A, fx.B, true); break;
                case FxType.RemoveRoutine: RemoveRoutineTag(R(fx.A), fx.B); break;
                case FxType.Tell: Tell(fx); break;
                case FxType.ModRelation: SetRelation(R(fx.A), R(fx.B), GetRelation(R(fx.A), R(fx.B)) + fx.F); break;
                case FxType.SetRelation: SetRelation(R(fx.A), R(fx.B), fx.F); break;
                case FxType.SetGoal: SetNpcGoal(R(fx.A), fx.B); break;
                case FxType.SetAutoQuests: SetAutoEnabled(R(fx.A), fx.B != 0); break;
                case FxType.StartAuto: StartAuto(R(fx.A), fx.B); break;
                case FxType.StopAuto: StopAuto(R(fx.A)); break;
                case FxType.AdvanceTime:
                    {
                        var clock = Clock;
                        clock.Pending += fx.B;
                        Em.SetComponentData(World, clock);
                        break;
                    }
                case FxType.AdvanceTimeTo:
                    {
                        var clock = Clock;
                        int ahead = MinutesUntil(clock.Now + clock.Pending, fx.B);
                        if (ahead > 0) { clock.Pending += ahead; Em.SetComponentData(World, clock); }
                        break;
                    }
                case FxType.ShowLink:
                case FxType.OpenLink: SetFlag(fx.A, 1); break;
                case FxType.HideLink:
                case FxType.CloseLink: SetFlag(fx.A, 2); break;
                case FxType.ShowImage: SetFlag(fx.A, fx.B + 1); break;      // value = compiled image index + 1
                case FxType.HideImage: SetFlag(fx.A, 0); break;
                case FxType.PlaySound: PushCue(fx.A); break;
                case FxType.StartLoop: SetFlag(fx.A, fx.B + 1); break;      // value = compiled sound index + 1; the view starts / changes the loop
                case FxType.StopLoop: SetFlag(fx.A, 0); break;
                case FxType.TeleportPlayer:
                    {
                        var p = PlayerData;
                        p.FromNode = p.Node; p.Node = R(fx.A); p.PendingNode = 0; p.Arrived = 1;
                        Em.SetComponentData(Player, p);
                        Touch();
                        break;
                    }
            }
        }

        // ------------------------------------------------------------------ sound effects

        const int MaxPendingCues = 32;

        /// <summary>Queues a one-shot sound for the view (the oldest ones are dropped if nobody is listening, e.g. a headless run).</summary>
        void PushCue(int sound)
        {
            if (!Em.HasBuffer<SoundCue>(World)) return;
            var cues = Em.GetBuffer<SoundCue>(World);
            if (cues.Length >= MaxPendingCues) cues.RemoveAt(0);
            cues.Add(new SoundCue { Sound = sound });
        }

        // ------------------------------------------------------------------ npc routines and places

        /// <summary>
        /// Points the npc's named place at another node ("home", "work"...). Every block written as "$place" follows;
        /// the npc re-plans on the next tick. <paramref name="isHome"/> also moves where it goes when no block applies.
        /// </summary>
        public void SetNpcPlace(int npc, int placeKey, int node, bool isHome)
        {
            var e = FindNpc(npc);
            if (e == Entity.Null) return;
            var places = Em.GetBuffer<NpcPlace>(e);
            bool found = false;
            for (int i = 0; i < places.Length && !found; i++)
                if (places[i].Key == placeKey) { places[i] = new NpcPlace { Key = placeKey, Node = node }; found = true; }
            if (!found) places.Add(new NpcPlace { Key = placeKey, Node = node });
            if (isHome)
            {
                var loc = Em.GetComponentData<NpcLocation>(e);
                loc.HomeNode = node;
                Em.SetComponentData(e, loc);
            }
            Touch();
        }

        /// <summary>
        /// Puts the blocks of routine set <paramref name="setIndex"/> into the npc: replacing its whole routine, or added
        /// to it. Added blocks that carry an id first remove the blocks that already have that id, so adding the same id
        /// again edits it.
        /// </summary>
        public void ApplyRoutineSet(int npc, int setIndex, bool replaceAll)
        {
            var e = FindNpc(npc);
            if (e == Entity.Null) return;
            ref var rules = ref Rules.Value;
            if (setIndex < 0 || setIndex >= rules.RoutineSets.Length) return;
            var set = rules.RoutineSets[setIndex];
            var blocks = Em.GetBuffer<ScheduleBlock>(e);
            if (replaceAll) blocks.Clear();
            else
                for (int k = 0; k < set.Count; k++)
                {
                    int tag = rules.RoutineBlocks[set.Start + k].Tag;
                    if (tag != 0) RemoveTagged(blocks, tag);
                }
            for (int k = 0; k < set.Count; k++) blocks.Add(rules.RoutineBlocks[set.Start + k]);
            Touch();
        }

        /// <summary>Removes every block with this id (the id of a block, or of the shared schedule it came from).</summary>
        public void RemoveRoutineTag(int npc, int tag)
        {
            var e = FindNpc(npc);
            if (e == Entity.Null || tag == 0) return;
            RemoveTagged(Em.GetBuffer<ScheduleBlock>(e), tag);
            Touch();
        }

        static void RemoveTagged(DynamicBuffer<ScheduleBlock> blocks, int tag)
        {
            for (int i = blocks.Length - 1; i >= 0; i--)
                if (blocks[i].Tag == tag) blocks.RemoveAt(i);
        }

        // ------------------------------------------------------------------ sleep

        /// <summary>Minutes from absolute minute <paramref name="from"/> to the next time the clock shows <paramref name="minuteOfDay"/>; 0 if it already does.</summary>
        public static int MinutesUntil(int from, int minuteOfDay)
            => (((minuteOfDay - from % GameTime.MinutesPerDay) % GameTime.MinutesPerDay) + GameTime.MinutesPerDay) % GameTime.MinutesPerDay;

        /// <summary>
        /// Whether the player can go to bed in <paramref name="node"/> at absolute minute <paramref name="now"/>: the node has a bed
        /// and the clock is inside the sleep window (rules.json sleep.from until sleep.wakeAt, across midnight).
        /// <paramref name="minutes"/> is how long the sleep lasts: up to the next waking time.
        /// </summary>
        public static bool CanSleep(BlobAssetReference<RuleBlob> blob, int node, int now, out int minutes)
        {
            minutes = 0;
            if (node == 0 || !blob.IsCreated) return false;
            ref var rules = ref blob.Value;
            int lo = 0, hi = rules.BedNodes.Length - 1;
            bool bed = false;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int v = rules.BedNodes[mid];
                if (v == node) { bed = true; break; }
                if (v < node) lo = mid + 1; else hi = mid - 1;
            }
            if (!bed) return false;

            int m = now % GameTime.MinutesPerDay;
            int from = rules.SleepFrom, wake = rules.SleepWake;
            bool inWindow = from == wake || (from < wake ? (m >= from && m < wake) : (m >= from || m < wake));
            if (!inWindow) return false;
            minutes = MinutesUntil(now, wake);
            if (minutes == 0) minutes = GameTime.MinutesPerDay;
            return true;
        }

        // ------------------------------------------------------------------ quests

        public bool StartQuest(int qi)
        {
            var states = Em.GetBuffer<QuestState>(World);
            if (qi < 0 || qi >= states.Length || states[qi].Status != (byte)QuestStatus.NotStarted) return false;
            states[qi] = new QuestState { Status = (byte)QuestStatus.Active, Stage = 0, StageAt = Clock.Now, StartedAt = Clock.Now };
            var q = Rules.Value.Quests[qi];
            PushMessage(q.TitleText, MsgKind.QuestStarted);
            if (q.StageCount > 0) PushMessage(Rules.Value.Stages[q.StageStart].TextId, MsgKind.QuestUpdated);
            Apply(q.StartFxStart, q.StartFxCount);
            StageStarted(qi, 0);
            return true;
        }

        public void AdvanceQuest(int qi)
        {
            var states = Em.GetBuffer<QuestState>(World);
            if (qi < 0 || qi >= states.Length || states[qi].Status != (byte)QuestStatus.Active) return;
            var q = Rules.Value.Quests[qi];
            var sr = Rules.Value.Stages[q.StageStart + states[qi].Stage];
            Apply(sr.FxStart, sr.FxCount);

            states = Em.GetBuffer<QuestState>(World);        // effects may have touched it
            if (states[qi].Status != (byte)QuestStatus.Active) return;
            if (sr.Next < 0) { CompleteQuest(qi); return; }
            states[qi] = new QuestState { Status = (byte)QuestStatus.Active, Stage = sr.Next, StageAt = Clock.Now, StartedAt = states[qi].StartedAt };
            PushMessage(Rules.Value.Stages[q.StageStart + sr.Next].TextId, MsgKind.QuestUpdated);
            StageStarted(qi, sr.Next);
        }

        /// <summary>The onStart effects of a stage that has just begun, if the quest is still at that stage (earlier effects may have moved it).</summary>
        void StageStarted(int qi, int stage)
        {
            var st = Em.GetBuffer<QuestState>(World)[qi];
            var q = Rules.Value.Quests[qi];
            if (st.Status != (byte)QuestStatus.Active || st.Stage != stage || stage >= q.StageCount) return;
            var sr = Rules.Value.Stages[q.StageStart + stage];
            Apply(sr.StartFxStart, sr.StartFxCount);
        }

        public void CompleteQuest(int qi)
        {
            var states = Em.GetBuffer<QuestState>(World);
            if (qi < 0 || qi >= states.Length) return;
            var st = states[qi];
            if (st.Status == (byte)QuestStatus.Completed || st.Status == (byte)QuestStatus.Failed) return;
            st.Status = (byte)QuestStatus.Completed;
            st.StageAt = Clock.Now;
            states[qi] = st;
            var q = Rules.Value.Quests[qi];
            PushMessage(q.TitleText, MsgKind.QuestCompleted);
            Apply(q.CompleteFxStart, q.CompleteFxCount);
        }

        public void FailQuest(int qi, bool runStageFailFx)
        {
            var states = Em.GetBuffer<QuestState>(World);
            if (qi < 0 || qi >= states.Length || states[qi].Status != (byte)QuestStatus.Active) return;
            var st = states[qi];
            var q = Rules.Value.Quests[qi];
            var sr = Rules.Value.Stages[q.StageStart + st.Stage];
            st.Status = (byte)QuestStatus.Failed;
            st.StageAt = Clock.Now;
            states[qi] = st;
            PushMessage(q.TitleText, MsgKind.QuestFailed);
            if (runStageFailFx) Apply(sr.FailFxStart, sr.FailFxCount);
        }

        /// <summary>Auto-start / auto-fail / auto-complete pass. Returns true if anything changed.</summary>
        public bool EvaluateQuests()
        {
            bool changed = false;
            int n = Rules.Value.Quests.Length;
            for (int qi = 0; qi < n; qi++)
            {
                var st = Em.GetBuffer<QuestState>(World)[qi];
                var q = Rules.Value.Quests[qi];
                if (st.Status == (byte)QuestStatus.NotStarted)
                {
                    if (q.AutoStartCond >= 0 && Eval(q.AutoStartCond)) changed |= StartQuest(qi);
                }
                else if (st.Status == (byte)QuestStatus.Active)
                {
                    var sr = Rules.Value.Stages[q.StageStart + st.Stage];
                    if (sr.FailCond >= 0 && Eval(sr.FailCond)) { FailQuest(qi, true); changed = true; }
                    else if (sr.CompleteCond >= 0 && Eval(sr.CompleteCond)) { AdvanceQuest(qi); changed = true; }
                }
            }
            return changed;
        }

        // ------------------------------------------------------------------ dialog

        /// <summary>
        /// Runs event <paramref name="eventIndex"/>: its program goes on (effects, conditions, images, sounds) until the first
        /// line or choice, which opens the dialogue; a program without one ends at once and nothing is shown.
        /// </summary>
        public void RunEvent(int eventIndex)
        {
            ref var ev = ref Rules.Value.Events[eventIndex];
            // an event with no line and no choice never opens the dialogue: it must not touch one another event opened
            if (ev.HasDialog == 0) { RunSilent(eventIndex); return; }
            var ws = Em.GetComponentData<WorldState>(World);
            ws.DialogEvent = ev.Id; ws.DialogEventIdx = eventIndex; ws.DialogNpc = ev.NpcForDialog;
            ws.DialogText = 0; ws.DialogWho = 0; ws.DialogStep = -1;
            Em.SetComponentData(World, ws);
            Em.GetBuffer<DialogImage>(World).Clear();
            Em.GetBuffer<DialogChoice>(World).Clear();
            Run(eventIndex, ev.ScriptStart);
        }

        /// <summary>The program of an event without dialogue: effects, sounds and conditions, leaving the dialogue state alone.</summary>
        void RunSilent(int eventIndex)
        {
            int pc = Rules.Value.Events[eventIndex].ScriptStart;
            int end = pc + Rules.Value.Events[eventIndex].ScriptCount;
            for (int guard = 0; pc < end && guard < 100000; guard++)
            {
                var s = Rules.Value.Script[pc];
                switch (s.Kind)
                {
                    case StepKind.Sound: PushCue(s.Sound); break;
                    case StepKind.Fx: Apply(s.Start, s.Count); break;
                    case StepKind.If:
                        if (!Eval(s.Cond)) { pc = s.Target; continue; }
                        break;
                    case StepKind.Jump: pc = s.Target; continue;
                }
                pc++;
            }
        }

        /// <summary>Runs the program of the event from <paramref name="pc"/> until a line or a choice waits for the player, or the end closes the dialogue.</summary>
        void Run(int eventIndex, int pc)
        {
            int end = Rules.Value.Events[eventIndex].ScriptStart + Rules.Value.Events[eventIndex].ScriptCount;
            for (int guard = 0; pc < end && guard < 100000; guard++)
            {
                var s = Rules.Value.Script[pc];
                switch (s.Kind)
                {
                    case StepKind.Say:
                        ShowLine(pc, pc + 1 < end && Rules.Value.Script[pc + 1].Kind == StepKind.Choice ? pc + 1 : -1);
                        return;
                    case StepKind.Choice:
                        if (FillChoices(s)) { ShowChoices(); return; }
                        pc = s.Target;              // no option is available: skip the whole choice
                        continue;
                    case StepKind.Show: SetDialogImage(s.Key, s.Image); break;
                    case StepKind.Hide: SetDialogImage(s.Key, -1); break;
                    case StepKind.Sound: PushCue(s.Sound); break;
                    case StepKind.Fx: Apply(s.Start, s.Count); break;
                    case StepKind.If:
                        if (!Eval(s.Cond)) { pc = s.Target; continue; }
                        break;
                    case StepKind.Jump: pc = s.Target; continue;
                }
                pc++;
            }
            var ws = Em.GetComponentData<WorldState>(World);
            if (ws.DialogOpen != 0) { Close(ref ws); Em.SetComponentData(World, ws); }
            Em.GetBuffer<DialogImage>(World).Clear();
        }

        /// <summary>Shows line <paramref name="step"/>; a choice right after it (<paramref name="choiceStep"/>, else -1) appears together with it.</summary>
        void ShowLine(int step, int choiceStep)
        {
            var s = Rules.Value.Script[step];
            var ws = Em.GetComponentData<WorldState>(World);
            ws.DialogOpen = 1; ws.DialogStep = step;
            ws.DialogText = s.TextId; ws.DialogNpc = s.WhoNpc; ws.DialogWho = s.WhoText;
            ws.Version++;
            Em.SetComponentData(World, ws);
            if (choiceStep < 0 || !FillChoices(Rules.Value.Script[choiceStep])) Em.GetBuffer<DialogChoice>(World).Clear();
        }

        /// <summary>A choice reached without a line just before it: the dialogue opens (or stays) with the last line on screen.</summary>
        void ShowChoices()
        {
            var ws = Em.GetComponentData<WorldState>(World);
            ws.DialogOpen = 1;
            ws.Version++;
            Em.SetComponentData(World, ws);
        }

        /// <summary>Puts the options of a Choice step whose condition holds on screen; false (and none shown) when no option is available.</summary>
        bool FillChoices(in ScriptStep s)
        {
            var choices = Em.GetBuffer<DialogChoice>(World);
            choices.Clear();
            for (int i = s.Start; i < s.Start + s.Count; i++)
                if (Eval(Rules.Value.Choices[i].Cond))
                    choices.Add(new DialogChoice { ChoiceIndex = i, TextId = Rules.Value.Choices[i].TextId });
            return choices.Length > 0;
        }

        /// <summary>Shows (<paramref name="image"/> &gt;= 0) or hides the dialogue image with this key.</summary>
        void SetDialogImage(int key, int image)
        {
            var images = Em.GetBuffer<DialogImage>(World);
            for (int i = images.Length - 1; i >= 0; i--) if (images[i].Key == key) images.RemoveAt(i);
            if (image >= 0) images.Add(new DialogImage { Key = key, Image = image });
        }

        static void Close(ref WorldState ws)
        {
            ws.DialogOpen = 0; ws.DialogEvent = 0; ws.DialogText = 0; ws.DialogNpc = 0; ws.DialogWho = 0;
            ws.DialogEventIdx = 0; ws.DialogStep = -1; ws.Dirty = 1; ws.Version++;
        }

        /// <summary>Click / key on a line: the program goes on (unless a choice is on screen: the player has to pick).</summary>
        public void Advance()
        {
            var ws = Em.GetComponentData<WorldState>(World);
            if (ws.DialogOpen == 0 || ws.DialogStep < 0) return;
            if (Em.GetBuffer<DialogChoice>(World).Length > 0) return;
            Run(ws.DialogEventIdx, ws.DialogStep + 1);
        }

        /// <summary>The player picked an option on screen: the program goes on from that option's commands.</summary>
        public void Choose(int choiceIndex)
        {
            var ws = Em.GetComponentData<WorldState>(World);
            if (ws.DialogOpen == 0) return;
            var choices = Em.GetBuffer<DialogChoice>(World);
            bool valid = false;
            for (int i = 0; i < choices.Length; i++) if (choices[i].ChoiceIndex == choiceIndex) { valid = true; break; }
            if (!valid) return;
            choices.Clear();
            Run(ws.DialogEventIdx, Rules.Value.Choices[choiceIndex].Target);
        }
    }
}
