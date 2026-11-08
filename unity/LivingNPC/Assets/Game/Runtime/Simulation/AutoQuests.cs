using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace LivingNPC.Simulation
{
    /// <summary>Why a template can or cannot be picked by an npc now (the debug panel shows it).</summary>
    public enum AutoPick : byte { Ok, NoStages, When, Slot, Score }

    /// <summary>The values a template's conditions and effects read while one of its instances is evaluated (see <see cref="RuleRuntime.Enter"/>).</summary>
    public struct AutoCtx
    {
        public FixedList32Bytes<int> Bind;
        public int StageAt, StartedAt, Roll;
    }

    // Automatic quests: every npc with "autoQuests" picks a template when it is free, fills its slots, runs its stages
    // (conditions and effects that name self and the slots read the instance's values) and, when it ends, waits a pause.
    public partial struct RuleRuntime
    {
        /// <summary>Outcome of an automatic quest ended by stopAuto (2 and 3 are QuestStatus.Completed / Failed).</summary>
        public const int AutoStopped = 4;
        const int MemorySize = 16;

        /// <summary>The instance being evaluated: [0] the npc ("self"), then the values of its slots. Empty elsewhere.</summary>
        public FixedList32Bytes<int> Bind;
        public int CtxStageAt, CtxStartedAt, CtxRoll;

        /// <summary>The id an argument stands for: "self" and slots (BindRef) become the instance's values.</summary>
        public int R(int v)
        {
            if (!BindRef.Is(v)) return v;
            int k = BindRef.Slot(v);
            return k < Bind.Length ? Bind[k] : BindRef.None;
        }

        AutoCtx SaveCtx() => new AutoCtx { Bind = Bind, StageAt = CtxStageAt, StartedAt = CtxStartedAt, Roll = CtxRoll };

        public void RestoreCtx(in AutoCtx c) { Bind = c.Bind; CtxStageAt = c.StageAt; CtxStartedAt = c.StartedAt; CtxRoll = c.Roll; }

        /// <summary>Makes conditions and effects read the values of instance <paramref name="a"/>; returns what to give back to <see cref="RestoreCtx"/>.</summary>
        public AutoCtx Enter(in NpcAuto a)
        {
            var saved = SaveCtx();
            Bind = a.Bind; CtxStageAt = a.StageAt; CtxStartedAt = a.StartedAt; CtxRoll = a.Roll;
            return saved;
        }

        // ------------------------------------------------------------------ relations, goals, switches

        /// <summary>What <paramref name="from"/> thinks of <paramref name="to"/> (npc ids, 0 = the player): -100..100, 0 when never set.</summary>
        public float GetRelation(int from, int to)
        {
            var e = TargetEntity(from);
            if (e == Entity.Null || !Em.HasBuffer<RelationValue>(e)) return 0;
            var buf = Em.GetBuffer<RelationValue>(e);
            for (int i = 0; i < buf.Length; i++) if (buf[i].Other == to) return buf[i].Value;
            return 0;
        }

        public void SetRelation(int from, int to, float value)
        {
            if (from == to || to == BindRef.None) return;
            var e = TargetEntity(from);
            if (e == Entity.Null || !Em.HasBuffer<RelationValue>(e)) return;
            value = math.clamp(value, -100, 100);
            var buf = Em.GetBuffer<RelationValue>(e);
            for (int i = 0; i < buf.Length; i++)
                if (buf[i].Other == to) { buf[i] = new RelationValue { Other = to, Value = value }; Touch(); return; }
            buf.Add(new RelationValue { Other = to, Value = value });
            Touch();
        }

        bool TryAuto(int npc, out Entity e, out NpcAuto a)
        {
            e = FindNpc(npc);
            a = default;
            if (e == Entity.Null || !Em.HasComponent<NpcAuto>(e)) return false;
            a = Em.GetComponentData<NpcAuto>(e);
            return true;
        }

        void SetNpcGoal(int npc, int goal)
        {
            if (!TryAuto(npc, out var e, out var a)) return;
            a.Goal = goal;
            Em.SetComponentData(e, a);
            Touch();
        }

        /// <summary>Turns on / off the picking of new automatic quests; the one running goes on.</summary>
        void SetAutoEnabled(int npc, bool on)
        {
            if (!TryAuto(npc, out var e, out var a)) return;
            a.Enabled = (byte)(on ? 1 : 0);
            Em.SetComponentData(e, a);
            Touch();
        }

        /// <summary>The npc finished template (index + 1, 0 = any) with outcome (0 = any) within the last days (0 = ever).</summary>
        bool Remembers(int npc, int template, int outcome, float days, int now)
        {
            var e = FindNpc(npc);
            if (e == Entity.Null || !Em.HasBuffer<NpcMemory>(e)) return false;
            ref var rules = ref Rules.Value;
            int id = template > 0 && template <= rules.Templates.Length ? rules.Templates[template - 1].Id : 0;
            if (template > 0 && id == 0) return false;
            var mem = Em.GetBuffer<NpcMemory>(e);
            for (int i = 0; i < mem.Length; i++)
            {
                var m = mem[i];
                if (id != 0 && m.Template != id) continue;
                if (outcome != 0 && m.Outcome != outcome) continue;
                if (days > 0 && now - m.At > days * GameTime.MinutesPerDay) continue;
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ texts

        /// <summary>
        /// tell: one line of group fx.A in the log, said by fx.C (else by self). Unless fx.B (everywhere), only when the player
        /// is in the same place as the one who speaks.
        /// </summary>
        void Tell(in FxNode fx)
        {
            ref var rules = ref Rules.Value;
            if (fx.A < 0 || fx.A >= rules.TextGroups.Length) return;
            var saved = SaveCtx();
            int speaker = fx.C != 0 ? R(fx.C) : Bind.Length > 0 ? Bind[0] : 0;
            if (Bind.Length == 0) Bind.Add(speaker);        // shared lines said from a quest or an event: self is who says them
            bool ok = speaker != 0 && speaker != BindRef.None;
            if (ok && fx.B == 0)
            {
                var e = FindNpc(speaker);
                var p = PlayerData;
                ok = e != Entity.Null && p.Node != 0 && Em.GetComponentData<NpcLocation>(e).Node == p.Node;
            }
            if (ok)
            {
                int line = PickLine(fx.A);
                if (line != 0) PushMessage(line, MsgKind.Npc, speaker);
            }
            RestoreCtx(saved);
        }

        /// <summary>A line of the first variant of the group whose condition holds now (which line changes with the time and the speaker); 0 when none holds.</summary>
        int PickLine(int group)
        {
            ref var rules = ref Rules.Value;
            var g = rules.TextGroups[group];
            for (int v = 0; v < g.VariantCount; v++)
            {
                var tv = rules.TextVariants[g.VariantStart + v];
                if (tv.Cond >= 0 && !Eval(tv.Cond)) continue;
                if (tv.LineCount <= 0) continue;
                uint h = math.hash(new uint4((uint)Clock.Now, (uint)group, (uint)(Bind.Length > 0 ? Bind[0] : 0), (uint)v));
                return rules.TextLines[tv.LineStart + (int)(h % (uint)tv.LineCount)];
            }
            return 0;
        }

        /// <summary>
        /// The player talks to <paramref name="npc"/> and no message answered: a line of the "talk" texts of the automatic
        /// quest it is running. False when it runs none or the template has no "talk" texts.
        /// </summary>
        public bool TalkAuto(int npc)
        {
            if (!TryAuto(npc, out _, out var a) || a.Template == 0) return false;
            ref var rules = ref Rules.Value;
            if (a.Template > rules.Templates.Length) return false;
            int group = rules.Templates[a.Template - 1].TalkGroup;
            if (group < 0) return false;
            var saved = Enter(a);
            int line = PickLine(group);
            if (line != 0) PushMessage(line, MsgKind.Npc, npc);
            RestoreCtx(saved);
            return line != 0;
        }

        // ------------------------------------------------------------------ picking a template

        /// <summary>How many automatic quests are running in the world.</summary>
        public int ActiveAutoCount()
        {
            int n = 0;
            var refs = Em.GetBuffer<EntityRef>(World);
            for (int i = 0; i < refs.Length; i++)
                if (Em.HasComponent<NpcAuto>(refs[i].Entity) && Em.GetComponentData<NpcAuto>(refs[i].Entity).Template != 0) n++;
            return n;
        }

        /// <summary>
        /// Every rules.json autoQuests.checkMinutes of game time: each free npc with automatic quests on, past its pause, picks
        /// a template (while fewer than maxActive run). Called on every tick; does nothing between two checks.
        /// </summary>
        public void CheckAutoQuests()
        {
            ref var rules = ref Rules.Value;
            if (rules.Templates.Length == 0 || rules.AutoMaxActive <= 0) return;
            int now = Clock.Now;
            int slot = now / math.max(1, rules.AutoCheckMinutes);
            var ws = Em.GetComponentData<WorldState>(World);
            if (ws.AutoCheck == slot) return;
            ws.AutoCheck = slot;
            Em.SetComponentData(World, ws);

            int active = ActiveAutoCount();
            int n = Em.GetBuffer<EntityRef>(World).Length;
            if (n == 0) return;
            int offset = (int)(math.hash(new int2(slot, n)) % (uint)n);      // nobody always picks first
            for (int j = 0; j < n && active < rules.AutoMaxActive; j++)
            {
                var r = Em.GetBuffer<EntityRef>(World)[(j + offset) % n];
                if (!Em.HasComponent<NpcAuto>(r.Entity)) continue;
                var a = Em.GetComponentData<NpcAuto>(r.Entity);
                if (a.Enabled == 0 || a.Template != 0 || now < a.NextAt || BusyQuest(r.Id) >= 0) continue;
                var rng = new Random(math.hash(new int3(now, r.Id, slot)) | 1u);
                if (!PickTemplate(r.Entity, r.Id, ref rng, out int ti, out var bind)) continue;
                Begin(r.Entity, ti, bind, rng.NextUInt() | 1u);
                active++;
            }
        }

        /// <summary>A template for the npc, at random weighted by score (a template with no valid slots or a score of 0 or less is out).</summary>
        bool PickTemplate(Entity e, int npc, ref Random rng, out int pick, out FixedList32Bytes<int> pickBind)
        {
            ref var rules = ref Rules.Value;
            pick = -1;
            pickBind = default;
            float total = 0;
            for (int ti = 0; ti < rules.Templates.Length; ti++)
            {
                if (TryTemplate(e, npc, ti, ref rng, true, out var bind, out float score, out _) != AutoPick.Ok) continue;
                total += score;
                if (rng.NextFloat() * total < score) { pick = ti; pickBind = bind; }      // each one stays with probability score / total
            }
            return pick >= 0;
        }

        /// <summary>
        /// Whether npc <paramref name="npc"/> can pick template <paramref name="ti"/> now: its "when" (skipped when
        /// <paramref name="checkWhen"/> is false), its slots filled at random (<paramref name="bind"/>), its score. On a slot
        /// that cannot be filled, <paramref name="failedSlot"/> says which.
        /// </summary>
        public AutoPick TryTemplate(Entity e, int npc, int ti, ref Random rng, bool checkWhen, out FixedList32Bytes<int> bind, out float score, out int failedSlot)
        {
            ref var rules = ref Rules.Value;
            var t = rules.Templates[ti];
            bind = default;
            bind.Add(npc);
            score = 0;
            failedSlot = -1;
            if (t.StageCount == 0) return AutoPick.NoStages;
            var saved = SaveCtx();
            Bind = bind; CtxStageAt = CtxStartedAt = Clock.Now; CtxRoll = 0;
            AutoPick result = AutoPick.Ok;
            if (checkWhen && t.Cond >= 0 && !Eval(t.Cond)) result = AutoPick.When;
            else if (!FillSlots(t, ref bind, ref rng, out failedSlot)) result = AutoPick.Slot;
            else
            {
                score = Weight(t, Em.GetComponentData<NpcAuto>(e), bind);
                if (checkWhen && score <= 0) result = AutoPick.Score;
            }
            RestoreCtx(saved);
            return result;
        }

        /// <summary><see cref="TryTemplate(Entity, int, int, ref Random, bool, out FixedList32Bytes{int}, out float, out int)"/> with the slots drawn from <paramref name="seed"/> (tools and tests).</summary>
        public AutoPick TryTemplateSeeded(Entity e, int npc, int ti, uint seed, bool checkWhen, out FixedList32Bytes<int> bind, out float score, out int failedSlot)
        {
            var rng = new Random(seed | 1u);
            return TryTemplate(e, npc, ti, ref rng, checkWhen, out bind, out score, out failedSlot);
        }

        bool FillSlots(in TemplateRule t, ref FixedList32Bytes<int> bind, ref Random rng, out int failed)
        {
            ref var rules = ref Rules.Value;
            failed = -1;
            for (int k = 0; k < t.SlotCount; k++)
            {
                var s = rules.Slots[t.SlotStart + k];
                int value = 0, count = 0;
                switch (s.Kind)
                {
                    case SlotKind.Place:
                        {
                            int owner = s.Of;
                            if (BindRef.Is(owner)) { int b = BindRef.Slot(owner); owner = b < bind.Length ? bind[b] : 0; }
                            var oe = FindNpc(owner);
                            if (oe != Entity.Null) value = NpcLogic.PlaceNode(Em.GetBuffer<NpcPlace>(oe), s.Place, 0);
                            break;
                        }
                    case SlotKind.Npc:
                        {
                            int n = Em.GetBuffer<EntityRef>(World).Length;
                            for (int i = 0; i < n; i++)
                            {
                                int id = Em.GetBuffer<EntityRef>(World)[i].Id;
                                if (Holds(bind, id)) continue;
                                if (s.Free != 0 && IsTaken(id)) continue;
                                if (Candidate(ref bind, id, s.Cond) && rng.NextInt(++count) == 0) value = id;
                            }
                            break;
                        }
                    case SlotKind.Node:
                        {
                            ref var nav = ref Nav.Value;
                            int n = s.ListCount > 0 ? s.ListCount : nav.NodeIds.Length;
                            for (int i = 0; i < n; i++)
                            {
                                int id = s.ListCount > 0 ? rules.ListValues[s.ListStart + i] : nav.NodeIds[i];
                                if (Candidate(ref bind, id, s.Cond) && rng.NextInt(++count) == 0) value = id;
                            }
                            break;
                        }
                    case SlotKind.Item:
                        {
                            int n = s.ListCount > 0 ? s.ListCount : rules.Items.Length;
                            for (int i = 0; i < n; i++)
                            {
                                int id = s.ListCount > 0 ? rules.ListValues[s.ListStart + i] : rules.Items[i].Id;
                                if (Candidate(ref bind, id, s.Cond) && rng.NextInt(++count) == 0) value = id;
                            }
                            break;
                        }
                }
                if (value == 0) { failed = k; return false; }
                bind.Add(value);
            }
            return true;
        }

        static bool Holds(in FixedList32Bytes<int> bind, int id)
        {
            for (int i = 0; i < bind.Length; i++) if (bind[i] == id) return true;
            return false;
        }

        /// <summary>The slot's condition with the candidate in the slot (it is the next value of the instance).</summary>
        bool Candidate(ref FixedList32Bytes<int> bind, int value, int cond)
        {
            if (cond < 0) return true;
            bind.Add(value);
            Bind = bind;
            bool ok = Eval(cond);
            bind.RemoveAt(bind.Length - 1);
            Bind = bind;
            return ok;
        }

        /// <summary>The active quest (index) with busyGiver whose giver is <paramref name="npc"/>, or -1: while it runs, the npc is busy.</summary>
        public int BusyQuest(int npc)
        {
            ref var rules = ref Rules.Value;
            var states = Em.GetBuffer<QuestState>(World);
            for (int qi = 0; qi < rules.Quests.Length && qi < states.Length; qi++)
                if (rules.Quests[qi].BusyNpc == npc && npc != 0 && states[qi].Status == (byte)QuestStatus.Active) return qi;
            return -1;
        }

        /// <summary>
        /// The npc is busy (the busy condition, and what "free" slots avoid): it runs an automatic quest of its own, is a
        /// character slot of someone else's, or gives a busyGiver quest that is active.
        /// </summary>
        public bool IsBusy(int npc) => npc != 0 && npc != BindRef.None && IsTaken(npc);

        bool IsTaken(int id)
        {
            if (BusyQuest(id) >= 0) return true;
            var refs = Em.GetBuffer<EntityRef>(World);
            for (int i = 0; i < refs.Length; i++)
            {
                if (!Em.HasComponent<NpcAuto>(refs[i].Entity)) continue;
                var a = Em.GetComponentData<NpcAuto>(refs[i].Entity);
                if (a.Template == 0) continue;
                if (refs[i].Id == id || Holds(a.Bind, id)) return true;
            }
            return false;
        }

        float Weight(in TemplateRule t, in NpcAuto a, in FixedList32Bytes<int> bind)
        {
            ref var rules = ref Rules.Value;
            float w = t.WeightBase;
            for (int i = 0; i < t.WeightCount; i++)
            {
                var term = rules.Weights[t.WeightStart + i];
                switch (term.Kind)
                {
                    case WeightKind.Stat: w += GetStat(bind[0], term.Key) * term.K; break;
                    case WeightKind.Goal: if (a.Goal == term.Key) w += term.K; break;
                    case WeightKind.Relation: if (term.Key < bind.Length) w += GetRelation(bind[0], bind[term.Key]) * term.K; break;
                }
            }
            return w;
        }

        // ------------------------------------------------------------------ running an instance

        static int RollFor(uint seed, int stage, int now) => (int)(math.hash(new uint3(seed, (uint)stage, (uint)now)) % 100u);

        /// <summary>startAuto: the npc starts the template now even if it would not pick it (pause, when, score), when it is free and the slots can be filled.</summary>
        void StartAuto(int npc, int ti)
        {
            if (ti < 0 || ti >= Rules.Value.Templates.Length || !TryAuto(npc, out var e, out var a) || a.Template != 0) return;
            var rng = new Random(math.hash(new int3(Clock.Now, npc, ti)) | 1u);
            if (TryTemplate(e, npc, ti, ref rng, false, out var bind, out _, out _) != AutoPick.Ok) return;
            Begin(e, ti, bind, rng.NextUInt() | 1u);
        }

        void Begin(Entity e, int ti, in FixedList32Bytes<int> bind, uint seed)
        {
            var t = Rules.Value.Templates[ti];
            var a = Em.GetComponentData<NpcAuto>(e);
            int now = Clock.Now;
            a.Template = ti + 1; a.Stage = 0; a.StageAt = now; a.StartedAt = now;
            a.Seed = seed; a.Roll = RollFor(seed, 0, now); a.Bind = bind;
            Em.SetComponentData(e, a);
            Touch();
            var saved = Enter(a);
            Apply(t.StartFxStart, t.StartFxCount);
            RestoreCtx(saved);
            StageBegins(e, ti, 0);
        }

        /// <summary>The onStart effects of a stage that has just begun, if the instance is still there (earlier effects may have ended it).</summary>
        void StageBegins(Entity e, int ti, int stage)
        {
            var a = Em.GetComponentData<NpcAuto>(e);
            if (a.Template != ti + 1 || a.Stage != stage) return;
            var sr = Rules.Value.Stages[Rules.Value.Templates[ti].StageStart + stage];
            var saved = Enter(a);
            Apply(sr.StartFxStart, sr.StartFxCount);
            RestoreCtx(saved);
        }

        /// <summary>The stages' fail / complete pass of every running instance. Returns true if anything changed.</summary>
        public bool EvaluateAuto()
        {
            bool changed = false;
            int n = Em.GetBuffer<EntityRef>(World).Length;
            for (int i = 0; i < n; i++)
            {
                var e = Em.GetBuffer<EntityRef>(World)[i].Entity;
                if (!Em.HasComponent<NpcAuto>(e)) continue;
                var a = Em.GetComponentData<NpcAuto>(e);
                if (a.Template == 0) continue;
                ref var rules = ref Rules.Value;
                if (a.Template > rules.Templates.Length || a.Stage >= rules.Templates[a.Template - 1].StageCount)
                {
                    a.Template = 0; a.Bind = default;       // the content changed under a running instance (F5)
                    Em.SetComponentData(e, a);
                    continue;
                }
                var sr = rules.Stages[rules.Templates[a.Template - 1].StageStart + a.Stage];
                var saved = Enter(a);
                bool fail = sr.FailCond >= 0 && Eval(sr.FailCond);
                bool done = !fail && sr.CompleteCond >= 0 && Eval(sr.CompleteCond);
                RestoreCtx(saved);
                if (fail) { FailAuto(e); changed = true; }
                else if (done) { AdvanceAuto(e); changed = true; }
            }
            return changed;
        }

        void AdvanceAuto(Entity e)
        {
            var a = Em.GetComponentData<NpcAuto>(e);
            int ti = a.Template - 1, stage = a.Stage;
            var sr = Rules.Value.Stages[Rules.Value.Templates[ti].StageStart + stage];
            var saved = Enter(a);
            Apply(sr.FxStart, sr.FxCount);
            RestoreCtx(saved);

            a = Em.GetComponentData<NpcAuto>(e);
            if (a.Template != ti + 1 || a.Stage != stage) return;      // its effects ended or moved it
            if (sr.Next < 0) { End(e, (int)QuestStatus.Completed); return; }
            a.Stage = sr.Next;
            a.StageAt = Clock.Now;
            a.Roll = RollFor(a.Seed, a.Stage, a.StageAt);
            Em.SetComponentData(e, a);
            Touch();
            StageBegins(e, ti, a.Stage);
        }

        void FailAuto(Entity e)
        {
            var a = Em.GetComponentData<NpcAuto>(e);
            int ti = a.Template - 1, stage = a.Stage;
            var sr = Rules.Value.Stages[Rules.Value.Templates[ti].StageStart + stage];
            var saved = Enter(a);
            Apply(sr.FailFxStart, sr.FailFxCount);
            RestoreCtx(saved);
            a = Em.GetComponentData<NpcAuto>(e);
            if (a.Template != ti + 1 || a.Stage != stage) return;
            End(e, (int)QuestStatus.Failed);
        }

        /// <summary>stopAuto: the npc drops what it is doing, with no effects; it counts as "stopped".</summary>
        void StopAuto(int npc)
        {
            if (!TryAuto(npc, out var e, out var a) || a.Template == 0) return;
            End(e, AutoStopped);
        }

        /// <summary>The end of an instance: the template's onComplete / onFail, the memory of it, then the pause.</summary>
        void End(Entity e, int outcome)
        {
            var a = Em.GetComponentData<NpcAuto>(e);
            int ti = a.Template - 1;
            var t = Rules.Value.Templates[ti];
            if (outcome != AutoStopped)
            {
                var saved = Enter(a);
                if (outcome == (int)QuestStatus.Completed) Apply(t.CompleteFxStart, t.CompleteFxCount);
                else Apply(t.FailFxStart, t.FailFxCount);
                RestoreCtx(saved);
                a = Em.GetComponentData<NpcAuto>(e);
                if (a.Template != ti + 1) return;
            }
            if (Em.HasBuffer<NpcMemory>(e))
            {
                var mem = Em.GetBuffer<NpcMemory>(e);
                mem.Add(new NpcMemory { Template = t.Id, Outcome = (byte)outcome, At = Clock.Now, Other = a.Bind.Length > 1 ? a.Bind[1] : 0 });
                if (mem.Length > MemorySize) mem.RemoveRange(0, mem.Length - MemorySize);
            }
            a.Template = 0; a.Stage = 0; a.Bind = default;
            a.NextAt = Clock.Now + (t.PauseMin >= 0 ? t.PauseMin : Rules.Value.AutoPauseMin);
            Em.SetComponentData(e, a);
            Touch();
        }
    }
}
