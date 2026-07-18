// Generates app.ico — Claude coral sunburst over a small usage bar-graph.
// Build: csc /nologo /target:exe /out:makeicon.exe /r:System.Drawing.dll makeicon.cs
// Run:   makeicon.exe   (writes app.ico next to it)
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static readonly Color Coral = Color.FromArgb(217, 119, 87);   // Claude coral
    static readonly Color Coral2 = Color.FromArgb(240, 150, 110);
    static readonly Color Bg1 = Color.FromArgb(34, 30, 44);       // deep aubergine
    static readonly Color Bg2 = Color.FromArgb(58, 44, 70);

    static Bitmap Render(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            // rounded-rect background with a soft diagonal gradient
            float r = s * 0.22f;
            using (var path = Rounded(new RectangleF(0, 0, s, s), r))
            using (var bg = new LinearGradientBrush(new RectangleF(0, 0, s, s), Bg1, Bg2, 55f))
                g.FillPath(bg, path);

            // --- Claude sunburst, centred in the upper two-thirds ---
            float cx = s * 0.50f, cy = s * 0.40f;
            float rr = s * 0.29f;      // petal length
            float hub = s * 0.05f;     // centre dot
            int petals = 12;
            // faint glow behind the burst so it reads even over the bars
            using (var glow = new PathGradientBrush(CircleP(cx, cy, rr * 1.05f)))
            {
                glow.CenterColor = Color.FromArgb(70, Coral2);
                glow.SurroundColors = new[] { Color.FromArgb(0, Coral2) };
                g.FillPath(glow, CircleP(cx, cy, rr * 1.05f));
            }
            using (var pen = new Pen(Coral, s * 0.05f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                for (int i = 0; i < petals; i++)
                {
                    double a = Math.PI * 2 * i / petals - Math.PI / 2;
                    float x1 = cx + (float)Math.Cos(a) * hub * 1.4f;
                    float y1 = cy + (float)Math.Sin(a) * hub * 1.4f;
                    float x2 = cx + (float)Math.Cos(a) * rr;
                    float y2 = cy + (float)Math.Sin(a) * rr;
                    g.DrawLine(pen, x1, y1, x2, y2);
                }
            }
            using (var hb = new SolidBrush(Coral))
                g.FillEllipse(hb, cx - hub, cy - hub, hub * 2, hub * 2);

            // --- usage bars across the bottom (the "graph"), full width, clear of the burst ---
            float pad = s * 0.15f;
            float baseY = s * 0.83f;
            float span = s - pad * 2;
            int nb = 5;
            float gap = s * 0.045f;
            float bw = (span - gap * (nb - 1)) / nb;
            float[] hs = { 0.14f, 0.22f, 0.17f, 0.30f, 0.24f };
            float x = pad;
            for (int i = 0; i < nb; i++)
            {
                float h = s * hs[i];
                var rect = new RectangleF(x, baseY - h, bw, h);
                Color c = i == 3 ? Coral2 : Color.FromArgb(200, 158, 166, 190);
                using (var br = new SolidBrush(c))
                using (var bp = Rounded(rect, bw * 0.3f))
                    g.FillPath(br, bp);
                x += bw + gap;
            }
        }
        return bmp;
    }

    static GraphicsPath CircleP(float cx, float cy, float r)
    {
        var p = new GraphicsPath();
        p.AddEllipse(cx - r, cy - r, r * 2, r * 2);
        return p;
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Write a multi-size .ico from PNG-compressed frames (Vista+ format).
    static void WriteIco(string path, int[] sizes)
    {
        var pngs = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (var bmp = Render(sizes[i]))
            using (var ms = new MemoryStream())
            { bmp.Save(ms, ImageFormat.Png); pngs[i] = ms.ToArray(); }

        using (var fs = new FileStream(path, FileMode.Create))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0);              // reserved
            w.Write((short)1);              // type: icon
            w.Write((short)sizes.Length);   // count
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); // width  (0 = 256)
                w.Write((byte)(s >= 256 ? 0 : s)); // height
                w.Write((byte)0);           // palette
                w.Write((byte)0);           // reserved
                w.Write((short)1);          // colour planes
                w.Write((short)32);         // bpp
                w.Write(pngs[i].Length);    // bytes of image data
                w.Write(offset);            // offset
                offset += pngs[i].Length;
            }
            foreach (var png in pngs) w.Write(png);
        }
    }

    static void Main()
    {
        WriteIco("app.ico", new[] { 16, 24, 32, 48, 64, 128, 256 });
        Console.WriteLine("wrote app.ico");
    }
}
