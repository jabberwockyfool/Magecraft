using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Magecraft
{
    public enum Severity { Erreur, Avertissement, Info }

    public sealed class Finding
    {
        public Severity Severity;
        public string File;      // nom du .scx.txt
        public int Line;         // 1-based, 0 = probleme au niveau du fichier
        public string Rule;
        public string Message;
        public string Fix;       // texte de remplacement propose, ou null
        public string Excerpt;
    }

    public sealed class ValidationReport
    {
        public List<Finding> Findings = new List<Finding>();
        public int TotalLines;
        public int PendingLines;      // lignes qui different du .scx deja compile
        public int FilesChecked;

        public int Errors { get { return Findings.Count(f => f.Severity == Severity.Erreur); } }
        public int Warnings { get { return Findings.Count(f => f.Severity == Severity.Avertissement); } }
        public bool UpToDate { get { return PendingLines == 0; } }
    }

    /// <summary>
    /// Controle les scripts traduits avant compilation. La reference est le texte
    /// reellement present dans les .scx de script-compile, relu par sc3tools : parite de
    /// lignes, caracteres refuses par le jeu, integrite des balises.
    /// </summary>
    public sealed class Validator
    {
        private static readonly Regex TagRe = new Regex(@"\[[^\]]*\]", RegexOptions.Compiled);

        /// <summary>Balises interpretees par le moteur. Le reste entre crochets est du texte.</summary>
        private static readonly HashSet<string> ControlTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "%p", "%e", "%18", "name", "line", "margin", "center", "evaluate",
            "linebreak", "alt-linebreak", "color", "parallel", "hardcoded-value",
            "auto-forward", "auto-forward-1a"
        };

        /// <summary>
        /// Balises dont l'absence casse le script ou le dialogue : pause de page, nom du
        /// locuteur, code evalue. Les autres (couleur, retour a la ligne, marge) relevent
        /// de la mise en forme et peuvent legitimement differer en francais.
        /// </summary>
        private static readonly HashSet<string> CriticalTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "%p", "%e", "%18", "name", "line", "evaluate", "hardcoded-value",
            "auto-forward", "auto-forward-1a", "parallel"
        };

        /// <summary>Balises dont la valeur doit rester identique au script compile (ce sont des opcodes).</summary>
        private static readonly HashSet<string> ExactTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "evaluate", "hardcoded-value", "%p", "%e", "%18", "auto-forward", "auto-forward-1a"
        };

        private readonly ProjectPaths _paths;
        private readonly Charset _charset;
        private readonly Sc3Tools _sc3;

        public Validator(ProjectPaths paths, Sc3Tools sc3, Charset charset)
        {
            _paths = paths;
            _sc3 = sc3;
            _charset = charset;
        }

        public ValidationReport Run(Action<string> log, Action<int, int> progress)
        {
            var report = new ValidationReport();
            var files = Directory.GetFiles(_paths.ScriptFr, "*.scx.txt").OrderBy(f => f).ToArray();

            using (var reference = Reference.Extract(_paths, _sc3, log))
            {
                // Une seule passe de detection du charset pour tout le corpus.
                var alphabet = new HashSet<char>();
                foreach (var f in files)
                    foreach (var c in StripTags(File.ReadAllText(f, Encoding.UTF8)))
                        alphabet.Add(c);
                _charset.Probe(alphabet, reference, log);

                for (int i = 0; i < files.Length; i++)
                {
                    CheckFile(files[i], reference, report);
                    if (progress != null) progress(i + 1, files.Length);
                }
            }
            report.FilesChecked = files.Length;
            return report;
        }

        private void CheckFile(string frPath, Reference reference, ValidationReport report)
        {
            string name = Path.GetFileName(frPath);
            CheckLineEndings(report, name, File.ReadAllText(frPath, Encoding.UTF8));
            var fr = File.ReadAllLines(frPath, Encoding.UTF8);
            string[] en = reference.Lines(name);

            if (en == null)
            {
                Add(report, Severity.Avertissement, name, 0, "reference",
                    "Aucun script correspondant dans script-compile : controles de structure impossibles.", null, null);
            }
            else if (en.Length != fr.Length)
            {
                Add(report, Severity.Erreur, name, 0, "lignes",
                    string.Format("{0} lignes ici contre {1} dans le script compile : la compilation echouera ou decalera le texte.",
                                  fr.Length, en.Length), null, null);
            }

            for (int i = 0; i < fr.Length; i++)
            {
                string line = fr[i];
                string enLine = (en != null && i < en.Length) ? en[i] : null;
                int no = i + 1;

                CheckBrackets(report, name, no, line, enLine);
                CheckCharset(report, name, no, line);

                if (enLine != null)
                {
                    CheckTags(report, name, no, line, enLine);

                    report.TotalLines++;
                    if (line.Trim().Length == 0 && enLine.Trim().Length > 0)
                    {
                        Add(report, Severity.Erreur, name, no, "vide",
                            "Ligne vide alors que le script compile contient du texte.", null, Excerpt(enLine));
                    }
                    else if (line != enLine)
                    {
                        report.PendingLines++;
                        Add(report, Severity.Info, name, no, "a-compiler",
                            "Texte different du script compile : sera applique a la prochaine construction.",
                            null, Excerpt(enLine));
                    }
                }
            }
        }

        // ------------------------------------------------------------------ regles

        /// <summary>
        /// Certaines lignes (tables de ponctuation de _system.scx) contiennent des crochets
        /// litteraux deja desequilibres dans le script d'origine. On ne signale donc que les
        /// desequilibres que le texte traduit introduit lui-meme.
        /// </summary>
        private void CheckBrackets(ValidationReport report, string file, int no, string fr, string en)
        {
            int frBalance = fr.Count(c => c == '[') - fr.Count(c => c == ']');
            int reference = en == null ? 0 : en.Count(c => c == '[') - en.Count(c => c == ']');
            if (frBalance != reference)
            {
                Add(report, Severity.Erreur, file, no, "crochets",
                    "Crochets desequilibres : une balise est mal fermee.", null, Excerpt(fr));
            }
        }

        /// <summary>
        /// sc3tools accepte les fins de ligne Windows completes, mais traite un retour
        /// chariot isole comme un caractere de texte -- et le refuse. C'est invisible dans
        /// un editeur : un seul \r en fin de fichier bloque toute la compilation.
        /// </summary>
        private void CheckLineEndings(ValidationReport report, string file, string raw)
        {
            int line = 1;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '\n') { line++; continue; }
                if (raw[i] != '\r') continue;
                if (i + 1 < raw.Length && raw[i + 1] == '\n') continue;   // CRLF normal

                Add(report, Severity.Erreur, file, line, "fin-de-ligne",
                    "Retour chariot isole : sc3tools le compte comme un caractere et refusera le fichier entier. " +
                    "« Corriger les caracteres » le supprime.", "\\r -> saut de ligne", null);
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
                Add(report, Severity.Erreur, file, no, "charset",
                    string.Format("Le caractere « {0} » (U+{1:X4}) n'existe pas dans le jeu : sc3tools refusera tout le fichier.{2}",
                                  c, (int)c, fix != null ? " Remplacement propose : " + fix : ""),
                    fix != null ? c + " -> " + fix : null, Excerpt(line));
            }
        }

        private void CheckTags(ValidationReport report, string file, int no, string fr, string en)
        {
            var frTags = ControlTagsOf(fr);
            var enTags = ControlTagsOf(en);

            // Comparaison par nom : une balise perdue casse l'affichage ou le script.
            var frNames = Counted(frTags.Select(TagName));
            var enNames = Counted(enTags.Select(TagName));

            foreach (var kv in enNames)
            {
                int got; frNames.TryGetValue(kv.Key, out got);
                if (got < kv.Value)
                    Add(report, CriticalTags.Contains(kv.Key) ? Severity.Erreur : Severity.Avertissement,
                        file, no, "balise",
                        string.Format("Balise [{0}] manquante ({1} attendue(s), {2} presente(s)).", kv.Key, kv.Value, got),
                        null, Excerpt(en));
            }
            foreach (var kv in frNames)
            {
                int expected; enNames.TryGetValue(kv.Key, out expected);
                if (kv.Value > expected)
                    Add(report, Severity.Avertissement, file, no, "balise",
                        string.Format("Balise [{0}] en trop ({1} presente(s), {2} dans le script compile).", kv.Key, kv.Value, expected),
                        null, Excerpt(fr));
            }

            // Les balises-opcodes doivent etre reproduites a l'identique, valeur comprise.
            var frExact = frTags.Where(t => ExactTags.Contains(TagName(t))).ToList();
            var enExact = enTags.Where(t => ExactTags.Contains(TagName(t))).ToList();
            if (frExact.Count == enExact.Count)
            {
                for (int k = 0; k < frExact.Count; k++)
                {
                    if (frExact[k] != enExact[k])
                        Add(report, Severity.Erreur, file, no, "balise",
                            string.Format("Balise modifiee : {0} au lieu de {1}. Cette valeur est du code, elle doit rester intacte.",
                                          frExact[k], enExact[k]), enExact[k], Excerpt(fr));
                }
            }
        }

        // ------------------------------------------------------------------ outils

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
