using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Magecraft
{
    public enum Severity { Error, Warning, Info }

    public sealed class Finding
    {
        public Severity Severity;
        public string File;      // text file name
        public int Line;         // 1-based, 0 means the whole file
        public string Rule;
        public string Message;
        public string Fix;       // suggested replacement, or null
        public string Excerpt;
    }

    public sealed class ValidationReport
    {
        public List<Finding> Findings = new List<Finding>();
        public int TotalLines;
        public int PendingLines;     // lines that differ from the compiled script
        public int FilesChecked;
        public int Untranslated;     // scripts with no text file at all

        public int Errors { get { return Findings.Count(f => f.Severity == Severity.Error); } }
        public int Warnings { get { return Findings.Count(f => f.Severity == Severity.Warning); } }
        public bool UpToDate { get { return PendingLines == 0; } }
    }

    /// <summary>
    /// Checks translated text before it is compiled. The reference is the text actually
    /// held by the .scx files, read back with sc3tools: line count, characters the game
    /// refuses, tag integrity.
    /// </summary>
    public sealed class Validator
    {
        private static readonly Regex TagRe = new Regex(@"\[[^\]]*\]", RegexOptions.Compiled);

        /// <summary>Tags the engine interprets. Anything else in brackets is literal text.</summary>
        private static readonly HashSet<string> ControlTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "%p", "%e", "%18", "name", "line", "margin", "center", "evaluate",
            "linebreak", "alt-linebreak", "color", "parallel", "hardcoded-value",
            "auto-forward", "auto-forward-1a"
        };

        /// <summary>
        /// Tags whose loss breaks the script or the dialogue: page break, speaker name,
        /// evaluated code. The rest (colour, line break, margin) is layout, which a
        /// translation may legitimately redistribute.
        /// </summary>
        private static readonly HashSet<string> CriticalTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "%p", "%e", "%18", "name", "line", "evaluate", "hardcoded-value",
            "auto-forward", "auto-forward-1a", "parallel"
        };

        /// <summary>Tags that must be reproduced exactly, value included: they are opcodes.</summary>
        private static readonly HashSet<string> ExactTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "evaluate", "hardcoded-value", "%p", "%e", "%18", "auto-forward", "auto-forward-1a"
        };

        private readonly Workspace _workspace;
        private readonly Sc3Tools _sc3;
        private readonly Charset _charset;

        public Validator(Workspace workspace, Sc3Tools sc3, Charset charset)
        {
            _workspace = workspace;
            _sc3 = sc3;
            _charset = charset;
        }

        public ValidationReport Run(Action<string> log, Action<int, int> progress)
        {
            var report = new ValidationReport();
            var scripts = _workspace.Scripts();

            using (var reference = Reference.Extract(_workspace, _sc3, log))
            {
                // One charset detection pass for the whole corpus.
                var alphabet = new HashSet<char>();
                foreach (var scx in scripts)
                {
                    string txt = _workspace.TextFor(Path.GetFileName(scx));
                    if (txt == null) continue;
                    foreach (var c in StripTags(File.ReadAllText(txt, Encoding.UTF8)))
                        alphabet.Add(c);
                }
                _charset.Probe(alphabet, reference, log);

                for (int i = 0; i < scripts.Count; i++)
                {
                    CheckScript(scripts[i], reference, report);
                    if (progress != null) progress(i + 1, scripts.Count);
                }
            }
            report.FilesChecked = scripts.Count;
            return report;
        }

        private void CheckScript(string scxPath, Reference reference, ValidationReport report)
        {
            string scxName = Path.GetFileName(scxPath);
            string txtPath = _workspace.TextFor(scxName);
            if (txtPath == null)
            {
                report.Untranslated++;
                Add(report, Severity.Info, scxName, 0, "no-text",
                    "No text file for this script: it will be packed unchanged.", null, null);
                return;
            }

            string name = Path.GetFileName(txtPath);
            CheckLineEndings(report, name, File.ReadAllText(txtPath, Encoding.UTF8));
            var text = File.ReadAllLines(txtPath, Encoding.UTF8);
            string[] source = reference.Lines(scxName);

            if (source == null)
            {
                Add(report, Severity.Warning, name, 0, "reference",
                    "Could not read the script's own text: structural checks unavailable.", null, null);
            }
            else if (source.Length != text.Length)
            {
                Add(report, Severity.Error, name, 0, "line-count",
                    string.Format("{0} lines here against {1} in the script: the build will fail or shift the text.",
                                  text.Length, source.Length), null, null);
            }

            for (int i = 0; i < text.Length; i++)
            {
                string line = text[i];
                string src = (source != null && i < source.Length) ? source[i] : null;
                int no = i + 1;

                CheckBrackets(report, name, no, line, src);
                CheckCharset(report, name, no, line);

                if (src == null) continue;

                CheckTags(report, name, no, line, src);

                report.TotalLines++;
                if (line.Trim().Length == 0 && src.Trim().Length > 0)
                {
                    Add(report, Severity.Error, name, no, "empty",
                        "Line is empty while the script holds text here.", null, Excerpt(src));
                }
                else if (line != src)
                {
                    report.PendingLines++;
                    Add(report, Severity.Info, name, no, "pending",
                        "Differs from the compiled script: the next build will apply it.", null, Excerpt(src));
                }
            }
        }

        // ------------------------------------------------------------------- rules

        /// <summary>
        /// Some lines (punctuation tables in system scripts) hold literal brackets that are
        /// already unbalanced in the original, so only report imbalance the text introduces.
        /// </summary>
        private void CheckBrackets(ValidationReport report, string file, int no, string text, string source)
        {
            int balance = text.Count(c => c == '[') - text.Count(c => c == ']');
            int expected = source == null ? 0 : source.Count(c => c == '[') - source.Count(c => c == ']');
            if (balance != expected)
            {
                Add(report, Severity.Error, file, no, "brackets",
                    "Unbalanced brackets: a tag is not closed properly.", null, Excerpt(text));
            }
        }

        /// <summary>
        /// sc3tools accepts complete Windows line endings, but treats a lone carriage return
        /// as a text character -- and rejects it. It is invisible in an editor, yet a single
        /// stray \r stops the whole file from compiling.
        /// </summary>
        private void CheckLineEndings(ValidationReport report, string file, string raw)
        {
            int line = 1;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '\n') { line++; continue; }
                if (raw[i] != '\r') continue;
                if (i + 1 < raw.Length && raw[i + 1] == '\n') continue;   // ordinary CRLF

                Add(report, Severity.Error, file, line, "line-ending",
                    "Lone carriage return: sc3tools counts it as a character and will reject the whole file. " +
                    "\"Fix characters\" removes it.", "\\r -> line break", null);
                line++;
            }
        }

        private void CheckCharset(ValidationReport report, string file, int no, string line)
        {
            var seen = new HashSet<char>();
            foreach (var c in StripTags(line))
            {
                if (c == '\t') continue;
                if (_charset.IsSupported(c) || !seen.Add(c)) continue;

                string fix = Charset.SuggestFix(c);
                Add(report, Severity.Error, file, no, "charset",
                    string.Format("The game has no character '{0}' (U+{1:X4}): sc3tools will reject the whole file.{2}",
                                  c, (int)c, fix != null ? " Suggested replacement: " + fix : ""),
                    fix != null ? c + " -> " + fix : null, Excerpt(line));
            }
        }

        private void CheckTags(ValidationReport report, string file, int no, string text, string source)
        {
            var textTags = ControlTagsOf(text);
            var sourceTags = ControlTagsOf(source);

            var textNames = Counted(textTags.Select(TagName));
            var sourceNames = Counted(sourceTags.Select(TagName));

            // Compare by name: a lost tag breaks either the display or the script itself.
            foreach (var kv in sourceNames)
            {
                int got; textNames.TryGetValue(kv.Key, out got);
                if (got < kv.Value)
                    Add(report, CriticalTags.Contains(kv.Key) ? Severity.Error : Severity.Warning,
                        file, no, "tag",
                        string.Format("Missing [{0}] tag ({1} expected, {2} present).", kv.Key, kv.Value, got),
                        null, Excerpt(source));
            }
            foreach (var kv in textNames)
            {
                int expected; sourceNames.TryGetValue(kv.Key, out expected);
                if (kv.Value > expected)
                    Add(report, Severity.Warning, file, no, "tag",
                        string.Format("Extra [{0}] tag ({1} present, {2} in the script).", kv.Key, kv.Value, expected),
                        null, Excerpt(text));
            }

            // Opcode tags must come back untouched, value included.
            var textExact = textTags.Where(t => ExactTags.Contains(TagName(t))).ToList();
            var sourceExact = sourceTags.Where(t => ExactTags.Contains(TagName(t))).ToList();
            if (textExact.Count == sourceExact.Count)
            {
                for (int k = 0; k < textExact.Count; k++)
                {
                    if (textExact[k] != sourceExact[k])
                        Add(report, Severity.Error, file, no, "tag",
                            string.Format("Altered tag: {0} instead of {1}. That value is code and must stay intact.",
                                          textExact[k], sourceExact[k]), sourceExact[k], Excerpt(text));
                }
            }
        }

        // ------------------------------------------------------------------- helpers

        public static string StripTags(string s)
        {
            return TagRe.Replace(s, "");
        }

        private static List<string> ControlTagsOf(string line)
        {
            return TagRe.Matches(line).Cast<Match>()
                        .Select(m => m.Value)
                        .Where(t => ControlTags.Contains(TagName(t)))
                        .ToList();
        }

        private static string TagName(string tag)
        {
            string inner = tag.Trim('[', ']');
            int sp = inner.IndexOf(' ');
            return sp < 0 ? inner : inner.Substring(0, sp);
        }

        private static Dictionary<string, int> Counted(IEnumerable<string> names)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names) { int c; d.TryGetValue(n, out c); d[n] = c + 1; }
            return d;
        }

        private static string Excerpt(string line)
        {
            line = line.Replace('\t', ' ');
            return line.Length <= 160 ? line : line.Substring(0, 157) + "...";
        }

        private static void Add(ValidationReport r, Severity sev, string file, int line, string rule, string msg, string fix, string excerpt)
        {
            r.Findings.Add(new Finding
            {
                Severity = sev, File = file, Line = line, Rule = rule,
                Message = msg, Fix = fix, Excerpt = excerpt
            });
        }
    }
}
