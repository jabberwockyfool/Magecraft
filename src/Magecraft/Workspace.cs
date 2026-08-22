using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Magecraft
{
    /// <summary>What kind of archive is being rebuilt.</summary>
    public enum BuildMode
    {
        /// <summary>Scripts: translated text is injected into .scx files with sc3tools.</summary>
        Scripts,
        /// <summary>Plain files: a folder of assets (CG, textures) replaces archive entries as-is.</summary>
        Files,
        /// <summary>M2 scenario: text is extracted to .txt files and injected back into PSB scripts.</summary>
        Scenario,
    }

    /// <summary>
    /// What the user points Magecraft at. Everything else is derived from these paths.
    /// </summary>
    public sealed class Workspace
    {
        public BuildMode Mode = BuildMode.Scripts;

        public string SourceDir = "";     // .scx scripts, or the asset folder in Files mode
        public string TxtDir = "";        // translated text (Scripts mode only)
        public string TemplateMpk = "";   // archive the table of contents is copied from
        public string OutMpk = "";
        public string Sc3Path = "";

        // Scenario mode only
        public string FreeMoteDir = "";   // folder holding PsbDecompile.exe and PsBuild.exe
        public string MdfKey = "";        // archive key, found in the game executable
        public int MdfKeyLength = 131;
        public string Language = "en";    // track being translated

        /// <summary>Scripts in name order, which is how script archives number their entries.</summary>
        public List<string> Scripts()
        {
            return Directory.GetFiles(SourceDir, "*.scx")
                            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                            .ToList();
        }

        /// <summary>Every file of the asset folder, indexed by name.</summary>
        public Dictionary<string, string> Assets()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(SourceDir))
                map[Path.GetFileName(f)] = f;
            return map;
        }

        /// <summary>
        /// Text file for a script, or null when the script has no translation yet.
        /// Both cc_01_01.scx.txt and cc_01_01.txt are accepted.
        /// </summary>
        public string TextFor(string scxName)
        {
            string withScx = Path.Combine(TxtDir, scxName + ".txt");
            if (File.Exists(withScx)) return withScx;
            string bare = Path.Combine(TxtDir, Path.GetFileNameWithoutExtension(scxName) + ".txt");
            return File.Exists(bare) ? bare : null;
        }

        /// <summary>Blocking problems, empty when the workspace is usable.</summary>
        public string[] Problems(bool needOutput)
        {
            var list = new List<string>();

            if (Mode == BuildMode.Scenario)
            {
                if (SourceDir.Trim().Length == 0) list.Add("Choose a workspace folder.");
                if (!File.Exists(TemplateMpk)) list.Add("Original archive (_info.psb.m) not found.");
                if (MdfKey.Trim().Length == 0) list.Add("The archive key is required.");
                if (!File.Exists(Path.Combine(FreeMoteDir, "PsbDecompile.exe")) ||
                    !File.Exists(Path.Combine(FreeMoteDir, "PsBuild.exe")))
                    list.Add("PsbDecompile.exe and PsBuild.exe not found in the FreeMote folder.");
                if (needOutput && OutMpk.Trim().Length == 0) list.Add("Choose the output folder.");
                return list.Distinct().ToArray();
            }

            if (!Directory.Exists(SourceDir))
            {
                list.Add(Mode == BuildMode.Scripts ? "Script folder not found." : "File folder not found.");
            }
            else if (Mode == BuildMode.Scripts && Directory.GetFiles(SourceDir, "*.scx").Length == 0)
            {
                list.Add("No .scx files in the script folder.");
            }
            else if (Mode == BuildMode.Files && Directory.GetFiles(SourceDir).Length == 0)
            {
                list.Add("The file folder is empty.");
            }

            if (Mode == BuildMode.Scripts)
            {
                if (!Directory.Exists(TxtDir)) list.Add("Text folder not found.");
                if (!File.Exists(Sc3Path)) list.Add("sc3tools.exe not found.");
            }
            else if (!File.Exists(TemplateMpk))
            {
                // Asset archives have gaps in their ids and an order of their own, so the
                // table of contents cannot be guessed from the folder.
                list.Add("Original archive required: its table of contents is copied over.");
            }

            if (TemplateMpk.Trim().Length > 0 && !File.Exists(TemplateMpk))
                list.Add("Original archive not found.");

            if (needOutput && OutMpk.Trim().Length == 0) list.Add("Choose the .mpk archive to write.");
            return list.Distinct().ToArray();
        }

        /// <summary>Looks for sc3tools.exe next to the program, then in the usual repository layout.</summary>
        public static string GuessSc3Tools(string exeDir)
        {
            string dir = exeDir;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                foreach (var relative in new[]
                {
                    "sc3tools.exe",
                    @"tools\sc3tools.exe",
                    @"text-replace-extract\sc3tools.exe",
                    @"tool\text-replace-extract\sc3tools.exe",
                })
                {
                    string candidate = Path.Combine(dir, relative);
                    if (File.Exists(candidate)) return candidate;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return "";
        }
    }
}
