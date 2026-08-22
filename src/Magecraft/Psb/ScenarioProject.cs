using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Magecraft
{
    public sealed class ScenarioResult
    {
        public int Files;
        public int Lines;
        public int Changed;
        public List<string> Failures = new List<string>();
        public string ArchivePath;
        public bool Ok { get { return Failures.Count == 0; } }
    }

    /// <summary>
    /// Extracts an M2 scenario archive to one plain text file per script, and builds it
    /// back. The workspace keeps the decompiled scripts next to the text, so a build only
    /// needs the text files the translator edited.
    ///
    ///   &lt;workspace&gt;\archive\    the archive, its scripts and their JSON  (managed here)
    ///   &lt;workspace&gt;\text\       one .txt per script -- what gets translated
    ///   &lt;workspace&gt;\reference\  the same lines in Japanese, for context
    /// </summary>
    public sealed class ScenarioProject
    {
        private readonly Workspace _workspace;
        private readonly FreeMoteRunner _tools;

        public ScenarioProject(Workspace workspace, FreeMoteRunner tools)
        {
            _workspace = workspace;
            _tools = tools;
        }

        private string Root { get { return _workspace.SourceDir; } }

        // Folder names follow the layout translation projects already use. An older
        // workspace built with "archive"/"text" keeps working: whichever exists wins.
        public string ArchiveDir { get { return Pick("script-compile", "archive"); } }
        public string TextDir { get { return Pick("script-decompile", "text"); } }
        public string ReferenceDir { get { return Path.Combine(Root, "reference"); } }

        private string Pick(string preferred, string legacy)
        {
            string a = Path.Combine(Root, preferred);
            string b = Path.Combine(Root, legacy);
            return (!Directory.Exists(a) && Directory.Exists(b)) ? b : a;
        }

        /// <summary>"scenario" for scenario_info.psb.m.</summary>
        private string BaseName
        {
            get
            {
                string n = Path.GetFileName(_workspace.TemplateMpk);
                int i = n.IndexOf("_info", StringComparison.OrdinalIgnoreCase);
                return i > 0 ? n.Substring(0, i) : Path.GetFileNameWithoutExtension(n);
            }
        }

        private string ScriptsDir { get { return Path.Combine(ArchiveDir, BaseName); } }
        private string InfoJson { get { return Path.Combine(ArchiveDir, BaseName + "_info.psb.m.json"); } }

        // ------------------------------------------------------------------ extract

        public ScenarioResult Extract(Action<string> log, Action<int, int> progress)
        {
            var result = new ScenarioResult();
            string info = _workspace.TemplateMpk;
            string body = Path.Combine(Path.GetDirectoryName(info), BaseName + "_body.bin");
            if (!File.Exists(body))
                throw new FileNotFoundException("Archive body not found: " + body);

            if (Directory.Exists(ArchiveDir)) Directory.Delete(ArchiveDir, true);
            Directory.CreateDirectory(ArchiveDir);
            Directory.CreateDirectory(TextDir);
            Directory.CreateDirectory(ReferenceDir);

            log("Copying the archive...");
            File.Copy(info, Path.Combine(ArchiveDir, Path.GetFileName(info)), true);
            File.Copy(body, Path.Combine(ArchiveDir, Path.GetFileName(body)), true);

            log("Extracting the archive...");
            var res = _tools.ExtractArchive(Path.Combine(ArchiveDir, Path.GetFileName(info)), _workspace.MdfKey);
            if (!res.Ok) throw new InvalidOperationException("Extraction failed: " + (res.FirstError ?? res.Output));

            log("Decompiling the scripts...");
            res = _tools.DecompileFolder(ScriptsDir);
            if (!res.Ok) log("  warning: " + (res.FirstError ?? "partial decompilation"));

            var jsons = Directory.GetFiles(ScriptsDir, "*.json")
                                 .Where(f => !f.EndsWith(".resx.json", StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(f => f).ToArray();
            log(string.Format("Reading {0} script(s)...", jsons.Length));

            for (int i = 0; i < jsons.Length; i++)
            {
                if (progress != null) progress(i + 1, jsons.Length);
                var scenario = Load(jsons[i]);
                string name = ScriptName(jsons[i]);

                int track = ScenarioText.TrackOf(scenario, _workspace.Language);
                if (track < 0)
                {
                    log("  (no " + _workspace.Language + " track) " + name);
                    continue;
                }

                var lines = ScenarioText.Read(scenario, track);
                var japanese = ScenarioText.Read(scenario, 0);
                if (lines.Count == 0) continue;

                File.WriteAllLines(Path.Combine(TextDir, name + ".txt"), lines, Utf8);
                File.WriteAllLines(Path.Combine(ReferenceDir, name + ".txt"), japanese, Utf8);
                result.Files++;
                result.Lines += lines.Count;
            }

            log(string.Format("Extracted: {0} file(s), {1} line(s) into {2}", result.Files, result.Lines, TextDir));
            return result;
        }

        // -------------------------------------------------------------------- build

        public ScenarioResult Build(Action<string> log, Action<int, int> progress)
        {
            var result = new ScenarioResult();
            if (!Directory.Exists(ScriptsDir))
                throw new DirectoryNotFoundException("Run Extract first: " + ScriptsDir + " is missing.");

            var texts = Directory.GetFiles(TextDir, "*.txt").OrderBy(f => f).ToArray();
            log(string.Format("{0} text file(s).", texts.Length));

            int step = 0, steps = texts.Length + 1;
            foreach (var txt in texts)
            {
                if (progress != null) progress(++step, steps);
                string name = Path.GetFileNameWithoutExtension(txt);
                string json = Path.Combine(ScriptsDir, name + ".scn.m.json");
                if (!File.Exists(json))
                {
                    result.Failures.Add(name + ": no matching script in the workspace.");
                    continue;
                }

                var scenario = Load(json);
                int track = ScenarioText.TrackOf(scenario, _workspace.Language);
                if (track < 0)
                {
                    result.Failures.Add(name + ": no " + _workspace.Language + " track.");
                    continue;
                }

                var lines = File.ReadAllLines(txt, Encoding.UTF8).ToList();
                string error;
                int changed = ScenarioText.Write(scenario, track, lines, out error);
                if (error != null)
                {
                    result.Failures.Add(name + ": " + error + " -- the file must keep one line per entry.");
                    continue;
                }

                result.Files++;
                result.Lines += lines.Count;
                if (changed == 0) continue;

                File.WriteAllText(json, scenario.ToString(Newtonsoft.Json.Formatting.None), Utf8);

                string produced;
                var res = _tools.CompileJson(json, ScriptsDir, out produced);
                if (!res.Ok || produced == null)
                {
                    result.Failures.Add(name + ": " + (res.FirstError ?? "compilation failed"));
                    continue;
                }

                string target = Path.Combine(ScriptsDir, name + ".scn.m");
                File.Copy(produced, target, true);
                File.Delete(produced);
                result.Changed += changed;
                log(string.Format("  {0}: {1} line(s) updated", name, changed));
            }

            log(string.Format("Scripts: {0} read, {1} line(s) changed, {2} failure(s).",
                              result.Files, result.Changed, result.Failures.Count));
            if (result.Failures.Count > 0) return result;

            if (progress != null) progress(steps, steps);
            log("Packing the archive...");
            var pack = _tools.PackArchive(InfoJson, _workspace.MdfKey, _workspace.MdfKeyLength);
            if (!pack.Ok) throw new InvalidOperationException("Packing failed: " + (pack.FirstError ?? pack.Output));

            result.ArchivePath = Deliver(log);
            return result;
        }

        /// <summary>
        /// Copies the freshly packed pair to the output folder. Both files keep the names
        /// the game looks for -- "&lt;name&gt;_info.psb.m" and "&lt;name&gt;_body.bin" -- so the
        /// destination is a folder, never a file name.
        /// </summary>
        private string Deliver(Action<string> log)
        {
            string outDir = _workspace.OutMpk.Trim();
            // Tolerate a file being picked: what was meant is the folder holding it.
            if (File.Exists(outDir) || Path.HasExtension(outDir))
                outDir = Path.GetDirectoryName(Path.GetFullPath(outDir));
            Directory.CreateDirectory(outDir);

            string outInfo = Path.Combine(outDir, BaseName + "_info.psb.m");
            string outBody = Path.Combine(outDir, BaseName + "_body.bin");
            string builtInfo = Path.Combine(ArchiveDir, BaseName + "_info.psb.m");
            string builtBody = Path.Combine(ArchiveDir, BaseName + "_body.bin");

            if (string.Equals(Path.GetFullPath(outInfo), Path.GetFullPath(builtInfo), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The output folder cannot be the workspace's own archive folder.");

            foreach (var pair in new[] { new[] { builtInfo, outInfo }, new[] { builtBody, outBody } })
            {
                if (File.Exists(pair[1]))
                {
                    string backup = pair[1] + ".bak";
                    try { File.Delete(backup); } catch { }
                    File.Move(pair[1], backup);
                }
                File.Copy(pair[0], pair[1], true);
            }

            log(string.Format("Written to {0}: {1} ({2:N0} bytes) and {3} ({4:N0} bytes). Previous files kept as .bak.",
                              outDir, Path.GetFileName(outInfo), new FileInfo(outInfo).Length,
                              Path.GetFileName(outBody), new FileInfo(outBody).Length));
            return outInfo;
        }

        // ------------------------------------------------------------------- helpers

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private static JObject Load(string path)
        {
            return JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        /// <summary>resg01_01.ks.scn.m.json -> resg01_01.ks</summary>
        private static string ScriptName(string jsonPath)
        {
            string n = Path.GetFileName(jsonPath);
            foreach (var suffix in new[] { ".scn.m.json", ".json" })
                if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return n.Substring(0, n.Length - suffix.Length);
            return n;
        }
    }
}
