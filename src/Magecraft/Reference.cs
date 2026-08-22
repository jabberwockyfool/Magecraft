using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Magecraft
{
    /// <summary>
    /// The text actually stored inside the compiled scripts, read back with sc3tools.
    ///
    /// This is the structure the game really uses -- line count and tags -- rather than a
    /// separate export that may have drifted. Comparing the translation against it tells
    /// you both whether the text is valid and what the next build will change.
    /// </summary>
    public sealed class Reference : IDisposable
    {
        private readonly string _work;
        private readonly Dictionary<string, string[]> _lines =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return _lines.Count; } }

        private Reference(string work) { _work = work; }

        /// <summary>Copies the scripts to a scratch folder and extracts all their text in one pass.</summary>
        public static Reference Extract(Workspace workspace, Sc3Tools sc3, Action<string> log)
        {
            string work = Path.Combine(Path.GetTempPath(), "magecraft-ref-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var reference = new Reference(work);

            var scripts = workspace.Scripts();
            if (scripts.Count == 0)
            {
                if (log != null) log("No .scx files found: structural checks are unavailable.");
                return reference;
            }

            foreach (var f in scripts) File.Copy(f, Path.Combine(work, Path.GetFileName(f)), true);

            var res = sc3.ExtractText("*.scx", work);
            if (!res.Ok && log != null) log("sc3tools: " + (res.FirstError ?? "partial extraction"));

            string txtDir = Path.Combine(work, "txt");
            if (Directory.Exists(txtDir))
            {
                foreach (var f in Directory.GetFiles(txtDir, "*.txt"))
                {
                    // sc3tools writes <name>.scx.txt, so dropping the .txt gives the script name.
                    reference._lines[Path.GetFileNameWithoutExtension(f)] = File.ReadAllLines(f, Encoding.UTF8);
                }
            }

            if (log != null)
                log(string.Format("Reference: read the text of {0} script(s).", reference._lines.Count));
            return reference;
        }

        /// <summary>Reference lines for a script name such as cc_01_01.scx, or null if absent.</summary>
        public string[] Lines(string scxName)
        {
            string[] v;
            return _lines.TryGetValue(scxName, out v) ? v : null;
        }

        /// <summary>
        /// A script paired with its own text, used as a neutral carrier when probing the
        /// charset: since the text came out of that very script, it is sure to compile.
        /// </summary>
        public bool TryPickTemplate(out string scxPath, out string[] lines)
        {
            foreach (var kv in _lines.OrderBy(k => k.Value.Length))
            {
                string scx = Path.Combine(_work, kv.Key);
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
