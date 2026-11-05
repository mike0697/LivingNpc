using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LivingNPC.Content
{
    public enum Severity { Info, Warning, Error }

    /// <summary>
    /// Collects problems found while loading/compiling external content. Content is authored by players and LLMs,
    /// so nothing here throws: a bad file is reported (with file + id) and skipped. The report is written to disk
    /// so that an LLM (or a human) can read it and fix the offending file.
    /// </summary>
    public class ValidationReport
    {
        public struct Entry { public Severity Severity; public string Source; public string Message; }

        public readonly List<Entry> Entries = new List<Entry>();
        public int ErrorCount { get; private set; }
        public int WarningCount { get; private set; }

        public void Info(string source, string msg) => Entries.Add(new Entry { Severity = Severity.Info, Source = source, Message = msg });
        public void Warn(string source, string msg) { WarningCount++; Entries.Add(new Entry { Severity = Severity.Warning, Source = source, Message = msg }); }
        public void Error(string source, string msg) { ErrorCount++; Entries.Add(new Entry { Severity = Severity.Error, Source = source, Message = msg }); }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Content report: {ErrorCount} error(s), {WarningCount} warning(s)");
            foreach (var e in Entries)
                sb.AppendLine($"[{e.Severity}] {e.Source}: {e.Message}");
            return sb.ToString();
        }

        public void Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, ToText());
            }
            catch { /* reporting must never break the game */ }
        }
    }
}
