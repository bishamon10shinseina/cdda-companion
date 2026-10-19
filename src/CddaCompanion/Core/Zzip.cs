using System.Buffers.Binary;
using System.Text;
using ZstdSharp;

namespace InlineMethod
{
    /// <summary>Stand-in for the Fody weaver attribute used by the vendored ZstdSharp source.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class InlineAttribute : Attribute { }
}

namespace CddaCompanion.Core
{
    public sealed class ZzipEntry
    {
        public string Name;
        public int Offset, Length;          // whole entry (name + checksum frames + zstd frame)
        public int FrameOffset, FrameLength; // compressed zstd frame only
        public ulong Checksum;
    }

    public sealed class ZzipException : Exception
    {
        public ZzipException(string msg) : base(msg) { }
    }

    /// <summary>
    /// Reader for CDDA's "zstandard zip" archives (src/zzip.h). Read-only, works on an in-memory copy,
    /// so the game can keep writing to the real file while we look at our copy.
    /// </summary>
    public sealed class ZzipArchive
    {
        public const ulong Seed = 0x1337C0DE;
        const uint SkippableBase = 0x184D2A50, ZstdMagic = 0xFD2FB528;
        const int NameMagic = 0, ChecksumMagic = 1, FooterMagic = 15;

        public readonly byte[] Data;
        public readonly List<ZzipEntry> Entries = new();
        public readonly Dictionary<string, ZzipEntry> ByName = new(StringComparer.Ordinal);

        ZzipArchive(byte[] data) { Data = data; }

        public static ZzipArchive Read(string path) => Parse(File.ReadAllBytes(path));

        /// <summary>Parses the footer; throws ZzipException if the archive is torn or corrupt.</summary>
        public static ZzipArchive Parse(byte[] data)
        {
            var z = new ZzipArchive(data);
            if (data.Length < 24) throw new ZzipException("file too short");
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            if (magic != SkippableBase + FooterMagic || size != 16) throw new ZzipException("missing footer header");
            ulong footerLen = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8));
            ulong footerSum = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16));
            if (footerLen == 0 || footerLen > (ulong)data.Length - 24) throw new ZzipException("bad footer length");
            var footer = data.AsSpan(data.Length - (int)footerLen);
            if (XxHash64.Hash(footer, Seed) != footerSum) throw new ZzipException("footer checksum mismatch (file changed while copying?)");

            var root = FlexRef.Root(footer.ToArray());
            if (!root.TryGet("entries", out var entries)) throw new ZzipException("footer has no entries");
            var names = entries.Keys;
            for (int i = 0; i < names.Length; i++)
            {
                var e = entries[i];
                if (!e.TryGet("offset", out var off) || !e.TryGet("len", out var len)) throw new ZzipException("entry without offset/len");
                var entry = new ZzipEntry { Name = names[i], Offset = checked((int)off.AsLong), Length = checked((int)len.AsLong) };
                z.ParseEntryFrames(entry);
                z.Entries.Add(entry);
                z.ByName[entry.Name] = entry;
            }
            return z;
        }

        void ParseEntryFrames(ZzipEntry e)
        {
            int pos = e.Offset, end = e.Offset + e.Length;
            if (pos < 0 || end > Data.Length || end < pos) throw new ZzipException($"entry {e.Name} out of range");
            while (pos + 8 <= end)
            {
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(pos));
                if ((magic & 0xFFFFFFF0) == SkippableBase)
                {
                    int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(pos + 4));
                    if ((magic & 0xF) == ChecksumMagic && size >= 8)
                        e.Checksum = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(pos + 8));
                    pos += 8 + size;
                }
                else if (magic == ZstdMagic)
                {
                    e.FrameOffset = pos; e.FrameLength = end - pos;
                    return;
                }
                else throw new ZzipException($"entry {e.Name}: unexpected bytes");
            }
            throw new ZzipException($"entry {e.Name}: no compressed frame");
        }

        public bool Verify(ZzipEntry e) =>
            XxHash64.Hash(Data.AsSpan(e.FrameOffset, e.FrameLength), Seed) == e.Checksum;

        public byte[] Extract(ZzipEntry e, Decompressor d)
        {
            var src = Data.AsSpan(e.FrameOffset, e.FrameLength);
            ulong size = Decompressor.GetDecompressedSize(src);
            if (size > int.MaxValue) throw new ZzipException($"entry {e.Name} too large");
            var dst = new byte[size];
            int n = d.Unwrap(src, dst);
            if (n != dst.Length) Array.Resize(ref dst, n);
            return dst;
        }

        /// <summary>Reads an archive file, retrying a few times if the game is in the middle of writing it.</summary>
        public static ZzipArchive ReadStable(string path, int attempts = 5)
        {
            for (int i = 1; ; i++)
            {
                try { return Read(path); }
                catch (Exception ex) when ((ex is ZzipException || ex is IOException) && i < attempts)
                {
                    Thread.Sleep(300 * i);
                }
            }
        }
    }

    /// <summary>Decompressors are not thread-safe and loading a dictionary costs time: keep one per thread per dictionary.</summary>
    public static class Zstd
    {
        static readonly Dictionary<string, byte[]> Dicts = new();
        [ThreadStatic] static Dictionary<string, Decompressor> _local;

        public static Decompressor For(string dictPath)
        {
            _local ??= new Dictionary<string, Decompressor>();
            string key = dictPath ?? "";
            if (_local.TryGetValue(key, out var d)) return d;
            d = new Decompressor();
            if (dictPath != null)
            {
                byte[] dict;
                lock (Dicts)
                {
                    if (!Dicts.TryGetValue(dictPath, out dict)) Dicts[dictPath] = dict = File.ReadAllBytes(dictPath);
                }
                d.LoadDictionary(dict);
            }
            _local[key] = d;
            return d;
        }

        /// <summary>Opens a zzip and returns the decompressed content of one entry.</summary>
        public static byte[] ReadEntry(string zzipPath, string entryName, string dictPath)
        {
            var z = ZzipArchive.ReadStable(zzipPath);
            if (!z.ByName.TryGetValue(entryName, out var e))
            {
                if (z.Entries.Count == 1) e = z.Entries[0];
                else throw new ZzipException($"{entryName} not found in {Path.GetFileName(zzipPath)}");
            }
            if (!z.Verify(e)) throw new ZzipException($"{entryName}: checksum mismatch");
            return z.Extract(e, For(dictPath));
        }
    }

    public static class Text
    {
        /// <summary>CDDA save files may start with a "# version N" line before the JSON.</summary>
        public static ReadOnlyMemory<byte> JsonPart(byte[] data)
        {
            int start = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) start = 3;
            if (start < data.Length && data[start] == (byte)'#')
            {
                int nl = Array.IndexOf(data, (byte)'\n', start);
                start = nl < 0 ? data.Length : nl + 1;
            }
            return new ReadOnlyMemory<byte>(data, start, data.Length - start);
        }

        public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
    }
}
