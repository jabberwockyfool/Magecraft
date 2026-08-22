using System;
using System.IO;

namespace Magecraft
{
    /// <summary>
    /// Localise le depot et les differents dossiers de travail. L'outil est concu pour
    /// vivre dans tool\ccfr\, mais il remonte l'arborescence pour retrouver la racine
    /// meme s'il est lance depuis bin\Debug ou depuis un raccourci.
    /// </summary>
    public sealed class ProjectPaths
    {
        public string Root { get; private set; }

        public string ScriptFr { get { return Path.Combine(Root, "script-decompile"); } }
        public string ScriptOut { get { return Path.Combine(Root, "script-compile"); } }
        public string Sc3Tools { get { return Path.Combine(Root, @"tool\text-replace-extract\sc3tools.exe"); } }
        public string EnscriptMpk { get { return Path.Combine(Root, @"languagebarrier\enscript.mpk"); } }
        public string Dictionnaire { get { return Path.Combine(Root, "Dictionnaire.md"); } }

        private ProjectPaths(string root) { Root = root; }

        /// <summary>Remonte depuis <paramref name="start"/> jusqu'a trouver la racine du depot.</summary>
        public static ProjectPaths Discover(string start)
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (LooksLikeRoot(dir.FullName)) return new ProjectPaths(dir.FullName);
                dir = dir.Parent;
            }
            return null;
        }

        public static bool LooksLikeRoot(string path)
        {
            return Directory.Exists(Path.Combine(path, "script-decompile"))
                && Directory.Exists(Path.Combine(path, "script-compile"));
        }

        /// <summary>Liste des problemes bloquants d'installation, vide si tout est en place.</summary>
        public string[] Check()
        {
            var problems = new System.Collections.Generic.List<string>();
            if (!Directory.Exists(ScriptFr)) problems.Add("Dossier introuvable : script-decompile");
            if (!Directory.Exists(ScriptOut)) problems.Add("Dossier introuvable : script-compile");
            if (!File.Exists(Sc3Tools)) problems.Add(@"Outil introuvable : tool\text-replace-extract\sc3tools.exe");
            return problems.ToArray();
        }

        /// <summary>Nom du .scx correspondant a un .txt (cc_01_01.scx.txt -> cc_01_01.scx).</summary>
        public static string ScxNameFor(string txtPath)
        {
            var name = Path.GetFileName(txtPath);
            return name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4)
                : name;
        }
    }
}
