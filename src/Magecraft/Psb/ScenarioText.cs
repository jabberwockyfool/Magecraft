using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Magecraft
{
    /// <summary>
    /// Reads and writes the translatable lines of an M2 scenario (.ks.scn.m decompiled to
    /// JSON).
    ///
    /// A scene holds a "texts" array; each entry is
    ///   [speaker, [ [null, japanese, len], [name, lang1, len], ... ], voices, n, data]
    /// so track 0 is always the original Japanese and track i+1 matches languages[i].
    /// </summary>
    public static class ScenarioText
    {
        private static readonly Regex Tag = new Regex(@"%[^;]*;", RegexOptions.Compiled);
        private static readonly Regex Ruby = new Regex(@"\[[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex Escape = new Regex(@"\\(.)", RegexOptions.Compiled);

        /// <summary>
        /// Number of characters the game counts for a line: markup does not show, a line
        /// break counts for nothing, and any other escape counts as the single character
        /// it stands for. Verified against 98366 of the 98367 lines shipped in the game.
        /// </summary>
        public static int ShownLength(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            string s = Ruby.Replace(Tag.Replace(text, ""), "");
            s = s.Replace("\\n", "");
            s = Escape.Replace(s, "$1");
            return s.Length;
        }

        /// <summary>Languages of a scenario, track 0 (Japanese) excluded.</summary>
        public static List<string> Languages(JObject scenario)
        {
            var langs = scenario["languages"] as JArray;
            return langs == null ? new List<string>() : langs.Select(l => (string)l).ToList();
        }

        /// <summary>Track index for a language code, or -1. "jp" is track 0.</summary>
        public static int TrackOf(JObject scenario, string language)
        {
            if (string.Equals(language, "jp", StringComparison.OrdinalIgnoreCase)) return 0;
            int i = Languages(scenario).FindIndex(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? -1 : i + 1;
        }

        /// <summary>Every text entry of the file, scene by scene, in a stable order.</summary>
        private static IEnumerable<JArray> Entries(JObject scenario)
        {
            var scenes = scenario["scenes"] as JArray;
            if (scenes == null) yield break;
            foreach (var scene in scenes.OfType<JObject>())
            {
                var texts = scene["texts"] as JArray;
                if (texts == null) continue;
                foreach (var entry in texts.OfType<JArray>()) yield return entry;
            }
        }

        /// <summary>Reads one line per text entry; an absent track gives an empty line.</summary>
        public static List<string> Read(JObject scenario, int track)
        {
            var lines = new List<string>();
            foreach (var entry in Entries(scenario))
            {
                var tracks = entry.Count > 1 ? entry[1] as JArray : null;
                if (tracks == null || track >= tracks.Count) { lines.Add(""); continue; }
                var t = tracks[track] as JArray;
                string text = (t != null && t.Count > 1) ? (string)t[1] : null;
                lines.Add(text ?? "");
            }
            return lines;
        }

        public static int Count(JObject scenario)
        {
            return Entries(scenario).Count();
        }

        /// <summary>
        /// Writes the lines back into a track and refreshes each character count.
        /// Returns how many lines actually changed.
        /// </summary>
        public static int Write(JObject scenario, int track, IList<string> lines, out string error)
        {
            error = null;
            var entries = Entries(scenario).ToList();
            if (entries.Count != lines.Count)
            {
                error = string.Format("{0} lines for {1} text entries", lines.Count, entries.Count);
                return 0;
            }

            int changed = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                var tracks = entries[i].Count > 1 ? entries[i][1] as JArray : null;
                if (tracks == null || track >= tracks.Count) continue;
                var t = tracks[track] as JArray;
                if (t == null || t.Count < 3) continue;

                string current = (string)t[1];
                string wanted = lines[i];
                if (current == wanted) continue;

                t[1] = wanted;
                t[2] = ShownLength(wanted);
                changed++;
            }
            return changed;
        }
    }
}
