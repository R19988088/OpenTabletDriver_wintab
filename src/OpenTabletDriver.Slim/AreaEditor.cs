using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 图形化的区域选择器（等同于原版 OTD 里那块可以直接拖的面板）。
    ///
    /// 画布表示"整块画布"（显示器是屏幕像素、数位板是毫米），里面的矩形就是要映射的区域：
    ///   · 拖矩形内部 = 平移
    ///   · 拖四个角 = 以中心缩放（锁定比例时保持宽高比）
    /// 不需要输入任何数字，松手即写回配置。
    /// </summary>
    public sealed class AreaEditor : Control
    {
        private const double Pad = 18;
        private const double HandleSize = 5;
        private const double HitRadius = 14;

        public AreaEditor(bool allowRotation)
        {
            AllowRotation = allowRotation;
            MinHeight = 150;
            Cursor = new Cursor(StandardCursorType.Hand);
            PointerPressed += OnPressed;
            PointerMoved += OnMoved;
            PointerReleased += OnReleased;
        }

        public bool AllowRotation { get; }

        /// <summary>整块画布的尺寸（显示器像素 / 数位板毫米）。</summary>
        public double FullWidth { get; private set; } = 100;
        public double FullHeight { get; private set; } = 100;

        /// <summary>当前选择的区域（中心坐标 + 宽高 + 旋转角）。</summary>
        public double AreaWidth { get; private set; } = 100;
        public double AreaHeight { get; private set; } = 100;
        public double CenterX { get; private set; } = 50;
        public double CenterY { get; private set; } = 50;
        public double Rotation { get; private set; }

        /// <summary>锁定比例时目标宽高比（宽/高），仅对可旋转的画布（数位板）有意义。</summary>
        public bool LockAspect { get; set; }

        /// <summary>锁定比例时不允许在下面这块画布上单独拉伸，只能整体平移。</summary>
        public bool AllowResize { get; set; } = true;
        public double AspectRatio { get; set; } = 1.0;

        public event Action? Changed;

        /// <summary>背景分区（显示器）：按虚拟桌面坐标画成一格格独立的屏幕。</summary>
        public sealed class Region
        {
            public double X { get; init; }
            public double Y { get; init; }
            public double Width { get; init; }
            public double Height { get; init; }
            public string Label { get; init; } = string.Empty;
            public bool Primary { get; init; }
        }

        private readonly List<Region> _regions = new();

        public void SetRegions(IEnumerable<Region> regions)
        {
            _regions.Clear();
            _regions.AddRange(regions);
            InvalidateVisual();
        }

        private enum Grip { None, Body, Corner, Rotate }

        private Grip _grip = Grip.None;
        private double _startX, _startY, _startCx, _startCy;

        // 拖角时固定不动的对角锚点（域坐标），以及被抓角的方向
        private double _anchorX, _anchorY;
        private int _cornerX = 1, _cornerY = 1;

        // ---- 坐标换算 ----

        private double Scale(out double originX, out double originY)
        {
            var availW = Math.Max(1, Bounds.Width - Pad * 2);
            var availH = Math.Max(1, Bounds.Height - Pad * 2);
            var scale = Math.Min(availW / FullWidth, availH / FullHeight);
            originX = Pad + (availW - FullWidth * scale) / 2.0;
            originY = Pad + (availH - FullHeight * scale) / 2.0;
            return scale;
        }

        private Point ToScreen(double x, double y, double scale, double ox, double oy) =>
            new(ox + x * scale, oy + y * scale);

        private static Point Rotate(double x, double y, double degrees)
        {
            var rad = degrees * Math.PI / 180.0;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);
            return new Point(x * cos - y * sin, x * sin + y * cos);
        }

        // ---- 绘制 ----

        public override void Render(DrawingContext ctx)
        {
            var scale = Scale(out var ox, out var oy);

            if (_regions.Count == 0)
            {
                var hostRect = new Rect(ToScreen(0, 0, scale, ox, oy), new Size(FullWidth * scale, FullHeight * scale));
                ctx.DrawRectangle(
                    new SolidColorBrush(Color.Parse("#F7F7F9")),
                    new Pen(Palette.PadBorderBrush, 1),
                    new RoundedRect(hostRect, new CornerRadius(8)),
                    default);
            }
            else
            {
                // 每块屏都画出来：这样能一眼看出"映到哪块屏"，也能跨屏拖选区
                foreach (var region in _regions)
                {
                    var rect = new Rect(
                        ToScreen(region.X, region.Y, scale, ox, oy),
                        new Size(region.Width * scale, region.Height * scale));

                    ctx.DrawRectangle(
                        new SolidColorBrush(Colors.White),
                        new Pen(region.Primary ? Palette.PadBorderBrush : Palette.SeparatorBrush, 1),
                        new RoundedRect(rect, new CornerRadius(4)),
                        default);

                    if (rect.Width > 40 && rect.Height > 20)
                    {
                        var text = new FormattedText(
                            region.Label,
                            System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            Typeface.Default,
                            10,
                            Palette.InkDimBrush);
                        ctx.DrawText(text, new Point(rect.X + 6, rect.Y + 4));
                    }
                }
            }

            var center = ToScreen(CenterX, CenterY, scale, ox, oy);
            var w = AreaWidth * scale;
            var h = AreaHeight * scale;

            using (ctx.PushTransform(Matrix.CreateRotation(-Rotation * Math.PI / 180.0) *
                                     Matrix.CreateTranslation(center.X, center.Y)))
            {
                var rect = new Rect(-w / 2, -h / 2, w, h);

                ctx.DrawRectangle(
                    new SolidColorBrush(Color.Parse("#1A0A84FF")),
                    new Pen(Palette.AccentBrush, 1.6),
                    new RoundedRect(rect, new CornerRadius(2)),
                    default);

                foreach (var corner in AllowResize
                    ? new[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft }
                    : Array.Empty<Point>())
                {
                    var handleRect = new Rect(corner.X - HandleSize, corner.Y - HandleSize, HandleSize * 2, HandleSize * 2);
                    ctx.DrawRectangle(
                        new SolidColorBrush(Color.Parse("#F7F7F9")),
                        new Pen(Palette.AccentBrush, 1.6),
                        new RoundedRect(handleRect, new CornerRadius(2)),
                        default);
                }
            }
        }

        // ---- 交互 ----

        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            var scale = Scale(out var ox, out var oy);
            var p = e.GetPosition(this);

            _startX = p.X;
            _startY = p.Y;
            _startCx = CenterX;
            _startCy = CenterY;

            // 命中判定要在屏幕像素里算：缩放后 1 个屏幕像素可能对应很多毫米，
            // 换成域单位算的话整个矩形都会被判成"按到了角"。
            var center = ToScreen(CenterX, CenterY, scale, ox, oy);
            var rotated = Rotate(p.X - center.X, p.Y - center.Y, Rotation);
            var halfW = AreaWidth * scale / 2.0;
            var halfH = AreaHeight * scale / 2.0;

            if (Math.Abs(Math.Abs(rotated.X) - halfW) < HitRadius && Math.Abs(Math.Abs(rotated.Y) - halfH) < HitRadius)
            {
                _grip = AllowResize && InsideArea(rotated.X, rotated.Y, halfW, halfH) ? Grip.Corner : Grip.Body;

                // 抓的是哪个角：对角固定不动，只让被抓的这个角跟着指针走
                _cornerX = rotated.X > 0 ? 1 : -1;
                _cornerY = rotated.Y > 0 ? 1 : -1;
                _anchorX = CenterX - _cornerX * AreaWidth / 2.0;
                _anchorY = CenterY - _cornerY * AreaHeight / 2.0;
            }
            else if (Math.Abs(rotated.X) <= halfW && Math.Abs(rotated.Y) <= halfH)
            {
                _grip = Grip.Body;
            }
            else
            {
                _grip = Grip.None;
            }

            if (_grip != Grip.None)
                e.Pointer.Capture(this);
        }

        private static bool InsideArea(double x, double y, double halfW, double halfH)
            => Math.Abs(x) <= halfW + 4 && Math.Abs(y) <= halfH + 4;

        private void OnMoved(object? sender, PointerEventArgs e)
        {
            if (e.Pointer.Captured != this || _grip == Grip.None)
                return;

            var scale = Scale(out var ox, out var oy);
            var p = e.GetPosition(this);

            if (_grip == Grip.Body)
            {
                CenterX = _startCx + (p.X - _startX) / scale;
                CenterY = _startCy + (p.Y - _startY) / scale;
                SnapMove(scale);
                ClampIntoBounds();
            }
            else if (_grip == Grip.Corner)
            {
                // 以"对角"为锚点缩放：只有被抓的那个角动，另外三条边都不动。
                // 指针先吸附到屏幕边缘，这样拖到屏幕边上正好贴齐。
                var px = (p.X - ox) / scale;
                var py = (p.Y - oy) / scale;
                px = SnapValue(px, _snapX, SnapPixels / Math.Max(scale, 1e-6));
                py = SnapValue(py, _snapY, SnapPixels / Math.Max(scale, 1e-6));

                var w = Math.Abs(px - _anchorX);
                var h = Math.Abs(py - _anchorY);

                if (LockAspect && AspectRatio > 0)
                {
                    if (w / AspectRatio >= h)
                        h = w / AspectRatio;
                    else
                        w = h * AspectRatio;
                }

                AreaWidth = Math.Min(Math.Max(6, w), FullWidth);
                AreaHeight = Math.Min(Math.Max(6, h), FullHeight);

                // 锚点不动，方向跟着指针走
                CenterX = _anchorX + (px >= _anchorX ? 1 : -1) * AreaWidth / 2.0;
                CenterY = _anchorY + (py >= _anchorY ? 1 : -1) * AreaHeight / 2.0;

                ClampIntoBounds();
            }

            // 拖动过程中只重绘，不写配置、也不热更新映射：
            // 笔在动的同时改映射会让光标自己推自己，看起来就是抖。松手再提交。
            InvalidateVisual();
        }

        private void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.Pointer.Captured != this)
                return;

            e.Pointer.Capture(null);
            var moved = _grip != Grip.None;
            _grip = Grip.None;

            if (moved)
            {
                Changed?.Invoke();
                InvalidateVisual();
            }
        }

        private void ClampIntoBounds()
        {
            AreaWidth = Math.Min(AreaWidth, FullWidth);
            AreaHeight = Math.Min(AreaHeight, FullHeight);
            CenterX = Math.Clamp(CenterX, AreaWidth / 2.0, FullWidth - AreaWidth / 2.0);
            CenterY = Math.Clamp(CenterY, AreaHeight / 2.0, FullHeight - AreaHeight / 2.0);
        }

        // ---- 吸附到屏幕边缘 ----

        private const double SnapPixels = 10;

        private readonly List<double> _snapX = new();
        private readonly List<double> _snapY = new();

        /// <summary>
        /// 把这一组坐标当成"屏幕边缘"（画布域单位）：拖到附近就贴上去，
        /// 所以拖到屏幕边界时会正好铺满、不会越界；要跨屏就往下一个屏幕边缘继续拖。
        /// </summary>
        public void SetSnapEdges(IEnumerable<double> xs, IEnumerable<double> ys)
        {
            _snapX.Clear();
            _snapX.AddRange(xs);
            _snapY.Clear();
            _snapY.AddRange(ys);
        }

        private void SnapMove(double scale)
        {
            if (_snapX.Count == 0 && _snapY.Count == 0)
                return;

            var tol = SnapPixels / Math.Max(scale, 1e-6);
            var halfW = AreaWidth / 2.0;
            var halfH = AreaHeight / 2.0;

            var bestX = tol;
            var dx = 0.0;
            foreach (var target in _snapX)
            {
                var a = target - (CenterX - halfW);
                var b = target - (CenterX + halfW);

                if (Math.Abs(a) < bestX)
                {
                    bestX = Math.Abs(a);
                    dx = a;
                }

                if (Math.Abs(b) < bestX)
                {
                    bestX = Math.Abs(b);
                    dx = b;
                }
            }

            var bestY = tol;
            var dy = 0.0;
            foreach (var target in _snapY)
            {
                var a = target - (CenterY - halfH);
                var b = target - (CenterY + halfH);

                if (Math.Abs(a) < bestY)
                {
                    bestY = Math.Abs(a);
                    dy = a;
                }

                if (Math.Abs(b) < bestY)
                {
                    bestY = Math.Abs(b);
                    dy = b;
                }
            }

            CenterX += dx;
            CenterY += dy;
        }

        private static double SnapValue(double value, List<double> targets, double tolerance)
        {
            var best = tolerance;
            var result = value;

            foreach (var target in targets)
            {
                if (Math.Abs(target - value) < best)
                {
                    best = Math.Abs(target - value);
                    result = target;
                }
            }

            return result;
        }
        // ---- 外部设置 ----

        public void SetFull(double width, double height)
        {
            FullWidth = Math.Max(1, width);
            FullHeight = Math.Max(1, height);
            InvalidateVisual();
        }

        public void SetArea(double width, double height, double cx, double cy, double rotation = 0)
        {
            AreaWidth = Math.Max(1, width);
            AreaHeight = Math.Max(1, height);
            CenterX = cx;
            CenterY = cy;
            Rotation = rotation;
            ClampIntoBounds();
            InvalidateVisual();
        }
    }
}
