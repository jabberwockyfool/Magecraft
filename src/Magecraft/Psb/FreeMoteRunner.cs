using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Magecraft
{
    /// <summary>Result of one FreeMote invocation.</summary>
    public sealed class ToolResult
    {
        public int ExitCode;
        public string Output = "";
        public bool Ok { get { return ExitCode == 0 && !Output.Contains("Exception"); } }

        public string FirstError
        {
            get
            {
                foreach (var line in Output.Split('\n'))
                    if (line.Contains("Exception") || line.StartsWith("Error"))
                        return line.Trim();
                return null;
            }
        }
    }

    /// <summary>
    /// Drives the FreeMote command line tools (PsbDecompile / PsBuild) for M2 PSB archives.
    ///
    /// Note: PsBuild writes its output into the current directory and renames it, so every
    /// call runs with an explicit working directory and the result is collected from there.
    /// Packing an archive must run from the folder holding the source subfolder, otherwise
    /// the files are not found and the body comes out empty.
    /// </summary>
    public sealed class FreeMoteRunner
    {
        private readonly string _dir;

        public FreeMoteRunner(string toolDir) { _dir = toolDir; }

        public string DecompilerPath { get { return Path.Combine(_dir, "PsbDecompile.exe"); } }
        public string BuilderPath { get { return Path.Combine(_dir, "PsBuild.exe"); } }

        public bool IsUsable
        {
            get { return File.Exists(DecompilerPath) && File.Exists(BuilderPath); }
        }

        /// <summary>Extracts info.psb.m + body.bin into a subfolder named after the archive.</summary>
        public ToolResult ExtractArchive(string infoPsb, string key)
        {
            return Run(DecompilerPath, Path.GetDirectoryName(infoPsb),
                       "info-psb", "-k", key, infoPsb);
        }

        /// <summary>Decompiles every PSB of a folder to JSON, next to each file.</summary>
        public ToolResult DecompileFolder(string folder)
        {
            return Run(DecompilerPath, folder, folder);
        }

        /// <summary>
        /// Compiles a JSON back to a PSB and returns the produced file, which PsBuild names
        /// "&lt;name&gt;.pure.&lt;ext&gt;" in the working directory.
        /// </summary>
        public ToolResult CompileJson(string jsonPath, string workDir, out string produced)
        {
            var before = Directory.GetFiles(workDir);
            var res = Run(BuilderPath, workDir, jsonPath);

            produced = null;
            foreach (var f in Directory.GetFiles(workDir))
            {
                if (Array.IndexOf(before, f) >= 0) continue;
                if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                produced = f;
                break;
            }
            return res;
        }

        /// <summary>Packs a folder back into info.psb.m + body.bin.</summary>
        public ToolResult PackArchive(string infoJson, string key, int keyLength)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(infoJson));
            return Run(BuilderPath, dir,
                       "info-psb", "-k", key, "-l", keyLength.ToString(), Path.GetFileName(infoJson));
        }

        private static ToolResult Run(string exe, string workDir, params string[] args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args)
                psi.Arguments += (a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a) + " ";

            var res = new ToolResult();
            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                res.ExitCode = p.ExitCode;
                res.Output = stdout + stderr;
            }
            return res;
        }
    }
}
