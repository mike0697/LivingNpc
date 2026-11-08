using System.Collections.Generic;
using System.Text.RegularExpressions;
using Unity.Collections;

namespace LivingNPC.Content
{
    // Placeholders in the texts the player reads, replaced when they are shown:
    //   {npc}                      the character the player talks to (or who says the line)
    //   {#0} {#1}...               the npc of an automatic quest and its slots (the compiler writes {self} and the slot names so)
    //   {x.first} {x.last} {x.full} {x.age}   first name, last name, both, age of a character
    //   {x:his|her|their}          a form for a male, a female and another character (the third is optional: the first is used)
    // A character shows its name, an item its name, a place its title.
    public partial class CompiledContent
    {
        static readonly Regex s_Token = new Regex(@"\{(npc|#[0-9])(?:\.([A-Za-z]+))?(?::([^{}]*))?\}");

        Dictionary<int, CompiledNpc> _npcById;

        public CompiledNpc NpcById(int id)
        {
            if (_npcById == null)
            {
                _npcById = new Dictionary<int, CompiledNpc>();
                foreach (var n in Npcs) _npcById[n.Id] = n;
            }
            return _npcById.TryGetValue(id, out var npc) ? npc : null;
        }

        /// <summary>The text with its placeholders replaced: <paramref name="npc"/> for {npc}, <paramref name="bind"/> for {#k}.</summary>
        public string Format(string text, int npc, FixedList32Bytes<int> bind = default)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text;
            return s_Token.Replace(text, m =>
            {
                int v;
                string who = m.Groups[1].Value;
                if (who == "npc") v = npc;
                else
                {
                    int k = who[1] - '0';
                    if (k >= bind.Length) return m.Value;
                    v = bind[k];
                }
                if (m.Groups[3].Success)
                {
                    var forms = m.Groups[3].Value.Split('|');
                    byte sex = NpcById(v)?.Sex ?? 0;
                    int pick = sex == 2 ? 1 : sex == 3 ? 2 : 0;
                    return forms[pick < forms.Length ? pick : 0];
                }
                return NameOf(v, m.Groups[2].Success ? m.Groups[2].Value.ToLowerInvariant() : "");
            });
        }

        /// <summary>What a value of a text shows: a character's name (or first / last / full / age), an item's name, a place's title.</summary>
        public string NameOf(int id, string field = "")
        {
            if (id == 0) return "";
            var npc = NpcById(id);
            if (npc != null)
            {
                string first = TextTable.Get(npc.FirstNameText), last = TextTable.Get(npc.LastNameText), name = TextTable.Get(npc.NameText);
                switch (field)
                {
                    case "first": return first.Length > 0 ? first : name;
                    case "last": return last.Length > 0 ? last : name;
                    case "full": return first.Length + last.Length > 0 ? $"{first} {last}".Trim() : name;
                    case "age": return npc.Age.ToString();
                    default: return name;
                }
            }
            foreach (var r in ItemRules)
                if (r.Id == id) { string n = TextTable.Get(r.NameText); return n.Length > 0 ? n : Ids.Name(id); }
            if (Db != null && Db.Maps.TryGetValue(Ids.Name(id), out var map))
            {
                string title = TextTable.Localize(map.Title);
                return title.Length > 0 ? title : map.Id;
            }
            return Ids.Name(id);
        }
    }
}
