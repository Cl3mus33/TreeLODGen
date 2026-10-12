using BCnEncoder.Decoder;
using BCnEncoder.Shared.ImageFiles;

namespace TreeLODGen;

// Loads game textures (DDS, any block compression) as ARGB pixels.
static class Textures
{
    // Leaf atlases reach 8192 px; telling bark from foliage does not need more than this.
    const int MaxSize = 2048;

    public sealed class Texture
    {
        // Width and Height are those of the file; Pixels may hold a reduced copy.
        public int Width, Height, StoredWidth, StoredHeight;
        public int[] Pixels = Array.Empty<int>();

        public int Texel(float u, float v)
        {
            u -= MathF.Floor(u); v -= MathF.Floor(v);
            return Pixels[Math.Min(StoredHeight - 1, (int)(v * StoredHeight)) * StoredWidth + Math.Min(StoredWidth - 1, (int)(u * StoredWidth))];
        }

        public int Alpha(float u, float v) => (Texel(u, v) >> 24) & 0xFF;

        // Share of the texture that passes the alpha test once it is shrunk so that its
        // longer side measures maxSide pixels.
        public float Coverage(int maxSide, int threshold)
        {
            var step = Math.Max(1, Math.Max(StoredWidth, StoredHeight) / Math.Max(1, maxSide));
            int width = Math.Max(1, StoredWidth / step), height = Math.Max(1, StoredHeight / step), passing = 0;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < step; dy++)
                for (var dx = 0; dx < step; dx++)
                    sum += (Pixels[(y * step + dy) * StoredWidth + x * step + dx] >> 24) & 0xFF;
                if (sum >= threshold * step * step) passing++;
            }
            return (float)passing / (width * height);
        }
    }

    // Set when textures must be looked up through a load order (loose files and archives).
    public static Func<string, string?>? Resolver;

    static readonly Dictionary<string, Texture?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Texture? Load(string relative, string[] roots)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        var dds = Resolver?.Invoke(relative) ?? roots.Select(r => Path.Combine(r, relative)).FirstOrDefault(File.Exists);
        if (dds == null) return null;
        if (Cache.TryGetValue(dds, out var cached)) return cached;

        Texture? texture = null;
        try
        {
            using var stream = File.OpenRead(dds);
            var file = DdsFile.Load(stream);
            var decoder = new BcDecoder();
            decoder.Options.IsParallel = true;
            var pixels = decoder.Decode(file);
            int width = (int)file.header.dwWidth, height = (int)file.header.dwHeight;

            var step = 1;
            while (Math.Max(width, height) / step > MaxSize) step *= 2;
            int storedWidth = Math.Max(1, width / step), storedHeight = Math.Max(1, height / step);
            var stored = new int[storedWidth * storedHeight];
            for (var y = 0; y < storedHeight; y++)
            for (var x = 0; x < storedWidth; x++)
            {
                var p = pixels[y * step * width + x * step];
                stored[y * storedWidth + x] = (p.a << 24) | (p.r << 16) | (p.g << 8) | p.b;
            }
            texture = new Texture { Width = width, Height = height, StoredWidth = storedWidth, StoredHeight = storedHeight, Pixels = stored };
        }
        catch (Exception)
        {
            // an unreadable texture is treated like a missing one: callers fall back to geometry alone
        }
        return Cache[dds] = texture;
    }
}
