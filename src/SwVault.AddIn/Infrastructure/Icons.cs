using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SwVault.Protocol;

namespace SwVault.AddIn.Infrastructure
{
    /// <summary>
    /// Draws the CommandManager icon strips and task-pane state icons at run time from Segoe MDL2
    /// glyphs, so the repository has no binary image assets. Glyphs are given as code points.
    /// </summary>
    internal static class Icons
    {
        private static readonly int[] StripSizes = { 20, 32, 40, 64, 96, 128 };
        private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwVault", "icons");

        public sealed class Glyph
        {
            public Glyph(char symbol, Color color)
            {
                Symbol = symbol;
                Color = color;
            }

            public char Symbol { get; }
            public Color Color { get; }
        }

        private static string FontName =>
            new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        /// <summary>Writes one horizontal strip per size and returns the file paths (SOLIDWORKS picks the size it needs).</summary>
        public static string[] CommandStrips(IList<Glyph> glyphs, string name)
        {
            Directory.CreateDirectory(Folder);
            var font = FontName;
            return StripSizes.Select(size =>
            {
                var path = Path.Combine(Folder, name + "_" + size + ".png");
                using (var bitmap = new Bitmap(size * glyphs.Count, size, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        for (var i = 0; i < glyphs.Count; i++) DrawGlyph(g, glyphs[i], new Rectangle(i * size, 0, size, size), font);
                    }
                    bitmap.Save(path, ImageFormat.Png);
                }
                return path;
            }).ToArray();
        }

        public static string TaskPaneIcon()
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, "taskpane.png");
            using (var bitmap = new Bitmap(16, 18, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                    DrawGlyph(g, new Glyph((char)0xE72E, Color.FromArgb(0, 102, 204)), new Rectangle(0, 1, 16, 16), FontName);
                bitmap.Save(path, ImageFormat.Png);
            }
            return path;
        }

        private static void DrawGlyph(Graphics g, Glyph glyph, Rectangle bounds, string fontName)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var inset = Math.Max(1, bounds.Width / 16);
            var circle = Rectangle.Inflate(bounds, -inset, -inset);
            using (var fill = new SolidBrush(glyph.Color)) g.FillEllipse(fill, circle);
            using (var font = new Font(fontName, bounds.Height * 0.5f, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.White))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(glyph.Symbol.ToString(), font, brush, new RectangleF(circle.X, circle.Y + 1, circle.Width, circle.Height), format);
            }
        }

        // ---------------------------------------------------------------- task pane state icons

        public const string Folder16 = "folder";

        /// <summary>16 px icons keyed by <see cref="StateKey"/>.</summary>
        public static ImageList StateImages()
        {
            var list = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            var font = FontName;
            void Add(string key, int codePoint, Color color)
            {
                var bitmap = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bitmap)) DrawGlyph(g, new Glyph((char)codePoint, color), new Rectangle(0, 0, 16, 16), font);
                list.Images.Add(key, bitmap);
            }
            Add(Folder16, 0xE8B7, Color.FromArgb(214, 160, 40));
            Add("uptodate", 0xE73E, Color.FromArgb(46, 139, 87));
            Add("outdated", 0xE896, Color.FromArgb(230, 145, 0));
            Add("modified", 0xE70F, Color.FromArgb(0, 102, 204));
            Add("conflict", 0xE7BA, Color.FromArgb(200, 40, 40));
            Add("new", 0xE710, Color.FromArgb(128, 64, 160));
            Add("notlocal", 0xE753, Color.FromArgb(150, 150, 150));
            Add("mine", 0xE72E, Color.FromArgb(0, 102, 204));
            Add("other", 0xE72E, Color.FromArgb(200, 40, 40));
            Add("released", 0xE8FB, Color.FromArgb(46, 139, 87));
            return list;
        }

        public static string StateKey(FileStatusDto s)
        {
            if (s.IsFolder) return Folder16;
            if (s.LockState == LockState.Other) return "other";
            switch (s.LocalState)
            {
                case LocalState.Conflict: return "conflict";
                case LocalState.LocalOnly: return "new";
                case LocalState.Modified: return "modified";
                case LocalState.Outdated: return "outdated";
                case LocalState.NotLocal:
                case LocalState.MissingLocally: return "notlocal";
            }
            if (s.LockState == LockState.MineHere || s.LockState == LockState.MineElsewhere) return "mine";
            if (string.Equals(s.State, "Released", StringComparison.OrdinalIgnoreCase)) return "released";
            return "uptodate";
        }
    }
}
