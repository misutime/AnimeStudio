using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class VfxPreviewControl : Control
    {
        private readonly Timer _timer = new() { Interval = 33 };
        private LibraryModelItem _item;
        private Image _texturePreview;
        private string _texturePreviewKey = "";
        private float _time;

        public VfxPreviewControl()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(35, 38, 42);
            ForeColor = Color.WhiteSmoke;
            _timer.Tick += (_, _) =>
            {
                _time += 0.033f;
                if (_time > 1000)
                {
                    _time = 0;
                }
                Invalidate();
            };
        }

        public void SetItem(LibraryModelItem item)
        {
            if (!string.Equals(_texturePreviewKey, item?.StableKey ?? "", StringComparison.Ordinal))
            {
                _texturePreview?.Dispose();
                _texturePreview = null;
                _texturePreviewKey = item?.StableKey ?? "";
            }
            _item = item;
            _time = 0;
            if (item?.IsVfx == true)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _texturePreview?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawBackground(g);

            if (_item == null)
            {
                DrawCenteredText(g, "选择一个 VFX 资源");
                return;
            }

            if (!_item.IsVfx)
            {
                DrawCenteredText(g, "模型动画列表");
                return;
            }

            DrawVfx(g, _item);
            DrawOverlay(g, _item);
        }

        private void DrawBackground(Graphics g)
        {
            using var brush = new LinearGradientBrush(ClientRectangle,
                Color.FromArgb(39, 43, 48),
                Color.FromArgb(18, 20, 23),
                LinearGradientMode.Vertical);
            g.FillRectangle(brush, ClientRectangle);

            using var gridPen = new Pen(Color.FromArgb(36, 255, 255, 255), 1);
            var step = 32;
            for (var x = Width / 2 % step; x < Width; x += step)
            {
                g.DrawLine(gridPen, x, 0, x, Height);
            }
            for (var y = Height / 2 % step; y < Height; y += step)
            {
                g.DrawLine(gridPen, 0, y, Width, y);
            }
        }

        private void DrawVfx(Graphics g, LibraryModelItem item)
        {
            var palette = GetPalette(item);
            var style = DetectStyle(item);
            var center = new PointF(Width * 0.5f, Height * 0.48f);
            var scale = MathF.Max(0.55f, MathF.Min(Width, Height) / 440.0f);
            var hintScale = Math.Clamp(GetHintFloat(item, "shape.m_Radius", 1.0f), 0.45f, 2.2f);
            if (HasHint(item, "main.m_StartSize") || HasHint(item, "main.startSize"))
            {
                hintScale *= Math.Clamp(GetHintFloat(item, "main.m_StartSize", 1.0f), 0.65f, 1.8f);
            }
            scale *= Math.Clamp(hintScale, 0.65f, 1.65f);

            DrawApproximationBadge(g, style, palette);
            if (TryGetTexturePreview(item, out var texture))
            {
                DrawTextureBackedParticles(g, texture, center, scale, palette, style);
                DrawTextureStructureHint(g, center, scale, palette, style);
                return;
            }

            switch (style)
            {
                case VfxStyle.Trail:
                    DrawTrail(g, center, scale, palette);
                    break;
                case VfxStyle.Projectile:
                    DrawProjectile(g, center, scale, palette);
                    break;
                case VfxStyle.Beam:
                    DrawBeam(g, center, scale, palette);
                    break;
                case VfxStyle.Shockwave:
                    DrawShockwave(g, center, scale, palette);
                    break;
                case VfxStyle.Aura:
                    DrawAura(g, center, scale, palette);
                    break;
                case VfxStyle.Smoke:
                    DrawSmoke(g, center, scale, palette);
                    break;
                case VfxStyle.Fire:
                    DrawFire(g, center, scale, palette);
                    break;
                case VfxStyle.Sparks:
                    DrawSparks(g, center, scale, palette);
                    break;
                case VfxStyle.Distortion:
                    DrawDistortion(g, center, scale, palette);
                    break;
                case VfxStyle.MeshParticles:
                    DrawMeshParticles(g, center, scale, palette);
                    break;
                case VfxStyle.GroundPlane:
                    DrawGroundPlane(g, center, scale, palette);
                    break;
                case VfxStyle.StretchBillboard:
                    DrawStretchBillboards(g, center, scale, palette);
                    break;
                case VfxStyle.TexturedBillboard:
                    DrawTexturedBillboards(g, center, scale, palette);
                    break;
                default:
                    DrawEmitter(g, center, scale, palette);
                    break;
            }
        }

        private void DrawMetadataOnly(Graphics g, LibraryModelItem item)
        {
            var palette = GetPalette(item);
            var cardWidth = Math.Min(520, Math.Max(280, Width - 56));
            var cardHeight = 250;
            var rect = new Rectangle((Width - cardWidth) / 2, Math.Max(24, (Height - cardHeight) / 2 - 16), cardWidth, cardHeight);

            using var shadow = new SolidBrush(Color.FromArgb(95, 0, 0, 0));
            using var card = new SolidBrush(Color.FromArgb(224, 24, 28, 33));
            using var border = new Pen(Color.FromArgb(110, palette.Primary), 1.5f);
            using var path = RoundedRect(rect, 10);
            using var shadowPath = RoundedRect(new Rectangle(rect.Left + 5, rect.Top + 8, rect.Width, rect.Height), 10);
            g.FillPath(shadow, shadowPath);
            g.FillPath(card, path);
            g.DrawPath(border, path);

            var iconRect = new Rectangle(rect.Left + 22, rect.Top + 24, 82, 82);
            DrawMetadataIcon(g, iconRect, palette);

            using var titleFont = new Font(FontFamily.GenericSansSerif, 15, FontStyle.Bold);
            using var labelFont = new Font(FontFamily.GenericSansSerif, 9, FontStyle.Bold);
            using var bodyFont = new Font(FontFamily.GenericSansSerif, 9, FontStyle.Regular);
            using var titleBrush = new SolidBrush(Color.WhiteSmoke);
            using var accentBrush = new SolidBrush(palette.Highlight);
            using var mutedBrush = new SolidBrush(Color.FromArgb(200, 214, 220, 226));

            var x = iconRect.Right + 18;
            g.DrawString(Ellipsis(item.Name, 44), titleFont, titleBrush, x, rect.Top + 24);
            g.DrawString("Unity VFX metadata", labelFont, accentBrush, x, rect.Top + 57);
            g.DrawString("不是真实粒子运行时预览", bodyFont, mutedBrush, x, rect.Top + 78);

            var y = rect.Top + 122;
            DrawMetric(g, rect.Left + 24, y, "组件", item.ComponentCount, palette);
            DrawMetric(g, rect.Left + 136, y, "材质引用", item.MaterialRefCount, palette);
            DrawMetric(g, rect.Left + 274, y, "Mesh引用", item.MeshRefCount, palette);
            DrawMetric(g, rect.Left + 400, y, "实例", item.OccurrenceCount, palette);

            var notes = string.IsNullOrWhiteSpace(item.ModelPreviewPath)
                ? "只解析到 ParticleSystem/Renderer/Material/Mesh 引用证据，尚未烘焙 Unity 粒子模块、shader 动画或 VFX Graph。"
                : "存在 Mesh/glTF 预览路径，但右侧面板仍不代表 Unity 运行时粒子效果。";
            using var noteBrush = new SolidBrush(Color.FromArgb(214, 235, 238, 242));
            g.DrawString(Wrap(notes, Math.Max(34, rect.Width / 8)), bodyFont, noteBrush, rect.Left + 24, rect.Top + 180);
        }

        private void DrawTrail(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            var drift = MathF.Sin(_time * 2.4f) * 16 * scale;
            var points = new[]
            {
                new PointF(center.X - 150 * scale, center.Y + 72 * scale + drift),
                new PointF(center.X - 72 * scale, center.Y + 20 * scale - drift * 0.2f),
                new PointF(center.X + 36 * scale, center.Y - 16 * scale + drift * 0.2f),
                new PointF(center.X + 150 * scale, center.Y - 70 * scale - drift),
            };
            using var glow = new Pen(Color.FromArgb(72, palette.Accent), 46 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var body = new Pen(Color.FromArgb(220, palette.Primary), 15 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var core = new Pen(Color.FromArgb(245, palette.Highlight), 4 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawCurve(glow, points, 0.35f);
            g.DrawCurve(body, points, 0.35f);
            g.DrawCurve(core, points, 0.35f);
            DrawParticleCloud(g, center, scale, palette, 26, 160, flatten: 0.45f, outward: false);
        }

        private void DrawProjectile(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            var t = (_time * 0.65f) % 1.0f;
            var x = center.X - 130 * scale + t * 260 * scale;
            var y = center.Y + MathF.Sin(t * MathF.Tau) * 18 * scale;
            using var trail = new Pen(Color.FromArgb(110, palette.Primary), 22 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var core = new SolidBrush(Color.FromArgb(245, palette.Highlight));
            g.DrawLine(trail, x - 95 * scale, y + 18 * scale, x, y);
            g.FillEllipse(core, x - 18 * scale, y - 18 * scale, 36 * scale, 36 * scale);
            DrawParticleCloud(g, new PointF(x - 54 * scale, y + 12 * scale), scale, palette, 22, 92, flatten: 0.55f, outward: false);
        }

        private void DrawBeam(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            var width = 16 + MathF.Sin(_time * 8) * 4;
            using var glow = new Pen(Color.FromArgb(78, palette.Accent), (width + 34) * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var beam = new Pen(Color.FromArgb(225, palette.Primary), width * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var core = new Pen(Color.FromArgb(245, palette.Highlight), 4 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(glow, center.X - 165 * scale, center.Y + 48 * scale, center.X + 165 * scale, center.Y - 48 * scale);
            g.DrawLine(beam, center.X - 165 * scale, center.Y + 48 * scale, center.X + 165 * scale, center.Y - 48 * scale);
            g.DrawLine(core, center.X - 160 * scale, center.Y + 43 * scale, center.X + 160 * scale, center.Y - 43 * scale);
        }

        private void DrawShockwave(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            var t = (_time * 0.9f) % 1.0f;
            for (var i = 0; i < 4; i++)
            {
                var phase = (t + i * 0.22f) % 1.0f;
                var radius = (32 + phase * 170) * scale;
                var alpha = (int)(180 * (1 - phase));
                using var pen = new Pen(Color.FromArgb(Math.Clamp(alpha, 16, 190), i % 2 == 0 ? palette.Primary : palette.Highlight), (9 - phase * 5) * scale);
                g.DrawEllipse(pen, center.X - radius, center.Y - radius * 0.42f, radius * 2, radius * 0.84f);
            }
            DrawRadialLines(g, center, scale, palette, 20, 120, wobble: true);
        }

        private void DrawAura(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            var pulse = 1 + MathF.Sin(_time * 3.6f) * 0.08f;
            for (var i = 0; i < 3; i++)
            {
                var rx = (92 + i * 36) * scale * pulse;
                var ry = (50 + i * 18) * scale * pulse;
                using var pen = new Pen(Color.FromArgb(180 - i * 45, i == 0 ? palette.Highlight : palette.Primary), (7 - i) * scale);
                g.DrawEllipse(pen, center.X - rx, center.Y - ry, rx * 2, ry * 2);
            }
            DrawParticleCloud(g, center, scale, palette, 36, 145, flatten: 0.45f, outward: false);
        }

        private void DrawSmoke(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            var seed = Math.Abs((_item?.StableKey ?? "smoke").GetHashCode(StringComparison.Ordinal));
            for (var i = 0; i < 22; i++)
            {
                var n = Hash01(seed + i * 191);
                var t = (n + _time * (0.06f + Hash01(seed + i * 337) * 0.08f)) % 1.0f;
                var angle = Hash01(seed + i * 557) * MathF.Tau;
                var radius = (20 + t * 126) * scale;
                var x = center.X + MathF.Cos(angle) * radius * 0.75f;
                var y = center.Y + MathF.Sin(angle) * radius * 0.42f - t * 92 * scale;
                var size = (24 + t * 54 + Hash01(seed + i * 719) * 28) * scale;
                using var brush = new SolidBrush(Color.FromArgb((int)(70 * (1 - t * 0.45f)), palette.Primary));
                g.FillEllipse(brush, x - size / 2, y - size / 2, size, size);
            }
        }

        private void DrawFire(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            DrawParticleCloud(g, center, scale, palette, 46, 140, flatten: 0.52f, outward: true);
            for (var i = 0; i < 16; i++)
            {
                var angle = -MathF.PI / 2 + (Hash01(i * 97) - 0.5f) * 0.9f;
                var len = (60 + Hash01(i * 131) * 90 + MathF.Sin(_time * 5 + i) * 10) * scale;
                var x = center.X + (Hash01(i * 199) - 0.5f) * 90 * scale;
                var y = center.Y + 70 * scale;
                using var pen = new Pen(Color.FromArgb(210, i % 2 == 0 ? palette.Primary : palette.Highlight), (4 + Hash01(i * 251) * 7) * scale)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawLine(pen, x, y, x + MathF.Cos(angle) * len, y + MathF.Sin(angle) * len);
            }
        }

        private void DrawSparks(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            DrawRadialLines(g, center, scale, palette, 32, 190, wobble: true);
            DrawParticleCloud(g, center, scale, palette, 42, 185, flatten: 0.72f, outward: true);
        }

        private void DrawDistortion(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            var t = (_time * 0.55f) % 1.0f;
            for (var i = 0; i < 6; i++)
            {
                var phase = (t + i * 0.16f) % 1.0f;
                var rx = (44 + phase * 155) * scale;
                var ry = (28 + phase * 90) * scale;
                using var pen = new Pen(Color.FromArgb((int)(95 * (1 - phase)), i % 2 == 0 ? palette.Highlight : palette.Primary), (3 + MathF.Sin(_time * 4 + i) * 1.2f) * scale);
                g.DrawEllipse(pen, center.X - rx, center.Y - ry, rx * 2, ry * 2);
            }

            using var wave = new Pen(Color.FromArgb(120, palette.Primary), 5 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (var i = 0; i < 5; i++)
            {
                var y = center.Y - 74 * scale + i * 36 * scale;
                var points = Enumerable.Range(0, 8)
                    .Select(j => new PointF(center.X - 150 * scale + j * 43 * scale, y + MathF.Sin(_time * 3 + i + j * 0.9f) * 8 * scale))
                    .ToArray();
                g.DrawCurve(wave, points, 0.55f);
            }
        }

        private void DrawMeshParticles(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            var seed = Math.Abs((_item?.StableKey ?? "mesh").GetHashCode(StringComparison.Ordinal));
            DrawParticleCloud(g, center, scale, palette, 18, 130, flatten: 0.56f, outward: true);
            for (var i = 0; i < 15; i++)
            {
                var phase = (Hash01(seed + i * 37) + _time * (0.08f + Hash01(seed + i * 41) * 0.24f)) % 1.0f;
                var angle = Hash01(seed + i * 53) * MathF.Tau + _time * (0.4f + Hash01(seed + i * 59) * 1.2f);
                var radius = (26 + phase * 142) * scale;
                var x = center.X + MathF.Cos(angle) * radius;
                var y = center.Y + MathF.Sin(angle) * radius * 0.58f;
                var size = (20 + Hash01(seed + i * 67) * 34) * scale;
                using var path = new GraphicsPath();
                for (var c = 0; c < 4; c++)
                {
                    var a = angle + c * MathF.PI / 2 + _time * 0.8f;
                    var p = new PointF(x + MathF.Cos(a) * size, y + MathF.Sin(a) * size * 0.72f);
                    if (c == 0) path.StartFigure();
                    if (c == 0) path.AddLine(p, p);
                    else path.AddLine(path.GetLastPoint(), p);
                }
                path.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(118, i % 2 == 0 ? palette.Primary : palette.Highlight));
                using var pen = new Pen(Color.FromArgb(190, palette.Highlight), 1.5f * scale);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
        }

        private void DrawGroundPlane(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            var pulse = 1 + MathF.Sin(_time * 2.8f) * 0.05f;
            using var shadow = new SolidBrush(Color.FromArgb(48, palette.Accent));
            g.FillEllipse(shadow, center.X - 180 * scale, center.Y - 42 * scale, 360 * scale, 84 * scale);
            for (var i = 0; i < 4; i++)
            {
                var rx = (72 + i * 38) * scale * pulse;
                var ry = (20 + i * 12) * scale * pulse;
                using var pen = new Pen(Color.FromArgb(185 - i * 34, i % 2 == 0 ? palette.Primary : palette.Highlight), (6 - i) * scale);
                g.DrawEllipse(pen, center.X - rx, center.Y - ry, rx * 2, ry * 2);
            }
            DrawParticleCloud(g, center, scale, palette, 48, 180, flatten: 0.24f, outward: false);
        }

        private void DrawStretchBillboards(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            var seed = Math.Abs((_item?.StableKey ?? "stretch").GetHashCode(StringComparison.Ordinal));
            for (var i = 0; i < 34; i++)
            {
                var phase = (Hash01(seed + i * 101) + _time * (0.18f + Hash01(seed + i * 103) * 0.4f)) % 1.0f;
                var angle = Hash01(seed + i * 107) * MathF.Tau;
                var radius = (18 + phase * 180) * scale;
                var x = center.X + MathF.Cos(angle) * radius;
                var y = center.Y + MathF.Sin(angle) * radius * 0.62f;
                var len = (28 + Hash01(seed + i * 109) * 86) * scale;
                using var pen = new Pen(Color.FromArgb((int)(210 * (1 - phase * 0.42f)), i % 3 == 0 ? palette.Highlight : palette.Primary), (3 + Hash01(seed + i * 113) * 6) * scale)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawLine(pen, x - MathF.Cos(angle) * len, y - MathF.Sin(angle) * len * 0.5f, x, y);
            }
        }

        private void DrawTexturedBillboards(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            if (TryGetTexturePreview(_item, out var texture))
            {
                DrawTextureBackedParticles(g, texture, center, scale, palette, VfxStyle.TexturedBillboard);
                return;
            }

            var seed = Math.Abs((_item?.StableKey ?? "billboard").GetHashCode(StringComparison.Ordinal));
            var textureBias = _item?.TextureRefCount > 0 ? 1.25f : 1.0f;
            for (var i = 0; i < 24; i++)
            {
                var phase = (Hash01(seed + i * 149) + _time * (0.08f + Hash01(seed + i * 151) * 0.18f)) % 1.0f;
                var angle = Hash01(seed + i * 157) * MathF.Tau;
                var radius = (20 + phase * 140) * scale;
                var x = center.X + MathF.Cos(angle) * radius;
                var y = center.Y + MathF.Sin(angle) * radius * 0.65f;
                var size = (16 + Hash01(seed + i * 163) * 34) * scale * textureBias;
                using var path = RoundedRect(new Rectangle((int)(x - size), (int)(y - size), (int)(size * 2), (int)(size * 2)), Math.Max(4, (int)(size * 0.25f)));
                using var fill = new SolidBrush(Color.FromArgb((int)(130 * (1 - phase * 0.2f)), i % 2 == 0 ? palette.Primary : palette.Highlight));
                using var border = new Pen(Color.FromArgb(170, palette.Highlight), 1.2f * scale);
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using var atlasPen = new Pen(Color.FromArgb(110, palette.Highlight), 1);
            var atlas = new RectangleF(center.X - 58 * scale, center.Y - 58 * scale, 116 * scale, 116 * scale);
            g.DrawRectangle(atlasPen, atlas.X, atlas.Y, atlas.Width, atlas.Height);
            g.DrawLine(atlasPen, atlas.Left + atlas.Width / 2, atlas.Top, atlas.Left + atlas.Width / 2, atlas.Bottom);
            g.DrawLine(atlasPen, atlas.Left, atlas.Top + atlas.Height / 2, atlas.Right, atlas.Top + atlas.Height / 2);
        }

        private void DrawTextureBackedParticles(Graphics g, Image texture, PointF center, float scale, VfxPalette palette, VfxStyle style)
        {
            var seed = Math.Abs((_item?.StableKey ?? "texture").GetHashCode(StringComparison.Ordinal));
            var count = style switch
            {
                VfxStyle.Trail or VfxStyle.Beam => 10,
                VfxStyle.GroundPlane or VfxStyle.Aura => 16,
                VfxStyle.MeshParticles => 18,
                VfxStyle.Fire or VfxStyle.Sparks => 24,
                _ => 20,
            };

            using var attributes = new ImageAttributes();
            var matrix = CreateAlphaMatrix(0.72f);
            attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

            for (var i = 0; i < count; i++)
            {
                var phase = (Hash01(seed + i * 197) + _time * (0.06f + Hash01(seed + i * 211) * 0.18f)) % 1.0f;
                var angle = Hash01(seed + i * 223) * MathF.Tau + _time * (0.15f + Hash01(seed + i * 227) * 0.55f);
                var radius = style switch
                {
                    VfxStyle.Trail or VfxStyle.Beam => 42 + i * 13,
                    VfxStyle.GroundPlane or VfxStyle.Aura => 80 + MathF.Sin(i) * 28,
                    _ => 22 + phase * 145,
                };
                var x = center.X + MathF.Cos(angle) * radius * scale;
                var y = center.Y + MathF.Sin(angle) * radius * scale * (style == VfxStyle.GroundPlane ? 0.26f : 0.62f);
                var size = (28 + Hash01(seed + i * 229) * 72) * scale;
                if (style == VfxStyle.Trail || style == VfxStyle.Beam)
                {
                    x = center.X - 155 * scale + i * 31 * scale;
                    y = center.Y + 62 * scale - i * 13 * scale + MathF.Sin(_time * 2.5f + i) * 8 * scale;
                    size *= 0.82f;
                }

                var rect = new Rectangle((int)(x - size / 2), (int)(y - size / 2), Math.Max(2, (int)size), Math.Max(2, (int)size));
                var state = g.Save();
                g.TranslateTransform(x, y);
                g.RotateTransform((angle * 180f / MathF.PI) + _time * 28f + i * 7f);
                g.TranslateTransform(-x, -y);
                g.DrawImage(texture, rect, 0, 0, texture.Width, texture.Height, GraphicsUnit.Pixel, attributes);
                g.Restore(state);
            }

            using var glow = new SolidBrush(Color.FromArgb(28, palette.Accent));
            g.FillEllipse(glow, center.X - 150 * scale, center.Y - 105 * scale, 300 * scale, 210 * scale);
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

        private static void DrawTextureStructureHint(Graphics g, PointF center, float scale, VfxPalette palette, VfxStyle style)
        {
            using var pen = new Pen(Color.FromArgb(84, palette.Highlight), Math.Max(1.0f, 2.0f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            switch (style)
            {
                case VfxStyle.Trail:
                case VfxStyle.Beam:
                    g.DrawCurve(pen, new[]
                    {
                        new PointF(center.X - 145 * scale, center.Y + 66 * scale),
                        new PointF(center.X - 68 * scale, center.Y + 18 * scale),
                        new PointF(center.X + 38 * scale, center.Y - 18 * scale),
                        new PointF(center.X + 142 * scale, center.Y - 64 * scale),
                    }, 0.35f);
                    break;
                case VfxStyle.Aura:
                case VfxStyle.GroundPlane:
                    g.DrawEllipse(pen, center.X - 130 * scale, center.Y - 44 * scale, 260 * scale, 88 * scale);
                    break;
                case VfxStyle.Shockwave:
                case VfxStyle.Sparks:
                    for (var i = 0; i < 10; i++)
                    {
                        var angle = i * MathF.Tau / 10.0f;
                        var inner = 24 * scale;
                        var outer = 118 * scale;
                        g.DrawLine(
                            pen,
                            center.X + MathF.Cos(angle) * inner,
                            center.Y + MathF.Sin(angle) * inner * 0.72f,
                            center.X + MathF.Cos(angle) * outer,
                            center.Y + MathF.Sin(angle) * outer * 0.72f);
                    }
                    break;
            }
        }

        private void DrawEmitter(Graphics g, PointF center, float scale, VfxPalette palette)
        {
            using var core = new SolidBrush(Color.FromArgb(220, palette.Primary));
            using var light = new SolidBrush(Color.FromArgb(245, palette.Highlight));
            g.FillEllipse(core, center.X - 24 * scale, center.Y - 24 * scale, 48 * scale, 48 * scale);
            g.FillEllipse(light, center.X - 10 * scale, center.Y - 10 * scale, 20 * scale, 20 * scale);
            var maxParticles = Math.Clamp(GetHintInt(_item, "main.m_MaxParticles", 58), 24, 180);
            var rate = Math.Clamp(GetHintFloat(_item, "emission.scalar", 1.0f), 0.55f, 2.2f);
            DrawParticleCloud(g, center, scale, palette, Math.Min(120, (int)(maxParticles * 0.42f)), 125 + rate * 42, flatten: 0.7f, outward: true);
        }

        private void DrawParticleCloud(Graphics g, PointF center, float scale, VfxPalette palette, int count, float spread, float flatten, bool outward)
        {
            var seed = Math.Abs((_item?.StableKey ?? "vfx").GetHashCode(StringComparison.Ordinal));
            for (var i = 0; i < count; i++)
            {
                var n = Hash01(seed + i * 977);
                var n2 = Hash01(seed + i * 1319);
                var phase = outward ? (n + _time * (0.25f + n2 * 0.5f)) % 1.0f : n;
                var angle = n2 * MathF.Tau + _time * (0.12f - n * 0.24f);
                var radius = (18 + phase * spread) * scale;
                var x = center.X + MathF.Cos(angle) * radius;
                var y = center.Y + MathF.Sin(angle) * radius * flatten - phase * (outward ? 24 : 8) * scale;
                var size = (2.5f + Hash01(seed + i * 1777) * 7.5f) * scale * (1.0f - phase * 0.25f);
                var alpha = (int)(210 * (1.0f - phase * 0.38f));
                using var brush = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 34, 230), i % 4 == 0 ? palette.Highlight : palette.Primary));
                g.FillEllipse(brush, x - size, y - size, size * 2, size * 2);
            }
        }

        private void DrawRadialLines(Graphics g, PointF center, float scale, VfxPalette palette, int count, float length, bool wobble)
        {
            var seed = Math.Abs((_item?.StableKey ?? "lines").GetHashCode(StringComparison.Ordinal));
            var t = (_time * 1.1f) % 1.0f;
            for (var i = 0; i < count; i++)
            {
                var angle = i * MathF.Tau / count + Hash01(seed + i * 17) * 0.28f;
                var phase = wobble ? (t + Hash01(seed + i * 71)) % 1.0f : 0.6f;
                var inner = 20 + phase * 28;
                var outer = 48 + phase * length;
                using var pen = new Pen(Color.FromArgb((int)(210 * (1 - phase * 0.4f)), i % 3 == 0 ? palette.Highlight : palette.Primary), (3 + Hash01(seed + i * 23) * 5) * scale)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawLine(
                    pen,
                    center.X + MathF.Cos(angle) * inner * scale,
                    center.Y + MathF.Sin(angle) * inner * scale,
                    center.X + MathF.Cos(angle) * outer * scale,
                    center.Y + MathF.Sin(angle) * outer * scale);
            }
        }

        private void DrawApproximationBadge(Graphics g, VfxStyle style, VfxPalette palette)
        {
            var text = $"approx {style}";
            using var font = new Font(FontFamily.GenericSansSerif, 8, FontStyle.Bold);
            using var fill = new SolidBrush(Color.FromArgb(190, 12, 14, 16));
            using var brush = new SolidBrush(palette.Highlight);
            var rect = new Rectangle(12, 12, Math.Min(170, Math.Max(92, (int)g.MeasureString(text, font).Width + 18)), 26);
            using var path = RoundedRect(rect, 6);
            g.FillPath(fill, path);
            g.DrawString(text, font, brush, rect.Left + 9, rect.Top + 6);
        }

        private void DrawOverlay(Graphics g, LibraryModelItem item)
        {
            var category = string.IsNullOrWhiteSpace(item.VfxCategory) ? "VFX" : item.VfxCategory;
            var status = !string.IsNullOrWhiteSpace(item.ModelPreviewPath)
                ? "approx preview + mesh evidence, not Unity runtime"
                : HasVfxHints(item)
                    ? "approx preview from decoded ParticleSystem hints"
                    : "approx preview from Unity metadata, not Unity runtime";
            var lines = new[]
            {
                item.Name,
                $"{category} | components {item.ComponentCount} | materials {item.MaterialRefCount} | textures {item.TextureRefCount} | meshes {item.MeshRefCount} | x{item.OccurrenceCount}",
                status
            };

            using var font = new Font(FontFamily.GenericSansSerif, 9, FontStyle.Regular);
            using var titleFont = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold);
            using var bg = new SolidBrush(Color.FromArgb(190, 12, 14, 16));
            using var text = new SolidBrush(Color.WhiteSmoke);
            var rect = new Rectangle(12, Height - 76, Math.Max(100, Width - 24), 64);
            using var path = RoundedRect(rect, 8);
            g.FillPath(bg, path);
            g.DrawString(Ellipsis(lines[0], 58), titleFont, text, rect.Left + 10, rect.Top + 8);
            g.DrawString(Ellipsis(lines[1], 78), font, text, rect.Left + 10, rect.Top + 29);
            g.DrawString(lines[2], font, text, rect.Left + 10, rect.Top + 47);
        }

        private static void DrawCenteredText(Graphics g, string text)
        {
            using var font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold);
            using var brush = new SolidBrush(Color.FromArgb(210, 230, 235, 240));
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, (g.VisibleClipBounds.Width - size.Width) / 2, (g.VisibleClipBounds.Height - size.Height) / 2);
        }

        private static VfxPalette GetPalette(LibraryModelItem item)
        {
            var text = GetSearchText(item);
            if (ContainsAny(text, "blood")) return new VfxPalette(Color.FromArgb(182, 32, 42), Color.FromArgb(255, 155, 120), Color.FromArgb(78, 10, 18));
            if (ContainsAny(text, "fire", "flame", "explosion", "impact")) return new VfxPalette(Color.FromArgb(255, 118, 40), Color.FromArgb(255, 230, 91), Color.FromArgb(128, 32, 18));
            if (ContainsAny(text, "ice", "water", "frost")) return new VfxPalette(Color.FromArgb(74, 190, 255), Color.FromArgb(218, 249, 255), Color.FromArgb(21, 74, 113));
            if (ContainsAny(text, "lightning", "beam", "laser", "spark")) return new VfxPalette(Color.FromArgb(158, 123, 255), Color.FromArgb(240, 236, 255), Color.FromArgb(49, 38, 118));
            if (ContainsAny(text, "poison", "aura", "buff", "debuff", "zone", "field")) return new VfxPalette(Color.FromArgb(66, 226, 134), Color.FromArgb(212, 255, 168), Color.FromArgb(24, 84, 58));
            if (ContainsAny(text, "smoke", "fog", "dust")) return new VfxPalette(Color.FromArgb(159, 168, 174), Color.FromArgb(238, 241, 243), Color.FromArgb(55, 61, 64));
            return new VfxPalette(Color.FromArgb(94, 178, 255), Color.FromArgb(255, 255, 255), Color.FromArgb(28, 62, 108));
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

        private static bool HasVfxHints(LibraryModelItem item)
        {
            return !string.IsNullOrWhiteSpace(item?.VfxPreviewHintsJson)
                && item.VfxPreviewHintsJson != "{}";
        }

        private bool TryGetTexturePreview(LibraryModelItem item, out Image image)
        {
            image = null;
            if (item?.VfxTexturePreviewPaths == null || item.VfxTexturePreviewPaths.Length == 0)
            {
                return false;
            }

            if (_texturePreview != null)
            {
                image = _texturePreview;
                return true;
            }

            foreach (var path in item.VfxTexturePreviewPaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        _texturePreview = Image.FromFile(path);
                        image = _texturePreview;
                        return true;
                    }
                }
                catch
                {
                    // Bad or unsupported texture preview; try the next one and keep the VFX browser responsive.
                }
            }

            return false;
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

        private static bool HasHint(LibraryModelItem item, string key)
        {
            return TryFindHint(item, key, out _);
        }

        private static int GetHintInt(LibraryModelItem item, string key, int fallback)
        {
            if (!TryFindHint(item, key, out var value))
            {
                return fallback;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            {
                return intValue;
            }
            if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            {
                return value.GetBoolean() ? 1 : 0;
            }
            return fallback;
        }

        private static bool GetHintBool(LibraryModelItem item, string key, bool fallback)
        {
            if (!TryFindHint(item, key, out var value))
            {
                return fallback;
            }
            if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            {
                return value.GetBoolean();
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            {
                return intValue != 0;
            }
            return fallback;
        }

        private static float GetHintFloat(LibraryModelItem item, string key, float fallback)
        {
            if (!TryFindHint(item, key, out var value))
            {
                return fallback;
            }
            return value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var floatValue)
                ? floatValue
                : fallback;
        }

        private static bool TryFindHint(LibraryModelItem item, string key, out JsonElement value)
        {
            value = default;
            if (!TryGetHints(item, out var root))
            {
                return false;
            }
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)
                    || property.Name.EndsWith("." + key, StringComparison.OrdinalIgnoreCase)
                    || key.EndsWith("." + property.Name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            return false;
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

        private static void DrawMetadataIcon(Graphics g, Rectangle rect, VfxPalette palette)
        {
            using var bg = new SolidBrush(Color.FromArgb(48, palette.Accent));
            using var pen = new Pen(Color.FromArgb(225, palette.Primary), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var thin = new Pen(Color.FromArgb(190, palette.Highlight), 2);
            g.FillEllipse(bg, rect);
            g.DrawEllipse(pen, rect);
            var cx = rect.Left + rect.Width / 2;
            var cy = rect.Top + rect.Height / 2;
            g.DrawLine(pen, cx - 22, cy, cx + 22, cy);
            g.DrawLine(pen, cx, cy - 22, cx, cy + 22);
            g.DrawEllipse(thin, cx - 10, cy - 10, 20, 20);
        }

        private static void DrawMetric(Graphics g, int x, int y, string label, int value, VfxPalette palette)
        {
            using var numberFont = new Font(FontFamily.GenericSansSerif, 17, FontStyle.Bold);
            using var labelFont = new Font(FontFamily.GenericSansSerif, 8, FontStyle.Regular);
            using var numberBrush = new SolidBrush(palette.Highlight);
            using var labelBrush = new SolidBrush(Color.FromArgb(190, 214, 220, 226));
            g.DrawString(value.ToString(), numberFont, numberBrush, x, y);
            g.DrawString(label, labelFont, labelBrush, x + 2, y + 31);
        }

        private static bool ContainsAny(string text, params string[] tokens)
        {
            return tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private static string Ellipsis(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= max)
            {
                return value ?? "";
            }
            return value[..Math.Max(1, max - 3)] + "...";
        }

        private static string Wrap(string value, int maxLineLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLineLength)
            {
                return value ?? "";
            }

            var chunks = Enumerable.Range(0, (value.Length + maxLineLength - 1) / maxLineLength)
                .Select(i => value.Substring(i * maxLineLength, Math.Min(maxLineLength, value.Length - i * maxLineLength)));
            return string.Join(Environment.NewLine, chunks);
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
