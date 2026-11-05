namespace LivingNPC
{
    /// <summary>Time is an integer count of minutes since day 0, 00:00.</summary>
    public static class GameTime
    {
        public const int MinutesPerDay = 1440;
        static readonly string[] s_Days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        static readonly string[] s_Keys = { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };

        public static int DayOf(int now) => now / MinutesPerDay;
        public static int MinuteOfDay(int now) => now % MinutesPerDay;
        public static int DayOfWeek(int now, int startDow) => (startDow + now / MinutesPerDay) % 7;

        public static string Format(int now, int startDow)
        {
            int m = MinuteOfDay(now);
            return $"Day {DayOf(now) + 1} ({s_Days[DayOfWeek(now, startDow)]}) {m / 60:00}:{m % 60:00}";
        }

        public static string Clock(int minuteOfDay) => $"{minuteOfDay / 60:00}:{minuteOfDay % 60:00}";

        /// <summary>Parses "HH:mm" (also "24:00"). Returns false on malformed input.</summary>
        public static bool TryParse(string s, out int minutes)
        {
            minutes = 0;
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Trim().Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
            if (h < 0 || h > 24 || m < 0 || m > 59) return false;
            minutes = h * 60 + m;
            return true;
        }

        /// <summary>Parses day names: "mon".."sun", "weekdays", "weekend", "all". Returns bitmask (bit0 = Monday).</summary>
        public static bool TryParseDay(string s, out int mask)
        {
            mask = 0;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim().ToLowerInvariant();
            switch (s)
            {
                case "all": case "daily": mask = 0x7F; return true;
                case "weekdays": mask = 0x1F; return true;
                case "weekend": mask = 0x60; return true;
            }
            for (int i = 0; i < 7; i++)
                if (s == s_Keys[i]) { mask = 1 << i; return true; }
            return false;
        }

        public static int RotateMaskNextDay(int mask) => ((mask << 1) | (mask >> 6)) & 0x7F;
    }
}
