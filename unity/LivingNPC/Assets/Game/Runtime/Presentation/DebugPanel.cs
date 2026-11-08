using System;
using System.Collections.Generic;
using System.Text;
using LivingNPC.Content;
using LivingNPC.Simulation;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace LivingNPC.Presentation
{
    /// <summary>
    /// F12: a look inside the running game, for authors and testers. Where every npc is and why (the routine block that
    /// applies, a forced move), the state of every quest with its conditions evaluated now, the events that ran, the clock,
    /// the flags and the links of the map; at the bottom the <see cref="DebugConsole"/>.
    /// Every button only writes a console command and runs it, so a change made here goes through the same checks and
    /// effects as a quest's, and the log shows how to write it.
    /// The pages are built again only when the world changes (its version or the clock), and only in the Layout pass of
    /// IMGUI, so the controls of a frame never change between its passes.
    /// </summary>
    public sealed class DebugPanel
    {
        enum Tab { Npcs, Player, Quests, Events, World }
        static readonly string[] s_TabNames = { "NPCs", "Player", "Quests", "Events", "World" };

        const string Grey = "#8a919c", Gold = "#f2c75a", Green = "#7fd87f", Red = "#ec7a6f", Blue = "#8fb8ff";
        /// <summary>IMGUI draws one label with at most ~16k characters: longer runs of text are split into pieces this long.</summary>
        const int ChunkChars = 4000;
        const string InputName = "LivingNPCDebugConsole";

        EntityManager _em;
        Entity _world, _player;
        CompiledContent _compiled;
        bool _bound;

        public readonly DebugConsole Console = new DebugConsole();
        public bool IsOpen { get; private set; }

        Tab _tab;
        int _npc;                       // selected npc id
        bool _showNotStarted = true;
        bool _branches = true;          // conditions shown branch by branch, with the values they read now
        string _filter = "";
        string _input = "";
        int _historyIndex = -1;         // -1: writing a new line; otherwise the history entry shown
        bool _focusInput;
        Vector2 _listScroll, _bodyScroll, _logScroll;
        int _seenLog = -1;

        // what the page was built for: built again when any of these changes
        Tab _builtTab; int _builtNpc = -1; uint _builtVersion; int _builtNow = -1, _builtPending = -1; bool _builtNotStarted, _builtBranches; string _builtFilter;
        readonly List<Item> _page = new List<Item>();
        readonly List<(int id, string label)> _npcList = new List<(int, string)>();
        readonly List<DebugConsole.Entry> _logView = new List<DebugConsole.Entry>();
        string _pendingCommand;         // a button clicked this frame: run after the panel is drawn
        bool _debugUsed;

        readonly Dictionary<int, string> _statNames = new Dictionary<int, string>();
        readonly Dictionary<int, string> _activityNames = new Dictionary<int, string>();

        GUIStyle _bodyStyle, _rowStyle, _listStyle, _listSelStyle, _tabStyle, _titleStyle, _toggleStyle, _fieldStyle, _smallButton, _logStyle;
        float _styleScale = -1;

        // ------------------------------------------------------------------ binding

        /// <summary>Points the panel at a freshly built world (new game, load, F5 reload).</summary>
        public void Bind(EntityManager em, Entity world, Entity player, CompiledContent compiled)
        {
            _em = em; _world = world; _player = player; _compiled = compiled;
            _bound = true;
            _builtNpc = -1;             // build the page again
            Console.Bind(em, world, player, compiled);
            _statNames.Clear();
            _activityNames.Clear();
            foreach (var s in compiled.Db.Rules.Stats)
                _statNames[Ids.Hash(s.Id)] = TextTable.Localize(s.Name) is { Length: > 0 } n ? n : s.Id;
            foreach (var a in compiled.Db.Rules.Activities)
                _activityNames[Ids.Hash(a.Id)] = TextTable.Localize(a.Name) is { Length: > 0 } n ? n : a.Id;
            if (_npc != 0 && compiled.Npcs.Find(n => n.Id == _npc) == null) _npc = 0;
        }

        public void Unbind() { _bound = false; _compiled = null; }

        public void Open() { IsOpen = true; _builtNpc = -1; _focusInput = true; }
        public void Close() => IsOpen = false;

        bool Alive => _bound && _compiled != null && _compiled.Rules.IsCreated && _em.Exists(_world) && _em.Exists(_player);

        // ------------------------------------------------------------------ drawing

        public void Draw()
        {
            if (!IsOpen) return;
            float u = Screen.height / 1080f;
            EnsureStyles(u);
            HandleConsoleKeys();

            var area = new Rect(Screen.width * 0.4f, 0, Screen.width * 0.6f, Screen.height);
            var old = GUI.color;
            GUI.color = new Color(0.03f, 0.04f, 0.06f, 0.95f);
            GUI.DrawTexture(area, Texture2D.whiteTexture);
            GUI.color = old;

            float pad = 14 * u;
            GUILayout.BeginArea(new Rect(area.x + pad, area.y + pad, area.width - 2 * pad, area.height - 2 * pad));
            bool alive = Alive;
            if (alive && Event.current.type == EventType.Layout) Rebuild();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Debug  <size={Mathf.RoundToInt(16 * u)}><color={Grey}>F12 closes</color>{(_debugUsed ? $"  <color={Gold}>· this game was changed with the console</color>" : "")}</size>", _titleStyle);
            GUILayout.FlexibleSpace();
            if (alive)
            {
                var clock = _em.GetComponentData<ClockState>(_world);
                GUILayout.Label(GameTime.Format(clock.Now, clock.StartDayOfWeek), _titleStyle);
            }
            GUILayout.EndHorizontal();

            if (!alive)
            {
                GUILayout.Label("No game in progress.", _bodyStyle);
                GUILayout.EndArea();
                return;
            }

            var tab = (Tab)GUILayout.Toolbar((int)_tab, s_TabNames, _tabStyle, GUILayout.Height(44 * u));
            if (tab != _tab) { _tab = tab; _bodyScroll = Vector2.zero; }
            GUILayout.Space(6 * u);

            if (_tab == Tab.Quests || _tab == Tab.Events)
            {
                GUILayout.BeginHorizontal();
                if (_tab == Tab.Quests) _showNotStarted = GUILayout.Toggle(_showNotStarted, " Show the quests not started   ", _toggleStyle, GUILayout.ExpandWidth(false));
                _branches = GUILayout.Toggle(_branches, " Conditions branch by branch", _toggleStyle, GUILayout.ExpandWidth(false));
                GUILayout.EndHorizontal();
            }
            if (_tab == Tab.World)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Filter flags and links:", _bodyStyle, GUILayout.ExpandWidth(false));
                _filter = GUILayout.TextField(_filter ?? "", _fieldStyle, GUILayout.Width(360 * u));
                GUILayout.EndHorizontal();
            }

            if (_tab == Tab.Npcs)
            {
                GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
                _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.Width(area.width * 0.28f));
                foreach (var (id, label) in _npcList)
                    if (GUILayout.Button(label, id == _npc ? _listSelStyle : _listStyle)) { _npc = id; _bodyScroll = Vector2.zero; }
                GUILayout.EndScrollView();
                GUILayout.Space(10 * u);
                DrawPage();
                GUILayout.EndHorizontal();
            }
            else DrawPage();

            DrawConsole(u, area.height);
            GUILayout.EndArea();

            // a command from a button runs once the frame's controls are all drawn; the pages follow at the next Layout
            if (_pendingCommand != null && Event.current.type != EventType.Layout)
            {
                string cmd = _pendingCommand;
                _pendingCommand = null;
                Console.Execute(cmd);
            }
        }

        void DrawPage()
        {
            _bodyScroll = GUILayout.BeginScrollView(_bodyScroll, GUILayout.ExpandHeight(true));
            foreach (var item in _page)
            {
                if (item.Actions == null) { GUILayout.Label(item.Text, _bodyStyle); continue; }
                GUILayout.BeginHorizontal();
                GUILayout.Label(item.Text, _rowStyle, GUILayout.ExpandWidth(true));
                foreach (var (label, command) in item.Actions)
                    if (GUILayout.Button(label, _smallButton, GUILayout.ExpandWidth(false))) _pendingCommand = command;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        void DrawConsole(float u, float height)
        {
            GUILayout.Space(8 * u);
            if (Event.current.type == EventType.Layout)
            {
                _logView.Clear();
                _logView.AddRange(Console.Log);
                if (Console.LogVersion != _seenLog) { _seenLog = Console.LogVersion; _logScroll.y = float.MaxValue; }      // follow the newest line
            }
            var old = GUI.color;
            _logScroll = GUILayout.BeginScrollView(_logScroll, GUI.skin.box, GUILayout.Height(height * 0.22f));
            GUI.color = old;
            if (_logView.Count == 0) GUILayout.Label($"<color={Grey}>Console: type help, or click a button above (it writes its command here). Tab completes names and ids.</color>", _logStyle);
            foreach (var e in _logView)
            {
                string color = e.Kind == DebugConsole.Kind.Input ? Blue : e.Kind == DebugConsole.Kind.Error ? Red : e.Kind == DebugConsole.Kind.Ok ? Green : null;
                GUILayout.Label(color == null ? Esc(e.Text) : $"<color={color}>{Esc(e.Text)}</color>", _logStyle);
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(InputName);
            _input = GUILayout.TextField(_input ?? "", _fieldStyle, GUILayout.ExpandWidth(true), GUILayout.Height(36 * u));
            if (GUILayout.Button("Run", _smallButton, GUILayout.Width(90 * u), GUILayout.Height(36 * u))) RunInput();
            GUILayout.EndHorizontal();
            if (_focusInput && Event.current.type == EventType.Repaint) { GUI.FocusControl(InputName); _focusInput = false; }
        }

        /// <summary>Enter runs the line, up / down walk the history (before the text field sees the keys).</summary>
        void HandleConsoleKeys()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || GUI.GetNameOfFocusedControl() != InputName) return;
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { RunInput(); e.Use(); return; }
            // Tab completes (and must not move the focus away); the '\t' character event that follows is swallowed too
            if (e.keyCode == KeyCode.Tab || e.character == '\t') { if (e.keyCode == KeyCode.Tab) CompleteInput(); e.Use(); return; }
            var history = Console.History;
            if (history.Count == 0) return;
            if (e.keyCode == KeyCode.UpArrow)
            {
                _historyIndex = _historyIndex < 0 ? history.Count - 1 : Math.Max(0, _historyIndex - 1);
                SetInput(history[_historyIndex]);
                e.Use();
            }
            else if (e.keyCode == KeyCode.DownArrow && _historyIndex >= 0)
            {
                _historyIndex++;
                if (_historyIndex >= history.Count) { _historyIndex = -1; SetInput(""); }
                else SetInput(history[_historyIndex]);
                e.Use();
            }
        }

        void CompleteInput()
        {
            string done = Console.Complete(_input, out var options);
            if (done != _input) SetInput(done);
            if (options.Count > 1)
                Console.Note(options.Count > 40 ? $"{options.Count} choices: {string.Join("  ", options.GetRange(0, 40))}  …" : string.Join("  ", options));
        }

        void RunInput()
        {
            string line = _input;
            SetInput("");
            _historyIndex = -1;
            if (!string.IsNullOrWhiteSpace(line)) _pendingCommand = line;
        }

        /// <summary>Changes the text of the field, also while it has the keyboard (IMGUI then keeps its own copy of the text).</summary>
        void SetInput(string text)
        {
            _input = text;
            if (GUI.GetNameOfFocusedControl() == InputName && GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) is TextEditor te)
            {
                te.text = text;
                te.MoveTextEnd();
            }
        }

        void EnsureStyles(float u)
        {
            if (_bodyStyle != null && Mathf.Approximately(u, _styleScale)) return;
            _styleScale = u;
            int body = Mathf.RoundToInt(19 * u);
            _bodyStyle = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, fontSize = body, alignment = TextAnchor.UpperLeft };
            _bodyStyle.normal.textColor = new Color(0.9f, 0.91f, 0.93f);
            _rowStyle = new GUIStyle(_bodyStyle) { alignment = TextAnchor.MiddleLeft };
            _logStyle = new GUIStyle(_bodyStyle) { fontSize = Mathf.RoundToInt(17 * u), margin = new RectOffset(4, 4, 0, 0), padding = new RectOffset(2, 2, 1, 1) };
            _titleStyle = new GUIStyle(_bodyStyle) { fontSize = Mathf.RoundToInt(24 * u), wordWrap = false, fontStyle = FontStyle.Bold };
            _titleStyle.normal.textColor = UiKit.AccentColor;
            _tabStyle = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(20 * u) };
            _smallButton = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(16 * u), padding = new RectOffset(Mathf.RoundToInt(8 * u), Mathf.RoundToInt(8 * u), Mathf.RoundToInt(3 * u), Mathf.RoundToInt(3 * u)) };
            _listStyle = new GUIStyle(GUI.skin.button) { richText = true, alignment = TextAnchor.MiddleLeft, fontSize = body, wordWrap = true };
            _listStyle.padding = new RectOffset(Mathf.RoundToInt(10 * u), Mathf.RoundToInt(6 * u), Mathf.RoundToInt(6 * u), Mathf.RoundToInt(6 * u));
            _listSelStyle = new GUIStyle(_listStyle);
            // a state of its own: GUIStyleState is shared by reference, and the list's must stay as it is
            _listSelStyle.normal = new GUIStyleState { background = _listStyle.active.background, textColor = UiKit.AccentColor };
            _toggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = body };
            _fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = body, alignment = TextAnchor.MiddleLeft };
        }

        // ------------------------------------------------------------------ page building

        void Rebuild()
        {
            var ws = _em.GetComponentData<WorldState>(_world);
            var clock = _em.GetComponentData<ClockState>(_world);
            _debugUsed = ws.DebugUsed != 0;
            if (_builtTab == _tab && _builtNpc == _npc && _builtVersion == ws.Version && _builtNow == clock.Now
                && _builtPending == clock.Pending && _builtNotStarted == _showNotStarted && _builtBranches == _branches && _builtFilter == _filter) return;
            _builtTab = _tab; _builtNpc = _npc; _builtVersion = ws.Version; _builtNow = clock.Now;
            _builtPending = clock.Pending; _builtNotStarted = _showNotStarted; _builtBranches = _branches; _builtFilter = _filter;

            var b = new Page();
            try
            {
                switch (_tab)
                {
                    case Tab.Npcs: BuildNpcList(); BuildNpc(b, clock); break;
                    case Tab.Player: BuildPlayer(b, clock); break;
                    case Tab.Quests: BuildQuests(b, clock); break;
                    case Tab.Events: BuildEvents(b, clock); break;
                    case Tab.World: BuildWorld(b, ws, clock); break;
                }
            }
            catch (Exception e)
            {
                // a debugging aid must never take the game down: show what went wrong instead
                b.Line($"<color={Red}>Could not read the world: {Esc(e.Message)}</color>");
                Debug.LogException(e);
            }
            _page.Clear();
            _page.AddRange(b.Items());
        }

        /// <summary>
        /// Builds every page as the panel would (every tab, every npc) and returns their text, buttons included as
        /// "[label: command]". For the headless tests, which cannot draw IMGUI.
        /// </summary>
        public string RenderAllForTests()
        {
            var sb = new StringBuilder();
            var keepTab = _tab;
            int keepNpc = _npc;
            foreach (Tab t in Enum.GetValues(typeof(Tab)))
            {
                _tab = t;
                var npcs = t == Tab.Npcs ? _compiled.Npcs.ConvertAll(n => n.Id) : new List<int> { _npc };
                foreach (int id in npcs)
                {
                    _npc = id;
                    _builtNpc = -1;
                    Rebuild();
                    sb.AppendLine($"=== {t} {Ids.Name(id)}");
                    foreach (var item in _page)
                    {
                        sb.AppendLine(item.Text);
                        if (item.Actions != null) foreach (var (label, command) in item.Actions) sb.AppendLine($"   [{label}: {command}]");
                    }
                }
            }
            _tab = keepTab; _npc = keepNpc; _builtNpc = -1;
            return sb.ToString();
        }

        /// <summary>A run of text (Actions null) or one line with its buttons: (label, console command).</summary>
        sealed class Item
        {
            public string Text;
            public (string label, string command)[] Actions;
        }

        /// <summary>Lines packed into labels short enough for IMGUI, and the rows that have buttons.</summary>
        sealed class Page
        {
            readonly List<Item> _items = new List<Item>();
            readonly StringBuilder _sb = new StringBuilder();

            public void Line(string text = "")
            {
                if (_sb.Length > 0 && _sb.Length + text.Length > ChunkChars) Flush();
                if (_sb.Length > 0) _sb.Append('\n');
                _sb.Append(text);
            }

            public void Heading(string text) { Line(""); Line($"<b><color={Gold}>{text}</color></b>"); }

            /// <summary>A line with buttons on its right; without buttons it is an ordinary line.</summary>
            public void Row(string text, params (string label, string command)[] actions)
            {
                if (actions == null || actions.Length == 0) { Line(text); return; }
                if (_sb.Length > 0) Flush();
                _items.Add(new Item { Text = text, Actions = actions });
            }

            void Flush() { _items.Add(new Item { Text = _sb.ToString() }); _sb.Clear(); }

            public List<Item> Items()
            {
                if (_sb.Length > 0) Flush();
                return _items;
            }
        }

        // ---- npcs

        void BuildNpcList()
        {
            _npcList.Clear();
            foreach (var n in _compiled.Npcs)
            {
                var e = FindNpc(n.Id);
                if (e == Entity.Null) continue;
                var loc = _em.GetComponentData<NpcLocation>(e);
                string where = loc.Node != 0 ? MapTitle(loc.Node) : "→ " + MapTitle(loc.NextNode);
                _npcList.Add((n.Id, $"{Esc(TextTable.Get(n.NameText))}\n<size={Mathf.RoundToInt(15 * _styleScale)}><color={Grey}>{Esc(where)}</color></size>"));
            }
            if (_npc == 0 && _npcList.Count > 0) _npc = _builtNpc = _npcList[0].id;
        }

        void BuildNpc(Page b, ClockState clock)
        {
            var n = _compiled.Npcs.Find(x => x.Id == _npc);
            var e = n == null ? Entity.Null : FindNpc(n.Id);
            if (e == Entity.Null) { b.Line("No NPC selected."); return; }

            var loc = _em.GetComponentData<NpcLocation>(e);
            var blocks = _em.GetBuffer<ScheduleBlock>(e, true);
            var places = _em.GetBuffer<NpcPlace>(e, true);
            int now = clock.Now;
            string id = Ids.Name(n.Id);
            var player = _em.GetComponentData<PlayerState>(_player);

            string tags = n.Def?.Tags != null && n.Def.Tags.Count > 0 ? $"   <color={Grey}>tags: {Esc(string.Join(", ", n.Def.Tags))}</color>" : "";
            b.Line($"<b><size={Mathf.RoundToInt(24 * _styleScale)}>{Esc(TextTable.Get(n.NameText))}</size></b>  <color={Grey}>{Esc(id)}</color>{tags}");
            b.Line();
            var whereActions = new List<(string, string)>();
            if (player.Node != 0 && loc.Node != player.Node) whereActions.Add(("Call here (2h)", $"moveNpc({id}, {Ids.Name(player.Node)}, forMinutes=120)"));
            if (loc.Node != 0 && loc.Node != player.Node) whereActions.Add(("Go there", $"teleportPlayer({Ids.Name(loc.Node)})"));
            b.Row(loc.Node != 0 ? $"Where: {MapName(loc.Node)}" : $"Where: travelling to {MapName(loc.NextNode)}, arrives {When(loc.ArriveAt, now)}", whereActions.ToArray());

            bool forced = loc.OverrideUntil > now;
            int active = NpcLogic.ActiveBlock(blocks, places, now, clock.StartDayOfWeek, out int blockNode);
            string why = forced ? "forced by a quest, an event or the console" : active >= 0 ? "routine" : "home: no routine block applies now";
            b.Line($"Wants to be at: {MapName(loc.Goal)}  <color={Grey}>({why})</color>");
            if (!forced && active >= 0 && blockNode != loc.Goal && loc.Goal != 0)
                b.Line($"<color={Grey}>leaving early for the next block: the one of now leads to {MapName(blockNode)}</color>");
            b.Line($"Doing: {(loc.Activity != 0 ? ActivityName(loc.Activity) : $"<color={Grey}>nothing</color>")}");
            b.Line($"Home: {MapName(loc.HomeNode)}");
            if (forced)
                b.Row($"Forced move: to {MapName(loc.OverrideNode)} until {When(loc.OverrideUntil, now)}",
                    ("Release", $"moveNpc({id}, {Ids.Name(loc.OverrideNode)}, forMinutes=0)"));

            b.Heading("Routine");
            if (blocks.Length == 0) b.Line($"<color={Grey}>no blocks: stays home</color>");
            for (int i = 0; i < blocks.Length; i++)
            {
                var blk = blocks[i];
                string at = blk.Place != 0
                    ? $"${Esc(Ids.Name(blk.Place))} → {MapTitleOrNone(NpcLogic.PlaceNode(places, blk.Place, blk.Node))}"
                    : MapTitleOrNone(blk.Node);
                string what = blk.Activity != 0 ? $", {ActivityName(blk.Activity)}" : "";
                string prio = blk.Priority != 0 ? $"  <color={Grey}>priority {blk.Priority}</color>" : "";
                string tag = blk.Tag != 0 ? $"  <color={Grey}>[{Esc(Ids.Name(blk.Tag))}]</color>" : "";
                string line = $"{DaysText(blk.DayMask)} {GameTime.Clock(blk.Start)}-{GameTime.Clock(blk.End)}  {at}{what}{prio}{tag}";
                b.Line(i == active && !forced ? $"<color={Gold}>► {line}  (now)</color>" : "   " + line);
            }

            b.Heading("Places");
            if (places.Length == 0) b.Line($"<color={Grey}>none</color>");
            for (int i = 0; i < places.Length; i++) b.Line($"{Esc(Ids.Name(places[i].Key))} → {MapName(places[i].Node)}");

            BuildStats(b, e, id);
            BuildInventory(b, e, id);
        }

        // ---- player

        void BuildPlayer(Page b, ClockState clock)
        {
            var p = _em.GetComponentData<PlayerState>(_player);
            b.Line("<b>Player</b>");
            b.Line();
            if (p.Node != 0) b.Line($"Where: {MapName(p.Node)}  <color={Grey}>(teleportPlayer(place) moves you)</color>");
            else b.Line($"Where: travelling from {MapName(p.FromNode)} to {MapName(p.PendingNode)}, arrives {When(clock.Now + clock.Pending, clock.Now)}");
            BuildStats(b, _player, null);
            BuildInventory(b, _player, null);
            b.Line();
            b.Line($"<color={Grey}>Money and other things are items: giveItem(item, count) adds them.</color>");
        }

        /// <summary>Stats of the player (<paramref name="target"/> null) or of an npc, with buttons that change them.</summary>
        void BuildStats(Page b, Entity e, string target)
        {
            b.Heading("Stats");
            string whose = target == null ? "" : $", target={target}";
            var stats = _em.GetBuffer<StatValue>(e, true);
            if (stats.Length == 0) b.Line($"<color={Grey}>none</color>");
            for (int i = 0; i < stats.Length; i++)
            {
                string range = "";
                foreach (var r in _compiled.StatRules)
                    if (r.Id == stats[i].Stat) { range = $"  <color={Grey}>({r.Min:0.#} to {r.Max:0.#}, baseline {r.Baseline:0.#})</color>"; break; }
                string stat = Ids.Name(stats[i].Stat);
                b.Row($"{StatName(stats[i].Stat)}: <b>{stats[i].Value:0.##}</b>{range}",
                    ("-10", $"modStat({stat}, -10{whose})"), ("-1", $"modStat({stat}, -1{whose})"),
                    ("+1", $"modStat({stat}, 1{whose})"), ("+10", $"modStat({stat}, 10{whose})"));
            }
        }

        void BuildInventory(Page b, Entity e, string target)
        {
            b.Heading("Inventory");
            string whose = target == null ? "" : $", target={target}";
            if (!_em.HasBuffer<InventorySlot>(e)) { b.Line($"<color={Grey}>none</color>"); return; }
            var items = _em.GetBuffer<InventorySlot>(e, true);
            if (items.Length == 0) b.Line($"<color={Grey}>empty</color>");
            for (int i = 0; i < items.Length; i++)
            {
                string item = Ids.Name(items[i].Item);
                b.Row($"{ItemName(items[i].Item)} × {items[i].Count}",
                    ("-1", $"takeItem({item}, 1{whose})"), ("+1", $"giveItem({item}, 1{whose})"), ("+10", $"giveItem({item}, 10{whose})"));
            }
        }

        // ---- quests

        void BuildQuests(Page b, ClockState clock)
        {
            var states = _em.GetBuffer<QuestState>(_world, true);
            ref var rules = ref _compiled.Rules.Value;
            var rt = RuleRuntime.Create(_em, _world, _player);
            int now = clock.Now;

            // the running ones first: they are the ones being tested
            QuestStatus[] order = { QuestStatus.Active, QuestStatus.NotStarted, QuestStatus.Completed, QuestStatus.Failed };
            foreach (var status in order)
            {
                if (status == QuestStatus.NotStarted && !_showNotStarted) continue;
                bool any = false;
                for (int qi = 0; qi < rules.Quests.Length && qi < states.Length; qi++)
                {
                    var st = states[qi];
                    if ((QuestStatus)st.Status != status) continue;
                    if (!any) { b.Heading(StatusName(status)); any = true; }
                    var q = rules.Quests[qi];
                    string id = Ids.Name(q.Id);
                    b.Line();
                    string header = $"<b>{Esc(TextTable.Get(q.TitleText))}</b>  <color={Grey}>{Esc(id)}</color>";
                    switch (status)
                    {
                        case QuestStatus.Active:
                            {
                                b.Row(header, ("Advance", $"advanceQuest({id})"), ("Complete", $"completeQuest({id})"), ("Fail", $"failQuest({id})"), ("Reset", $"resetQuest({id})"));
                                var sr = rules.Stages[q.StageStart + st.Stage];
                                b.Line($"Stage <b>{Esc(Ids.Name(sr.Id))}</b> ({st.Stage + 1} of {q.StageCount}) for {Duration(now - st.StageAt)}; quest started {Duration(now - st.StartedAt)} ago");
                                string text = TextTable.Get(sr.TextId);
                                if (text.Length > 0) b.Line($"<color={Grey}>journal: {Esc(text)}</color>");
                                if (sr.CompleteCond >= 0) Condition(b, rt, "Completes when its condition holds", sr.CompleteCond, false);
                                else b.Line($"<color={Grey}>no condition: an effect advances it</color>");
                                if (sr.FailCond >= 0) Condition(b, rt, "Fails when its condition holds", sr.FailCond, true);
                                string next = sr.Next < 0 ? "end of the quest" : Ids.Name(rules.Stages[q.StageStart + sr.Next].Id);
                                b.Line($"<color={Grey}>next: {Esc(next)}</color>");
                                break;
                            }
                        case QuestStatus.NotStarted:
                            b.Row(header, ("Start", $"startQuest({id})"));
                            if (q.AutoStartCond >= 0) Condition(b, rt, "Starts by itself when its condition holds", q.AutoStartCond, false);
                            else b.Line($"<color={Grey}>started by an effect (startQuest)</color>");
                            break;
                        default:
                            b.Row(header, ("Reset", $"resetQuest({id})"));
                            b.Line($"<color={Grey}>{(status == QuestStatus.Completed ? "completed" : "failed")} {When(st.StageAt, now)}, at stage {Esc(Ids.Name(rules.Stages[q.StageStart + st.Stage].Id))}</color>");
                            break;
                    }
                }
            }
            if (rules.Quests.Length == 0) b.Line("No quests in this world.");
        }

        // ---- events

        void BuildEvents(Page b, ClockState clock)
        {
            var states = _em.GetBuffer<EventState>(_world, true);
            ref var rules = ref _compiled.Rules.Value;
            var rt = RuleRuntime.Create(_em, _world, _player);
            int now = clock.Now;
            int count = Math.Min(rules.Events.Length, states.Length);

            b.Line("<b>Last events that ran</b>");
            var ran = new List<int>();
            for (int i = 0; i < count; i++) if (states[i].Count > 0) ran.Add(i);
            ran.Sort((x, y) => states[y].LastAt.CompareTo(states[x].LastAt));
            if (ran.Count == 0) b.Line($"<color={Grey}>none yet</color>");
            for (int k = 0; k < ran.Count && k < 12; k++)
            {
                var es = states[ran[k]];
                b.Line($"{When(es.LastAt, now)}  {Esc(Ids.Name(rules.Events[ran[k]].Id))}  <color={Grey}>× {es.Count}</color>");
            }

            b.Heading("All events  <color=#8a919c>(in the order they are checked: highest priority first; Run skips the condition)</color>");
            for (int i = 0; i < count; i++)
            {
                var ev = rules.Events[i];
                var es = states[i];
                string id = Ids.Name(ev.Id);
                b.Line();
                string title = $"<b>{Esc(id)}</b>  <color={Grey}>{TriggerText(ev)}{(ev.HasDialog != 0 ? ", dialogue" : "")}</color>";
                if (es.Count > 0) b.Row(title, ("Run", $"run({id})"), ("Reset", $"resetEvent({id})"));
                else b.Row(title, ("Run", $"run({id})"));

                string state;
                bool showTree = false;
                if (ev.Once != 0 && es.Count > 0) state = $"<color={Grey}>done (runs once)</color>";
                else if (ev.CooldownMin > 0 && es.Count > 0 && now - es.LastAt < ev.CooldownMin)
                    state = $"waiting: can run again in {Duration(ev.CooldownMin - (now - es.LastAt))}";
                else if (ev.Cond >= 0) { state = $"condition now: {YesNo(rt.Eval(ev.Cond))}"; showTree = _branches; }
                else state = "no condition";
                string history = es.Count > 0 ? $"ran {es.Count} time{(es.Count == 1 ? "" : "s")}, last {When(es.LastAt, now)}" : "never ran";
                string limits = (ev.Once != 0 ? "once" : "") + (ev.CooldownMin > 0 ? (ev.Once != 0 ? ", " : "") + $"cooldown {Duration(ev.CooldownMin)}" : "");
                b.Line($"{state}  <color={Grey}>· {history}{(limits.Length > 0 ? " · " + limits : "")}</color>");
                if (showTree) Branches(b, rt, ev.Cond, false);
            }
            if (count == 0) b.Line("No events in this world.");
        }

        string TriggerText(in EventRule ev)
        {
            switch (ev.Trigger)
            {
                case TriggerType.EnterNode: return ev.TriggerNode != 0 ? $"on entering {Esc(MapTitle(ev.TriggerNode))}" : "on entering any place";
                case TriggerType.Tick: return ev.TriggerNode != 0 ? $"every tick at {Esc(MapTitle(ev.TriggerNode))}" : "every tick, anywhere";
                case TriggerType.Talk: return $"talking to {Esc(NpcName(ev.TriggerNpc))}";
                case TriggerType.Wake: return ev.TriggerNode != 0 ? $"on waking at {Esc(MapTitle(ev.TriggerNode))}" : "on waking";
            }
            return "";
        }

        // ---- conditions, branch by branch

        /// <summary>"label: true/false", then its branches when the option is on.</summary>
        void Condition(Page b, RuleRuntime rt, string label, int root, bool badWhenTrue)
        {
            b.Line($"{label}: {YesNo(rt.Eval(root), badWhenTrue)}");
            if (_branches) Branches(b, rt, root, badWhenTrue);
        }

        /// <summary>The tree of a condition with the result of every branch and the values it reads now.</summary>
        void Branches(Page b, RuleRuntime rt, int root, bool badWhenTrue)
        {
            var res = new FixedList512Bytes<byte>();
            rt.EvalTree(root, ref res);
            var clock = _em.GetComponentData<ClockState>(_world);
            ref var rules = ref _compiled.Rules.Value;
            Walk(b, ref rules, res, rules.Conds[root].Lo, root, 1, rt, clock, badWhenTrue);
        }

        void Walk(Page b, ref RuleBlob rules, in FixedList512Bytes<byte> res, int lo, int i, int depth, RuleRuntime rt, ClockState clock, bool badWhenTrue)
        {
            ref var c = ref rules.Conds[i];
            bool r = res[i - lo] != 0;
            string indent = new string(' ', depth * 6);
            string mark = YesNo(r, badWhenTrue);
            switch (c.Type)
            {
                case CondType.All:
                case CondType.Any:
                    b.Line($"{indent}{mark}  <color={Grey}>{(c.Type == CondType.All ? "all of these:" : "any of these:")}</color>");
                    for (int k = 0; k < c.ChildCount; k++) Walk(b, ref rules, res, lo, rules.ChildRefs[c.ChildStart + k], depth + 1, rt, clock, badWhenTrue);
                    return;
                case CondType.Not:
                    if (c.ChildCount == 1 && rules.Conds[rules.ChildRefs[c.ChildStart]].Type == CondType.Always)
                    {
                        // what the compiler puts in place of a condition it could not read
                        b.Line($"{indent}{mark}  <color={Red}>never: this condition is invalid, see the content report</color>");
                        return;
                    }
                    b.Line($"{indent}{mark}  <color={Grey}>not:</color>");
                    for (int k = 0; k < c.ChildCount; k++) Walk(b, ref rules, res, lo, rules.ChildRefs[c.ChildStart + k], depth + 1, rt, clock, !badWhenTrue);
                    return;
                default:
                    b.Line($"{indent}{mark}  {Describe(c, ref rules, rt, clock)}");
                    return;
            }
        }

        /// <summary>One test of a condition in words, with the value it reads now in grey.</summary>
        string Describe(in CondNode c, ref RuleBlob rules, RuleRuntime rt, ClockState clock)
        {
            string Now(string s) => $"  <color={Grey}>(now {s})</color>";
            int now = clock.Now;
            switch (c.Type)
            {
                case CondType.Always: return "always";
                case CondType.Time: return $"time from {GameTime.Clock(c.A)} to {GameTime.Clock(c.B)}{Now(GameTime.Clock(GameTime.MinuteOfDay(now)))}";
                case CondType.Day: return $"day: {DaysText((byte)c.A)}{Now(s_Days[GameTime.DayOfWeek(now, clock.StartDayOfWeek)])}";
                case CondType.Date: return $"game day {c.A + 1} to {(c.B == int.MaxValue ? "the end" : (c.B + 1).ToString())}{Now("day " + (GameTime.DayOf(now) + 1))}";
                case CondType.After: return $"after day {GameTime.DayOf(c.A) + 1} {GameTime.Clock(GameTime.MinuteOfDay(c.A))}{Now("day " + (GameTime.DayOf(now) + 1) + " " + GameTime.Clock(GameTime.MinuteOfDay(now)))}";
                case CondType.PlayerAt:
                    {
                        var p = _em.GetComponentData<PlayerState>(_player);
                        return $"player at {MapName(c.A)}{Now(p.Node != 0 ? Esc(MapTitle(p.Node)) : "travelling")}";
                    }
                case CondType.NpcAt: return $"{Esc(NpcName(c.A))} at {MapName(c.B)}{Now(NpcWhere(c.A))}";
                case CondType.NpcWithPlayer: return $"{Esc(NpcName(c.A))} with the player{Now(NpcWhere(c.A))}";
                case CondType.Activity:
                    {
                        var e = FindNpc(c.A);
                        int act = e == Entity.Null ? 0 : _em.GetComponentData<NpcLocation>(e).Activity;
                        return $"{Esc(NpcName(c.A))} doing {ActivityName(c.B)}{Now(act != 0 ? ActivityName(act) : "nothing")}";
                    }
                case CondType.Stat: return $"{WhoText(c.A)} {StatName(c.B)} {OpText(c.Op)} {c.F:0.##}{Now($"{rt.GetStat(c.A, c.B):0.##}")}";
                case CondType.Flag: return $"flag {Esc(Ids.Name(c.A))} {OpText(c.Op)} {c.F:0.##}{Now($"{rt.GetFlag(c.A):0.##}")}";
                case CondType.HasItem: return $"{WhoText(c.A)} has {ItemName(c.B)} {OpText(c.Op)} {c.F:0}{Now(rt.GetItemCount(c.A, c.B).ToString())}";
                case CondType.Quest:
                    {
                        var q = rules.Quests[c.A];
                        var st = _em.GetBuffer<QuestState>(_world, true)[c.A];
                        string want = c.B < 0 ? "" : " " + StatusName((QuestStatus)c.B).ToLowerInvariant();
                        string stage = c.C < 0 ? "" : $" at stage {Esc(Ids.Name(rules.Stages[q.StageStart + c.C].Id))}";
                        string have = StatusName((QuestStatus)st.Status).ToLowerInvariant();
                        if (st.Status == (byte)QuestStatus.Active) have += $", stage {Esc(Ids.Name(rules.Stages[q.StageStart + st.Stage].Id))}";
                        return $"quest {Esc(Ids.Name(q.Id))}{want}{stage}{Now(have)}";
                    }
                case CondType.Elapsed:
                    {
                        var q = rules.Quests[c.A];
                        var st = _em.GetBuffer<QuestState>(_world, true)[c.A];
                        string since = c.B == 1 ? "the quest" : "the stage";
                        string have = st.Status == (byte)QuestStatus.Active ? Duration(now - (c.B == 1 ? st.StartedAt : st.StageAt)) : "the quest is not running";
                        return $"{Duration(c.C)} since {since} of {Esc(Ids.Name(q.Id))} began{Now(have)}";
                    }
            }
            return c.Type.ToString();
        }

        string NpcWhere(int npc)
        {
            var e = FindNpc(npc);
            if (e == Entity.Null) return "not in the world";
            var loc = _em.GetComponentData<NpcLocation>(e);
            return loc.Node != 0 ? Esc(MapTitle(loc.Node)) : "travelling to " + Esc(MapTitle(loc.NextNode));
        }

        string WhoText(int target) => target == 0 ? "player's" : Esc(NpcName(target)) + "'s";

        static string OpText(CmpOp op)
        {
            switch (op)
            {
                case CmpOp.Eq: return "=";
                case CmpOp.Ne: return "≠";
                case CmpOp.Lt: return "<";
                case CmpOp.Le: return "≤";
                case CmpOp.Gt: return ">";
                default: return "≥";
            }
        }

        // ---- world

        void BuildWorld(Page b, WorldState ws, ClockState clock)
        {
            ref var rules = ref _compiled.Rules.Value;
            b.Line("<b>Clock</b>");
            b.Row($"{GameTime.Format(clock.Now, clock.StartDayOfWeek)}  <color={Grey}>(one tick = {clock.TickMinutes} min)</color>",
                ("+10 min", "advanceTime(10)"), ("+1 h", "advanceTime(60)"), ("+6 h", "advanceTime(360)"), ("→ 07:00", "advanceTimeTo(07:00)"));
            if (clock.Pending > 0) b.Line($"Time passing: {Duration(clock.Pending)} still to simulate{(clock.Sleeping != 0 ? ", sleeping" : "")}");
            if (ws.DialogOpen != 0 && ws.DialogEventIdx >= 0 && ws.DialogEventIdx < rules.Events.Length)
                b.Line($"Dialogue open: {Esc(Ids.Name(rules.Events[ws.DialogEventIdx].Id))}");
            b.Line($"<color={Grey}>world version {ws.Version}{(ws.DebugUsed != 0 ? ", changed with the debug console" : "")}</color>");

            string filter = (_filter ?? "").Trim().ToLowerInvariant();
            bool Match(string s) => filter.Length == 0 || s.ToLowerInvariant().Contains(filter);

            var flags = _em.GetBuffer<FlagValue>(_world, true);
            var game = new List<(string name, float value)>();
            var inner = new List<(string name, float value)>();
            for (int i = 0; i < flags.Length; i++)
            {
                string name = Ids.Name(flags[i].Key);
                if (!Match(name)) continue;
                // the state of links, overlay images and looping sounds is kept as flags too, under these prefixes
                bool system = name.StartsWith("link:") || name.StartsWith("image:") || name.StartsWith("sound:");
                (system ? inner : game).Add((name, flags[i].Value));
            }
            game.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
            inner.Sort((x, y) => string.CompareOrdinal(x.name, y.name));

            b.Heading($"Flags ({game.Count})");
            b.Line($"<color={Grey}>setFlag(name, value) creates or changes one</color>");
            if (game.Count == 0) b.Line($"<color={Grey}>none{(filter.Length > 0 ? " matching" : "")}</color>");
            foreach (var (name, value) in game)
                b.Row($"{Esc(name)} = <b>{value:0.##}</b>", ("-1", $"addFlag({name}, -1)"), ("+1", $"addFlag({name}, 1)"), ("0", $"setFlag({name}, 0)"));

            b.Heading("Links of the map");
            ref var nav = ref _compiled.Nav.Value;
            var rt = RuleRuntime.Create(_em, _world, _player);
            int shown = 0;
            for (int a = 0; a < nav.Count; a++)
                for (int k = nav.EdgeStart[a]; k < nav.EdgeStart[a + 1]; k++)
                {
                    var edge = nav.Edges[k];
                    string from = MapTitle(nav.NodeIds[a]), to = MapTitle(nav.NodeIds[edge.To]);
                    string link = Ids.Name(edge.LinkId);
                    if (!Match(from) && !Match(to) && !Match(link)) continue;
                    bool visible = rt.LinkVisible(edge), open = rt.LinkOpen(edge);
                    bool changed = rt.GetFlag(edge.VisibleKey) != 0 || rt.GetFlag(edge.OpenKey) != 0;
                    string cond = edge.Cond >= 0 ? $", condition {YesNo(rt.Eval(edge.Cond))}" : "";
                    string state = $"{(visible ? "visible" : $"<color={Red}>hidden</color>")}, {(open ? "open" : $"<color={Red}>closed</color>")}{cond}";
                    string text = $"{Esc(from)} → {Esc(to)}  <color={Grey}>{Esc(link)}, {edge.Minutes} min</color>  {state}{(changed ? $"  <color={Gold}>changed by the game</color>" : "")}";
                    if (edge.LinkId != 0)
                        b.Row(text, visible ? ("Hide", $"hideLink({link})") : ("Show", $"showLink({link})"),
                                    open ? ("Close", $"closeLink({link})") : ("Open", $"openLink({link})"));
                    else b.Line(text);
                    shown++;
                }
            if (shown == 0) b.Line($"<color={Grey}>none{(filter.Length > 0 ? " matching" : "")}</color>");

            b.Heading($"Inner state ({inner.Count})  <color=#8a919c>(links, images on screen, looping sounds)</color>");
            foreach (var (name, value) in inner) b.Line($"<color={Grey}>{Esc(name)} = {value:0.##}</color>");
        }

        // ------------------------------------------------------------------ names and formats

        Entity FindNpc(int id)
        {
            var idx = _em.GetBuffer<EntityRef>(_world, true);
            for (int i = 0; i < idx.Length; i++) if (idx[i].Id == id) return idx[i].Entity;
            return Entity.Null;
        }

        string NpcName(int id)
        {
            foreach (var n in _compiled.Npcs) if (n.Id == id) return TextTable.Get(n.NameText);
            return Ids.Name(id);
        }

        /// <summary>The title of a map as the player reads it, or its id when it has none.</summary>
        string MapTitle(int node)
        {
            if (node == 0) return "nowhere";
            string id = Ids.Name(node);
            if (_compiled.Db.Maps.TryGetValue(id, out var map))
            {
                string title = TextTable.Localize(map.Title);
                if (title.Length > 0) return title;
            }
            return id;
        }

        string MapTitleOrNone(int node) => node == 0 ? $"<color={Red}>undefined place</color>" : Esc(MapTitle(node));

        /// <summary>Title and id of a map: the title for reading, the id for writing quests.</summary>
        string MapName(int node)
        {
            if (node == 0) return $"<color={Grey}>nowhere</color>";
            string id = Ids.Name(node), title = MapTitle(node);
            return title != id ? $"{Esc(title)} <color={Grey}>({Esc(id)})</color>" : Esc(id);
        }

        string StatName(int id) => Esc(_statNames.TryGetValue(id, out var n) ? n : Ids.Name(id));
        string ActivityName(int id) => Esc(_activityNames.TryGetValue(id, out var n) ? n : Ids.Name(id));

        string ItemName(int id)
        {
            foreach (var r in _compiled.ItemRules)
                if (r.Id == id) { string n = TextTable.Get(r.NameText); return Esc(n.Length > 0 ? n : Ids.Name(id)); }
            return Esc(Ids.Name(id));
        }

        static string StatusName(QuestStatus s)
        {
            switch (s)
            {
                case QuestStatus.Active: return "Active";
                case QuestStatus.NotStarted: return "Not started";
                case QuestStatus.Completed: return "Completed";
                default: return "Failed";
            }
        }

        /// <summary>"true" in green or "false" in red (inverted for a condition that is bad news when it holds).</summary>
        static string YesNo(bool value, bool badWhenTrue = false)
            => value != badWhenTrue ? $"<color={Green}>{(value ? "true" : "false")}</color>" : $"<color={Red}>{(value ? "true" : "false")}</color>";

        /// <summary>An absolute minute: only the time when it is today, with the day otherwise.</summary>
        static string When(int minute, int now)
        {
            int day = GameTime.DayOf(minute);
            string clock = GameTime.Clock(GameTime.MinuteOfDay(minute));
            if (day == GameTime.DayOf(now)) return clock;
            return $"day {day + 1} {clock}";
        }

        static string Duration(int minutes)
        {
            if (minutes < 0) minutes = 0;
            int d = minutes / GameTime.MinutesPerDay, h = minutes % GameTime.MinutesPerDay / 60, m = minutes % 60;
            if (d > 0) return h > 0 ? $"{d}d {h}h" : $"{d}d";
            if (h > 0) return m > 0 ? $"{h}h {m:00}m" : $"{h}h";
            return $"{m}m";
        }

        static readonly string[] s_Days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        static string DaysText(byte mask)
        {
            switch (mask & 0x7F)
            {
                case 0x7F: return "every day";
                case 0x1F: return "weekdays";
                case 0x60: return "weekend";
            }
            var parts = new List<string>();
            for (int i = 0; i < 7; i++) if (((mask >> i) & 1) != 0) parts.Add(s_Days[i]);
            return parts.Count > 0 ? string.Join(",", parts) : "never";
        }

        /// <summary>Content text as plain text: a "&lt;" written by an author must not start a rich-text tag (IMGUI has no escape).</summary>
        static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace('<', '‹');
    }
}
