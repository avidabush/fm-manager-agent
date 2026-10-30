using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ManagerAgentSetup
{
    /// <summary>
    /// Minimal zip reader (stored + deflate, no zip64/encryption). Uses only DeflateStream from
    /// System.dll, so Setup depends on nothing beyond what's built into Windows.
    /// </summary>
    internal sealed class Zip : IDisposable
    {
        internal sealed class Entry
        {
            public string Name;            // forward slashes
            public int Method;             // 0 = stored, 8 = deflate
            public long CompressedSize, Size, LocalHeaderOffset;
            public bool IsDirectory => Name.EndsWith("/");
        }

        private readonly FileStream _fs;
        private readonly BinaryReader _r;
        public readonly List<Entry> Entries = new List<Entry>();

        public Zip(string path)
        {
            _fs = File.OpenRead(path);
            _r = new BinaryReader(_fs);
            ReadCentralDirectory();
        }

        private void ReadCentralDirectory()
        {
            // End of central directory: signature 0x06054b50, within the last 64 KB + 22 bytes.
            long len = _fs.Length;
            if (len < 22) throw new InvalidDataException("Not a zip file (too small).");
            int scan = (int)Math.Min(len, 65557);
            _fs.Seek(len - scan, SeekOrigin.Begin);
            var tail = _r.ReadBytes(scan);
            int eocd = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
                if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06) { eocd = i; break; }
            if (eocd < 0) throw new InvalidDataException("Not a zip file (no directory).");

            int count = BitConverter.ToUInt16(tail, eocd + 10);
            uint cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
            if (cdOffset == 0xFFFFFFFF || count == 0xFFFF) throw new InvalidDataException("Zip64 archives aren't supported.");

            _fs.Seek(cdOffset, SeekOrigin.Begin);
            for (int i = 0; i < count; i++)
            {
                if (_r.ReadUInt32() != 0x02014b50) throw new InvalidDataException("Corrupt zip directory.");
                _r.ReadUInt16(); _r.ReadUInt16();                 // version made by / needed
                ushort flags = _r.ReadUInt16();
                ushort method = _r.ReadUInt16();
                _r.ReadUInt32();                                   // time + date
                _r.ReadUInt32();                                   // crc
                uint csize = _r.ReadUInt32(), usize = _r.ReadUInt32();
                ushort nameLen = _r.ReadUInt16(), extraLen = _r.ReadUInt16(), commentLen = _r.ReadUInt16();
                _r.ReadUInt16(); _r.ReadUInt16(); _r.ReadUInt32(); // disk, internal attr, external attr
                uint localOffset = _r.ReadUInt32();
                var nameBytes = _r.ReadBytes(nameLen);
                _fs.Seek(extraLen + commentLen, SeekOrigin.Current);
                var enc = (flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.GetEncoding(437);
                Entries.Add(new Entry
                {
                    Name = enc.GetString(nameBytes).Replace('\\', '/'),
                    Method = method,
                    CompressedSize = csize,
                    Size = usize,
                    LocalHeaderOffset = localOffset,
                });
            }
        }

        public void ExtractTo(Entry e, string dest)
        {
            if (e.Method != 0 && e.Method != 8) throw new InvalidDataException("Unsupported compression in " + e.Name);
            _fs.Seek(e.LocalHeaderOffset, SeekOrigin.Begin);
            if (_r.ReadUInt32() != 0x04034b50) throw new InvalidDataException("Corrupt zip entry " + e.Name);
            _fs.Seek(22, SeekOrigin.Current);
            ushort nameLen = _r.ReadUInt16(), extraLen = _r.ReadUInt16();
            _fs.Seek(nameLen + extraLen, SeekOrigin.Current);

            var tmp = dest + ".tmp";
            using (var outFile = File.Create(tmp))
            {
                var limited = new SubStream(_fs, e.CompressedSize);
                if (e.Method == 0) limited.CopyTo(outFile);
                else using (var inflate = new DeflateStream(limited, CompressionMode.Decompress, true)) inflate.CopyTo(outFile);
            }
            if (new FileInfo(tmp).Length != e.Size)
            {
                File.Delete(tmp);
                throw new InvalidDataException("Damaged file in zip: " + e.Name);
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
        }

        /// <summary>Extracts everything into <paramref name="folder"/> (with a path-traversal guard).</summary>
        public static void ExtractAll(string zipPath, string folder)
        {
            using (var z = new Zip(zipPath))
            {
                var root = Path.GetFullPath(folder);
                foreach (var e in z.Entries)
                {
                    var dest = Path.GetFullPath(Path.Combine(root, e.Name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.IsDirectory) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    z.ExtractTo(e, dest);
                }
            }
        }

        public void Dispose()
        {
            _r.Dispose();
            _fs.Dispose();
        }

        /// <summary>Read-only window over the archive stream.</summary>
        private sealed class SubStream : Stream
        {
            private readonly Stream _s;
            private long _left;
            public SubStream(Stream s, long length) { _s = s; _left = length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_left <= 0) return 0;
                int n = _s.Read(buffer, offset, (int)Math.Min(count, _left));
                _left -= n;
                return n;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
