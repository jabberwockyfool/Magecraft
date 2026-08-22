using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Magecraft
{
    public sealed class BuildOptions
    {
        public bool Rebuild;            // recompiler meme les scripts deja a jour
        public bool PackMpk = true;     // reconstruire languagebarrier\enscript.mpk
        public string InstallDir;       // racine du jeu, ou null pour ne pas installer
    }

    public sealed class BuildResult
    {
        public int Compiled;
        public int Skipped;
        public List<string> Failures = new List<string>();
        public string MpkPath;
        public bool Ok { get { return Failures.Count == 0; } }
    }

    /// <summary>
    /// Chaine de construction : script-decompile (texte FR) -> script-compile (.scx)
    /// -> languagebarrier\enscript.mpk -> eventuellement le dossier du jeu.
    /// </summary>
    public sealed class Builder
    {
        private readonly ProjectPaths _paths;
        private readonly Sc3Tools _sc3;

        public Builder(ProjectPaths paths, Sc3Tools sc3)
        {
            _paths = paths;
            _sc3 = sc3;
        }

        public BuildResult Run(BuildOptions opt, Action<string> log, Action<int, int> progress)
        {
            var result = new BuildResult();
            var txts = Directory.GetFiles(_paths.ScriptFr, "*.scx.txt").OrderBy(f => f).ToArray();
            Directory.CreateDirectory(_paths.ScriptOut);

            string work = Path.Combine(Path.GetTempPath(), "ccfr-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                for (int i = 0; i < txts.Length; i++)
                {
                    string txt = txts[i];
                    string scxName = ProjectPaths.ScxNameFor(txt);
                    string scxOut = Path.Combine(_paths.ScriptOut, scxName);
                    if (progress != null) progress(i + 1, txts.Length);

                    if (!File.Exists(scxOut))
                    {
                        result.Failures.Add(scxName + " : aucun script de base dans script-compile.");
                        continue;
                    }
                    if (!opt.Rebuild && File.GetLastWriteTimeUtc(scxOut) > File.GetLastWriteTimeUtc(txt))
                    {
                        result.Skipped++;
                        continue;
                    }

                    // replace-text ecrit sur place : on travaille sur une copie, et on ne
                    // remplace le script du depot qu'en cas de succes.
                    string tmpScx = Path.Combine(work, scxName);
                    string tmpTxt = Path.Combine(work, scxName + ".txt");
                    File.Copy(scxOut, tmpScx, true);
                    File.Copy(txt, tmpTxt, true);

                    var res = _sc3.ReplaceText(scxName, scxName + ".txt", work);
                    if (!res.Ok)
                    {
                        result.Failures.Add(scxName + " : " + (res.FirstError ?? "echec de sc3tools"));
                        log("  ECHEC  " + scxName + " -> " + (res.FirstError ?? "?"));
                        continue;
                    }

                    File.Copy(tmpScx, scxOut, true);
                    result.Compiled++;
                    log("  ok     " + scxName);
                }
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }

            log(string.Format("Scripts : {0} compile(s), {1} deja a jour, {2} en echec.",
                              result.Compiled, result.Skipped, result.Failures.Count));

            if (opt.PackMpk && result.Ok)
            {
                result.MpkPath = PackEnscript(log, progress);
                if (opt.InstallDir != null) Install(opt.InstallDir, result.MpkPath, log);
            }
            else if (opt.PackMpk)
            {
                log("Archive non reconstruite : corrige d'abord les scripts en echec.");
            }

            return result;
        }

        /// <summary>
        /// Reconstruit enscript.mpk en reprenant l'ordre, les identifiants et les noms de
        /// l'archive existante : le jeu adresse les scripts par identifiant, pas par nom.
        /// </summary>
        public string PackEnscript(Action<string> log, Action<int, int> progress)
        {
            string mpk = _paths.EnscriptMpk;
            if (!File.Exists(mpk))
                throw new FileNotFoundException("Archive de reference introuvable : " + mpk);

            var toc = Mpk.ReadToc(mpk);
            var missing = toc.Where(e => !File.Exists(Path.Combine(_paths.ScriptOut, e.Name))).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "Scripts absents de script-compile : " + string.Join(", ", missing.Take(5).Select(e => e.Name)) +
                    (missing.Count > 5 ? " (+" + (missing.Count - 5) + ")" : ""));

            string tmp = mpk + ".new";
            log(string.Format("Construction de l'archive ({0} entrees)...", toc.Count));
            Mpk.Write(tmp, toc, name => Path.Combine(_paths.ScriptOut, name), progress);

            string backup = mpk + ".bak";
            try { File.Delete(backup); } catch { }
            File.Replace(tmp, mpk, backup);
            log(string.Format("Archive ecrite : {0} ({1:N0} octets). Sauvegarde : {2}",
                              mpk, new FileInfo(mpk).Length, Path.GetFileName(backup)));
            return mpk;
        }

        private static void Install(string gameDir, string mpk, Action<string> log)
        {
            string dest = Path.Combine(gameDir, @"languagebarrier\enscript.mpk");
            if (!Directory.Exists(Path.GetDirectoryName(dest)))
            {
                log("Installation ignoree : " + Path.GetDirectoryName(dest) + " est introuvable.");
                return;
            }
            File.Copy(mpk, dest, true);
            log("Copie dans le jeu : " + dest);
        }

        /// <summary>
        /// Remplace dans les textes FR les caracteres refuses par le jeu quand une
        /// correspondance sure existe. Retourne le nombre de remplacements effectues.
        /// </summary>
        public int AutoFix(Charset charset, Action<string> log)
        {
            int total = 0, touched = 0;
            foreach (var path in Directory.GetFiles(_paths.ScriptFr, "*.scx.txt"))
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                var sb = new StringBuilder(text.Length);
                int count = 0;

                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];

                    // Retour chariot isole : sc3tools le prend pour du texte et refuse le fichier.
                    if (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))
                    {
                        sb.Append('\n');
                        count++;
                        continue;
                    }

                    string fix;
                    if (!charset.IsSupported(c) && Charset.Fixes.TryGetValue(c, out fix))
                    {
                        sb.Append(fix);
                        count++;
                    }
                    else sb.Append(c);
                }

                if (count > 0)
                {
                    File.WriteAllText(path, sb.ToString(), Charset.Utf8NoBom);
                    log(string.Format("  {0} : {1} remplacement(s)", Path.GetFileName(path), count));
                    total += count;
                    touched++;
                }
            }
            log(string.Format("Correction automatique : {0} remplacement(s) dans {1} fichier(s).", total, touched));
            return total;
        }
    }
}
