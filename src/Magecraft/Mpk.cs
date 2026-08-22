using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Magecraft
{
    /// <summary>Une entree du sommaire d'une archive MPK.</summary>
    public sealed class MpkEntry
    {
        public uint Id;
        public string Name;
        public long Offset;
        public long Size;
    }

    /// <summary>
    /// Lecture / ecriture des archives MPK de MAGES.
    /// Format (identique a mpkpack.py) : entete 0x40, sommaire de 0x100 octets par entree
    /// a partir de 0x40, donnees alignees sur 2 Ko, aucune compression.
    /// </summary>
    public static class Mpk
    {
        private const int TocStart = 0x40;
        private const int EntrySize = 0x100;
        private const int NameMax = 0xE0 - 1;
        private const int Align = 2048;

        public static List<MpkEntry> ReadToc(string path)
        {
            var list = new List<MpkEntry>();
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                if (new string(br.ReadChars(3)) != "MPK" || br.ReadByte() != 0)
                    throw new InvalidDataException("Ce fichier n'est pas une archive MPK : " + path);
                br.ReadUInt32();                       // version
                ulong count = br.ReadUInt64();

                for (ulong i = 0; i < count; i++)
                {
                    fs.Position = TocStart + (long)i * EntrySize;
                    br.ReadUInt32();                   // compression, toujours 0 ici
                    var e = new MpkEntry { Id = br.ReadUInt32() };
                    e.Offset = (long)br.ReadUInt64();
                    br.ReadUInt64();                   // taille compressee
                    e.Size = (long)br.ReadUInt64();
                    var name = br.ReadBytes(NameMax + 1);
                    int len = Array.IndexOf(name, (byte)0);
                    e.Name = Encoding.ASCII.GetString(name, 0, len < 0 ? name.Length : len);
                    list.Add(e);
                }
            }
            return list;
        }

        /// <summary>Extrait toutes les entrees dans <paramref name="outDir"/>.</summary>
        public static void ExtractAll(string path, string outDir, Action<string> log = null)
        {
            Directory.CreateDirectory(outDir);
            var toc = ReadToc(path);
            using (var fs = File.OpenRead(path))
            {
                foreach (var e in toc)
                {
                    fs.Position = e.Offset;
                    var buf = new byte[e.Size];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    File.WriteAllBytes(Path.Combine(outDir, e.Name), buf);
                    if (log != null) log(e.Name);
                }
            }
        }

        /// <summary>
        /// Reecrit une archive en conservant l'ordre, les identifiants et les noms de
        /// <paramref name="toc"/>. <paramref name="fileFor"/> donne le fichier a inserer
        /// pour un nom d'entree ; s'il renvoie null l'entree est ecrite vide.
        /// </summary>
        public static void Write(string outPath, IList<MpkEntry> toc, Func<string, string> fileFor, Action<int, int> progress = null)
        {
            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("MPK\0"));
                bw.Write(new byte[] { 0x00, 0x00, 0x02, 0x00 });   // version 2.0
                bw.Write((ulong)toc.Count);
                bw.Write(new byte[TocStart - 16]);

                long pos = RoundUp(TocStart + (long)toc.Count * EntrySize);
                var sizes = new long[toc.Count];
                var offsets = new long[toc.Count];

                for (int i = 0; i < toc.Count; i++)
                {
                    string src = fileFor(toc[i].Name);
                    fs.Position = pos;
                    offsets[i] = pos;
                    if (src != null && File.Exists(src))
                    {
                        using (var input = File.OpenRead(src))
                        {
                            input.CopyTo(fs);
                            sizes[i] = input.Length;
                        }
                    }
                    pos = RoundUp(pos + sizes[i]);
                    if (progress != null) progress(i + 1, toc.Count);
                }

                // Le dernier bloc doit exister physiquement, sinon le jeu lit hors fichier.
                fs.SetLength(pos);

                for (int i = 0; i < toc.Count; i++)
                {
                    fs.Position = TocStart + (long)i * EntrySize;
                    bw.Write((uint)0);              // compression
                    bw.Write(toc[i].Id);
                    bw.Write((ulong)offsets[i]);
                    bw.Write((ulong)sizes[i]);      // taille compressee
                    bw.Write((ulong)sizes[i]);      // taille reelle
                    var name = Encoding.ASCII.GetBytes(toc[i].Name);
                    if (name.Length > NameMax) Array.Resize(ref name, NameMax);
                    bw.Write(name);
                    bw.Write((byte)0);
                }
            }
        }

        private static long RoundUp(long v)
        {
            return ((v + Align - 1) / Align) * Align;
        }
    }
}
