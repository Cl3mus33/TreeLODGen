using System.IO.Compression;
using System.Text;

namespace TreeLODGen;

sealed record TreeRecord(string Plugin, string Owner, uint LocalId, string EditorId, string Model, short[] Bounds)
{
    // Same test TexGen applies before it makes a billboard: smaller plants get no tree LOD.
    public bool IsLodSized(float minMagnitude = 512f, float minHeight = 256f)
    {
        float dx = Bounds[3] - Bounds[0], dy = Bounds[4] - Bounds[1], dz = Bounds[5] - Bounds[2];
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz) >= minMagnitude && Bounds[5] >= minHeight;
    }
}

// Reads the TREE base records of a load order. A later plugin overriding a record wins.
static class Plugins
{
    const uint FlagCompressed = 0x00040000;

    public static List<TreeRecord> WinningTrees(Vfs vfs, IEnumerable<string> loadOrder, Action<string> log)
    {
        var winners = new Dictionary<(string master, uint id), TreeRecord>();
        foreach (var plugin in loadOrder)
        {
            var path = vfs.Resolve(plugin);
            if (path == null) { log($"  plugin not found: {plugin}"); continue; }
            foreach (var (key, record) in ReadTrees(path, plugin)) winners[key] = record;
        }
        return winners.Values.ToList();
    }

    // Active plugins in load order for a Mod Organizer 2 profile. Base game and Creation
    // Club files never appear in plugins.txt and are always active.
    public static List<string> ActiveLoadOrder(string profileDir)
    {
        var listed = File.ReadAllLines(Path.Combine(profileDir, "plugins.txt")).Where(l => l.Length > 0 && l[0] != '#').ToList();
        var active = listed.Where(l => l[0] == '*').Select(l => l[1..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inactive = listed.Where(l => l[0] != '*').ToHashSet(StringComparer.OrdinalIgnoreCase);
        return File.ReadAllLines(Path.Combine(profileDir, "loadorder.txt"))
            .Where(l => l.Length > 0 && l[0] != '#')
            .Where(l => active.Contains(l) || !inactive.Contains(l))
            .ToList();
    }

    // Active plugins in load order as the game itself reads them: the base game, the
    // Creation Club files listed in Skyrim.ccc, then the starred lines of plugins.txt.
    public static List<string> ActiveLoadOrder(string gameData, string pluginsTxt)
    {
        var order = new List<string> { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };
        var creationClub = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gameData).TrimEnd('\\'))!, "Skyrim.ccc");
        if (File.Exists(creationClub)) order.AddRange(File.ReadAllLines(creationClub).Select(l => l.Trim()).Where(l => l.Length > 0));
        if (File.Exists(pluginsTxt)) order.AddRange(File.ReadAllLines(pluginsTxt).Where(l => l.StartsWith('*')).Select(l => l[1..].Trim()));
        return order.Where(p => File.Exists(Path.Combine(gameData, p))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static IEnumerable<((string master, uint id) key, TreeRecord record)> ReadTrees(string path, string pluginName)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.Latin1);

        // TES4 header: the master list gives meaning to the top byte of every FormID
        reader.ReadChars(4);
        var headerSize = reader.ReadUInt32();
        stream.Position += 16;
        var masters = new List<string>();
        foreach (var (type, data) in Subrecords(reader.ReadBytes((int)headerSize)))
            if (type == "MAST") masters.Add(Encoding.Latin1.GetString(data).TrimEnd('\0'));

        while (stream.Position + 24 <= stream.Length)
        {
            var type = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var label = new string(reader.ReadChars(4));
            stream.Position += 12;
            if (type != "GRUP") { stream.Position += size; continue; }
            var end = stream.Position - 24 + size;
            if (label != "TREE") { stream.Position = end; continue; }

            while (stream.Position < end)
            {
                reader.ReadChars(4);
                var dataSize = reader.ReadUInt32();
                var flags = reader.ReadUInt32();
                var formId = reader.ReadUInt32();
                stream.Position += 8;
                var data = reader.ReadBytes((int)dataSize);
                if ((flags & FlagCompressed) != 0)
                {
                    using var zlib = new ZLibStream(new MemoryStream(data, 4, data.Length - 4), CompressionMode.Decompress);
                    var inflated = new byte[BitConverter.ToUInt32(data, 0)];
                    zlib.ReadExactly(inflated);
                    data = inflated;
                }

                string editorId = "", model = "";
                var bounds = new short[6];
                foreach (var (subType, subData) in Subrecords(data))
                {
                    if (subType == "EDID") editorId = Encoding.Latin1.GetString(subData).TrimEnd('\0');
                    else if (subType == "MODL" && model == "") model = Encoding.Latin1.GetString(subData).TrimEnd('\0');
                    else if (subType == "OBND" && subData.Length >= 12) Buffer.BlockCopy(subData, 0, bounds, 0, 12);
                }

                var masterIndex = (int)(formId >> 24);
                var owner = masterIndex < masters.Count ? masters[masterIndex] : pluginName;
                if (model != "")
                    yield return ((owner.ToLowerInvariant(), formId & 0x00FFFFFF), new TreeRecord(pluginName, owner, formId & 0x00FFFFFF, editorId, model, bounds));
            }
            // tree records all sit in this one top-level group
            yield break;
        }
    }

    static IEnumerable<(string type, byte[] data)> Subrecords(byte[] body)
    {
        var offset = 0;
        var oversize = 0;
        while (offset + 6 <= body.Length)
        {
            var type = Encoding.Latin1.GetString(body, offset, 4);
            int size = BitConverter.ToUInt16(body, offset + 4);
            // a field longer than 64 KB is announced by an XXXX field carrying its real size
            if (type == "XXXX") { oversize = BitConverter.ToInt32(body, offset + 6); offset += 6 + size; continue; }
            if (oversize > 0) { size = oversize; oversize = 0; }
            if (offset + 6 + size > body.Length) yield break;
            yield return (type, body[(offset + 6)..(offset + 6 + size)]);
            offset += 6 + size;
        }
    }
}
