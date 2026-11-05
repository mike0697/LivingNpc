using System.Collections.Generic;

namespace LivingNPC
{
    /// <summary>
    /// Maps human-readable string ids (used in the external JSON content) to stable 32-bit ids used by the
    /// ECS side. 0 is reserved for "none" (and for "player" where a target is expected).
    /// </summary>
    public static class Ids
    {
        static readonly Dictionary<int, string> s_Names = new Dictionary<int, string>();
        static readonly List<string> s_Collisions = new List<string>();

        public static IReadOnlyList<string> Collisions => s_Collisions;

        public static int Hash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619;
            }
            int id = (int)h;
            if (id == 0) id = 1;

            if (s_Names.TryGetValue(id, out var existing))
            {
                if (existing != s) s_Collisions.Add($"'{existing}' and '{s}' hash to the same id");
            }
            else
            {
                s_Names[id] = s;
            }
            return id;
        }

        /// <summary>
        /// Flag keys holding the runtime state of a map link (1 = forced on, 2 = forced off, unset = the value
        /// written in the JSON). Shared by the compiler, the simulation and the view so they always agree.
        /// </summary>
        public static int LinkVisibleKey(string linkId) => Hash("link:" + linkId + ":visible");
        public static int LinkOpenKey(string linkId) => Hash("link:" + linkId + ":open");

        /// <summary>
        /// Flag key holding the runtime state of an overlay image shown by the showImage / hideImage effects:
        /// value = index into CompiledContent.Images + 1 while shown, 0 (or unset) while hidden.
        /// </summary>
        public static int ImageKey(string imageKey) => Hash("image:" + imageKey + ":shown");

        /// <summary>
        /// Flag keys holding the runtime state of a looping sound (startMusic / stopMusic, startAmbient / stopAmbient):
        /// value = index into CompiledContent.Sounds + 1 while it plays, 0 (or unset) while it is off.
        /// There is one music channel; every ambient name is a channel of its own, so ambients layer on top of each other.
        /// </summary>
        public static int MusicKey() => Hash("sound:music");

        /// <summary>The npc place that is also where it goes when no routine block applies (setPlace on it moves NpcLocation.HomeNode too).</summary>
        public const string HomePlace = "home";
        public static int AmbientKey(string name) => Hash("sound:ambient:" + name);

        public static string Name(int id) => id == 0 ? "" : (s_Names.TryGetValue(id, out var n) ? n : id.ToString());

        /// <summary>The string behind <paramref name="id"/> when it was hashed in this session; false for an id this session never saw.</summary>
        public static bool TryName(int id, out string name) => s_Names.TryGetValue(id, out name);
        public static void Reset() { s_Names.Clear(); s_Collisions.Clear(); }
    }
}
