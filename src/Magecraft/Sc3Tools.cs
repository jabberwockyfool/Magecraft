using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Magecraft
{
    /// <summary>Raw outcome of one sc3tools.exe invocation.</summary>
    public sealed class Sc3Result
    {
        public int ExitCode;
        public string Output = "";
        public bool Ok { get { return ExitCode == 0 && !Output.Contains("Error:"); } }

        /// <summary>The character the charset rejected, or '\0' when the error is something else.</summary>
        public char RejectedChar
        {
            get
            {
                var m = Regex.Match(Output, @"character '(.)' is not present in the charset");
                return m.Success ? m.Groups[1].Value[0] : '\0';
            }
        }

        public string FirstError
        {
            get
            {
                var m = Regex.Match(Output, @"Error: (.+)");
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }
        }
    }

    /// <summary>Wrapper around sc3tools.exe (Committee of Zero).</summary>
    public sealed class Sc3Tools
    {
        private static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

        private readonly string _exe;
        private readonly string _game;

        public Sc3Tools(string exePath, string game = "cc")
        {
            _exe = exePath;
            _game = game;
        }

        /// <summary>Rewrites the text of <paramref name="scx"/> in place from <paramref name="txt"/>.</summary>
        public Sc3Result ReplaceText(string scx, string txt, string workingDir)
        {
            return Run(workingDir, "replace-text", scx, txt, _game);
        }

        /// <summary>Extracts the text of <paramref name="scx"/> into &lt;workingDir&gt;\txt\&lt;name&gt;.txt.</summary>
        public Sc3Result ExtractText(string scx, string workingDir)
        {
            return Run(workingDir, "extract-text", scx, _game);
        }

        private Sc3Result Run(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo(_exe)
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.Arguments += Quote(a) + " ";

            var res = new Sc3Result();
            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                res.ExitCode = p.ExitCode;
                res.Output = Ansi.Replace(stdout + stderr, "");
            }
            return res;
        }

        private static string Quote(string s)
        {
            return s.IndexOf(' ') >= 0 ? "\"" + s + "\"" : s;
        }
    }
}
