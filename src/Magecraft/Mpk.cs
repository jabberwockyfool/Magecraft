using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Magecraft
{
    /// <summary>One entry of an MPK archive's table of contents.</summary>
    public sealed class MpkEntry
    {
        public uint Id;
        public string Name;
        public long Offset;
        public long Size;
    }

    /// <summary>
    /// One entry to write, together with where its payload comes from: either a file on
    /// disk, or the archive it was read from when nothing replaces it.
    /// </summary>
    public sealed class MpkBuildEntry
    {
        public uint Id;
        public string Name;

        public string SourceFile;      // payload taken from disk
        public string SourceArchive;   // otherwise copied straight out of this archive
        public long SourceOffset;
        public long SourceSize;

        public bool FromDisk { get { return SourceFile != null; } }

        public static MpkBuildEntry FromFile(uint id, string name, string file)
        {
            return new MpkBuildEntry { Id = id, Name = name, SourceFile = file };
        }

        public static MpkBuildEntry FromArchive(MpkEntry entry, string archive)
        {
            return new MpkBuildEntry
            {
                Id = entry.Id,
                Name = entry.Name,
                SourceArchive = archive,
                SourceOffset = entry.Offset,
                SourceSize = entry.Size,
            };
        }
    }

    /// <summary>
    /// Reads and writes MAGES. MPK archives.
    /// Layout (same as mpkpack.py): 0x40 header, then one 0x100-byte table entry per file
    /// starting at 0x40, payloads aligned on 2 KB, no compression.
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
                    throw new InvalidDataException("This file is not an MPK archive: " + path);
                br.ReadUInt32();                       // version
                ulong count = br.ReadUInt64();

                for (ulong i = 0; i < count; i++)
                {
                    fs.Position = TocStart + (long)i * EntrySize;
                    br.ReadUInt32();                   // compression, always 0 here
                    var e = new MpkEntry { Id = br.ReadUInt32() };
                    e.Offset = (long)br.ReadUInt64();
                    br.ReadUInt64();                   // compressed size
                    e.Size = (long)br.ReadUInt64();
                    var name = br.ReadBytes(NameMax + 1);
                    int len = Array.IndexOf(name, (byte)0);
                    e.Name = Encoding.ASCII.GetString(name, 0, len < 0 ? name.Length : len);
                    list.Add(e);
                }
            }
            return list;
        }

        /// <summary>Extracts every entry into <paramref name="outDir"/>.</summary>
        public static void ExtractAll(string path, string outDir, Action<int, int> progress = null)
        {
            Directory.CreateDirectory(outDir);
            var toc = ReadToc(path);
            using (var fs = File.OpenRead(path))
            {
                for (int i = 0; i < toc.Count; i++)
                {
                    fs.Position = toc[i].Offset;
                    using (var outFile = File.Create(Path.Combine(outDir, toc[i].Name)))
                        Copy(fs, outFile, toc[i].Size);
                    if (progress != null) progress(i + 1, toc.Count);
                }
            }
        }

        /// <summary>
        /// Writes an archive from <paramref name="entries"/>, keeping their order, ids and
        /// names exactly as given. The game addresses files by id, and ids are not always a
        /// plain 0..n-1 run -- c0data.mpk skips 136 of them -- so a rebuilt table generally
        /// has to be copied from the original archive rather than derived from a folder.
        /// </summary>
        public static void Write(string outPath, IList<MpkBuildEntry> entries, Action<int, int> progress = null)
        {
            // One handle per source archive, reused across entries.
            var archives = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(Encoding.ASCII.GetBytes("MPK\0"));
                    bw.Write(new byte[] { 0x00, 0x00, 0x02, 0x00 });   // version 2.0
                    bw.Write((ulong)entries.Count);
                    bw.Write(new byte[TocStart - 16]);

                    long pos = RoundUp(TocStart + (long)entries.Count * EntrySize);
                    var sizes = new long[entries.Count];
                    var offsets = new long[entries.Count];

                    for (int i = 0; i < entries.Count; i++)
                    {
                        var e = entries[i];
                        fs.Position = pos;
                        offsets[i] = pos;

                        if (e.FromDisk)
                        {
                            if (File.Exists(e.SourceFile))
                            {
                                using (var input = File.OpenRead(e.SourceFile))
                                {
                                    input.CopyTo(fs);
                                    sizes[i] = input.Length;
                                }
                            }
                        }
                        else if (e.SourceArchive != null)
                        {
                            FileStream src;
                            if (!archives.TryGetValue(e.SourceArchive, out src))
                            {
                                src = File.OpenRead(e.SourceArchive);
                                archives[e.SourceArchive] = src;
                            }
                            src.Position = e.SourceOffset;
                            Copy(src, fs, e.SourceSize);
                            sizes[i] = e.SourceSize;
                        }

                        pos = RoundUp(pos + sizes[i]);
                        if (progress != null) progress(i + 1, entries.Count);
                    }

                    // The last block must physically exist, otherwise the game reads past the end.
                    fs.SetLength(pos);

                    for (int i = 0; i < entries.Count; i++)
                    {
                        fs.Position = TocStart + (long)i * EntrySize;
                        bw.Write((uint)0);              // compression
                        bw.Write(entries[i].Id);
                        bw.Write((ulong)offsets[i]);
                        bw.Write((ulong)sizes[i]);      // compressed size
                        bw.Write((ulong)sizes[i]);      // real size
                        var name = Encoding.ASCII.GetBytes(entries[i].Name);
                        if (name.Length > NameMax) Array.Resize(ref name, NameMax);
                        bw.Write(name);
                        bw.Write((byte)0);
                    }
                }
            }
            finally
            {
                foreach (var s in archives.Values) s.Dispose();
            }
        }

        private static void Copy(Stream from, Stream to, long count)
        {
            var buffer = new byte[81920];
            while (count > 0)
            {
                int want = (int)Math.Min(buffer.Length, count);
                int read = from.Read(buffer, 0, want);
                if (read <= 0) break;
                to.Write(buffer, 0, read);
                count -= read;
            }
        }

        private static long RoundUp(long v)
        {
            return ((v + Align - 1) / Align) * Align;
        }
    }
}
