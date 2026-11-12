using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace LivingNPC.Simulation
{
    // Frame order:  CommandSystem -> ClockSystem -> NpcSystem (parallel job) -> RuleSystem
    //
    // The UI (managed) only writes GameCommand elements and reads state. All game logic lives in these systems,
    // so the same simulation can run headless (tests, LLM playtests, dedicated servers).

    // ====================================================================== commands

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct CommandSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<WorldState>();
            state.RequireForUpdate<PlayerState>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var world = SystemAPI.GetSingletonEntity<WorldState>();
            var cmdBuf = em.GetBuffer<GameCommand>(world);
            if (cmdBuf.Length == 0) return;

            var cmds = cmdBuf.ToNativeArray(Allocator.Temp);
            cmdBuf.Clear();

            var player = SystemAPI.GetSingletonEntity<PlayerState>();
            var rt = RuleRuntime.Create(em, world, player);

            for (int i = 0; i < cmds.Length; i++)
            {
                var cmd = cmds[i];
                var ws = em.GetComponentData<WorldState>(world);
                var clock = em.GetComponentData<ClockState>(world);
                var p = em.GetComponentData<PlayerState>(player);

                switch (cmd.Type)
                {
                    case CommandType.Travel:
                        {
                            if (ws.DialogOpen != 0 || clock.Pending > 0 || p.Node == 0) break;
                            ref var nav = ref rt.Nav.Value;
                            int from = nav.IndexOf(p.Node);
                            if (from < 0) break;
                            for (int e = nav.EdgeStart[from]; e < nav.EdgeStart[from + 1]; e++)
                            {
                                var edge = nav.Edges[e];
                                if (edge.LinkId != cmd.A) continue;
                                if (!rt.LinkVisible(edge)) break;
                                if (!rt.LinkOpen(edge) || !rt.Eval(edge.Cond))
                                {
                                    if (edge.LockedText != 0) rt.PushMessage(edge.LockedText, MsgKind.Info);
                                    break;
                                }
                                p.FromNode = p.Node;
                                p.PendingNode = nav.NodeIds[edge.To];
                                p.Node = 0;
                                clock.Pending = edge.Minutes;
                                em.SetComponentData(player, p);
                                em.SetComponentData(world, clock);
                                rt.Touch();
                                break;
                            }
                            break;
                        }
                    case CommandType.Wait:
                        {
                            if (ws.DialogOpen != 0 || clock.Pending > 0 || p.Node == 0) break;
                            clock.Pending = math.clamp(cmd.A, 1, GameTime.MinutesPerDay);
                            em.SetComponentData(world, clock);
                            rt.Touch();
                            break;
                        }
                    case CommandType.Sleep:
                        {
                            if (ws.DialogOpen != 0 || clock.Pending > 0 || p.Node == 0) break;
                            if (!RuleRuntime.CanSleep(rt.Rules, p.Node, clock.Now, out int minutes)) break;
                            clock.Pending = minutes;
                            clock.Sleeping = 1;
                            em.SetComponentData(world, clock);
                            rt.Touch();
                            break;
                        }
                    case CommandType.Talk:
                        {
                            if (ws.DialogOpen != 0 || clock.Pending > 0 || p.Node == 0) break;
                            var npc = rt.FindNpc(cmd.A);
                            if (npc == Entity.Null || em.GetComponentData<NpcLocation>(npc).Node != p.Node) break;
                            ws.TalkNpc = cmd.A;
                            em.SetComponentData(world, ws);
                            rt.Touch();
                            break;
                        }
                    case CommandType.Button:
                        {
                            // a picture of the bar: the "button" events run like talk ones, when the world is still
                            if (cmd.A == 0 || ws.DialogOpen != 0 || clock.Pending > 0 || p.Node == 0) break;
                            ws.Button = cmd.A;
                            em.SetComponentData(world, ws);
                            rt.Touch();
                            break;
                        }
                    case CommandType.Choose:
                        rt.Choose(cmd.A);
                        break;
                    case CommandType.Advance:
                        rt.Advance();
                        break;
                }
            }
            cmds.Dispose();
        }
    }

    // ====================================================================== clock

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CommandSystem))]
    public partial struct ClockSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ClockState>();
            state.RequireForUpdate<PlayerState>();
        }

        /// <summary>
        /// Time only moves when something asks for it (travel, waiting, an advanceTime effect): one tick per frame,
        /// so a long jump is simulated tick by tick and quests/events can fire at the exact minute.
        /// A dialog freezes the clock until the player answers.
        /// </summary>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingletonRW<ClockState>();
            var ws = SystemAPI.GetSingletonRW<WorldState>();
            var player = SystemAPI.GetSingletonRW<PlayerState>();

            clock.ValueRW.Ticked = 0;
            clock.ValueRW.LastStep = 0;
            if (ws.ValueRO.DialogOpen != 0) return;

            if (clock.ValueRO.Pending > 0)
            {
                int step = math.min(clock.ValueRO.TickMinutes, clock.ValueRO.Pending);
                clock.ValueRW.Now += step;
                clock.ValueRW.Pending -= step;
                clock.ValueRW.LastStep = step;
                clock.ValueRW.Ticked = 1;
                clock.ValueRW.TickSerial++;
                ws.ValueRW.Version++;
                if (clock.ValueRO.Pending <= 0 && clock.ValueRO.Sleeping != 0)
                {
                    clock.ValueRW.Sleeping = 0;      // the sleep is over: the rules of this frame see the waking up
                    ws.ValueRW.Woke = 1;
                }
            }

            if (clock.ValueRO.Pending <= 0 && player.ValueRO.PendingNode != 0)
            {
                player.ValueRW.Node = player.ValueRO.PendingNode;
                player.ValueRW.PendingNode = 0;
                player.ValueRW.Arrived = 1;
                ws.ValueRW.Dirty = 1;
                ws.ValueRW.Version++;
            }
        }
    }

    // ====================================================================== npc

    public static class NpcLogic
    {
        /// <summary>The node a named place of the npc points to now, or <paramref name="fallback"/> when the npc has no such place.</summary>
        public static int PlaceNode(DynamicBuffer<NpcPlace> places, int key, int fallback)
        {
            for (int i = 0; i < places.Length; i++) if (places[i].Key == key) return places[i].Node;
            return fallback;
        }

        /// <summary>
        /// Highest-priority block active at time <paramref name="t"/>; falls back to the npc's home. A block written as
        /// "$place" goes wherever that place points now; one whose place is not defined (yet) is skipped.
        /// </summary>
        public static void Resolve(DynamicBuffer<ScheduleBlock> blocks, DynamicBuffer<NpcPlace> places, int t, int startDow, int home, out int node, out int activity)
        {
            int i = ActiveBlock(blocks, places, t, startDow, out node);
            if (i < 0) { node = home; activity = 0; }
            else activity = blocks[i].Activity;
        }

        /// <summary>
        /// Index of the block <see cref="Resolve"/> follows at time <paramref name="t"/> (the first of the highest priority),
        /// with the node it leads to; -1 when none applies and the npc goes home.
        /// </summary>
        public static int ActiveBlock(DynamicBuffer<ScheduleBlock> blocks, DynamicBuffer<NpcPlace> places, int t, int startDow, out int node)
        {
            int m = t % GameTime.MinutesPerDay;
            int dow = GameTime.DayOfWeek(t, startDow);
            node = 0;
            int best = int.MinValue, found = -1;
            for (int i = 0; i < blocks.Length; i++)
            {
                var b = blocks[i];
                if (((b.DayMask >> dow) & 1) == 0 || m < b.Start || m >= b.End) continue;
                int target = b.Place != 0 ? PlaceNode(places, b.Place, b.Node) : b.Node;
                if (target == 0) continue;
                if (b.Priority > best) { best = b.Priority; node = target; found = i; }
            }
            return found;
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ClockSystem))]
    public partial struct NpcSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ClockState>();
            state.RequireForUpdate<NavGraphRef>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<ClockState>();
            if (clock.Ticked == 0) return;
            new NpcTickJob
            {
                Now = clock.Now,
                Step = clock.LastStep,
                StartDow = clock.StartDayOfWeek,
                Nav = SystemAPI.GetSingleton<NavGraphRef>().Value,
                Rules = SystemAPI.GetSingleton<RuleRef>().Value,
            }.ScheduleParallel();
        }
    }

    /// <summary>Plan (where should I be?), move (walk the shortest path hop by hop) and stats (drift + activity effects).</summary>
    [BurstCompile]
    public partial struct NpcTickJob : IJobEntity
    {
        public int Now, Step, StartDow;
        public BlobAssetReference<NavGraphBlob> Nav;
        public BlobAssetReference<RuleBlob> Rules;

        void Execute(ref NpcLocation loc, DynamicBuffer<ScheduleBlock> blocks, DynamicBuffer<NpcPlace> places, DynamicBuffer<StatValue> stats)
        {
            ref var nav = ref Nav.Value;

            // --- plan
            int n0, a0;
            if (loc.OverrideUntil > Now) { n0 = loc.OverrideNode; a0 = 0; }
            else
            {
                if (loc.OverrideUntil != 0) { loc.OverrideUntil = 0; loc.OverrideNode = 0; }
                NpcLogic.Resolve(blocks, places, Now, StartDow, loc.HomeNode, out n0, out a0);
            }
            int goal = n0;
            int origin = loc.Node != 0 ? loc.Node : loc.NextNode;
            if (loc.OverrideUntil <= Now)
            {
                // leave early enough to arrive when the next block starts
                for (int it = 0; it < 3; it++)
                {
                    int d = nav.Distance(origin, goal);
                    if (d <= 0 || d > 240) break;
                    NpcLogic.Resolve(blocks, places, Now + d, StartDow, loc.HomeNode, out int g, out int _);
                    if (g == goal) break;
                    goal = g;
                }
            }
            loc.Goal = goal;

            // --- move (hop chaining keeps nominal travel times independent of the tick length)
            int t0 = Now;
            for (int guard = 0; guard < 32; guard++)
            {
                if (loc.Node == 0)
                {
                    if (loc.NextNode != 0 && Now >= loc.ArriveAt) { t0 = loc.ArriveAt; loc.Node = loc.NextNode; loc.NextNode = 0; }
                    else break;
                }
                if (loc.Node == goal) break;
                int hop = nav.NextHopId(loc.Node, goal, out int minutes);
                if (hop == 0) break;
                loc.NextNode = hop;
                loc.ArriveAt = t0 + math.max(1, minutes);
                loc.Node = 0;
            }
            loc.Activity = (loc.Node != 0 && loc.Node == n0) ? a0 : 0;

            // --- stats
            ref var rules = ref Rules.Value;
            float dt = Step / 60f;
            for (int i = 0; i < stats.Length; i++)
            {
                var s = stats[i];
                for (int r = 0; r < rules.Stats.Length; r++)
                {
                    if (rules.Stats[r].Id != s.Stat) continue;
                    ref var rule = ref rules.Stats[r];
                    float v = s.Value;
                    v += (rule.Baseline - v) * math.min(1f, rule.DriftPerHour * dt);
                    v += rule.ConstantPerHour * dt;
                    if (loc.Activity != 0)
                        for (int a = 0; a < rules.ActivityEffects.Length; a++)
                            if (rules.ActivityEffects[a].Activity == loc.Activity && rules.ActivityEffects[a].Stat == s.Stat)
                                v += rules.ActivityEffects[a].PerHour * dt;
                    s.Value = math.clamp(v, rule.Min, rule.Max);
                    stats[i] = s;
                    break;
                }
            }
        }
    }

    // ====================================================================== rules (quests + events + automatic quests)

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NpcSystem))]
    public partial struct RuleSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<WorldState>();
            state.RequireForUpdate<RuleRef>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var world = SystemAPI.GetSingletonEntity<WorldState>();
            var player = SystemAPI.GetSingletonEntity<PlayerState>();
            RuleRuntime rt = default;
            bool created = false;

            for (int pass = 0; pass < 4; pass++)
            {
                var ws = em.GetComponentData<WorldState>(world);
                var clock = em.GetComponentData<ClockState>(world);
                var p = em.GetComponentData<PlayerState>(player);

                bool ticked = pass == 0 && clock.Ticked != 0;
                bool arrived = p.Arrived != 0;
                bool woke = ws.Woke != 0;
                int talk = ws.TalkNpc;
                int button = ws.Button;
                if (!(ticked || arrived || woke || talk != 0 || button != 0 || ws.Dirty != 0)) break;
                if (ws.DialogOpen != 0) break;

                if (!created) { rt = RuleRuntime.Create(em, world, player); created = true; }

                // consume the pulses first: effects below may raise new ones, handled by the next pass
                ws.Dirty = 0; ws.TalkNpc = 0; ws.Button = 0; ws.Woke = 0; p.Arrived = 0;
                em.SetComponentData(world, ws);
                em.SetComponentData(player, p);

                if (woke) rt.Apply(rt.Rules.Value.SleepFxStart, rt.Rules.Value.SleepFxCount);
                if (ticked) rt.CheckAutoQuests();
                for (int i = 0; i < 4; i++) if (!rt.EvaluateQuests() & !rt.EvaluateAuto()) break;

                bool dialogOpened = false, talkHandled = false;
                int eventCount = rt.Rules.Value.Events.Length;
                for (int ei = 0; ei < eventCount; ei++)
                {
                    var ev = rt.Rules.Value.Events[ei];
                    bool match = false;
                    switch (ev.Trigger)
                    {
                        case TriggerType.EnterNode: match = arrived && (ev.TriggerNode == 0 || ev.TriggerNode == p.Node); break;
                        case TriggerType.Tick: match = ticked && (ev.TriggerNode == 0 || ev.TriggerNode == p.Node); break;
                        case TriggerType.Talk: match = talk != 0 && ev.TriggerNpc == talk; break;
                        case TriggerType.Wake: match = woke && (ev.TriggerNode == 0 || ev.TriggerNode == p.Node); break;
                        case TriggerType.Button: match = button != 0 && ev.TriggerButton == button && (ev.TriggerNode == 0 || ev.TriggerNode == p.Node); break;
                    }
                    if (!match) continue;

                    var eventStates = em.GetBuffer<EventState>(world);
                    var es = eventStates[ei];
                    if (ev.Once != 0 && es.Count > 0) continue;
                    if (ev.CooldownMin > 0 && es.Count > 0 && clock.Now - es.LastAt < ev.CooldownMin) continue;
                    if (!rt.Eval(ev.Cond)) continue;

                    if (ev.HasDialog != 0 && (dialogOpened || clock.Sleeping != 0)) continue;     // one dialogue at a time; a sleeper is not woken by one: it waits

                    eventStates[ei] = new EventState { Count = es.Count + 1, LastAt = clock.Now };
                    if (ev.TextId != 0) rt.PushMessage(ev.TextId, MsgKind.Event, ev.TriggerNpc);
                    rt.RunEvent(ei);
                    if (em.GetComponentData<WorldState>(world).DialogOpen != 0) dialogOpened = true;
                    if (ev.Trigger == TriggerType.Talk) talkHandled = true;
                }
                if (talk != 0 && !talkHandled && !rt.TalkAuto(talk)) rt.PushMessage(rt.Rules.Value.DefaultTalkText, MsgKind.Talk, talk);

                for (int i = 0; i < 4; i++) if (!rt.EvaluateQuests() & !rt.EvaluateAuto()) break;
            }
        }
    }
}
