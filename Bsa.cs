using K4os.Compression.LZ4.Streams;
using System.Text;

namespace TreeLODGen;

// Minimal reader for Skyrim SE archives (BSA version 105): lists files and extracts them.
// Vanilla trees and their textures only exist inside these archives.
sealed class Bsa
{
    const uint FlagCompressed = 0x4;
    const uint FlagEmbeddedNames = 0x100;
    const uint SizeToggleCompression = 0x40000000;

    public readonly string ArchivePath;
    readonly bool _compressedByDefault, _embeddedNames;
    readonly Dictionary<string, (uint size, uint offset)> _files = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Files => _files.Keys;

    public Bsa(string path)
    {
        ArchivePath = path;
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.Latin1);
        if (new string(reader.ReadChars(4)) != "BSA\0") throw new InvalidDataException($"not a BSA: {path}");
        var version = reader.ReadUInt32();
        if (version != 105) throw new InvalidDataException($"unsupported BSA version {version}: {path}");
        reader.ReadUInt32(); // folder record offset
        var flags = reader.ReadUInt32();
        var folderCount = reader.ReadUInt32();
        var fileCount = reader.ReadUInt32();
        reader.ReadUInt32(); // total folder name length
        reader.ReadUInt32(); // total file name length
        reader.ReadUInt32(); // file flags + padding
        _compressedByDefault = (flags & FlagCompressed) != 0;
        _embeddedNames = (flags & FlagEmbeddedNames) != 0;

        var folderFileCounts = new uint[folderCount];
        for (var i = 0; i < folderCount; i++)
        {
            reader.ReadUInt64(); // name hash
            folderFileCounts[i] = reader.ReadUInt32();
            reader.ReadUInt32(); // padding
            reader.ReadUInt64(); // offset
        }

        var entries = new List<(string folder, uint size, uint offset)>((int)fileCount);
        for (var i = 0; i < folderCount; i++)
        {
            var length = reader.ReadByte();
            var folder = new string(reader.ReadChars(length)).TrimEnd('\0');
            for (var f = 0; f < folderFileCounts[i]; f++)
            {
                reader.ReadUInt64(); // name hash
                var size = reader.ReadUInt32();
                var offset = reader.ReadUInt32();
                entries.Add((folder, size, offset));
            }
        }

        foreach (var (folder, size, offset) in entries)
        {
            var name = new StringBuilder();
            for (var c = reader.ReadByte(); c != 0; c = reader.ReadByte()) name.Append((char)c);
            _files[folder + "\\" + name] = (size, offset);
        }
    }

    public bool Contains(string relativePath) => _files.ContainsKey(relativePath);

    public byte[] Extract(string relativePath)
    {
        var (size, offset) = _files[relativePath];
        var compressed = _compressedByDefault ^ ((size & SizeToggleCompression) != 0);
        var length = (int)(size & ~SizeToggleCompression & ~0x80000000);

        using var stream = File.OpenRead(ArchivePath);
        using var reader = new BinaryReader(stream);
        stream.Position = offset;
        if (_embeddedNames)
        {
            var nameLength = reader.ReadByte();
            stream.Position += nameLength;
            length -= nameLength + 1;
        }
        if (!compressed) return reader.ReadBytes(length);

        var originalSize = reader.ReadUInt32();
        using var frame = LZ4Stream.Decode(new MemoryStream(reader.ReadBytes(length - 4)));
        var result = new byte[originalSize];
        frame.ReadExactly(result);
        return result;
    }
}
