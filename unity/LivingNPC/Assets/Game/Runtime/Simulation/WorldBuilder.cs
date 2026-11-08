using System.Collections.Generic;
using LivingNPC.Content;
using Unity.Collections;
using Unity.Entities;

namespace LivingNPC.Simulation
{
    /// <summary>
    /// Instantiates the compiled content as entities. There is no scene/subscene baking on purpose:
    /// the world is rebuilt from external files at runtime, which is what makes mods and LLM edits possible.
    /// </summary>
    public static class WorldBuilder
    {
        public struct Handles { public Entity World, Player; }

        public static Handles Build(EntityManager em, CompiledContent c)
        {
            var rules = c.Db.Rules;

            // ---- the world singleton
            var world = em.CreateEntity();
            em.AddComponentData(world, new ClockState
            {
                Now = rules.StartMinute,
                TickMinutes = System.Math.Max(1, rules.TickMinutes),
                StartDayOfWeek = ((rules.StartDayOfWeek % 7) + 7) % 7,
            });
            em.AddComponentData(world, new WorldState { Dirty = 1, Version = 1 });

            // The blobs stay owned by CompiledContent (dispose it when the world is torn down or reloaded).
            em.AddComponentData(world, new NavGraphRef { Value = c.Nav });
            em.AddComponentData(world, new RuleRef { Value = c.Rules });

            var flags = em.AddBuffer<FlagValue>(world);
            var quests = em.AddBuffer<QuestState>(world);
            for (int i = 0; i < c.QuestCount; i++) quests.Add(new QuestState { Status = (byte)QuestStatus.NotStarted });
            var events = em.AddBuffer<EventState>(world);
            for (int i = 0; i < c.EventCount; i++) events.Add(default);
            em.AddBuffer<GameMessage>(world);
            em.AddBuffer<GameCommand>(world);
            em.AddBuffer<DialogChoice>(world);
            em.AddBuffer<DialogImage>(world);
            em.AddBuffer<SoundCue>(world);

            // ---- player
            var player = em.CreateEntity();
            em.AddComponentData(player, new PlayerState { Node = c.StartNode, Arrived = 1 });
            var pStats = em.AddBuffer<StatValue>(player);
            foreach (var s in c.StatRules) pStats.Add(new StatValue { Stat = s.Id, Value = s.Baseline });
            var pItems = em.AddBuffer<InventorySlot>(player);
            foreach (var it in c.PlayerItems) pItems.Add(it);
            em.AddBuffer<RelationValue>(player);

            // ---- nodes (one entity per map: handy for tooling/inspection and future per-node data)
            foreach (var m in c.Db.MapList)
            {
                var e = em.CreateEntity();
                em.AddComponentData(e, new NodeTag { Id = Ids.Hash(m.Id) });
            }

            // ---- npcs (buffers are re-fetched after structural changes: handles are invalidated by them)
            var refs = new List<EntityRef>();
            foreach (var npc in c.Npcs)
            {
                var e = em.CreateEntity();
                em.AddComponentData(e, new NpcTag { Id = npc.Id, NameText = npc.NameText });
                em.AddComponentData(e, new NpcLocation { HomeNode = npc.Home });
                em.AddBuffer<ScheduleBlock>(e);
                em.AddBuffer<NpcPlace>(e);
                em.AddBuffer<StatValue>(e);
                em.AddBuffer<InventorySlot>(e);
                em.AddComponentData(e, new NpcInfo { Age = npc.Age, Sex = npc.Sex });
                em.AddComponentData(e, new NpcAuto { Enabled = (byte)(npc.AutoQuests ? 1 : 0), Goal = npc.Goal });
                em.AddBuffer<RelationValue>(e);
                em.AddBuffer<NpcMemory>(e);
                var sched = em.GetBuffer<ScheduleBlock>(e);
                foreach (var b in npc.Blocks) sched.Add(b);
                var places = em.GetBuffer<NpcPlace>(e);
                foreach (var p in npc.Places) places.Add(p);
                var stats = em.GetBuffer<StatValue>(e);
                foreach (var s in npc.Stats) stats.Add(s);
                var items = em.GetBuffer<InventorySlot>(e);
                foreach (var it in npc.Items) items.Add(it);
                var relations = em.GetBuffer<RelationValue>(e);
                foreach (var r in npc.Relations) relations.Add(r);

                NpcLogic.Resolve(sched, places, rules.StartMinute, rules.StartDayOfWeek, npc.Home, out int node, out int act);
                em.SetComponentData(e, new NpcLocation { Node = node, Goal = node, HomeNode = npc.Home, Activity = act });
                refs.Add(new EntityRef { Id = npc.Id, Entity = e });
            }
            refs.Sort((x, y) => x.Id.CompareTo(y.Id));      // sorted by id for binary search
            var index = em.AddBuffer<EntityRef>(world);
            foreach (var r in refs) index.Add(r);

            return new Handles { World = world, Player = player };
        }
    }

    public struct NodeTag : IComponentData { public int Id; }
}
