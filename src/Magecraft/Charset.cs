using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Magecraft
{
    /// <summary>
    /// Determine quels caracteres CHAOS;CHILD sait encoder.
    ///
    /// La table est embarquee dans sc3tools.exe mais melangee a celle des autres jeux :
    /// la lire directement donne des faux positifs (U+2026 y figure alors que le jeu le
    /// refuse). On interroge donc sc3tools lui-meme, une seule fois par caractere, et on
    /// met le verdict en cache.
    /// </summary>
    public sealed class Charset
    {
        // Remplacements surs pour les caracteres francais que le jeu ne connait pas.
        public static readonly Dictionary<char, string> Fixes = new Dictionary<char, string>
        {
            { 'œ', "oe" },      // oe ligature
            { 'Œ', "OE" },      // OE ligature
            { '«', "“" },  // guillemet francais ouvrant -> guillemet courbe
            { '»', "”" },  // guillemet francais fermant -> guillemet courbe
            { '…', "..." },     // points de suspension
            { 'Ÿ', "Y" },       // Y trema
            { ' ', " " },       // espace insecable
            { ' ', " " },       // espace fine insecable
            { '‑', "-" },       // trait d'union insecable
            { '−', "-" },       // signe moins
            { '″', "”" },  // double prime
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

        /// <summary>Suggestion de remplacement, ou null s'il n'y en a pas d'evidente.</summary>
        public static string SuggestFix(char c)
        {
            string fix;
            return Fixes.TryGetValue(c, out fix) ? fix : null;
        }

        /// <summary>
        /// Teste aupres de sc3tools tous les caracteres encore inconnus. Ne fait rien si
        /// tout est deja en cache. Retourne false si aucun modele compilable n'a ete trouve.
        /// </summary>
        public bool Probe(IEnumerable<char> chars, Reference reference, Action<string> log)
        {
            var unknown = chars.Where(c => c != '\n' && c != '\r' && !IsKnown(c)).Distinct().ToList();
            if (unknown.Count == 0) return true;

            string work = Path.Combine(Path.GetTempPath(), "ccfr-charset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string scxSource; string[] template; int lineIdx;
                if (!TryPrepare(work, reference, out scxSource, out template, out lineIdx))
                {
                    log("Impossible de preparer un script de test : detection du charset abandonnee.");
                    return false;
                }

                log(string.Format("Test de {0} caractere(s) inconnu(s) via sc3tools...", unknown.Count));

                // sc3tools s'arrete au premier caractere fautif : on retire et on relance.
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
                            log("sc3tools a renvoye une erreur inattendue : " + (res.FirstError ?? "?"));
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

        // ---------------------------------------------------------------- interne

        /// <summary>
        /// Prend un couple .scx / texte issu de script-compile : comme ce texte sort du
        /// script lui-meme, il compile forcement, ce qui en fait un support neutre pour
        /// tester les caracteres.
        /// </summary>
        private bool TryPrepare(string work, Reference reference, out string scxSource, out string[] template, out int lineIdx)
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

            // replace-text modifie le .scx sur place : on repart d'une copie fraiche.
            string name = Path.GetFileName(scxSource);
            File.Copy(scxSource, Path.Combine(work, name), true);
            File.WriteAllLines(Path.Combine(work, name + ".txt"), lines, Utf8NoBom);
            return _sc3.ReplaceText(name, name + ".txt", work);
        }

        /// <summary>Insere du texte juste avant la derniere balise, pour ne casser aucun opcode.</summary>
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
            catch { /* cache illisible : on repartira de zero */ }
        }

        private void SaveCache()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Verdicts de sc3tools sur le charset CHAOS;CHILD. '-' = refuse par le jeu.");
                foreach (var c in _bad.OrderBy(c => c)) sb.AppendLine("-" + ((int)c).ToString("X4"));
                foreach (var c in _good.OrderBy(c => c)) sb.AppendLine("+" + ((int)c).ToString("X4"));
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile));
                File.WriteAllText(_cacheFile, sb.ToString(), Utf8NoBom);
            }
            catch { /* cache non ecrit : on reprobera au prochain lancement */ }
        }
    }
}
