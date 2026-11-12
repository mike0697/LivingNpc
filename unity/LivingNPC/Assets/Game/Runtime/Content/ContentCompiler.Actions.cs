using System;
using System.Collections.Generic;
using System.Linq;
using LivingNPC.Simulation;
using Newtonsoft.Json.Linq;

namespace LivingNPC.Content
{
    /// <summary>An entry of the menu of actions on a character, ready for the view.</summary>
    public class CompiledAction
    {
        public string Id;
        /// <summary>Hashed id: what CommandType.Action sends (events with "trigger": "action").</summary>
        public int Hash;
        public int LabelText;
        /// <summary>Absolute path of the small picture, null = none.</summary>
        public string Icon;
        /// <summary>Choosing it also talks to the character.</summary>
        public bool Talk;
        /// <summary>The characters it is offered to; null = everybody.</summary>
        public HashSet<int> Npcs;
        /// <summary>Root of the condition (self = the character), -1 = always.</summary>
        public int When = -1;
        /// <summary>Sub-actions opened in a panel beside the menu; null for an action that does something.</summary>
        public List<CompiledAction> Children;
        /// <summary>Sub-actions per page of that panel, 0 = all.</summary>
        public int PageSize;

        public bool OfferedTo(int npc) => Npcs == null || Npcs.Contains(npc);
    }

    /// <summary>How the menu pages and closes its panels (actions.json "pageSize", "labels").</summary>
    public class CompiledActionMenu
    {
        public const int DefaultPageSize = 6;
        /// <summary>Entries per page of the first menu, 0 = all.</summary>
        public int PageSize;
        public int NextText, BackText, CloseText;
    }

    public partial class CompiledContent
    {
        /// <summary>The menu of actions on a character (actions.json); null when the world has none: a click talks.</summary>
        public List<CompiledAction> Actions;
        /// <summary>Pages and words of the buttons of the menu (with <see cref="Actions"/>).</summary>
        public CompiledActionMenu ActionMenu;
    }

    // actions.json: the menu that opens when the player clicks a character; "action" events run when an entry is chosen.
    public partial class ContentCompiler
    {
        readonly HashSet<int> _actionIds = new HashSet<int>();
        readonly HashSet<int> _chosenActions = new HashSet<int>();      // actions some "action" event listens to

        void CompileActions(CompiledContent result)
        {
            var def = _db.Actions;
            if (def == null) return;
            string src = _db.ActionsSource ?? "actions.json";
            var list = CompileActionList(def.Actions, src, null, 0);
            if (list.Count == 0) _rep.Warn(src, "actions.json has no actions: clicking a character opens an empty menu");
            result.Actions = list;
            var menu = new CompiledActionMenu { PageSize = Math.Max(0, def.PageSize) };
            if (def.PageSize < 0) _rep.Warn(src, "pageSize: 0 (all on one page) or more");
            JToken Label(string key, string fallback)
            {
                foreach (var kv in def.Labels ?? new Dictionary<string, JToken>())
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                return new JValue(fallback);
            }
            foreach (var k in (def.Labels ?? new Dictionary<string, JToken>()).Keys)
                if (!new[] { "next", "back", "close" }.Contains(k.ToLowerInvariant())) _rep.Warn(src, $"labels.{k}: unknown (next, back, close)");
            menu.NextText = TextTable.Register("actions:label:next", Label("next", "Next \u203A"));
            menu.BackText = TextTable.Register("actions:label:back", Label("back", "\u2039 Back"));
            menu.CloseText = TextTable.Register("actions:label:close", Label("close", "Close"));
            result.ActionMenu = menu;
        }

        /// <summary>A list of actions (the menu or the sub-actions of one); pictures of sub-actions are looked for in their parent's pack.</summary>
        List<CompiledAction> CompileActionList(List<ActionDef> defs, string src, string packDir, int depth)
        {
            var list = new List<CompiledAction>();
            foreach (var a in defs ?? new List<ActionDef>())
            {
                if (a == null) continue;
                if (string.IsNullOrWhiteSpace(a.Id)) { _rep.Error(src, "an action has no 'id'"); continue; }
                string at = $"{src}#{a.Id}";
                if (_actionIds.Contains(Ids.Hash("action:" + a.Id))) { _rep.Error(at, $"another action already has the id '{a.Id}' (ids are shared by the sub-actions too)"); continue; }
                a.PackDir ??= packDir;
                var ca = new CompiledAction { Id = a.Id, Hash = Ids.Hash("action:" + a.Id) };
                ca.LabelText = TextTable.Register($"action:{a.Id}:label", a.Label ?? new JValue(a.Id));
                if (!string.IsNullOrWhiteSpace(a.Icon))
                {
                    ca.Icon = ImagePath(a.PackDir, a.Icon.Trim(), out var err);
                    if (ca.Icon == null) _rep.Warn(at, $"icon: {err}");
                }
                switch ((a.Do ?? "").Trim().ToLowerInvariant())
                {
                    case "": break;
                    case "talk": ca.Talk = true; break;
                    case "trade": _rep.Warn(at, "do 'trade': trading does not exist yet; the action only runs its events"); break;
                    default: _rep.Error(at, $"do '{a.Do}': unknown (talk, trade; omitted = only its events)"); break;
                }
                if (a.Npcs != null || a.Tags != null)
                {
                    ca.Npcs = new HashSet<int>();
                    foreach (var id in a.Npcs ?? new List<string>())
                    {
                        if (_db.Npcs.ContainsKey(id ?? "")) ca.Npcs.Add(Ids.Hash(id));
                        else _rep.Warn(at, $"npcs: unknown character '{id}'");
                    }
                    if (a.Tags != null)
                        foreach (var n in _db.NpcList)
                            if (n.Tags != null && n.Tags.Any(t => a.Tags.Contains(t))) ca.Npcs.Add(Ids.Hash(n.Id));
                    if (ca.Npcs.Count == 0) _rep.Warn(at, "npcs / tags: no character matches, the action is never offered");
                }
                if (a.When != null && a.When.Type != JTokenType.Null)
                {
                    _tpl = new TplCtx { Id = a.Id };        // self = the character clicked
                    ca.When = CompileCond(a.When, at + ".when");
                    _tpl = null;
                }
                _actionIds.Add(ca.Hash);
                if (a.Actions != null)
                {
                    if (depth >= 4) { _rep.Error(at, "sub-actions go at most 4 levels deep"); continue; }
                    if (ca.Talk || !string.IsNullOrWhiteSpace(a.Do)) _rep.Warn(at, "an action with sub-actions only opens them: 'do' is ignored");
                    ca.Talk = false;
                    ca.Children = CompileActionList(a.Actions, src, a.PackDir, depth + 1);
                    ca.PageSize = a.PageSize ?? CompiledActionMenu.DefaultPageSize;
                    if (ca.PageSize < 0) { _rep.Warn(at, "pageSize: 0 (all on one page) or more"); ca.PageSize = CompiledActionMenu.DefaultPageSize; }
                    if (ca.Children.Count == 0) _rep.Warn(at, "it has no sub-actions: it is never offered");
                }
                else if (a.PageSize != null) _rep.Warn(at, "pageSize is used only with sub-actions (\"actions\")");
                list.Add(ca);
            }
            return list;
        }

        /// <summary>After the events: an action that does not talk and that no event listens to does nothing.</summary>
        void CheckActions(CompiledContent result)
        {
            if (result.Actions == null) return;
            void Walk(List<CompiledAction> list)
            {
                foreach (var a in list)
                {
                    if (a.Children != null) { Walk(a.Children); continue; }
                    if (!a.Talk && !_chosenActions.Contains(a.Hash))
                        _rep.Warn(_db.ActionsSource ?? "actions.json", $"action '{a.Id}' does not talk and no event has trigger 'action' with action '{a.Id}': it does nothing");
                }
            }
            Walk(result.Actions);
        }
    }
}
