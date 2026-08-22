using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Magecraft
{
    /// <summary>
    /// Works out which characters the game can actually encode.
    ///
    /// The table does live inside sc3tools.exe, but mixed together with the other games':
    /// reading it directly yields false positives (U+2026 appears there even though
    /// CHAOS;CHILD rejects it). So we ask sc3tools itself, once per character, and cache
    /// the verdicts.
    /// </summary>
    public sealed class Charset
    {
        /// <summary>Safe replacements for characters the game does not know.</summary>
        public static readonly Dictionary<char, string> Fixes = new Dictionary<char, string>
        {
            { 'œ', "oe" },       // oe ligature
            { 'Œ', "OE" },       // OE ligature
            { '«', "“" },   // << -> curly opening quote
            { '»', "”" },   // >> -> curly closing quote
            { '…', "..." },      // ellipsis
            { 'Ÿ', "Y" },        // Y with diaeresis
            { ' ', " " },        // no-break space
            { ' ', " " },        // narrow no-break space
            { '‑', "-" },        // non-breaking hyphen
            { '−', "-" },        // minus sign
            { '″', "”" },   // double prime
        };

        private readonly Sc3Tools _sc3;
        private readonly string _cacheFile;
        private readonly HashSet<char> _good = new HashSet<char>();
        private readonly HashSet<char> _bad = new HashSet<char>();

        public Charset(Sc3Tools sc3, string cacheFile)
        {
            _sc3 = sc3;
            _cacheFile = cacheFile;
            LoadCache();
        }

        public bool IsKnown(char c) { return _good.Contains(c) || _bad.Contains(c); }
        public bool IsSupported(char c) { return !_bad.Contains(c); }
        public IEnumerable<char> Rejected { get { return _bad.OrderBy(c => c); } }

        /// <summary>Suggested replacement, or null when there is no obvious one.</summary>
        public static string SuggestFix(char c)
        {
            string fix;
            return Fixes.TryGetValue(c, out fix) ? fix : null;
        }

        /// <summary>
        /// Asks sc3tools about every character not yet known. Does nothing when the cache
        /// already covers them. Returns false if no compilable template could be prepared.
        /// </summary>
        public bool Probe(IEnumerable<char> chars, Reference reference, Action<string> log)
        {
            var unknown = chars.Where(c => c != '\n' && c != '\r' && !IsKnown(c)).Distinct().ToList();
            if (unknown.Count == 0) return true;

            string work = Path.Combine(Path.GetTempPath(), "magecraft-charset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string scxSource; string[] template; int lineIdx;
                if (!TryPrepare(reference, out scxSource, out template, out lineIdx))
                {
                    log("Could not prepare a test script: charset detection skipped.");
                    return false;
                }

                log(string.Format("Testing {0} unknown character(s) against sc3tools...", unknown.Count));

                // sc3tools stops at the first offending character, so we drop it and retry.
                foreach (var chunk in Chunk(unknown, 150))
                {
                    var pending = new List<char>(chunk);
                    while (pending.Count > 0)
                    {
                        var res = TryCompile(work, scxSource, template, lineIdx, pending);
                        if (res.Ok)
                        {
                            foreach (var c in pending) _good.Add(c);
                            break;
                        }
                        char bad = res.RejectedChar;
                        if (bad == '\0')
                        {
                            log("sc3tools returned an unexpected error: " + (res.FirstError ?? "?"));
                            return false;
                        }
                        _bad.Add(bad);
                        pending.RemoveAll(c => c == bad);
                    }
                }
                SaveCache();
                return true;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }

        // ------------------------------------------------------------------ internals

        private static bool TryPrepare(Reference reference, out string scxSource, out string[] template, out int lineIdx)
        {
            scxSource = null; template = null; lineIdx = -1;
            if (reference == null) return false;

            string[] lines;
            if (!reference.TryPickTemplate(out scxSource, out lines)) return false;

            lineIdx = Array.FindIndex(lines, l => l.IndexOf('[') >= 0 && l.Length > 20);
            if (lineIdx < 0) return false;

            template = lines;
            return true;
        }

        private Sc3Result TryCompile(string work, string scxSource, string[] template, int lineIdx, List<char> probe)
        {
            var lines = (string[])template.Clone();
            lines[lineIdx] = Insert(lines[lineIdx], new string(probe.ToArray()));

            // replace-text edits the .scx in place, so start from a fresh copy every time.
            string name = Path.GetFileName(scxSource);
            File.Copy(scxSource, Path.Combine(work, name), true);
            File.WriteAllLines(Path.Combine(work, name + ".txt"), lines, Utf8NoBom);
            return _sc3.ReplaceText(name, name + ".txt", work);
        }

        /// <summary>Inserts text right before the last tag so no opcode is disturbed.</summary>
        private static string Insert(string line, string text)
        {
            int at = line.LastIndexOf('[');
            return at < 0 ? line + text : line.Substring(0, at) + text + line.Substring(at);
        }

        private static IEnumerable<List<T>> Chunk<T>(List<T> src, int size)
        {
            for (int i = 0; i < src.Count; i += size)
                yield return src.GetRange(i, Math.Min(size, src.Count - i));
        }

        internal static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private void LoadCache()
        {
            if (!File.Exists(_cacheFile)) return;
            try
            {
                foreach (var line in File.ReadAllLines(_cacheFile, Encoding.UTF8))
                {
                    if (line.Length < 2) continue;
                    int code;
                    if (!int.TryParse(line.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out code)) continue;
                    if (line[0] == '+') _good.Add((char)code);
                    else if (line[0] == '-') _bad.Add((char)code);
                }
            }
            catch { /* unreadable cache: start over */ }
        }

        private void SaveCache()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# sc3tools verdicts on the game charset. '-' means the game rejects it.");
                foreach (var c in _bad.OrderBy(c => c)) sb.AppendLine("-" + ((int)c).ToString("X4"));
                foreach (var c in _good.OrderBy(c => c)) sb.AppendLine("+" + ((int)c).ToString("X4"));
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile));
                File.WriteAllText(_cacheFile, sb.ToString(), Utf8NoBom);
            }
            catch { /* cache not written: we will probe again next time */ }
        }
    }
}
