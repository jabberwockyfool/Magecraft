using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Magecraft
{
    public sealed class BuildOptions
    {
        public bool KeepCompiledScripts;   // also drop the built .scx next to the archive
        public bool PackDespiteFailures;   // write the archive even if some scripts failed
    }

    public sealed class BuildResult
    {
        public int Replaced;               // entries taken from the folder
        public int Kept;                   // entries copied from the original archive
        public List<string> Failures = new List<string>();
        public List<string> Ignored = new List<string>();   // folder files absent from the archive
        public string ArchivePath;
        public bool Ok { get { return Failures.Count == 0; } }
    }

    /// <summary>
    /// Builds an MPK archive, either from translated scripts or from a folder of assets.
    ///
    /// Source files are never modified: sc3tools rewrites scripts in place, so it only
    /// ever sees temporary copies.
    /// </summary>
    public sealed class Builder
    {
        private readonly Workspace _workspace;
        private readonly Sc3Tools _sc3;

        public Builder(Workspace workspace, Sc3Tools sc3)
        {
            _workspace = workspace;
            _sc3 = sc3;
        }

        public BuildResult Run(BuildOptions options, Action<string> log, Action<int, int> progress)
        {
            return _workspace.Mode == BuildMode.Scripts
                ? BuildScripts(options, log, progress)
                : BuildFiles(log, progress);
        }

        // ------------------------------------------------------------------- scripts

        private BuildResult BuildScripts(BuildOptions options, Action<string> log, Action<int, int> progress)
        {
            var result = new BuildResult();
            var scripts = _workspace.Scripts();

            string work = Path.Combine(Path.GetTempPath(), "magecraft-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                log(string.Format("{0} script(s) found.", scripts.Count));

                for (int i = 0; i < scripts.Count; i++)
                {
                    string name = Path.GetFileName(scripts[i]);
                    if (progress != null) progress(i + 1, scripts.Count * 2);

                    string staged = Path.Combine(work, name);
                    File.Copy(scripts[i], staged, true);

                    string txt = _workspace.TextFor(name);
                    if (txt == null)
                    {
                        log("  (no text)  " + name);
                        continue;
                    }

                    File.Copy(txt, staged + ".txt", true);
                    var res = _sc3.ReplaceText(name, name + ".txt", work);
                    if (res.Ok)
                    {
                        result.Replaced++;
                        log("  built      " + name);
                    }
                    else
                    {
                        result.Failures.Add(name + ": " + (res.FirstError ?? "sc3tools failed"));
                        log("  FAILED     " + name + " -> " + (res.FirstError ?? "?"));
                        // Fall back to the original script so the archive stays complete.
                        File.Copy(scripts[i], staged, true);
                    }
                }

                log(string.Format("Scripts: {0} built, {1} failed.", result.Replaced, result.Failures.Count));

                if (result.Failures.Count > 0 && !options.PackDespiteFailures)
                {
                    log("Archive not written. Tick \"Build even if some scripts fail\" to force it.");
                    return result;
                }

                var entries = PlanFromFolder(n => Path.Combine(work, n), scripts.Select(Path.GetFileName), result, log);
                result.ArchivePath = WriteArchive(entries, log, progress, scripts.Count);

                if (options.KeepCompiledScripts)
                {
                    string dir = Path.Combine(OutDir(), "compiled-scx");
                    Directory.CreateDirectory(dir);
                    foreach (var name in scripts.Select(Path.GetFileName))
                    {
                        string built = Path.Combine(work, name);
                        if (File.Exists(built)) File.Copy(built, Path.Combine(dir, name), true);
                    }
                    log("Compiled scripts copied to " + dir);
                }

                return result;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }

        // --------------------------------------------------------------------- files

        /// <summary>
        /// Rebuilds an asset archive: every entry of the original is kept, and replaced by
        /// the folder's file when one carries the same name.
        /// </summary>
        private BuildResult BuildFiles(Action<string> log, Action<int, int> progress)
        {
            var result = new BuildResult();
            var assets = _workspace.Assets();
            log(string.Format("{0} file(s) in the folder.", assets.Count));

            var entries = PlanFromFolder(n => assets.ContainsKey(n) ? assets[n] : null, assets.Keys, result, log);
            result.ArchivePath = WriteArchive(entries, log, progress, 0);
            return result;
        }

        // --------------------------------------------------------------------- shared

        /// <summary>
        /// Decides what each archive entry will contain. With an original archive, its
        /// table of contents is authoritative -- order, ids and names are copied, and an
        /// entry the folder does not provide is streamed back out of it. Without one, the
        /// entries are the folder's own files, sorted by name and numbered 0..n-1.
        /// </summary>
        private List<MpkBuildEntry> PlanFromFolder(Func<string, string> fileForName, IEnumerable<string> folderNames, BuildResult result, Action<string> log)
        {
            string template = _workspace.TemplateMpk.Trim();
            var entries = new List<MpkBuildEntry>();

            if (template.Length == 0)
            {
                uint id = 0;
                foreach (var name in folderNames.OrderBy(n => n, StringComparer.Ordinal))
                    entries.Add(MpkBuildEntry.FromFile(id++, name, fileForName(name)));
                result.Replaced = entries.Count;
                log(string.Format("No original archive: {0} entries numbered 0..{1} in name order.",
                                  entries.Count, entries.Count - 1));
                return entries;
            }

            var toc = Mpk.ReadToc(template);
            var known = new HashSet<string>(toc.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            int replaced = 0, kept = 0;

            foreach (var e in toc)
            {
                string file = fileForName(e.Name);
                if (file != null && File.Exists(file))
                {
                    entries.Add(MpkBuildEntry.FromFile(e.Id, e.Name, file));
                    replaced++;
                }
                else
                {
                    entries.Add(MpkBuildEntry.FromArchive(e, template));
                    kept++;
                }
            }

            // A file the original archive does not list cannot simply be added: entries are
            // addressed by id, so inventing one would not make the game load it.
            result.Ignored = folderNames.Where(n => !known.Contains(n))
                                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                        .ToList();

            result.Replaced = replaced;
            result.Kept = kept;
            log(string.Format("Table of contents copied from {0}: {1} entries, {2} replaced, {3} kept.",
                              Path.GetFileName(template), toc.Count, replaced, kept));
            if (result.Ignored.Count > 0)
                log(string.Format("{0} file(s) ignored, absent from the archive: {1}{2}",
                                  result.Ignored.Count, string.Join(", ", result.Ignored.Take(6)),
                                  result.Ignored.Count > 6 ? ", ..." : ""));
            return entries;
        }

        private string OutDir()
        {
            return Path.GetDirectoryName(Path.GetFullPath(_workspace.OutMpk.Trim()));
        }

        private string WriteArchive(List<MpkBuildEntry> entries, Action<string> log, Action<int, int> progress, int progressOffset)
        {
            string outPath = _workspace.OutMpk.Trim();
            Directory.CreateDirectory(OutDir());

            // The original archive may be the one we are about to overwrite, and entries
            // still stream out of it, so move it aside first rather than truncate it.
            string template = _workspace.TemplateMpk.Trim();
            if (File.Exists(outPath))
            {
                string backup = outPath + ".bak";
                try { File.Delete(backup); } catch { }
                File.Move(outPath, backup);
                log("Existing archive kept as " + Path.GetFileName(backup) + ".");

                if (string.Equals(Path.GetFullPath(template), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var e in entries.Where(e => e.SourceArchive != null)) e.SourceArchive = backup;
                    log("Entries kept from the original are read from that backup.");
                }
            }

            log(string.Format("Writing the archive ({0} entries)...", entries.Count));
            int total = progressOffset + entries.Count;
            Mpk.Write(outPath, entries,
                      (done, count) => { if (progress != null) progress(progressOffset + done, total); });

            log(string.Format("Done: {0} ({1:N0} bytes).", outPath, new FileInfo(outPath).Length));
            return outPath;
        }

        // ------------------------------------------------------------------- fixing

        /// <summary>
        /// Replaces characters the game rejects wherever a safe mapping exists, and removes
        /// stray carriage returns. Returns how many replacements were made.
        /// </summary>
        public int FixCharacters(Charset charset, Action<string> log)
        {
            int total = 0, touched = 0;

            foreach (var scx in _workspace.Scripts())
            {
                string path = _workspace.TextFor(Path.GetFileName(scx));
                if (path == null) continue;

                string text = File.ReadAllText(path, Encoding.UTF8);
                var sb = new StringBuilder(text.Length);
                int count = 0;

                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];

                    // A lone carriage return reads as text to sc3tools and dooms the file.
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
                    log(string.Format("  {0}: {1} replacement(s)", Path.GetFileName(path), count));
                    total += count;
                    touched++;
                }
            }

            log(string.Format("Fixed {0} character(s) across {1} file(s).", total, touched));
            return total;
        }

        /// <summary>
        /// Files mode dry run: what the build would replace, keep and ignore.
        /// </summary>
        public BuildResult PreviewFiles(Action<string> log)
        {
            var result = new BuildResult();
            var assets = _workspace.Assets();
            log(string.Format("{0} file(s) in the folder.", assets.Count));
            PlanFromFolder(n => assets.ContainsKey(n) ? assets[n] : null, assets.Keys, result, log);
            return result;
        }
    }
}
