using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace LivingNPC
{
    /// <summary>
    /// All player-facing strings live here (managed side) and are referenced from ECS by int id.
    /// Content may provide a plain string or an object {"it": "...", "en": "..."}.
    /// </summary>
    public static class TextTable
    {
        static readonly Dictionary<int, Dictionary<string, string>> s_Texts = new Dictionary<int, Dictionary<string, string>>();
        public static string Language = "en";
        public const string DefaultLanguage = "en";

        public static void Reset() => s_Texts.Clear();

        public static int Register(string key, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;
            var map = new Dictionary<string, string>();
            if (token.Type == JTokenType.String) map["*"] = token.Value<string>();
            else if (token is JObject o) foreach (var p in o.Properties()) map[p.Name] = p.Value.ToString();
            else map["*"] = token.ToString();
            int id = IdOf(key);
            s_Texts[id] = map;
            return id;
        }

        /// <summary>Text ids are the hash of their key with this prefix, so they never clash with other ids.</summary>
        public const string IdPrefix = "text:";
        public static int IdOf(string key) => Ids.Hash(IdPrefix + key);

        /// <summary>Resolves a localized JSON value directly (for managed-side presentation of map titles etc.).</summary>
        public static string Localize(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return "";
            if (token.Type == JTokenType.String) return token.Value<string>();
            if (token is JObject o)
            {
                var t = o[Language] ?? o[DefaultLanguage];
                if (t != null) return t.ToString();
                foreach (var p in o.Properties()) return p.Value.ToString();
            }
            return token.ToString();
        }

        public static string Get(int id)
        {
            if (id == 0 || !s_Texts.TryGetValue(id, out var map)) return "";
            if (map.TryGetValue(Language, out var t)) return t;
            if (map.TryGetValue("*", out t)) return t;
            if (map.TryGetValue(DefaultLanguage, out t)) return t;
            foreach (var kv in map) return kv.Value;
            return "";
        }
    }
}
