using nifly;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TreeLODGen;

// Shows the LOD model that was just generated as a slowly turning wireframe, the way a 3D
// editor displays an object's edges: foliage in green, bark in orange.
sealed class PreviewPanel : Panel
{
    const int MaxEdges = 6000;

    sealed class Model
    {
        public float[] Points = Array.Empty<float>(); // x, y, z per edge end, centred, unit height
        public bool[] Bark = Array.Empty<bool>();     // per edge
        public string Name = "";
        public int CrownTris, BarkTris;
    }

    Model? _model;
    float _angle;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };

    public PreviewPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Panel;
        _timer.Tick += (_, _) => { _angle += 0.012f; if (_model != null) Invalidate(); };
        _timer.Start();
    }

    // Reads the edges of a LOD file. Safe to call from a worker thread; hand the result to Show.
    public static object? Load(string name, string lodPath)
    {
        using var nif = new NifFile();
        if (!File.Exists(lodPath) || nif.Load(lodPath) != 0) return null;

        var points = new List<float>();
        var bark = new List<bool>();
        var model = new Model { Name = name };
        foreach (var shape in nif.GetShapes())
        {
            var isBark = shape.name.get().Contains("Trunk", StringComparison.OrdinalIgnoreCase) || shape.name.get().StartsWith("Bark ", StringComparison.Ordinal);
            var transform = shape.transform;
            var verts = nif.GetVertsForShape(shape).Select(v => transform.ApplyTransform(v)).ToList();
            var tris = new vectorTriangle();
            shape.GetTriangles(tris);
            if (isBark) model.BarkTris += tris.Count; else model.CrownTris += tris.Count;

            var seen = new HashSet<(ushort, ushort)>();
            foreach (var t in tris)
                foreach (var (a, b) in new[] { (t.p1, t.p2), (t.p2, t.p3), (t.p3, t.p1) })
                {
                    if (!seen.Add(a < b ? (a, b) : (b, a))) continue;
                    points.AddRange(new[] { verts[a].x, verts[a].y, verts[a].z, verts[b].x, verts[b].y, verts[b].z });
                    bark.Add(isBark);
                }
        }
        if (bark.Count == 0) return null;

        // keep the drawing light: thin out foliage edges evenly, bark is kept whole
        if (bark.Count > MaxEdges)
        {
            var step = (int)Math.Ceiling(bark.Count / (double)MaxEdges);
            var keptPoints = new List<float>(); var keptBark = new List<bool>();
            for (var e = 0; e < bark.Count; e++)
            {
                if (!bark[e] && e % step != 0) continue;
                keptPoints.AddRange(points.GetRange(e * 6, 6)); keptBark.Add(bark[e]);
            }
            points = keptPoints; bark = keptBark;
        }

        // centre on the trunk axis and scale to unit height
        float minZ = float.MaxValue, maxZ = float.MinValue, sumX = 0, sumY = 0;
        for (var i = 0; i < points.Count; i += 3) { sumX += points[i]; sumY += points[i + 1]; minZ = Math.Min(minZ, points[i + 2]); maxZ = Math.Max(maxZ, points[i + 2]); }
        float centerX = sumX / (points.Count / 3), centerY = sumY / (points.Count / 3), height = Math.Max(1f, maxZ - minZ);
        for (var i = 0; i < points.Count; i += 3)
        {
            points[i] = (points[i] - centerX) / height;
            points[i + 1] = (points[i + 1] - centerY) / height;
            points[i + 2] = (points[i + 2] - minZ) / height - 0.5f;
        }
        model.Points = points.ToArray();
        model.Bark = bark.ToArray();
        return model;
    }

    public void Show(object? loaded)
    {
        if (loaded is Model model) { _model = model; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);

        // viewport grid
        using (var grid = new Pen(Theme.Grid))
            for (var i = 1; i < 8; i++)
            {
                g.DrawLine(grid, Width * i / 8, 0, Width * i / 8, Height);
                g.DrawLine(grid, 0, Height * i / 8, Width, Height * i / 8);
            }

        using var caption = new SolidBrush(Theme.Dim);
        if (_model == null)
        {
            var text = "The wireframe of each LOD model\nappears here while it is generated.";
            var size = g.MeasureString(text, Font);
            g.DrawString(text, Font, caption, (Width - size.Width) / 2, (Height - size.Height) / 2);
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cos = MathF.Cos(_angle), sin = MathF.Sin(_angle);
        const float tilt = 0.22f; // seen from slightly above
        float cosT = MathF.Cos(tilt), sinT = MathF.Sin(tilt);
        var scale = (Height - 70) * 0.92f;
        float originX = Width / 2f, originY = (Height - 30) / 2f;

        using var leaf = new Pen(Color.FromArgb(105, Theme.Accent), 1f);
        using var wood = new Pen(Color.FromArgb(235, Theme.Orange), 1.4f);
        var p = _model.Points;
        for (var pass = 0; pass < 2; pass++) // foliage first, bark over it
            for (int edge = 0, i = 0; edge < _model.Bark.Length; edge++, i += 6)
            {
                if (_model.Bark[edge] != (pass == 1)) continue;
                float x1 = p[i] * cos - p[i + 1] * sin, d1 = p[i] * sin + p[i + 1] * cos;
                float x2 = p[i + 3] * cos - p[i + 4] * sin, d2 = p[i + 3] * sin + p[i + 4] * cos;
                g.DrawLine(pass == 1 ? wood : leaf,
                    originX + x1 * scale, originY - (p[i + 2] * cosT - d1 * sinT) * scale,
                    originX + x2 * scale, originY - (p[i + 5] * cosT - d2 * sinT) * scale);
            }

        using var name = new SolidBrush(Theme.Text);
        g.DrawString(_model.Name, Font, name, 12, Height - 44);
        g.DrawString($"LOD 4 model: crown {_model.CrownTris} triangles, trunk {_model.BarkTris}", Font, caption, 12, Height - 24);
    }
}

static class Theme
{
    public static readonly Color Back = Color.FromArgb(24, 27, 33);
    public static readonly Color Panel = Color.FromArgb(18, 20, 25);
    public static readonly Color Field = Color.FromArgb(34, 38, 47);
    public static readonly Color Grid = Color.FromArgb(32, 36, 44);
    public static readonly Color Text = Color.FromArgb(222, 227, 235);
    public static readonly Color Dim = Color.FromArgb(138, 148, 163);
    public static readonly Color Accent = Color.FromArgb(120, 226, 170);
    public static readonly Color Orange = Color.FromArgb(255, 170, 60);
}
