using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Magecraft
{
    /// <summary>Resultat brut d'un appel a sc3tools.exe.</summary>
    public sealed class Sc3Result
    {
        public int ExitCode;
        public string Output = "";
        public bool Ok { get { return ExitCode == 0 && !Output.Contains("Error:"); } }

        /// <summary>Caractere refuse par le charset, ou '\0' si l'erreur est d'une autre nature.</summary>
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

    /// <summary>Enveloppe autour de sc3tools.exe (Committee of Zero).</summary>
    public sealed class Sc3Tools
    {
        private static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
        public const string Game = "cc";

        private readonly string _exe;
        public Sc3Tools(string exePath) { _exe = exePath; }

        /// <summary>Reecrit le texte de <paramref name="scx"/> (modifie sur place) a partir de <paramref name="txt"/>.</summary>
        public Sc3Result ReplaceText(string scx, string txt, string workingDir)
        {
            return Run(workingDir, "replace-text", scx, txt, Game);
        }

        /// <summary>Extrait le texte de <paramref name="scx"/> vers &lt;workingDir&gt;\txt\&lt;nom&gt;.txt.</summary>
        public Sc3Result ExtractText(string scx, string workingDir)
        {
            return Run(workingDir, "extract-text", scx, Game);
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
