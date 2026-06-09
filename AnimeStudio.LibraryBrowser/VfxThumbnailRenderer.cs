using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AnimeStudio.LibraryBrowser
{
    internal static class VfxThumbnailRenderer
    {
        private const int Size = 256;

        public static void RenderToFile(LibraryModelItem item, string outputPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var temp = outputPath + ".tmp.png";
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            using var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(47, 52, 56));

            using var background = new LinearGradientBrush(
                new Rectangle(0, 0, Size, Size),
                Color.FromArgb(42, 46, 51),
                Color.FromArgb(22, 25, 29),
                LinearGradientMode.ForwardDiagonal);
            g.FillRectangle(background, 0, 0, Size, Size);

            var palette = GetPalette(item);
            DrawApproximateThumbnail(g, item, palette);
            DrawBadge(g, item, palette);

            bitmap.Save(temp, ImageFormat.Png);
            File.Move(temp, outputPath, true);
        }

        private static VfxPalette GetPalette(LibraryModelItem item)
        {
            var text = GetSearchText(item);
            if (ContainsAny(text, "blood"))
            {
                return new VfxPalette(Color.FromArgb(182, 32, 42), Color.FromArgb(255, 155, 120), Color.FromArgb(78, 10, 18));
            }
            if (ContainsAny(text, "fire", "flame", "explosion", "impact", "hit", "burst"))
            {
                return new VfxPalette(Color.FromArgb(255, 116, 37), Color.FromArgb(255, 218, 81), Color.FromArgb(112, 26, 17));
            }
            if (ContainsAny(text, "ice", "water", "frost"))
            {
                return new VfxPalette(Color.FromArgb(78, 192, 255), Color.FromArgb(202, 245, 255), Color.FromArgb(20, 67, 104));
            }
            if (ContainsAny(text, "lightning", "beam", "laser", "spark"))
            {
                return new VfxPalette(Color.FromArgb(149, 111, 255), Color.FromArgb(238, 231, 255), Color.FromArgb(46, 32, 111));
            }
            if (ContainsAny(text, "poison", "aura", "buff", "debuff", "zone", "field"))
            {
                return new VfxPalette(Color.FromArgb(61, 224, 135), Color.FromArgb(206, 255, 167), Color.FromArgb(22, 80, 56));
            }
            if (ContainsAny(text, "trail", "slash", "arrow", "projectile", "bolt"))
            {
                return new VfxPalette(Color.FromArgb(94, 178, 255), Color.FromArgb(255, 255, 255), Color.FromArgb(26, 59, 102));
            }
            if (ContainsAny(text, "smoke", "fog", "dust"))
            {
                return new VfxPalette(Color.FromArgb(150, 161, 168), Color.FromArgb(231, 235, 238), Color.FromArgb(53, 57, 60));
            }

            return new VfxPalette(Color.FromArgb(255, 190, 72), Color.FromArgb(255, 245, 186), Color.FromArgb(69, 51, 25));
        }

        private static void DrawApproximateThumbnail(Graphics g, LibraryModelItem item, VfxPalette palette)
        {
            var style = DetectStyle(item);
            using var glow = new SolidBrush(Color.FromArgb(42, palette.Accent));
            g.FillEllipse(glow, 24, 36, 208, 190);

            using var texture = TryLoadTexturePreview(item);
            if (texture != null)
            {
                DrawTextureBackedThumbnail(g, item, texture, palette, style);
                using var fontTexture = new Font(FontFamily.GenericSansSerif, 8, FontStyle.Bold);
                using var brushTexture = new SolidBrush(Color.FromArgb(222, 235, 240, 245));
                g.DrawString("TEXTURE APPROX", fontTexture, brushTexture, 126, 230);
                return;
            }

            switch (style)
            {
                case VfxStyle.Trail:
                    DrawTrail(g, palette);
                    break;
                case VfxStyle.Projectile:
                    DrawProjectile(g, palette);
                    break;
                case VfxStyle.Beam:
                    DrawBeam(g, palette);
                    break;
                case VfxStyle.Shockwave:
                    DrawShockwave(g, palette);
                    break;
                case VfxStyle.Aura:
                    DrawAura(g, palette);
                    break;
                case VfxStyle.Smoke:
                    DrawSmoke(g, item, palette);
                    break;
                case VfxStyle.Fire:
                    DrawFire(g, item, palette);
                    break;
                case VfxStyle.Sparks:
                    DrawSparks(g, item, palette);
                    break;
                case VfxStyle.Distortion:
                    DrawDistortion(g, palette);
                    break;
                case VfxStyle.MeshParticles:
                    DrawMeshParticles(g, item, palette);
                    break;
                case VfxStyle.GroundPlane:
                    DrawGroundPlane(g, item, palette);
                    break;
                case VfxStyle.StretchBillboard:
                    DrawStretchBillboards(g, item, palette);
                    break;
                case VfxStyle.TexturedBillboard:
                    DrawTexturedBillboards(g, item, palette);
                    break;
                default:
                    DrawEmitter(g, item, palette);
                    break;
            }

            using var font = new Font(FontFamily.GenericSansSerif, 8, FontStyle.Bold);
            using var brush = new SolidBrush(Color.FromArgb(222, 235, 240, 245));
            g.DrawString("APPROX", font, brush, 172, 230);
        }

        private static void DrawTextureBackedThumbnail(Graphics g, LibraryModelItem item, Image texture, VfxPalette palette, VfxStyle style)
        {
            var seed = Math.Abs(item.StableKey.GetHashCode(StringComparison.Ordinal));
            using var attributes = new ImageAttributes();
            var matrix = CreateAlphaMatrix(0.72f);
            attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

            var count = style switch
            {
                VfxStyle.Trail or VfxStyle.Beam => 8,
                VfxStyle.GroundPlane or VfxStyle.Aura => 12,
                VfxStyle.MeshParticles => 16,
                _ => 14,
            };

            for (var i = 0; i < count; i++)
            {
                var angle = Hash01(seed + i * 67) * MathF.Tau;
                var radius = style switch
                {
                    VfxStyle.Trail or VfxStyle.Beam => 20 + i * 12,
                    VfxStyle.GroundPlane or VfxStyle.Aura => 62 + MathF.Sin(i) * 18,
                    _ => 14 + Hash01(seed + i * 71) * 92,
                };
                var x = 128 + MathF.Cos(angle) * radius;
                var y = 128 + MathF.Sin(angle) * radius * (style == VfxStyle.GroundPlane ? 0.28f : 0.64f);
                var size = 22 + Hash01(seed + i * 73) * 58;
                if (style == VfxStyle.Trail || style == VfxStyle.Beam)
                {
                    x = 42 + i * 24;
                    y = 172 - i * 11 + MathF.Sin(i) * 8;
                    size *= 0.78f;
                }

                var rect = new Rectangle((int)(x - size / 2), (int)(y - size / 2), Math.Max(2, (int)size), Math.Max(2, (int)size));
                var state = g.Save();
                g.TranslateTransform(x, y);
                g.RotateTransform(angle * 180f / MathF.PI + i * 11);
                g.TranslateTransform(-x, -y);
                g.DrawImage(texture, rect, 0, 0, texture.Width, texture.Height, GraphicsUnit.Pixel, attributes);
                g.Restore(state);
            }

            using var accent = new Pen(Color.FromArgb(135, palette.Highlight), 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (style == VfxStyle.Trail || style == VfxStyle.Beam)
            {
                g.DrawCurve(accent, new[] { new PointF(34, 176), new PointF(82, 132), new PointF(154, 106), new PointF(222, 76) }, 0.35f);
            }
            else if (style == VfxStyle.Aura || style == VfxStyle.GroundPlane)
            {
                g.DrawEllipse(accent, 38, 88, 180, 76);
            }
        }

        private static ColorMatrix CreateAlphaMatrix(float alpha)
        {
            return new ColorMatrix(new[]
            {
                new[] { 1f, 0f, 0f, 0f, 0f },
                new[] { 0f, 1f, 0f, 0f, 0f },
                new[] { 0f, 0f, 1f, 0f, 0f },
                new[] { 0f, 0f, 0f, alpha, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f },
            });
        }

        private static Image TryLoadTexturePreview(LibraryModelItem item)
        {
            if (item?.VfxTexturePreviewPaths == null || item.VfxTexturePreviewPaths.Length == 0)
            {
                return null;
            }

            foreach (var path in item.VfxTexturePreviewPaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        return Image.FromFile(path);
                    }
                }
                catch
                {
                    // Keep thumbnail generation resilient; the next path may still work.
                }
            }

            return null;
        }

        private static void DrawBadge(Graphics g, LibraryModelItem item, VfxPalette palette)
        {
            var text = string.IsNullOrWhiteSpace(item.VfxCategory) ? "VFX" : item.VfxCategory;
            using var badgeBrush = new SolidBrush(Color.FromArgb(220, 18, 21, 24));
            using var textBrush = new SolidBrush(palette.Highlight);
            using var font = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Bold);
            var width = Math.Min(212, Math.Max(56, (int)g.MeasureString(text, font).Width + 18));
            var rect = new Rectangle(14, 14, width, 28);
            using var path = RoundedRect(rect, 7);
            g.FillPath(badgeBrush, path);
            g.DrawString(text, font, textBrush, rect.Left + 9, rect.Top + 5);
        }

        private static void DrawMiniMetric(Graphics g, int x, int y, string label, int value, VfxPalette palette)
        {
            using var valueFont = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold);
            using var labelFont = new Font(FontFamily.GenericSansSerif, 7, FontStyle.Regular);
            using var valueBrush = new SolidBrush(palette.Highlight);
            using var labelBrush = new SolidBrush(Color.FromArgb(180, 226, 230, 235));
            g.DrawString(value.ToString(), valueFont, valueBrush, x, y);
            g.DrawString(label, labelFont, labelBrush, x, y + 18);
        }

        private static void DrawTrail(Graphics g, VfxPalette palette)
        {
            using var glow = new Pen(Color.FromArgb(90, palette.Accent), 38) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var body = new Pen(Color.FromArgb(225, palette.Primary), 12) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var core = new Pen(Color.FromArgb(245, palette.Highlight), 3) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var points = new[] { new PointF(34, 176), new PointF(82, 130), new PointF(151, 104), new PointF(222, 72) };
            g.DrawCurve(glow, points, 0.35f);
            g.DrawCurve(body, points, 0.35f);
            g.DrawCurve(core, points, 0.35f);
        }

        private static void DrawProjectile(Graphics g, VfxPalette palette)
        {
            using var trail = new Pen(Color.FromArgb(120, palette.Primary), 24) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var core = new SolidBrush(palette.Highlight);
            g.DrawLine(trail, 38, 168, 162, 104);
            g.FillEllipse(core, 152, 88, 38, 38);
        }

        private static void DrawBeam(Graphics g, VfxPalette palette)
        {
            using var glow = new Pen(Color.FromArgb(95, palette.Accent), 42) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var beam = new Pen(Color.FromArgb(225, palette.Primary), 16) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var core = new Pen(Color.FromArgb(245, palette.Highlight), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(glow, 28, 164, 228, 92);
            g.DrawLine(beam, 28, 164, 228, 92);
            g.DrawLine(core, 36, 158, 220, 96);
        }

        private static void DrawShockwave(Graphics g, VfxPalette palette)
        {
            for (var i = 0; i < 4; i++)
            {
                using var pen = new Pen(Color.FromArgb(190 - i * 34, i % 2 == 0 ? palette.Primary : palette.Highlight), 8 - i);
                g.DrawEllipse(pen, 38 - i * 8, 82 - i * 8, 180 + i * 16, 92 + i * 16);
            }
            DrawRadialLines(g, palette, 16, 95);
        }

        private static void DrawAura(Graphics g, VfxPalette palette)
        {
            using var p1 = new Pen(Color.FromArgb(225, palette.Primary), 8);
            using var p2 = new Pen(Color.FromArgb(170, palette.Highlight), 3);
            g.DrawEllipse(p1, 36, 78, 184, 110);
            g.DrawEllipse(p2, 62, 94, 132, 76);
            DrawDots(g, 26, palette, 42, 214, 64, 194);
        }

        private static void DrawSmoke(Graphics g, LibraryModelItem item, VfxPalette palette)
        {
            var seed = Math.Abs(item.StableKey.GetHashCode(StringComparison.Ordinal));
            for (var i = 0; i < 20; i++)
            {
                var x = 46 + Hash01(seed + i * 71) * 164;
                var y = 58 + Hash01(seed + i * 97) * 132;
                var size = 28 + Hash01(seed + i * 131) * 54;
                using var brush = new SolidBrush(Color.FromArgb(58, palette.Primary));
                g.FillEllipse(brush, x - size / 2, y - size / 2, size, size);
            }
        }

        private static void DrawFire(Graphics g, LibraryModelItem item, VfxPalette palette)
        {
            var seed = Math.Abs(item.StableKey.GetHashCode(StringComparison.Ordinal));
            for (var i = 0; i < 18; i++)
            {
                var x = 62 + Hash01(seed + i * 41) * 130;
                var len = 60 + Hash01(seed + i * 67) * 90;
                using var pen = new Pen(Color.FromArgb(215, i % 2 == 0 ? palette.Primary : palette.Highlight), 5 + Hash01(seed + i * 83) * 8)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawLine(pen, x, 194, x + (Hash01(seed + i * 101) - 0.5f) * 45, 194 - len);
            }
            DrawDots(g, 24, palette, 42, 214, 54, 190);
        }

        private static void DrawSparks(Graphics g, LibraryModelItem item, VfxPalette palette)
        {
            DrawRadialLines(g, palette, 28, 118);
            DrawDots(g, 42, palette, 36, 222, 42, 218);
        }

        private static void DrawDistortion(Graphics g, VfxPalette palette)
        {
            for (var i = 0; i < 6; i++)
            {
                using var pen = new Pen(Color.FromArgb(130 - i * 14, i % 2 == 0 ? palette.Highlight : palette.Primary), 3);
                g.DrawEllipse(pen, 52 - i * 7, 78 - i * 4, 152 + i * 14, 94 + i * 8);
            }
            using var wave = new Pen(Color.FromArgb(150, palette.Primary), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (var i = 0; i < 4; i++)
            {
                var y = 74 + i * 34;
                var points = new[] { new PointF(42, y), new PointF(82, y + 9), new PointF(128, y - 7), new PointF(176, y + 7), new PointF(216, y - 2) };
                g.DrawCurve(wave, points, 0.55f);
            }
        }

        private static void DrawEmitter(Graphics g, LibraryModelItem item, VfxPalette palette)
        {
            using var core = new SolidBrush(Color.FromArgb(225, palette.Primary));
            using var light = new SolidBrush(Color.FromArgb(245, palette.Highlight));
            g.FillEllipse(core, 104, 104, 48, 48);
            g.FillEllipse(light, 118, 118, 20, 20);
            DrawDots(g, 36, palette, 42, 214, 42, 214);
        }

        private static void DrawMeshParticles(Graphics g, LibraryModelItem item, VfxPalette palette)
        {
            var seed = Math.Abs(item.StableKey.GetHashCode(StringComparison.Ordinal));
            for (var i = 0; i < 13; i++)
            {
                var angle = Hash01(seed + i * 31) * MathF.Tau;
                var radius = 28 + Hash01(seed + i * 37) * 88;
                var x = 128 + MathF.Cos(angle) * radius;
                var y = 128 + MathF.Sin(angle) * radius * 0.62f;
                var size = 13 + Hash01(seed + i * 41) * 24;
                using var path = new GraphicsPath();
                path.AddPolygon(new[]
                {
                    new PointF(x, y - size),
                    new PointF(x + size * 0.85f, y),
                    new PointF(x, y + size),
                    new PointF(x - size * 0.85f, y),
                });
                using var fill = new SolidBrush(Color.FromArgb(128, i % 2 == 0 ? palette.Primary : palette.Highlight));
                using var pen = new Pen(Color.FromArgb(190, palette.Highlight), 2);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            DrawDots(g, 18, palette, 42, 214, 48, 206);
        }

        private static void DrawGroundPlane(Graphics g, LibraryModelItem item, VfxPalette palette)
        {
            using var shadow = new SolidBrush(Color.FromArgb(54, palette.Accent));
            g.FillEllipse(shadow, 20, 82, 216, 92);
            using var p1 = new Pen(Color.FromArgb(220, palette.Primary), 8);
            using var p2 = new Pen(Color.FromArgb(170, palette.Highlight), 3);
            g.DrawEllipse(p1, 30, 91, 196, 74);
            g.DrawEllipse(p2, 62, 104, 132, 48);
            DrawDots(g, 32, palette, 34, 222, 84, 178);
        }

        private static void DrawStretchBillboards(Graphics g, LibraryModelItem item, VfxPalette palette)
        {
            var seed = Math.Abs(item.StableKey.GetHashCode(StringComparison.Ordinal));
            for (var i = 0; i < 28; i++)
            {
                var angle = Hash01(seed + i * 73) * MathF.Tau;
                var radius = 28 + Hash01(seed + i * 79) * 96;
                var x = 128 + MathF.Cos(angle) * radius;
                var y = 128 + MathF.Sin(angle) * radius * 0.65f;
                var len = 18 + Hash01(seed + i * 83) * 62;
                using var pen = new Pen(Color.FromArgb(210, i % 3 == 0 ? palette.Highlight : palette.Primary), 4 + Hash01(seed + i * 89) * 5)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawLine(pen, x - MathF.Cos(angle) * len, y - MathF.Sin(angle) * len * 0.5f, x, y);
            }
        }

        private static void DrawTexturedBillboards(Graphics g, LibraryModelItem item, VfxPalette palette)
        {
            var seed = Math.Abs(item.StableKey.GetHashCode(StringComparison.Ordinal));
            for (var i = 0; i < 18; i++)
            {
                var x = 42 + Hash01(seed + i * 127) * 172;
                var y = 54 + Hash01(seed + i * 131) * 146;
                var size = 15 + Hash01(seed + i * 137) * 33;
                using var fill = new SolidBrush(Color.FromArgb(132, i % 2 == 0 ? palette.Primary : palette.Highlight));
                using var pen = new Pen(Color.FromArgb(175, palette.Highlight), 1.4f);
                var rect = new RectangleF(x - size / 2, y - size / 2, size, size);
                g.FillRectangle(fill, rect);
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            }
            using var atlasPen = new Pen(Color.FromArgb(130, palette.Highlight), 1);
            g.DrawRectangle(atlasPen, 91, 91, 74, 74);
            g.DrawLine(atlasPen, 128, 91, 128, 165);
            g.DrawLine(atlasPen, 91, 128, 165, 128);
        }

        private static void DrawRadialLines(Graphics g, VfxPalette palette, int count, float length)
        {
            for (var i = 0; i < count; i++)
            {
                var angle = i * MathF.Tau / count;
                using var pen = new Pen(Color.FromArgb(210, i % 3 == 0 ? palette.Highlight : palette.Primary), 4 + i % 4)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawLine(pen,
                    128 + MathF.Cos(angle) * 28,
                    128 + MathF.Sin(angle) * 28,
                    128 + MathF.Cos(angle) * length,
                    128 + MathF.Sin(angle) * length);
            }
        }

        private static void DrawDots(Graphics g, int count, VfxPalette palette, int minX, int maxX, int minY, int maxY)
        {
            var random = new Random(count * 117);
            for (var i = 0; i < count; i++)
            {
                var radius = random.Next(2, 7);
                using var brush = new SolidBrush(Color.FromArgb(random.Next(110, 230), i % 3 == 0 ? palette.Highlight : palette.Primary));
                var x = random.Next(minX, maxX);
                var y = random.Next(minY, maxY);
                g.FillEllipse(brush, x - radius, y - radius, radius * 2, radius * 2);
            }
        }

        private static VfxStyle DetectStyle(LibraryModelItem item)
        {
            var text = GetSearchText(item);
            if (ContainsAny(text, "component:trailrenderer", "component:linerenderer") || GetHintBool(item, "trail.enabled", false))
            {
                return VfxStyle.Trail;
            }
            var renderMode = GetHintInt(item, "renderer.m_RenderMode", -1);
            if (renderMode == 4 && item.MeshRefCount > 0)
            {
                if (ContainsAny(text, "beam", "laser", "ray")) return VfxStyle.Beam;
                if (ContainsAny(text, "shockwave", "shock", "wave", "burst", "explosion", "impact", "nova")) return VfxStyle.Shockwave;
                if (ContainsAny(text, "slash", "trail", "streak", "swipe")) return VfxStyle.Trail;
                if (ContainsAny(text, "aura", "buff", "debuff", "ring", "circle", "zone", "field")) return VfxStyle.GroundPlane;
                return VfxStyle.MeshParticles;
            }
            if (renderMode == 1)
            {
                return VfxStyle.StretchBillboard;
            }
            if (renderMode == 2)
            {
                return VfxStyle.GroundPlane;
            }
            if (item.TextureRefCount > 0 && !ContainsAny(text, "trail", "beam", "shock", "impact", "aura", "ring"))
            {
                return VfxStyle.TexturedBillboard;
            }
            if (HasHintPrefix(item, "shape.") && ContainsAny(text, "cone", "burst", "shock", "impact"))
            {
                return VfxStyle.Shockwave;
            }
            if (ContainsAny(text, "trail", "slash", "streak")) return VfxStyle.Trail;
            if (ContainsAny(text, "projectile", "bullet", "missile", "arrow", "bolt", "fireball")) return VfxStyle.Projectile;
            if (ContainsAny(text, "beam", "laser", "ray")) return VfxStyle.Beam;
            if (ContainsAny(text, "shockwave", "wave", "burst", "explosion", "impact", "nova")) return VfxStyle.Shockwave;
            if (ContainsAny(text, "aura", "buff", "debuff", "ring", "circle", "zone", "field")) return VfxStyle.Aura;
            if (ContainsAny(text, "smoke", "fog", "dust", "mist")) return VfxStyle.Smoke;
            if (ContainsAny(text, "fire", "flame", "burn")) return VfxStyle.Fire;
            if (ContainsAny(text, "spark", "sparkle", "electric", "lightning")) return VfxStyle.Sparks;
            if (ContainsAny(text, "distortion", "dissolve", "warp", "heatwave", "refract")) return VfxStyle.Distortion;
            return VfxStyle.Emitter;
        }

        private static string GetSearchText(LibraryModelItem item)
        {
            return $"{item.VfxCategory} {item.Name} {item.ResourceKind} {string.Join(" ", item.Signals ?? Array.Empty<string>())}".ToLowerInvariant();
        }

        private static bool HasHintPrefix(LibraryModelItem item, string prefix)
        {
            if (!TryGetHints(item, out var root))
            {
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static int GetHintInt(LibraryModelItem item, string key, int fallback)
        {
            if (!TryGetHints(item, out var root))
            {
                return fallback;
            }
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)
                    && !property.Name.EndsWith("." + key, StringComparison.OrdinalIgnoreCase)
                    && !key.EndsWith("." + property.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value))
                {
                    return value;
                }
            }
            return fallback;
        }

        private static bool GetHintBool(LibraryModelItem item, string key, bool fallback)
        {
            if (!TryGetHints(item, out var root))
            {
                return fallback;
            }
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)
                    && !property.Name.EndsWith("." + key, StringComparison.OrdinalIgnoreCase)
                    && !key.EndsWith("." + property.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (property.Value.ValueKind == JsonValueKind.True || property.Value.ValueKind == JsonValueKind.False)
                {
                    return property.Value.GetBoolean();
                }
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value))
                {
                    return value != 0;
                }
            }
            return fallback;
        }

        private static bool TryGetHints(LibraryModelItem item, out JsonElement root)
        {
            root = default;
            if (string.IsNullOrWhiteSpace(item?.VfxPreviewHintsJson))
            {
                return false;
            }
            try
            {
                using var document = JsonDocument.Parse(item.VfxPreviewHintsJson);
                root = document.RootElement.Clone();
                return root.ValueKind == JsonValueKind.Object;
            }
            catch
            {
                return false;
            }
        }

        private static float Hash01(int value)
        {
            unchecked
            {
                var x = (uint)value;
                x ^= x >> 16;
                x *= 0x7feb352d;
                x ^= x >> 15;
                x *= 0x846ca68b;
                x ^= x >> 16;
                return (x & 0x00ffffff) / 16777215.0f;
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static bool ContainsAny(string text, params string[] tokens)
        {
            return tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private readonly record struct VfxPalette(Color Primary, Color Highlight, Color Accent);

        private enum VfxStyle
        {
            Emitter,
            Trail,
            Projectile,
            Beam,
            Shockwave,
            Aura,
            Smoke,
            Fire,
            Sparks,
            Distortion,
            MeshParticles,
            GroundPlane,
            StretchBillboard,
            TexturedBillboard
        }
    }
}
