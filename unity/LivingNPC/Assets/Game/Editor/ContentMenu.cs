using System.IO;
using System.Linq;
using System.Text;
using LivingNPC.Content;
using LivingNPC.Simulation;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace LivingNPC.EditorTools
{
    public static class ContentMenu
    {
        [MenuItem("LivingNPC/Validate Content")]
        public static void ValidateContent()
        {
            var db = ContentLoader.Load();
            var compiled = ContentCompiler.Compile(db);
            var text = db.Report.ToText();
            Debug.Log(text);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "livingnpc_content_report.txt"), text);
            compiled.Dispose();
            EditorUtility.DisplayDialog("LivingNPC", $"{db.Report.ErrorCount} errors, {db.Report.WarningCount} warnings.\nDetails in the Console.", "OK");
        }

        [MenuItem("LivingNPC/Choose World...")]
        public static void ChooseWorld()
        {
            var dir = ContentLoader.WorldsDir();
            var picked = EditorUtility.OpenFolderPanel($"World (current: {ContentLoader.SelectedWorld})", dir, "");
            if (string.IsNullOrEmpty(picked)) return;
            if (!Path.GetFullPath(Path.GetDirectoryName(picked)).Equals(Path.GetFullPath(dir), System.StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("LivingNPC", $"Pick a subfolder of:\n{dir}", "OK");
                return;
            }
            ContentLoader.SelectedWorld = Path.GetFileName(picked);
            Debug.Log($"LivingNPC: selected world = {ContentLoader.SelectedWorld}");
        }

        [MenuItem("LivingNPC/Open Mods Folder (project)")]
        public static void OpenModsFolder()
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Mods"));
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        [MenuItem("LivingNPC/Open Persistent Data (user mods + report)")]
        public static void OpenPersistent()
        {
            Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, "Mods"));
            EditorUtility.RevealInFinder(Application.persistentDataPath);
        }

        /// <summary>
        /// Headless end-to-end check, usable from CI:
        /// Unity -batchmode -projectPath . -executeMethod LivingNPC.EditorTools.ContentMenu.SmokeTest -quit -logFile -
        /// </summary>
        public static void SmokeTest()
        {
            var sb = new StringBuilder();
            int exit = 0;
            World world = null;
            CompiledContent compiledForDispose = null;
            try
            {
                var db = ContentLoader.Load();
                var compiled = ContentCompiler.Compile(db);
                compiledForDispose = compiled;
                sb.AppendLine(db.Report.ToText());
                if (!compiled.Valid) throw new System.Exception("content invalid");
                if (db.Report.ErrorCount > 0) exit = 2;

                world = DefaultWorldInitialization.Initialize("SmokeWorld", false);
                var em = world.EntityManager;
                var h = WorldBuilder.Build(em, compiled);

                void Pump(int max = 5000) { for (int i = 0; i < max; i++) { world.Update(); var c = em.GetComponentData<ClockState>(h.World); var w = em.GetComponentData<WorldState>(h.World); if (c.Pending == 0 && w.DialogOpen == 0 && em.GetBuffer<GameCommand>(h.World).Length == 0 && em.GetComponentData<PlayerState>(h.Player).PendingNode == 0 && w.Dirty == 0) { world.Update(); break; } } }
                void Cmd(CommandType t, int a = 0) => em.GetBuffer<GameCommand>(h.World).Add(new GameCommand { Type = t, A = a });
                void Dump(string title)
                {
                    var clock = em.GetComponentData<ClockState>(h.World);
                    var p = em.GetComponentData<PlayerState>(h.Player);
                    sb.AppendLine($"--- {title}: {GameTime.Format(clock.Now, clock.StartDayOfWeek)} player@{Ids.Name(p.Node)}");
                    var idx = em.GetBuffer<EntityRef>(h.World);
                    foreach (var r in idx)
                    {
                        var l = em.GetComponentData<NpcLocation>(r.Entity);
                        var st = em.GetBuffer<StatValue>(r.Entity);
                        sb.AppendLine($"   {Ids.Name(r.Id),-8} at {(l.Node == 0 ? "->" + Ids.Name(l.NextNode) : Ids.Name(l.Node)),-18} goal {Ids.Name(l.Goal),-16} act {Ids.Name(l.Activity),-9} hap {st[0].Value:0.0} en {st[1].Value:0.0}");
                    }
                    var qs = em.GetBuffer<QuestState>(h.World);
                    for (int i = 0; i < qs.Length; i++) sb.AppendLine($"   quest#{i} status {(QuestStatus)qs[i].Status} stage {qs[i].Stage}");
                    var msgs = em.GetBuffer<GameMessage>(h.World);
                    foreach (var m in msgs.AsNativeArray().ToArray().Skip(System.Math.Max(0, msgs.Length - 4)))
                        sb.AppendLine($"   msg[{m.Kind}] {TextTable.Get(m.TextId)}");
                }
                int Link(string id) => Ids.Hash(id);

                // UI smoke: build the view, bind, refresh against live ECS state (no exceptions expected)
                var uiRoot = new GameObject("SmokeUI");
                var view = new LivingNPC.Presentation.GameView(uiRoot.transform);
                view.Bind(em, h.World, h.Player, db, compiled);

                // the scenario assumes the square; the start node is content-defined
                em.SetComponentData(h.Player, new PlayerState { Node = Ids.Hash("square_fountain"), Arrived = 1 });
                Pump(); view.Tick(); Dump("start");
                Cmd(CommandType.Wait, 60 * 9); Pump(); Dump("after waiting 9h (Mon ~17:05)");
                Cmd(CommandType.Talk, Ids.Hash("lia")); Pump(); view.Tick();
                var ws = em.GetComponentData<WorldState>(h.World);
                sb.AppendLine($"dialog open: {ws.DialogOpen} (expected 1)");
                if (ws.DialogOpen == 0) exit = 3;
                var choices = em.GetBuffer<DialogChoice>(h.World);
                Cmd(CommandType.Choose, choices[0].ChoiceIndex); Pump(); Dump("after accepting Lia's quest");
                Cmd(CommandType.Wait, 150); Pump();
                Cmd(CommandType.Travel, Link("square_fountain_to_alley")); Pump(); view.Tick(); Dump("arrived in the alley ~19:45");
                var q0 = em.GetBuffer<QuestState>(h.World)[db.QuestList.FindIndex(q => q.Id == "q_lost_notebook")];
                sb.AppendLine($"notebook quest stage: {q0.Stage} (expected 1 = return)");
                if (q0.Stage != 1) exit = 4;
                Cmd(CommandType.Wait, 60 * 24 * 4 + 60 * 3); Pump(); Dump("after a few days");
                // inventory: starting items, give/take clamping, condition, save round trip
                var rt = RuleRuntime.Create(em, h.World, h.Player);
                int bread = Ids.Hash("bread"), potion = Ids.Hash("potion_health"), anselmo = Ids.Hash("anselmo");
                void Expect(string what, int got, int want) { sb.AppendLine($"inventory {what}: {got} (expected {want})"); if (got != want) exit = 5; }
                Expect("player bread (start)", rt.GetItemCount(0, bread), 2);
                Expect("anselmo potion (start)", rt.GetItemCount(anselmo, potion), 2);
                Expect("give 3 bread", rt.AddItem(0, bread, 3), 3);
                Expect("take 10 bread (clamped)", rt.AddItem(0, bread, -10), -5);
                Expect("player bread (empty)", rt.GetItemCount(0, bread), 0);
                Expect("slot removed", em.GetBuffer<InventorySlot>(h.Player).Length, 0);
                rt.AddItem(0, potion, 4); rt.AddItem(anselmo, bread, 1);
                var invSave = SaveGame.Capture(new SaveContext(em, h.World, h.Player, compiled));
                rt.AddItem(0, potion, -4); rt.AddItem(anselmo, bread, -1);
                SaveGame.Apply(new SaveContext(em, h.World, h.Player, compiled), invSave);
                Expect("restored player potion", rt.GetItemCount(0, potion), 4);
                Expect("restored anselmo bread", rt.GetItemCount(anselmo, bread), 1);
                Expect("restored anselmo potion", rt.GetItemCount(anselmo, potion), 2);

                // save / load: round trip through a file, tolerance (missing / unknown / newer data), backup, rest check.
                // Old formats are not kept loadable: a layout change raises SaveGame.FormatVersion (and updates the sample in step 3).
                {
                    void Check(string label, bool ok, int code) { sb.AppendLine($"save {label}: {(ok ? "ok" : "FAILED")}"); if (!ok) exit = code; }
                    void Fresh() { em.DestroyEntity(em.UniversalQuery); h = WorldBuilder.Build(em, compiled); }
                    int Flag(string key) => (int)(RuleRuntime.Create(em, h.World, h.Player).GetFlag(Ids.Hash(key)) * 10);     // tenths, to compare floats exactly

                    var svPath = Path.Combine(Path.GetTempPath(), "livingnpc_save_smoke.json");
                    foreach (var f in new[] { svPath, svPath + ".bak", svPath + ".tmp", svPath + "_other.json" }) if (File.Exists(f)) File.Delete(f);

                    rt.SetFlag(Ids.Hash("smoke_save_flag"), 3.5f);
                    Check("allowed at rest", SaveGame.CanSave(em, h.World, h.Player, out _), 11);
                    var svWs = em.GetComponentData<WorldState>(h.World);
                    svWs.DialogOpen = 1; em.SetComponentData(h.World, svWs);
                    Check("refused while a dialog is open", !SaveGame.CanSave(em, h.World, h.Player, out _), 11);
                    svWs.DialogOpen = 0; em.SetComponentData(h.World, svWs);

                    var svBefore = SaveGame.Capture(new SaveContext(em, h.World, h.Player, compiled));
                    SaveGame.Write(svPath, svBefore);
                    SaveGame.Write(svPath, svBefore);
                    Check("previous save kept as .bak", File.Exists(svPath + ".bak"), 12);
                    int svNow = em.GetComponentData<ClockState>(h.World).Now;
                    int svNode = em.GetComponentData<PlayerState>(h.Player).Node;
                    sb.AppendLine($"   saved: {svBefore["meta"]}");

                    // 1. brand new world from the same content, then load the file
                    Fresh();
                    var svFile = SaveGame.Read(svPath, out var svProblem);
                    Check("file readable" + (svProblem != null ? " (" + svProblem + ")" : ""), svFile != null, 12);
                    var svCtx = new SaveContext(em, h.World, h.Player, compiled);
                    SaveGame.Apply(svCtx, svFile);
                    foreach (var w in svCtx.Warnings) sb.AppendLine("   warning: " + w);
                    var svAfter = SaveGame.Capture(svCtx);
                    var svDiff = ((Newtonsoft.Json.Linq.JObject)svBefore["sections"]).Properties()
                        .Where(sec => !Newtonsoft.Json.Linq.JToken.DeepEquals(sec.Value, svAfter["sections"][sec.Name])).Select(sec => sec.Name).ToList();
                    Check("round trip identical" + (svDiff.Count > 0 ? " (differs: " + string.Join(", ", svDiff) + ")" : ""), svDiff.Count == 0, 13);
                    Check("clock restored", em.GetComponentData<ClockState>(h.World).Now == svNow, 13);
                    Check("player place restored", em.GetComponentData<PlayerState>(h.Player).Node == svNode, 13);
                    Check("flag restored", Flag("smoke_save_flag") == 35, 13);
                    Check("enter-node events not replayed", em.GetComponentData<PlayerState>(h.Player).Arrived == 0, 13);

                    // 2. missing section tolerated, unknown section kept and written back
                    Fresh();
                    var svPartial = (Newtonsoft.Json.Linq.JObject)svBefore.DeepClone();
                    ((Newtonsoft.Json.Linq.JObject)svPartial["sections"]).Remove("npcs");
                    ((Newtonsoft.Json.Linq.JObject)svPartial["sections"])["economy"] = Newtonsoft.Json.Linq.JObject.Parse("{\"gold\":5}");
                    var svCtx2 = new SaveContext(em, h.World, h.Player, compiled);
                    SaveGame.Apply(svCtx2, svPartial);
                    Check("missing npcs section: clock still restored", em.GetComponentData<ClockState>(h.World).Now == svNow, 14);
                    bool svPlaced = true;
                    foreach (var r in em.GetBuffer<EntityRef>(h.World)) { var l = em.GetComponentData<NpcLocation>(r.Entity); if (l.Node == 0) svPlaced = false; }
                    Check("npcs missing from the save placed by their routine", svPlaced, 14);
                    var svAgain = SaveGame.Capture(svCtx2);
                    Check("unknown section carried over", (int?)svAgain["sections"]["economy"]?["gold"] == 5, 14);
                    SaveGame.ClearCarried();
                    Check("a new game forgets the carried section", SaveGame.Capture(svCtx2)["sections"]["economy"] == null, 14);

                    // 3. a hand-written save: the layout the sections read (and an example of it)
                    Fresh();
                    const string svHand = "{\"format\":\"livingnpc-save\",\"version\":2,\"sections\":{\"clock\":{\"now\":4000},\"flags\":{\"smoke_old\":2},"
                        + "\"quests\":{\"q_lost_notebook\":{\"status\":\"active\",\"stage\":\"return\",\"startedAt\":3800,\"stageAt\":3900}},"
                        + "\"messages\":[{\"at\":3900,\"kind\":\"questUpdated\",\"text\":\"quest:q_lost_notebook:stage:return\"}],"
                        + "\"player\":{\"node\":\"square_fountain\",\"items\":{\"bread\":7}}}}";
                    var handCtx = new SaveContext(em, h.World, h.Player, compiled);
                    SaveGame.Apply(handCtx, Newtonsoft.Json.Linq.JObject.Parse(svHand));
                    foreach (var w in handCtx.Warnings) sb.AppendLine("   warning: " + w);
                    Check("hand-written save loads", em.GetComponentData<ClockState>(h.World).Now == 4000 && Flag("smoke_old") == 20, 15);
                    int notebook = db.QuestList.FindIndex(q => q.Id == "q_lost_notebook");
                    var handQuest = em.GetBuffer<QuestState>(h.World)[notebook];
                    Check("quest stage and times", handQuest.Status == (byte)QuestStatus.Active && handQuest.Stage == 1 && handQuest.StageAt == 3900 && handQuest.StartedAt == 3800, 15);
                    var handMsgs = em.GetBuffer<GameMessage>(h.World);
                    int stage1Text = compiled.Rules.Value.Stages[compiled.Rules.Value.Quests[notebook].StageStart + 1].TextId;
                    Check("message text found by stage id", handMsgs.Length == 1 && handMsgs[0].TextId == stage1Text, 15);
                    Check("player items", RuleRuntime.Create(em, h.World, h.Player).GetItemCount(0, Ids.Hash("bread")) == 7, 15);

                    // 4. files: an older or newer format is refused, a damaged file falls back to its backup
                    foreach (int other in new[] { SaveGame.FormatVersion - 1, SaveGame.FormatVersion + 1 })
                    {
                        var svOther = (Newtonsoft.Json.Linq.JObject)svBefore.DeepClone();
                        svOther["version"] = other;
                        File.WriteAllText(svPath + "_other.json", svOther.ToString());
                        Check($"format {other} refused", SaveGame.Read(svPath + "_other.json", out var svOtherProblem) == null && svOtherProblem != null, 16);
                    }
                    File.WriteAllText(svPath, "{ not json");
                    Check("damaged file falls back to .bak", SaveGame.Read(svPath, out _) != null, 16);

                    // back to the state the rest of the test expects, and prove the view follows a restored world
                    Fresh();
                    SaveGame.Apply(new SaveContext(em, h.World, h.Player, compiled), SaveGame.Read(svPath + ".bak", out _));
                    rt = RuleRuntime.Create(em, h.World, h.Player);
                    view.Bind(em, h.World, h.Player, db, compiled);
                    Pump(); view.Tick();
                    foreach (var f in new[] { svPath, svPath + ".bak", svPath + ".tmp", svPath + "_other.json" }) if (File.Exists(f)) File.Delete(f);
                }

                // sleeping: beds only, the sleep window, waking at wakeAt, effects on waking, no dialogue during the sleep.
                // With LIVINGNPC_MODS=<folder holding the pack smoke_sleep: events ev_smoke_wake (wake in inn_bedroom: setFlag smoke_woke + advanceTimeTo 09:00)
                // and ev_smoke_night (tick popup in inn_bedroom 02:00-03:00)> it also checks the wake trigger, advanceTimeTo and the sleeper's peace.
                {
                    void Check(string label, bool ok) { sb.AppendLine($"sleep {label}: {(ok ? "ok" : "FAILED")}"); if (!ok) exit = 18; }
                    int bedroom = Ids.Hash("inn_bedroom"), square = Ids.Hash("square_fountain"), energy = Ids.Hash("energy");
                    bool wakeFixture = db.EventList.Any(e => e.Id == "ev_smoke_wake");
                    int wakeMinute = wakeFixture ? 9 * 60 : 7 * 60;
                    ClockState Clk() => em.GetComponentData<ClockState>(h.World);
                    void GoTo(int node) { em.SetComponentData(h.Player, new PlayerState { Node = node, Arrived = 1 }); Pump(); }
                    void WaitUntil(int hour) { int m = RuleRuntime.MinutesUntil(Clk().Now, hour * 60); if (m > 0) { Cmd(CommandType.Wait, m); Pump(); } }

                    GoTo(square); WaitUntil(12);
                    Check("not in the daytime", !RuleRuntime.CanSleep(compiled.Rules, bedroom, Clk().Now, out _));
                    WaitUntil(21);
                    Check("no bed at the square", !RuleRuntime.CanSleep(compiled.Rules, square, Clk().Now, out _));
                    int t0 = Clk().Now;
                    Cmd(CommandType.Sleep); Pump();
                    Check("command ignored without a bed", Clk().Now == t0 && Clk().Sleeping == 0);
                    Check("bed at 21:00 sleeps 10 hours", RuleRuntime.CanSleep(compiled.Rules, bedroom, Clk().Now, out int sleepMinutes) && sleepMinutes == 600);

                    GoTo(bedroom);
                    rt.SetStat(0, energy, 10);
                    t0 = Clk().Now;
                    Cmd(CommandType.Sleep); Pump(); view.Tick();
                    var slept = Clk();
                    var sleptWs = em.GetComponentData<WorldState>(h.World);
                    Check($"wakes at {GameTime.Clock(wakeMinute)} of the next day ({GameTime.Format(slept.Now, slept.StartDayOfWeek)})", slept.Now == t0 + 600 + (wakeMinute - 7 * 60) && slept.Now % GameTime.MinutesPerDay == wakeMinute && slept.Sleeping == 0);
                    Check("nothing interrupted the sleep", sleptWs.DialogOpen == 0);
                    Check("energy restored on waking", rt.GetStat(0, energy) == 100);
                    Check("still in the bedroom", em.GetComponentData<PlayerState>(h.Player).Node == bedroom);
                    if (wakeFixture) Check("wake event ran", rt.GetFlag(Ids.Hash("smoke_woke")) == 1);

                    GoTo(square); WaitUntil(2);         // away from the bedroom: the test event of the pack would open a dialogue at 02:00 there
                    Check("bed at 02:00 sleeps until 07:00 of the same day", RuleRuntime.CanSleep(compiled.Rules, bedroom, Clk().Now, out sleepMinutes) && sleepMinutes == 300);
                }

                // elapsed / after conditions: runs only when the test quest q_smoke_elapsed exists (pack smoke_sleep: stage a = elapsed since the stage
                // 2h, stage b = elapsed since the quest 5h, stage c = after file-day 40 06:30)
                int elapsedQ = db.QuestList.FindIndex(q => q.Id == "q_smoke_elapsed");
                if (elapsedQ >= 0)
                {
                    void Check(string label, bool ok) { sb.AppendLine($"elapsed {label}: {(ok ? "ok" : "FAILED")}"); if (!ok) exit = 19; }
                    ClockState Clk() => em.GetComponentData<ClockState>(h.World);
                    QuestState Qs() => em.GetBuffer<QuestState>(h.World)[elapsedQ];
                    void Wait(int minutes) { while (minutes > 0) { int step = System.Math.Min(minutes, GameTime.MinutesPerDay); Cmd(CommandType.Wait, step); Pump(); minutes -= step; } }

                    em.SetComponentData(h.Player, new PlayerState { Node = Ids.Hash("square_fountain"), Arrived = 1 }); Pump();
                    rt.StartQuest(elapsedQ); Pump();
                    int began = Clk().Now;
                    Check("start recorded", Qs().StartedAt == began && Qs().StageAt == began);
                    Wait(119); Check("stage a still waiting after 1h59", Qs().Stage == 0);
                    Wait(1); Check("stage a done after 2h", Qs().Stage == 1);
                    Check("stage start moved, quest start kept", Qs().StageAt == began + 120 && Qs().StartedAt == began);
                    var elapsedSave = SaveGame.Capture(new SaveContext(em, h.World, h.Player, compiled));
                    Check("save keeps the quest start", (int?)elapsedSave["sections"]["quests"]["q_smoke_elapsed"]?["startedAt"] == began);
                    Wait(179); Check("stage b counts from the quest start: not done at 4h59", Qs().Stage == 1);
                    Wait(1); Check("stage b done at 5h after the quest began", Qs().Stage == 2);
                    int target = 40 * GameTime.MinutesPerDay + 6 * 60 + 30;
                    Check("test starts before the target day", Clk().Now < target - 1);
                    Wait(target - 1 - Clk().Now); Check("after: not yet at 06:29", Qs().Status == (byte)QuestStatus.Active);
                    Wait(1); Check($"after: done at {GameTime.Format(Clk().Now, Clk().StartDayOfWeek)}", Qs().Status == (byte)QuestStatus.Completed);
                }

                // routine changes (setPlace / addRoutine / removeRoutine / setRoutine): runs only when the test quest exists
                // (LIVINGNPC_MODS=<folder holding the pack smoke_routine: npc smoke_worker + quest q_smoke_routine, see SmokeFixtures/README.md>)
                int routineQ = db.QuestList.FindIndex(q => q.Id == "q_smoke_routine");
                if (routineQ >= 0)
                {
                    int worker = Ids.Hash("smoke_worker");
                    int startDow = em.GetComponentData<ClockState>(h.World).StartDayOfWeek;
                    int monday = (((7 - startDow) % 7) + 7) * GameTime.MinutesPerDay;       // a Monday in the second week
                    Entity Worker() { foreach (var r in em.GetBuffer<EntityRef>(h.World)) if (r.Id == worker) return r.Entity; return Entity.Null; }
                    string Where(int hour, int minute = 0)
                    {
                        var e = Worker();
                        NpcLogic.Resolve(em.GetBuffer<ScheduleBlock>(e, true), em.GetBuffer<NpcPlace>(e, true), monday + hour * 60 + minute, startDow,
                            em.GetComponentData<NpcLocation>(e).HomeNode, out int n, out int a);
                        return Ids.Name(n) + (a != 0 ? "/" + Ids.Name(a) : "");
                    }
                    void ExpectAt(string label, string got, string want) { sb.AppendLine($"routine {label}: {got} (expected {want})"); if (got != want) exit = 17; }

                    ExpectAt("work (content)", Where(9), "blacksmith/work");
                    ExpectAt("evening (content)", Where(19), "tavern");
                    ExpectAt("night (content)", Where(3), "inn_bedroom");
                    rt.StartQuest(routineQ);                                    // setPlace work -> tavern
                    ExpectAt("work place moved, shared schedule follows", Where(9), "tavern/work");
                    rt.AdvanceQuest(routineQ);                                  // setPlace home -> church
                    ExpectAt("home moved", Where(3), "church");
                    if (em.GetComponentData<NpcLocation>(Worker()).HomeNode != Ids.Hash("church")) { sb.AppendLine("routine home node not updated"); exit = 17; }
                    rt.AdvanceQuest(routineQ);                                  // addRoutine with an existing id: edits the evening block
                    ExpectAt("evening block edited", Where(19), "inn");
                    int evening = 0;
                    foreach (var b in em.GetBuffer<ScheduleBlock>(Worker(), true)) if (b.Tag == Ids.Hash("evening")) evening++;
                    sb.AppendLine($"routine evening blocks: {evening} (expected 1)"); if (evening != 1) exit = 17;
                    rt.AdvanceQuest(routineQ);                                  // removeRoutine work_day
                    ExpectAt("shared schedule removed", Where(9), "church");

                    // the changes survive a save and load into a fresh world
                    var routineSave = SaveGame.Capture(new SaveContext(em, h.World, h.Player, compiled));
                    em.DestroyEntity(em.UniversalQuery);
                    h = WorldBuilder.Build(em, compiled);
                    var routineCtx = new SaveContext(em, h.World, h.Player, compiled);
                    SaveGame.Apply(routineCtx, routineSave);
                    foreach (var w in routineCtx.Warnings) sb.AppendLine("   warning: " + w);
                    rt = RuleRuntime.Create(em, h.World, h.Player);
                    ExpectAt("after load: home", Where(3), "church");
                    ExpectAt("after load: evening", Where(19), "inn");
                    ExpectAt("after load: shared schedule still removed", Where(9), "church");
                    if (em.GetComponentData<NpcLocation>(Worker()).HomeNode != Ids.Hash("church")) { sb.AppendLine("routine home node lost on load"); exit = 17; }

                    rt.AdvanceQuest(routineQ);                                  // setRoutine: replaces everything
                    ExpectAt("setRoutine drops the old blocks", Where(19), "church");
                    ExpectAt("setRoutine block follows $work", Where(6, 30), "tavern");
                    rt.AdvanceQuest(routineQ);                                  // setPlace job (a place the npc never had) + block at $job
                    ExpectAt("place created by an effect", Where(12, 30), "guild");
                    view.Bind(em, h.World, h.Player, db, compiled);
                    Pump(); view.Tick();
                }

                // commands in sequence: runs only when the test event exists (LIVINGNPC_MODS with the pack smoke_sequence: an effect between
                // two lines, if / else on a flag, a choice with a hidden option and nested lines, and a script without lines)
                int seqEvent = -1, silentEvent = -1;
                for (int i = 0; i < compiled.Rules.Value.Events.Length; i++)
                {
                    if (compiled.Rules.Value.Events[i].Id == Ids.Hash("ev_smoke_sequence")) seqEvent = i;
                    if (compiled.Rules.Value.Events[i].Id == Ids.Hash("ev_smoke_silent")) silentEvent = i;
                }
                if (seqEvent >= 0 && silentEvent >= 0)
                {
                    void Check(string label, bool ok) { sb.AppendLine($"sequence {label}: {(ok ? "ok" : "FAILED")}"); if (!ok) exit = 21; }
                    WorldState Ws() => em.GetComponentData<WorldState>(h.World);
                    string Line() => Ws().DialogOpen != 0 ? TextTable.Get(Ws().DialogText) : "(closed)";
                    int Choices() => em.GetBuffer<DialogChoice>(h.World).Length;
                    int ChoiceNamed(string text) { foreach (var c in em.GetBuffer<DialogChoice>(h.World)) if (TextTable.Get(c.TextId) == text) return c.ChoiceIndex; return -1; }
                    int pick = Ids.Hash("smoke_seq_pick");        // bread: from the inventory checks above

                    rt.SetFlag(Ids.Hash("smoke_seq"), 0);
                    int breadBefore = rt.GetItemCount(0, bread);
                    rt.RunEvent(seqEvent);
                    Check("first line, effect after it not yet run", Line() == "uno" && rt.GetItemCount(0, bread) == breadBefore);
                    rt.Advance();
                    Check("effect between the lines, else branch", rt.GetItemCount(0, bread) == breadBefore + 1 && Line() == "flag no");
                    rt.Advance();
                    Check("choice shown with its line, hidden option left out", Line() == "scegli" && Choices() == 2);
                    rt.Advance();
                    Check("a click does not skip the choice", Line() == "scegli");
                    rt.Choose(ChoiceNamed("A"));
                    Check("option A: its effect and its own line", rt.GetFlag(pick) == 1 && Line() == "hai scelto A");
                    rt.Advance();
                    Check("after the choice the script goes on", Line() == "fine");
                    rt.Advance();
                    Check("end closes the dialogue", Ws().DialogOpen == 0);

                    rt.SetFlag(Ids.Hash("smoke_seq"), 1);
                    rt.RunEvent(seqEvent); rt.Advance();
                    Check("then branch", Line() == "flag si");
                    rt.Advance(); rt.Choose(ChoiceNamed("B"));
                    Check("option B without lines goes straight on", rt.GetFlag(pick) == 2 && Line() == "fine");
                    rt.Advance();

                    rt.RunEvent(silentEvent);
                    Check("script without lines runs and opens nothing", rt.GetFlag(Ids.Hash("smoke_silent")) == 1 && Ws().DialogOpen == 0
                        && compiled.Rules.Value.Events[silentEvent].HasDialog == 0 && compiled.Rules.Value.Events[seqEvent].HasDialog == 1);
                    Pump(); view.Tick();
                }

                // showImage / hideImage: runs only when a test quest "q_smoke_image" exists
                // (e.g. LIVINGNPC_MODS=<folder with a pack whose quest shows image key "smoke" on start and hides it on stage complete)
                int smokeQ = db.QuestList.FindIndex(q => q.Id == "q_smoke_image");
                if (smokeQ >= 0)
                {
                    var overlay = uiRoot.transform.Find("LivingNPCCanvas/OverlayImages");
                    int imageKey = Ids.ImageKey("smoke");
                    rt.StartQuest(smokeQ); Pump(); view.Tick();
                    sb.AppendLine($"overlay after showImage: flag {rt.GetFlag(imageKey)}, images on screen {overlay.childCount} (expected flag >= 1, 1 image)");
                    if (rt.GetFlag(imageKey) < 1 || overlay.childCount != 1) exit = 6;
                    rt.AdvanceQuest(smokeQ); Pump(); view.Tick();
                    sb.AppendLine($"overlay after hideImage: flag {rt.GetFlag(imageKey)}, images on screen {overlay.childCount} (expected flag 0, 0 images)");
                    if (rt.GetFlag(imageKey) != 0 || overlay.childCount != 0) exit = 7;
                }

                // npc pictures: runs only when the test npc "smoke_npc" exists (a pack with images/ and an npc that uses them)
                var smokeNpc = compiled.Npcs.Find(n => n.Def.Id == "smoke_npc");
                if (smokeNpc != null)
                {
                    int npcId = Ids.Hash("smoke_npc"), relax = Ids.Hash("relax");
                    Entity npcEntity = Entity.Null;
                    foreach (var r in em.GetBuffer<EntityRef>(h.World)) if (r.Id == npcId) npcEntity = r.Entity;
                    int node = em.GetComponentData<NpcLocation>(npcEntity).Node;
                    var plain = smokeNpc.Images.OnMap(0, node);
                    var relaxed = smokeNpc.Images.OnMap(relax, node);
                    var relaxedElsewhere = smokeNpc.Images.OnMap(relax, Ids.Hash("alley"));
                    sb.AppendLine($"npc pictures: default {Path.GetFileName(plain?.Path)}, relax {Path.GetFileName(relaxed?.Path)} x={relaxed?.X} (alley x={relaxedElsewhere?.X}) (expected smoke_default.png, smoke_relax.png x=77, alley x=10)");
                    if (plain == null || !plain.Path.EndsWith("smoke_default.png") || relaxed == null || !relaxed.Path.EndsWith("smoke_relax.png")) exit = 8;
                    if (relaxed == null || relaxedElsewhere == null || relaxed.X != 77 || relaxedElsewhere.X != 10) exit = 8;

                    em.SetComponentData(h.Player, new PlayerState { Node = node, Arrived = 1 });
                    rt.Touch(); view.Tick();
                    var npcLayer = uiRoot.transform.Find("LivingNPCCanvas/Map/NpcImages");
                    var portrait = uiRoot.transform.Find("LivingNPCCanvas/PortraitImages");
                    var onMap = npcLayer.Find("Npc_" + npcId);
                    bool clickable = onMap != null && onMap.GetComponent("Button") != null && onMap.GetComponent("AlphaHit") != null;
                    sb.AppendLine($"npc on map: {npcLayer.childCount} picture(s), clickable {clickable} (expected 1, True)");
                    if (npcLayer.childCount != 1 || !clickable) exit = 9;

                    Cmd(CommandType.Talk, npcId); Pump(); view.Tick();
                    var talkState = em.GetComponentData<WorldState>(h.World);
                    sb.AppendLine($"talking: dialog {talkState.DialogOpen}, speaker {Ids.Name(talkState.DialogNpc)}, map pictures {npcLayer.childCount}, talk pictures {portrait.childCount} (expected 1, smoke_npc, 0, 1)");
                    if (talkState.DialogOpen != 1 || npcLayer.childCount != 0 || portrait.childCount != 1) exit = 10;
                }

                view.Audio.Dispose();
                Object.DestroyImmediate(uiRoot);
                LivingNPC.Presentation.TextureCache.Clear();
                sb.AppendLine("SMOKE OK");
            }
            catch (System.Exception e)
            {
                sb.AppendLine("SMOKE FAILED: " + e);
                exit = 1;
            }
            finally { world?.Dispose(); compiledForDispose?.Dispose(); }
            Debug.Log(sb.ToString());
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "livingnpc_smoke.txt"), sb.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(exit);
        }

        /// <summary>
        /// Sounds only: Unity -batchmode -nographics -projectPath . -executeMethod LivingNPC.EditorTools.ContentMenu.SoundSmokeTest -quit
        /// Needs LIVINGNPC_MODS=<project>/SmokeFixtures/smoke_mods (pack smoke_sound: sfx/click.wav, sfx/rain.wav, music/theme.wav, music/theme2.wav; quest q_smoke_sound
        /// starts music theme.wav, ambient rain and plays click.wav, and on its first stage's completion changes the music to theme2.wav and stops the rain;
        /// event ev_smoke_sound_script has a sound on each of its two lines). Playing needs an audio device, so this checks the state, not the noise.
        /// Result in %TEMP%/livingnpc_sound_smoke.txt.
        /// </summary>
        public static void SoundSmokeTest()
        {
            var sb = new StringBuilder();
            int exit = 0;
            World world = null;
            CompiledContent compiled = null;
            GameObject uiRoot = null;
            LivingNPC.Presentation.GameView view = null;
            void Check(string label, bool ok) { sb.AppendLine($"sound {label}: {(ok ? "ok" : "FAILED")}"); if (!ok) exit = 20; }
            try
            {
                var db = ContentLoader.Load();
                compiled = ContentCompiler.Compile(db);
                foreach (var e in db.Report.Entries) if (e.Severity == Severity.Error && e.Source.Contains("smoke_sound")) sb.AppendLine($"   content error: {e.Source}: {e.Message}");
                if (!compiled.Valid) throw new System.Exception("content invalid");
                int soundQ = db.QuestList.FindIndex(q => q.Id == "q_smoke_sound");
                if (soundQ < 0) throw new System.Exception("quest q_smoke_sound not found: set LIVINGNPC_MODS to SmokeFixtures/smoke_mods");

                world = DefaultWorldInitialization.Initialize("SoundSmokeWorld", false);
                var em = world.EntityManager;
                var h = WorldBuilder.Build(em, compiled);
                void Pump() { for (int i = 0; i < 50; i++) world.Update(); }
                uiRoot = new GameObject("SmokeUI");
                view = new LivingNPC.Presentation.GameView(uiRoot.transform);
                view.Bind(em, h.World, h.Player, db, compiled);
                var rt = RuleRuntime.Create(em, h.World, h.Player);
                int music = Ids.MusicKey(), rain = Ids.AmbientKey("rain");

                Pump(); view.Tick();
                Check("nothing plays at the start", view.Audio.LoopCount == 0 && em.GetBuffer<SoundCue>(h.World).Length == 0);
                rt.StartQuest(soundQ);
                Check("flags of music and rain set", rt.GetFlag(music) >= 1 && rt.GetFlag(rain) >= 1);
                Check("one-shot queued as a cue", em.GetBuffer<SoundCue>(h.World).Length == 1);
                Pump(); view.Tick();
                Check("cues played and cleared", em.GetBuffer<SoundCue>(h.World).Length == 0);
                Check("music and rain loop", view.Audio.LoopCount == 2 && (view.Audio.LoopPath(music) ?? "").EndsWith("theme.wav") && (view.Audio.LoopPath(rain) ?? "").EndsWith("rain.wav"));

                // a sound step plays together with the line that follows it
                int scriptEvent = -1;
                for (int i = 0; i < compiled.Rules.Value.Events.Length; i++) if (compiled.Rules.Value.Events[i].Id == Ids.Hash("ev_smoke_sound_script")) scriptEvent = i;
                Check("script event compiled", scriptEvent >= 0);
                if (scriptEvent >= 0)
                {
                    rt.RunEvent(scriptEvent);
                    Check("script sound plays with the first line", em.GetBuffer<SoundCue>(h.World).Length == 1);
                    rt.Advance();
                    Check("script sound plays with the second line", em.GetBuffer<SoundCue>(h.World).Length == 2);
                    rt.Advance();                                     // closes the dialogue
                    Pump(); view.Tick();
                    Check("script cues played and cleared", em.GetBuffer<SoundCue>(h.World).Length == 0 && em.GetComponentData<WorldState>(h.World).DialogOpen == 0);
                }

                var save = SaveGame.Capture(new SaveContext(em, h.World, h.Player, compiled));
                var saved = save["sections"]["sounds"] as Newtonsoft.Json.Linq.JObject;
                Check("save lists both loops by file", saved != null && saved.Count == 2 && (string)saved["sound:music"]?["file"] == "music/theme.wav" && (string)saved["sound:ambient:rain"]?["file"] == "sfx/rain.wav");
                Check("flags section leaves them out", ((Newtonsoft.Json.Linq.JObject)save["sections"]["flags"]).Properties().All(p => !p.Name.StartsWith("sound:")));

                em.DestroyEntity(em.UniversalQuery);                  // a brand new world, then the save
                h = WorldBuilder.Build(em, compiled);
                var ctx = new SaveContext(em, h.World, h.Player, compiled);
                SaveGame.Apply(ctx, save);
                foreach (var w in ctx.Warnings) sb.AppendLine("   warning: " + w);
                rt = RuleRuntime.Create(em, h.World, h.Player);
                Check("loops restored from the save", rt.GetFlag(music) >= 1 && rt.GetFlag(rain) >= 1 && ctx.Warnings.Count == 0);
                view.Bind(em, h.World, h.Player, db, compiled);
                Pump(); view.Tick();
                Check("reload keeps the same loops playing", view.Audio.LoopCount == 2 && (view.Audio.LoopPath(music) ?? "").EndsWith("theme.wav"));

                rt.AdvanceQuest(soundQ);                              // music changes to theme2, rain stops
                Pump(); view.Tick();
                Check("music changed, rain stopped", view.Audio.LoopCount == 1 && (view.Audio.LoopPath(music) ?? "").EndsWith("theme2.wav") && rt.GetFlag(rain) == 0);

                // a content reload without sounds (F5 after the author removed them) silences the loops
                view.Audio.KeepOnly(new int[0], 0);
                Check("channels the content no longer has are dropped", view.Audio.LoopCount == 0);
                if (exit == 0) sb.AppendLine("SOUND SMOKE OK");
            }
            catch (System.Exception e)
            {
                sb.AppendLine("SOUND SMOKE FAILED: " + e);
                exit = 1;
            }
            finally
            {
                view?.Audio.Dispose();
                if (uiRoot != null) Object.DestroyImmediate(uiRoot);
                world?.Dispose(); compiled?.Dispose();
            }
            Debug.Log(sb.ToString());
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "livingnpc_sound_smoke.txt"), sb.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(exit);
        }
    }
}
