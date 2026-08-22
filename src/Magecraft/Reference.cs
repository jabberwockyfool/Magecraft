using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Magecraft
{
    /// <summary>
    /// Reference de controle : le texte reellement contenu dans les .scx de script-compile,
    /// reextrait par sc3tools.
    ///
    /// C'est la structure des scripts du jeu -- nombre de lignes et balises -- et non un
    /// export anglais qui peut avoir vieilli. Comparer script-decompile a cette reference
    /// dit donc a la fois si le texte est valide et ce que la prochaine compilation changera.
    /// </summary>
    public sealed class Reference : IDisposable
    {
        private readonly string _work;
        private readonly Dictionary<string, string[]> _lines =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return _lines.Count; } }

        private Reference(string work) { _work = work; }

        /// <summary>Copie les scripts compiles puis en extrait le texte en une seule passe.</summary>
        public static Reference Extract(ProjectPaths paths, Sc3Tools sc3, Action<string> log)
        {
            string work = Path.Combine(Path.GetTempPath(), "ccfr-ref-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var reference = new Reference(work);

            var scx = Directory.GetFiles(paths.ScriptOut, "*.scx");
            if (scx.Length == 0)
            {
                if (log != null) log("Aucun script .scx dans script-compile : controles de structure impossibles.");
                return reference;
            }

            foreach (var f in scx) File.Copy(f, Path.Combine(work, Path.GetFileName(f)), true);

            var res = sc3.ExtractText("*.scx", work);
            if (!res.Ok && log != null) log("sc3tools : " + (res.FirstError ?? "extraction partielle"));

            string txtDir = Path.Combine(work, "txt");
            if (Directory.Exists(txtDir))
                foreach (var f in Directory.GetFiles(txtDir, "*.txt"))
                    reference._lines[Path.GetFileName(f)] = File.ReadAllLines(f, Encoding.UTF8);

            if (log != null)
                log(string.Format("Reference : texte de {0} script(s) relu depuis script-compile.", reference._lines.Count));
            return reference;
        }

        /// <summary>Lignes de reference pour un nom de fichier texte, ou null si absent.</summary>
        public string[] Lines(string txtFileName)
        {
            string[] v;
            return _lines.TryGetValue(txtFileName, out v) ? v : null;
        }

        /// <summary>Un couple .scx / texte qui compile, utilise pour sonder le charset.</summary>
        public bool TryPickTemplate(out string scxPath, out string[] lines)
        {
            foreach (var kv in _lines.OrderBy(k => k.Value.Length))
            {
                string scx = Path.Combine(_work, ProjectPaths.ScxNameFor(kv.Key));
                if (!File.Exists(scx)) continue;
                if (!kv.Value.Any(l => l.IndexOf('[') >= 0 && l.Length > 20)) continue;
                scxPath = scx;
                lines = kv.Value;
                return true;
            }
            scxPath = null; lines = null;
            return false;
        }

        public void Dispose()
        {
            try { Directory.Delete(_work, true); } catch { }
        }
    }
}
