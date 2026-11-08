using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LivingNPC.Simulation;
using Newtonsoft.Json.Linq;

namespace LivingNPC.Content
{
    // The automatic quests: templates an npc picks and runs by itself (templates/), the shared lines they say (lines/),
    // the goals (rules.json "goals") and who the npcs are (first name, last name, age, sex, relations).
    public partial class ContentCompiler
    {
        readonly List<TemplateRule> _templates = new List<TemplateRule>();
        readonly List<SlotRule> _slots = new List<SlotRule>();
        readonly List<WeightTerm> _weights = new List<WeightTerm>();
        readonly List<int> _listValues = new List<int>();
        readonly List<TextGroupRule> _textGroups = new List<TextGroupRule>();
        readonly List<TextVariantRule> _textVariants = new List<TextVariantRule>();
        readonly List<int> _textLines = new List<int>();
        readonly Dictionary<string, int> _templateIndex = new Dictionary<string, int>();
        readonly Dictionary<string, Dictionary<string, int>> _templateStageIndex = new Dictionary<string, Dictionary<string, int>>();
        readonly Dictionary<string, int> _linesIndex = new Dictionary<string, int>();      // lines id -> index into _textGroups
        readonly HashSet<int> _goalIds = new HashSet<int>();
        int _autoCheck = 60, _autoMax = 3, _autoPause = GameTime.MinutesPerDay;

        /// <summary>The template (or shared lines) whose conditions, effects and texts are being compiled; null elsewhere.</summary>
        TplCtx _tpl;

        sealed class TplCtx
        {
            public string Id;
            /// <summary>slot name -> (index in the instance's Bind: 1.., kind).</summary>
            public readonly Dictionary<string, (int bind, SlotKind kind)> Slots = new Dictionary<string, (int, SlotKind)>();
            /// <summary>The highest Bind index that is filled at this point (0: only self).</summary>
            public int MaxBind;
            /// <summary>The Bind index "it" stands for (the slot being filled), -1 none.</summary>
            public int It = -1;
            /// <summary>Compiling a stage: elapsed and chance refer to the running instance.</summary>
            public bool InStage;
            /// <summary>The template's text groups: name -> index into _textGroups.</summary>
            public readonly Dictionary<string, int> Groups = new Dictionary<string, int>();

            public SlotKind KindOf(int bind)
            {
                foreach (var s in Slots.Values) if (s.bind == bind) return s.kind;
                return SlotKind.Npc;
            }
        }

        static readonly Regex s_SlotName = new Regex(@"^[A-Za-z_][A-Za-z0-9_\-]*$");
        static readonly HashSet<string> s_ReservedSlots = new HashSet<string> { "self", "it", "npc", "player" };

        static string KindWord(SlotKind k) => k == SlotKind.Npc ? "a character" : k == SlotKind.Item ? "an item" : "a place";

        /// <summary>
        /// Inside a template: "self", "it" and the slot names (also written {name}) stand for the values of the running
        /// instance (BindRef). False when <paramref name="id"/> is not one of them (a real id, checked by the caller).
        /// </summary>
        bool TryBound(string id, SlotKind want, out int value)
        {
            value = 0;
            if (_tpl == null || string.IsNullOrEmpty(id)) return false;
            string name = id.Length > 2 && id[0] == '{' && id[id.Length - 1] == '}' ? id.Substring(1, id.Length - 2) : id;
            int bind;
            SlotKind kind;
            if (name == "self") { bind = 0; kind = SlotKind.Npc; }
            else if (name == "it" && _tpl.It > 0) { bind = _tpl.It; kind = _tpl.KindOf(bind); }
            else if (_tpl.Slots.TryGetValue(name, out var s)) { bind = s.bind; kind = s.kind; }
            else return false;
            if (bind > _tpl.MaxBind) throw new CondException($"'{name}' is not filled yet here: a slot can only use self, itself (it) and the slots above it");
            bool fits = kind == want || (want == SlotKind.Node && kind == SlotKind.Place);
            if (!fits) throw new CondException($"'{name}' is {KindWord(kind)}, not {KindWord(want)}");
            value = BindRef.Of(bind);
            return true;
        }

        static readonly Regex s_Placeholder = new Regex(@"\{([A-Za-z_][A-Za-z0-9_\-]*)((?:\.[A-Za-z]+)?(?::[^{}]*)?)\}");

        /// <summary>
        /// The texts of a template with {self} and the slot names turned into {#0}, {#1}... (the index in the instance's Bind,
        /// which the view replaces with the name). {npc} stays as it is. Outside templates the text is returned unchanged.
        /// </summary>
        JToken BindTexts(JToken t, string src)
        {
            if (_tpl == null || t == null) return t;
            string Map(string s) => s_Placeholder.Replace(s, m =>
            {
                string name = m.Groups[1].Value, rest = m.Groups[2].Value;
                if (name == "npc") return m.Value;
                if (name == "self") return "{#0" + rest + "}";
                if (_tpl.Slots.TryGetValue(name, out var slot)) return "{#" + slot.bind + rest + "}";
                _rep.Error(src, $"unknown placeholder '{m.Value}': use {{self}}, {{npc}} or a slot of the template" + (_tpl.Slots.Count > 0 ? $" ({string.Join(", ", _tpl.Slots.Keys)})" : ""));
                return m.Value;
            });
            if (t.Type == JTokenType.String) return new JValue(Map((string)t));
            if (t is JObject o)
            {
                var copy = new JObject();
                foreach (var p in o.Properties()) copy[p.Name] = p.Value.Type == JTokenType.String ? new JValue(Map((string)p.Value)) : p.Value.DeepClone();
                return copy;
            }
            return t;
        }

        // ------------------------------------------------------------------ goals and who the npcs are

        void CompileGoals(CompiledContent result)
        {
            foreach (var g in _db.Rules.Goals)
            {
                if (g == null || string.IsNullOrEmpty(g.Id)) { _rep.Error("rules.json", "goal without id"); continue; }
                int id = Ids.Hash(g.Id);
                if (!_goalIds.Add(id)) _rep.Warn("rules.json", $"goal '{g.Id}' is declared twice");
                string name = TextTable.Localize(g.Name);
                result.GoalNames[id] = name.Length > 0 ? name : g.Id;
            }
        }

        int GoalRef(string name)
        {
            int h = Ids.Hash(name);
            if (!_goalIds.Contains(h)) throw new CondException($"unknown goal '{name}' (declare it in rules.json \"goals\")");
            return h;
        }

        /// <summary>"m" / "f" / "x" (and male, female, other...) -> 1 / 2 / 3; 0 when it is not one of them.</summary>
        public static byte ParseSex(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "m": case "male": case "man": case "maschio": case "uomo": return 1;
                case "f": case "female": case "woman": case "femmina": case "donna": return 2;
                case "x": case "other": case "altro": case "nb": return 3;
                default: return 0;
            }
        }

        /// <summary>The name of an npc that has none: first name and last name (in every language they are written in), else its id.</summary>
        static JToken DefaultName(NpcDef def)
        {
            if (def.FirstName == null && def.LastName == null) return new JValue(def.Id);
            string Pick(JToken t, string lang)
            {
                if (t == null || t.Type == JTokenType.Null) return "";
                if (t is JObject o) return (string)(o[lang] ?? o.Properties().FirstOrDefault()?.Value) ?? "";
                return t.ToString();
            }
            var langs = new List<string>();
            foreach (var t in new[] { def.FirstName, def.LastName })
                if (t is JObject o) foreach (var p in o.Properties()) if (!langs.Contains(p.Name)) langs.Add(p.Name);
            if (langs.Count == 0) return new JValue($"{Pick(def.FirstName, "")} {Pick(def.LastName, "")}".Trim());
            var name = new JObject();
            foreach (var l in langs) name[l] = $"{Pick(def.FirstName, l)} {Pick(def.LastName, l)}".Trim();
            return name;
        }

        void CompileNpcIdentity(NpcDef def, CompiledNpc npc)
        {
            string src = def.Source;
            if (def.FirstName != null && def.FirstName.Type != JTokenType.Null) npc.FirstNameText = TextTable.Register($"npc:{def.Id}:first", def.FirstName);
            if (def.LastName != null && def.LastName.Type != JTokenType.Null) npc.LastNameText = TextTable.Register($"npc:{def.Id}:last", def.LastName);
            if (def.Age is int age)
            {
                if (age < 0) _rep.Warn(src, $"npc '{def.Id}': age cannot be negative, using 0");
                npc.Age = Math.Max(0, age);
            }
            if (!string.IsNullOrEmpty(def.Sex))
            {
                npc.Sex = ParseSex(def.Sex);
                if (npc.Sex == 0) _rep.Warn(src, $"npc '{def.Id}': sex '{def.Sex}' is not m, f or x");
            }
            npc.AutoQuests = def.AutoQuests;
            if (!string.IsNullOrEmpty(def.Goal))
            {
                int goal = Ids.Hash(def.Goal);
                if (_goalIds.Contains(goal)) npc.Goal = goal;
                else _rep.Error(src, $"npc '{def.Id}': unknown goal '{def.Goal}' (declare it in rules.json \"goals\")");
            }
            if (def.Relations == null) return;
            foreach (var kv in def.Relations)
            {
                int other;
                if (kv.Key == "player") other = 0;
                else if (_npcIds.Contains(Ids.Hash(kv.Key))) other = Ids.Hash(kv.Key);
                else { _rep.Error(src, $"npc '{def.Id}': relation with unknown npc '{kv.Key}'"); continue; }
                if (kv.Key == def.Id) { _rep.Warn(src, $"npc '{def.Id}': a relation with itself is ignored"); continue; }
                float v = kv.Value;
                if (v < -100 || v > 100) { _rep.Warn(src, $"npc '{def.Id}': relation with '{kv.Key}' goes from -100 to 100"); v = Math.Max(-100, Math.Min(100, v)); }
                npc.Relations.Add(new RelationValue { Other = other, Value = v });
            }
        }

        // ------------------------------------------------------------------ templates

        void IndexTemplates()
        {
            for (int i = 0; i < _db.TemplateList.Count; i++)
            {
                var t = _db.TemplateList[i];
                _templateIndex[t.Id] = i;
                var map = new Dictionary<string, int>();
                for (int s = 0; s < t.Stages.Count; s++)
                    if (!string.IsNullOrEmpty(t.Stages[s].Id)) map[t.Stages[s].Id] = s;
                _templateStageIndex[t.Id] = map;
            }
        }

        void CompileAutoRules()
        {
            var a = _db.Rules.AutoQuests ?? new AutoQuestsDef();
            if (a.CheckMinutes < 1) _rep.Warn("rules.json", "autoQuests.checkMinutes must be 1 or more; using 60");
            _autoCheck = a.CheckMinutes < 1 ? 60 : a.CheckMinutes;
            if (a.MaxActive < 0) _rep.Warn("rules.json", "autoQuests.maxActive cannot be negative; using 0");
            _autoMax = Math.Max(0, a.MaxActive);
            if (a.PauseDays < 0) _rep.Warn("rules.json", "autoQuests.pauseDays cannot be negative; using 0");
            _autoPause = (int)Math.Round(Math.Max(0, a.PauseDays) * GameTime.MinutesPerDay);
            if (_templates.Count > 0 && !_db.NpcList.Any(n => n.AutoQuests))
                _rep.Info("templates", "there are automatic quests but no character has \"autoQuests\": true (only startAuto starts them)");
        }

        void CompileLines()
        {
            foreach (var l in _db.LinesList)
            {
                _tpl = new TplCtx { Id = l.Id };        // the shared lines know only self: the npc that says them
                _linesIndex[l.Id] = CompileGroup(l.Id, $"lines:{l.Id}", l.Variants, $"{l.Source}#{l.Id}");
            }
            _tpl = null;
        }

        /// <summary>
        /// A group of lines: a list of variants [{ "when": cond, "lines": [text, ...] }], or just a list of lines (one variant
        /// that always holds). Text keys use the line itself: the message log of a save keeps them.
        /// </summary>
        int CompileGroup(string id, string key, JToken t, string src)
        {
            var group = new TextGroupRule { Id = Ids.Hash(id), VariantStart = _textVariants.Count };
            bool IsLine(JToken x) => x.Type == JTokenType.String || (x is JObject xo && xo["lines"] == null && xo["when"] == null);
            var variants = new List<JToken>();
            if (t is JArray arr)
            {
                if (arr.Count > 0 && arr.All(IsLine)) variants.Add(new JObject { ["lines"] = arr });
                else variants.AddRange(arr);
            }
            else if (t is JObject one && one["lines"] != null) variants.Add(one);
            else if (t != null && t.Type != JTokenType.Null && IsLine(t)) variants.Add(new JObject { ["lines"] = new JArray(t) });
            if (variants.Count == 0) _rep.Error(src, "no lines: write a list of texts, or variants { \"when\": …, \"lines\": [ … ] }");

            for (int v = 0; v < variants.Count; v++)
            {
                string vsrc = $"{src}[{v}]";
                if (!(variants[v] is JObject vo)) { _rep.Error(vsrc, "a variant must be { \"when\": …, \"lines\": [ … ] }"); continue; }
                var rule = new TextVariantRule { Cond = -1, LineStart = _textLines.Count };
                if (IsSet(vo["when"])) rule.Cond = CompileCond(vo["when"], vsrc + ".when");
                var lines = vo["lines"] is JArray la ? la.ToList() : IsSet(vo["lines"]) ? new List<JToken> { vo["lines"] } : new List<JToken>();
                foreach (var line in lines)
                {
                    if (!IsLine(line)) { _rep.Error(vsrc, "a line must be a text (or {\"it\": …, \"en\": …})"); continue; }
                    _textLines.Add(TextTable.Register($"{key}:{v}:{ContentHash(line)}", BindTexts(line, vsrc)));
                }
                rule.LineCount = _textLines.Count - rule.LineStart;
                if (rule.LineCount == 0) { _rep.Error(vsrc, "the variant has no lines"); continue; }
                _textVariants.Add(rule);
            }
            group.VariantCount = _textVariants.Count - group.VariantStart;
            _textGroups.Add(group);
            return _textGroups.Count - 1;
        }

        /// <summary>tell: { lines, npc?, everywhere? }: one line of a group, said in the log by the npc.</summary>
        FxNode CompileTell(JObject o)
        {
            string name = Req(o, "lines");
            if (!((_tpl != null && _tpl.Groups.TryGetValue(name, out int group)) || _linesIndex.TryGetValue(name, out group)))
                throw new CondException($"unknown lines '{name}' (a group of the template's texts, or a file in lines/)");
            int npc = 0;
            if (IsSet(o["npc"])) npc = NpcRef(o, "npc");
            else if (_tpl == null) throw new CondException("tell outside an automatic quest needs 'npc': who says it");
            bool everywhere = (bool?)o["everywhere"] ?? false;
            return new FxNode { Type = FxType.Tell, A = group, B = everywhere ? 1 : 0, C = npc };
        }

        void CompileTemplates()
        {
            for (int ti = 0; ti < _db.TemplateList.Count; ti++)
            {
                var t = _db.TemplateList[ti];
                string src = $"{t.Source}#{t.Id}";
                _fxPackDir = t.PackDir;
                var ctx = new TplCtx { Id = t.Id };
                _tpl = ctx;

                // the slot names first: every part of the template can use them
                var kinds = new List<SlotKind>();
                for (int k = 0; k < t.Slots.Count; k++)
                {
                    var sd = t.Slots[k] ?? new SlotDef();
                    var kind = SlotKind.Npc;
                    switch ((sd.Type ?? "npc").ToLowerInvariant())
                    {
                        case "npc": case "character": kind = SlotKind.Npc; break;
                        case "place": kind = SlotKind.Place; break;
                        case "node": kind = SlotKind.Node; break;
                        case "item": kind = SlotKind.Item; break;
                        default: _rep.Error($"{src}.slots[{k}]", $"unknown slot type '{sd.Type}' (npc, place, node, item)"); break;
                    }
                    kinds.Add(kind);
                    if (string.IsNullOrEmpty(sd.Id) || !s_SlotName.IsMatch(sd.Id)) { _rep.Error($"{src}.slots[{k}]", "a slot needs an id (letters, digits, _ and -)"); continue; }
                    if (s_ReservedSlots.Contains(sd.Id)) { _rep.Error($"{src}.slots[{k}]", $"'{sd.Id}' cannot be the name of a slot"); continue; }
                    if (k + 1 >= BindRef.Max) { _rep.Error($"{src}.slots[{k}]", $"at most {BindRef.Max - 1} slots"); continue; }
                    if (ctx.Slots.ContainsKey(sd.Id)) { _rep.Error($"{src}.slots[{k}]", $"slot '{sd.Id}' is there twice"); continue; }
                    ctx.Slots[sd.Id] = (k + 1, kind);
                }

                var rule = new TemplateRule { Id = Ids.Hash(t.Id), Cond = -1, TalkGroup = -1, PauseMin = -1 };
                rule.TitleText = TextTable.Register($"template:{t.Id}:title", BindTexts(t.Title ?? new JValue(t.Id), src + ".title"));
                ctx.MaxBind = 0;
                if (IsSet(t.When)) rule.Cond = CompileCond(t.When, src + ".when");

                rule.SlotStart = _slots.Count;
                int slotCount = Math.Min(t.Slots.Count, BindRef.Max - 1);
                for (int k = 0; k < slotCount; k++)
                {
                    ctx.MaxBind = k + 1; ctx.It = k + 1;
                    _slots.Add(CompileSlot(t.Slots[k] ?? new SlotDef(), kinds[k], k, src));
                    ctx.It = -1;
                }
                rule.SlotCount = slotCount;
                ctx.MaxBind = slotCount;

                CompileWeight(t, ctx, ref rule, src);

                foreach (var kv in t.Texts)
                    ctx.Groups[kv.Key] = CompileGroup(kv.Key, $"template:{t.Id}:text:{kv.Key}", kv.Value, $"{src}.texts.{kv.Key}");
                if (ctx.Groups.TryGetValue("talk", out int talk)) rule.TalkGroup = talk;

                string key = $"template:{t.Id}";
                (rule.StartFxStart, rule.StartFxCount) = CompileEffects(t.OnStart, src + ".onStart", key + ":start");
                (rule.CompleteFxStart, rule.CompleteFxCount) = CompileEffects(t.OnComplete, src + ".onComplete", key + ":done");
                (rule.FailFxStart, rule.FailFxCount) = CompileEffects(t.OnFail, src + ".onFail", key + ":fail");

                ctx.InStage = true;
                rule.StageStart = _stages.Count;
                for (int si = 0; si < t.Stages.Count; si++)
                {
                    var s = t.Stages[si];
                    string ssrc = $"{src}.stages[{si}]";
                    string stageId = string.IsNullOrEmpty(s.Id) ? "stage" + si : s.Id;
                    var sr = new StageRule { Id = Ids.Hash(stageId), CompleteCond = -1, FailCond = -1, Next = si + 1 < t.Stages.Count ? si + 1 : -1 };
                    sr.TextId = TextTable.Register($"{key}:stage:{stageId}", BindTexts(s.Text ?? new JValue(""), ssrc + ".text"));
                    if (s.Complete != null) sr.CompleteCond = CompileCond(s.Complete, ssrc + ".complete");
                    if (s.Fail != null) sr.FailCond = CompileCond(s.Fail, ssrc + ".fail");
                    (sr.StartFxStart, sr.StartFxCount) = CompileEffects(s.OnStart, ssrc + ".onStart", $"{key}:stage:{stageId}:start");
                    (sr.FxStart, sr.FxCount) = CompileEffects(s.OnComplete, ssrc + ".onComplete", $"{key}:stage:{stageId}:done");
                    (sr.FailFxStart, sr.FailFxCount) = CompileEffects(s.OnFail, ssrc + ".onFail", $"{key}:stage:{stageId}:fail");
                    if (!string.IsNullOrEmpty(s.Next))
                    {
                        if (s.Next == "end") sr.Next = -1;
                        else if (_templateStageIndex[t.Id].TryGetValue(s.Next, out var ni)) sr.Next = ni;
                        else _rep.Error(ssrc, $"next stage '{s.Next}' not found");
                    }
                    if (s.Complete == null && s.Fail == null)
                        _rep.Warn(ssrc, "stage without 'complete' or 'fail': it never ends by itself (only stopAuto ends it)");
                    _stages.Add(sr);
                }
                rule.StageCount = t.Stages.Count;
                ctx.InStage = false;
                if (t.Stages.Count == 0) { _rep.Error(src, $"template '{t.Id}' has no stages: it is never picked"); rule.Cond = Never(); }

                if (t.PauseDays is float days)
                {
                    if (days < 0) _rep.Warn(src, "pauseDays cannot be negative; using 0");
                    rule.PauseMin = (int)Math.Round(Math.Max(0, days) * GameTime.MinutesPerDay);
                }
                _templates.Add(rule);
            }
            _tpl = null;
        }

        SlotRule CompileSlot(SlotDef sd, SlotKind kind, int k, string src)
        {
            string at = $"{src}.slots.{sd.Id ?? k.ToString()}";
            var r = new SlotRule { Id = Ids.Hash(sd.Id ?? "slot" + k), Kind = kind, Cond = -1, Free = (byte)(sd.Free ? 1 : 0), ListStart = _listValues.Count };
            try
            {
                switch (kind)
                {
                    case SlotKind.Place:
                        {
                            if (string.IsNullOrEmpty(sd.Place)) throw new CondException("a place slot needs 'place' (home, work…: a place of the character 'of')");
                            r.Place = Ids.Hash(sd.Place.TrimStart('$'));
                            string owner = string.IsNullOrEmpty(sd.Of) ? "self" : sd.Of;
                            if (TryBound(owner, SlotKind.Npc, out int bound))
                            {
                                if (BindRef.Slot(bound) > k) throw new CondException("'of' must be self or a slot above this one");
                                r.Of = bound;
                            }
                            else if (_db.Npcs.ContainsKey(owner)) r.Of = Ids.Hash(owner);
                            else throw new CondException($"unknown npc '{owner}'");
                            break;
                        }
                    case SlotKind.Node:
                        foreach (var n in sd.Nodes ?? new List<string>())
                        {
                            if (!_db.Maps.ContainsKey(n)) { _rep.Error(at, $"unknown node '{n}'"); continue; }
                            _listValues.Add(Ids.Hash(n));
                        }
                        break;
                    case SlotKind.Item:
                        foreach (var n in sd.Items ?? new List<string>())
                        {
                            if (!_itemIds.Contains(Ids.Hash(n))) { _rep.Error(at, $"unknown item '{n}'"); continue; }
                            _listValues.Add(Ids.Hash(n));
                        }
                        break;
                }
            }
            catch (CondException e) { _rep.Error(at, e.Message); r.Cond = Never(); }
            if (kind != SlotKind.Place && IsSet(sd.When)) r.Cond = CompileCond(sd.When, at + ".when");
            else if (kind == SlotKind.Place && IsSet(sd.When)) _rep.Warn(at, "a place slot has no 'when': it is the place 'place' of 'of'");
            r.ListCount = _listValues.Count - r.ListStart;
            return r;
        }

        void CompileWeight(TemplateDef t, TplCtx ctx, ref TemplateRule rule, string src)
        {
            var w = t.Weight ?? new WeightDef();
            rule.WeightBase = w.Base;
            rule.WeightStart = _weights.Count;
            foreach (var kv in w.Stats ?? new Dictionary<string, float>())
            {
                int stat = Ids.Hash(kv.Key);
                if (!_statIds.Contains(stat)) { _rep.Error(src + ".weight", $"unknown stat '{kv.Key}' (declare it in rules.json)"); continue; }
                _weights.Add(new WeightTerm { Kind = WeightKind.Stat, Key = stat, K = kv.Value });
            }
            foreach (var kv in w.Goals ?? new Dictionary<string, float>())
            {
                int goal = Ids.Hash(kv.Key);
                if (!_goalIds.Contains(goal)) { _rep.Error(src + ".weight", $"unknown goal '{kv.Key}' (declare it in rules.json \"goals\")"); continue; }
                _weights.Add(new WeightTerm { Kind = WeightKind.Goal, Key = goal, K = kv.Value });
            }
            foreach (var kv in w.Relations ?? new Dictionary<string, float>())
            {
                if (!ctx.Slots.TryGetValue(kv.Key, out var slot) || slot.kind != SlotKind.Npc) { _rep.Error(src + ".weight", $"relations: '{kv.Key}' is not a character slot of the template"); continue; }
                _weights.Add(new WeightTerm { Kind = WeightKind.Relation, Key = slot.bind, K = kv.Value });
            }
            rule.WeightCount = _weights.Count - rule.WeightStart;
        }
    }
}
