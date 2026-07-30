using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Adbc.Drivers.Build.Archives
{
    internal enum TarEntryType
    {
        File,
        Directory,
        HardLink,
        SymbolicLink,
        CharacterDevice,
        BlockDevice,
        Fifo,
        Contiguous,
        Unknown,
    }

    internal sealed class TarEntry
    {
        public TarEntry(string name, TarEntryType type, long length, string? linkTarget, int mode, char typeFlag)
        {
            Name = name;
            Type = type;
            Length = length;
            LinkTarget = linkTarget;
            Mode = mode;
            TypeFlag = typeFlag;
        }

        /// <summary>Entry name exactly as recorded in the archive.</summary>
        public string Name { get; }

        public TarEntryType Type { get; }

        public long Length { get; }

        public string? LinkTarget { get; }

        public int Mode { get; }

        /// <summary>Raw tar type flag, for diagnostics on unsupported entries.</summary>
        public char TypeFlag { get; }
    }

    /// <summary>
    /// Forward-only reader for ustar/GNU tar streams.
    /// </summary>
    /// <remarks>
    /// <c>System.Formats.Tar</c> only exists on .NET 7 and later, and this assembly
    /// must also load into MSBuild.exe on .NET Framework, so the format is read
    /// directly. GNU long names and the PAX <c>path</c>/<c>linkpath</c> attributes are
    /// honoured; everything else about an extended header is ignored.
    /// </remarks>
    internal sealed class TarReader : IDisposable
    {
        private const int BlockSize = 512;

        private readonly Stream _stream;
        private readonly bool _ownsStream;
        private readonly byte[] _header = new byte[BlockSize];

        private long _remaining;
        private long _entryLength;
        private bool _finished;

        public TarReader(Stream stream, bool ownsStream = false)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _ownsStream = ownsStream;
        }

        /// <summary>
        /// Advances to the next entry. The entry's content must be read (or ignored)
        /// through <see cref="OpenEntry"/> before the next call.
        /// </summary>
        public TarEntry? MoveNext()
        {
            if (_finished)
            {
                return null;
            }

            SkipCurrentEntryRemainder();

            string? longName = null;
            string? longLinkName = null;
            string? paxPath = null;
            string? paxLinkPath = null;

            while (true)
            {
                if (!TryReadBlock())
                {
                    _finished = true;
                    return null;
                }

                if (IsAllZero(_header))
                {
                    // A single zero block ends the archive for our purposes; the second
                    // one is optional padding that some writers omit.
                    _finished = true;
                    return null;
                }

                ValidateChecksum();

                char typeFlag = (char)_header[156];
                long size = ParseNumeric(_header, 124, 12);
                if (size < 0)
                {
                    throw new InvalidDataException("The tar archive declares a negative entry size.");
                }

                if (typeFlag == 'L' || typeFlag == 'K')
                {
                    string value = ReadStringPayload(size);
                    if (typeFlag == 'L')
                    {
                        longName = value;
                    }
                    else
                    {
                        longLinkName = value;
                    }

                    continue;
                }

                if (typeFlag == 'x' || typeFlag == 'X' || typeFlag == 'g')
                {
                    string payload = ReadStringPayload(size);
                    ParsePaxAttributes(payload, ref paxPath, ref paxLinkPath);
                    continue;
                }

                string name = paxPath
                    ?? longName
                    ?? CombineUstarName(
                        ReadString(_header, 345, 155),
                        ReadString(_header, 0, 100));

                string? linkTarget = paxLinkPath ?? longLinkName ?? ReadString(_header, 157, 100);
                if (string.IsNullOrEmpty(linkTarget))
                {
                    linkTarget = null;
                }

                TarEntryType type = MapType(typeFlag, name);
                if (type == TarEntryType.Directory)
                {
                    // Directory entries carry no payload even if a writer records a size.
                    size = 0;
                }

                long mode = ParseNumeric(_header, 100, 8);
                _remaining = size;
                _entryLength = size;
                return new TarEntry(name, type, size, linkTarget, (int)Math.Max(0, mode), typeFlag);
            }
        }

        /// <summary>
        /// Returns a stream over the current entry's content. The stream must be fully
        /// read or disposed before <see cref="MoveNext"/> is called again.
        /// </summary>
        public Stream OpenEntry() => new EntryStream(this);

        public void Dispose()
        {
            if (_ownsStream)
            {
                _stream.Dispose();
            }
        }

        private static TarEntryType MapType(char typeFlag, string name)
        {
            switch (typeFlag)
            {
                case '0':
                case '\0':
                    // Historic convention: a trailing slash on an old-style entry means
                    // a directory.
                    return name.EndsWith("/", StringComparison.Ordinal)
                        ? TarEntryType.Directory
                        : TarEntryType.File;
                case '1': return TarEntryType.HardLink;
                case '2': return TarEntryType.SymbolicLink;
                case '3': return TarEntryType.CharacterDevice;
                case '4': return TarEntryType.BlockDevice;
                case '5': return TarEntryType.Directory;
                case '6': return TarEntryType.Fifo;
                case '7': return TarEntryType.Contiguous;
                default: return TarEntryType.Unknown;
            }
        }

        private static string CombineUstarName(string prefix, string name) =>
            prefix.Length == 0 ? name : prefix + "/" + name;

        private static bool IsAllZero(byte[] block)
        {
            foreach (byte b in block)
            {
                if (b != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static string ReadString(byte[] buffer, int offset, int length)
        {
            int end = offset;
            int limit = offset + length;
            while (end < limit && buffer[end] != 0)
            {
                end++;
            }

            return Encoding.UTF8.GetString(buffer, offset, end - offset);
        }

        /// <summary>
        /// Parses an octal tar numeric field, honouring the GNU base-256 extension
        /// signalled by the high bit of the first byte.
        /// </summary>
        private static long ParseNumeric(byte[] buffer, int offset, int length)
        {
            if ((buffer[offset] & 0x80) != 0)
            {
                long value = buffer[offset] & 0x7f;
                for (int i = 1; i < length; i++)
                {
                    value = (value << 8) | buffer[offset + i];
                }

                return value;
            }

            long result = 0;
            bool any = false;
            for (int i = 0; i < length; i++)
            {
                byte b = buffer[offset + i];
                if (b == 0 || b == (byte)' ')
                {
                    if (any)
                    {
                        break;
                    }

                    continue;
                }

                if (b < (byte)'0' || b > (byte)'7')
                {
                    throw new InvalidDataException("The tar archive contains a malformed numeric field.");
                }

                result = (result * 8) + (b - '0');
                any = true;
            }

            return any ? result : 0;
        }

        private static void ParsePaxAttributes(string payload, ref string? path, ref string? linkPath)
        {
            int index = 0;
            while (index < payload.Length)
            {
                int space = payload.IndexOf(' ', index);
                if (space < 0)
                {
                    break;
                }

                if (!int.TryParse(
                        payload.Substring(index, space - index),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int recordLength)
                    || recordLength <= 0
                    || index + recordLength > payload.Length)
                {
                    break;
                }

                string record = payload.Substring(space + 1, (index + recordLength) - (space + 1)).TrimEnd('\n');
                int equals = record.IndexOf('=');
                if (equals > 0)
                {
                    string key = record.Substring(0, equals);
                    string value = record.Substring(equals + 1);
                    if (string.Equals(key, "path", StringComparison.Ordinal))
                    {
                        path = value;
                    }
                    else if (string.Equals(key, "linkpath", StringComparison.Ordinal))
                    {
                        linkPath = value;
                    }
                }

                index += recordLength;
            }
        }

        private void ValidateChecksum()
        {
            long declared = ParseNumeric(_header, 148, 8);
            int unsigned = 0;
            int signed = 0;
            for (int i = 0; i < BlockSize; i++)
            {
                int value = i >= 148 && i < 156 ? ' ' : _header[i];
                unsigned += value;
                signed += i >= 148 && i < 156 ? ' ' : (sbyte)_header[i];
            }

            if (declared != unsigned && declared != signed)
            {
                throw new InvalidDataException(
                    "The tar archive has an invalid header checksum; the download may be corrupt or is not a tar archive.");
            }
        }

        private string ReadStringPayload(long size)
        {
            if (size > 1 << 20)
            {
                throw new InvalidDataException("The tar archive contains an implausibly large extended header.");
            }

            byte[] payload = new byte[size];
            ReadExactly(payload, 0, payload.Length);
            SkipPadding(size);
            return Encoding.UTF8.GetString(payload).TrimEnd('\0');
        }

        private bool TryReadBlock()
        {
            int read = 0;
            while (read < BlockSize)
            {
                int n = _stream.Read(_header, read, BlockSize - read);
                if (n == 0)
                {
                    if (read == 0)
                    {
                        return false;
                    }

                    throw new InvalidDataException("The tar archive ended in the middle of a header block.");
                }

                read += n;
            }

            return true;
        }

        private void ReadExactly(byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buffer, offset + read, count - read);
                if (n == 0)
                {
                    throw new InvalidDataException("The tar archive ended unexpectedly.");
                }

                read += n;
            }
        }

        /// <summary>
        /// Consumes whatever the caller did not read of the current entry, plus the
        /// block padding that follows it. Safe whether the payload was read in full,
        /// in part, or not at all.
        /// </summary>
        private void SkipCurrentEntryRemainder()
        {
            if (_entryLength <= 0)
            {
                _remaining = 0;
                _entryLength = 0;
                return;
            }

            if (_remaining > 0)
            {
                byte[] buffer = new byte[64 * 1024];
                while (_remaining > 0)
                {
                    int want = (int)Math.Min(buffer.Length, _remaining);
                    int n = _stream.Read(buffer, 0, want);
                    if (n == 0)
                    {
                        throw new InvalidDataException("The tar archive ended unexpectedly.");
                    }

                    _remaining -= n;
                }
            }

            SkipPadding(_entryLength);
            _entryLength = 0;
        }

        private void SkipPadding(long size)
        {
            int padding = (int)(((size + BlockSize - 1) / BlockSize * BlockSize) - size);
            if (padding == 0)
            {
                return;
            }

            byte[] buffer = new byte[padding];
            ReadExactly(buffer, 0, padding);
        }

        /// <summary>Read-only view over the current entry's payload.</summary>
        private sealed class EntryStream : Stream
        {
            private readonly TarReader _reader;
            private readonly long _length;

            public EntryStream(TarReader reader)
            {
                _reader = reader;
                _length = reader._remaining;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => _length;

            public override long Position
            {
                get => _length - _reader._remaining;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_reader._remaining <= 0)
                {
                    return 0;
                }

                int want = (int)Math.Min(count, _reader._remaining);
                int read = _reader._stream.Read(buffer, offset, want);
                if (read == 0)
                {
                    throw new InvalidDataException("The tar archive ended in the middle of an entry.");
                }

                _reader._remaining -= read;
                return read;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
