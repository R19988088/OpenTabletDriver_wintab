using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 按键页左边那支笔：几何 1:1 取自 icons8 的 Wacom 图标（图标里斜放的那支笔），只做两件事——
    /// 转成竖直（笔尖朝下），以及把笔杆中间那段按原图切片上下拉长，笔尖和尾帽的比例一点不动。
    /// 四个蓝点标出可映射的位置。
    /// </summary>
    public sealed class PenView : Control
    {
        private const double Total = 400;       // 画出来的笔总长
        private const double Scale = 4.9;       // 图标单位的等比缩放（宽度随之而来，不额外压扁）
        private const double SliceStart = 0.26; // 拉长用的那一段（占笔长的比例），落在浅色笔杆上、侧键之上
        private const double SliceEnd = 0.32;

        // 图标里那个侧键：中心、沿笔身方向放大几倍（高度 3 倍），再从中间断成上下两键
        private const double ButtonCx = 33.3;
        private const double ButtonCy = 34.3;
        private const double SideButtonStretch = 3;

        // 图标坐标里这支笔的包围盒中心
        private const double PivotX = 34.5;
        private const double PivotY = 35.6;

        private const string Silhouette = "M 21.65625 22 C 21.484375 22 21.316406 22.054688 21.1875 22.1875 " +
            "C 20.925781 22.449219 20.925781 22.894531 21.1875 23.15625 C 21.226563 23.195313 21.523438 23.492188 21.8125 23.78125 " +
            "L 21.6875 23.90625 C 21.398438 24.191406 21.3125 24.625 21.46875 25 L 24.09375 31.46875 " +
            "C 24.214844 31.746094 24.457031 31.953125 24.75 32.03125 C 24.886719 32.074219 24.9375 32.105469 25 32.125 " +
            "C 25.152344 32.171875 25.363281 32.21875 25.59375 32.3125 C 26.058594 32.5 26.601563 32.820313 26.84375 33.0625 " +
            "L 40.75 46.9375 C 41.066406 47.253906 41.5625 47.457031 42 47.4375 C 42.183594 47.429688 42.351563 47.394531 42.5 47.34375 " +
            "L 43.34375 48.1875 C 44.402344 49.246094 46.128906 49.246094 47.1875 48.1875 " +
            "C 48.246094 47.128906 48.246094 45.402344 47.1875 44.34375 L 46.34375 43.5 " +
            "C 46.394531 43.351563 46.429688 43.183594 46.4375 43 C 46.457031 42.5625 46.253906 42.035156 45.9375 41.71875 " +
            "L 32.0625 27.84375 C 31.53125 27.3125 31.0625 25.78125 31.0625 25.78125 " +
            "C 31.019531 25.617188 30.933594 25.464844 30.8125 25.34375 C 30.667969 25.203125 30.574219 25.140625 30.46875 25.09375 " +
            "L 24 22.46875 C 23.84375 22.394531 23.671875 22.363281 23.5 22.375 " +
            "C 23.273438 22.40625 23.0625 22.519531 22.90625 22.6875 L 22.78125 22.8125 L 22.15625 22.1875 " +
            "C 22.023438 22.054688 21.828125 22 21.65625 22 Z";

        private const string NibFacet = "M 23.84375 24.5625 L 28.3125 26.375 L 25.375 29.3125 L 23.5625 24.84375 Z";

        private const string BodyBand = "M 29.71875 27.78125 C 29.953125 28.273438 30.171875 28.796875 30.625 29.25 " +
            "L 44.375 43 C 44.339844 43.070313 44.308594 43.113281 44.28125 43.15625 " +
            "C 44.085938 43.417969 43.707031 43.792969 43.25 44.25 C 42.878906 44.621094 42.566406 44.90625 42.3125 45.125 " +
            "C 42.1875 45.164063 42.070313 45.226563 41.96875 45.3125 L 28.25 31.625 " +
            "C 27.789063 31.164063 27.277344 30.941406 26.78125 30.71875 Z";

        private const string SideButton = "M 32 32.125 C 31.75 32.101563 31.519531 32.167969 31.34375 32.34375 " +
            "C 30.996094 32.691406 31.050781 33.300781 31.46875 33.71875 C 31.609375 33.859375 33.609375 35.859375 33.75 36 " +
            "C 34.167969 36.417969 34.777344 36.472656 35.125 36.125 " +
            "C 35.476563 35.777344 35.417969 35.167969 35 34.75 L 32.71875 32.46875 " +
            "C 32.507813 32.257813 32.25 32.148438 32 32.125 Z";

        private const string TailDot = "M 45.1875 45.15625 L 45.78125 45.75 C 46.074219 46.042969 46.074219 46.488281 45.78125 46.78125 " +
            "C 45.488281 47.074219 45.042969 47.074219 44.75 46.78125 L 44.15625 46.1875 " +
            "C 44.332031 46.023438 44.507813 45.867188 44.6875 45.6875 C 44.867188 45.507813 45.023438 45.332031 45.1875 45.15625 Z";

        private static readonly Geometry SilhouetteGeometry = StreamGeometry.Parse(Silhouette);
        private static readonly Geometry NibGeometry = StreamGeometry.Parse(NibFacet);
        private static readonly Geometry BandGeometry = StreamGeometry.Parse(BodyBand);
        private static readonly Geometry ButtonGeometry = StreamGeometry.Parse(SideButton);
        private static readonly Geometry TailGeometry = StreamGeometry.Parse(TailDot);

        private static readonly IBrush BodyBrush = new SolidColorBrush(Color.Parse("#4A4C53"));
        private static readonly IBrush BandBrush = new SolidColorBrush(Color.Parse("#C9CBD1"));
        private static readonly IBrush NibBrush = new SolidColorBrush(Color.Parse("#26282D"));
        private static readonly IBrush ButtonBrush = new SolidColorBrush(Color.Parse("#8E9098"));
        private static readonly IBrush TailBrush = new SolidColorBrush(Color.Parse("#EDEFF3"));
        private static readonly IBrush RingBrush = new SolidColorBrush(Color.Parse("#F7F7FA"));

        public PenView()
        {
            Width = 132;
            Height = Total + 40;
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        }

        public override void Render(DrawingContext context)
        {
            var top = Math.Max(10, (Bounds.Height - Total) / 2);
            var cx = Bounds.Width / 2;

            // 把图标里的斜笔立起来：绕包围盒中心转 45°、等比缩放、上下翻转（笔尖朝下）
            var pre = Matrix.CreateTranslation(-PivotX, -PivotY)
                      * Matrix.CreateRotation(Math.PI / 4)
                      * Matrix.CreateScale(Scale, Scale)
                      * Matrix.CreateScale(1, -1);

            // 量出立起来之后的上下范围：笔顶对齐面板顶端，笔底按原比例落在 top+extent
            var nibLocal = pre.Transform(new Point(21.7, 22.2));
            var capLocal = pre.Transform(new Point(47.2, 48.2));
            var extent = Math.Abs(capLocal.Y - nibLocal.Y);
            var capTopLocal = Math.Min(nibLocal.Y, capLocal.Y);
            var m = pre * Matrix.CreateTranslation(cx, top - capTopLocal);

            var extra = Math.Max(0, Total - extent);
            var sliceTop = top + extent * SliceStart;
            var sliceBottom = top + extent * SliceEnd;
            var sliceH = Math.Max(1, sliceBottom - sliceTop);
            var gapBottom = sliceTop + extra;

            // 上半段（尾帽）：原位
            using (context.PushClip(new Rect(0, 0, Bounds.Width, sliceTop)))
            {
                using (context.PushTransform(m))
                    DrawPen(context);
            }

            // 中间空档：把选中的那段笔杆纵向拉长填进去（两侧边缘是直线，接缝对得上）
            var stretch = Matrix.CreateTranslation(0, -sliceTop) * Matrix.CreateScale(1, extra / sliceH)
                          * Matrix.CreateTranslation(0, sliceTop);
            using (context.PushClip(new Rect(0, sliceTop - 2, Bounds.Width, gapBottom - sliceTop + 2)))
            {
                using (context.PushTransform(m * stretch))
                    DrawPen(context);
            }

            // 下半段（握持、侧键、笔尖）：整体下移，让出笔杆长度
            var down = m * Matrix.CreateTranslation(0, extra);
            using (context.PushClip(new Rect(0, gapBottom - 3, Bounds.Width, Total)))
            {
                using (context.PushTransform(down))
                    DrawPen(context);

                // 侧键单独画：它要 3 倍高再切成上下两键，裁剪按屏幕坐标算，所以放在变换外面
                DrawSideButtons(context, down);
            }

            // 橡皮尾、笔尖两个热点（侧键的两个在 DrawSideButtons 里画）
            DrawDot(context, m.Transform(new Point(46.6, 47.3)));
            DrawDot(context, down.Transform(new Point(22.2, 22.8)));
        }

        /// <summary>
        /// 侧键：图标里那个细长胶囊沿笔身方向放大 3 倍，再从中间断开成上下两个键。
        /// </summary>
        private void DrawSideButtons(DrawingContext context, Matrix m)
        {
            var pivot = new Point(ButtonCx, ButtonCy);

            // 沿笔身方向（图标里是 45° 斜向）放大 3 倍：[[2,1],[1,2]]，垂直方向不放大，
            // 这样键还是顺着笔杆长，不会歪
            var stretch = new Matrix(2, 1, 1, 2, 0, 0);
            var local = Matrix.CreateTranslation(-ButtonCx, -ButtonCy)
                        * stretch
                        * Matrix.CreateTranslation(ButtonCx, ButtonCy);

            // 先在图标坐标里拉长，再做整体的旋转缩放
            var mb = local * m;
            var center = mb.Transform(pivot);

            var half = 2.0 * SideButtonStretch * Scale;   // 拉伸后的像素半长
            const double gap = 3;

            if (half > gap)
            {
                using (context.PushClip(new Rect(0, center.Y - half, Bounds.Width, half - gap)))
                {
                    using (context.PushTransform(mb))
                        context.DrawGeometry(ButtonBrush, new Pen(NibBrush, 0.1), ButtonGeometry);
                }

                using (context.PushClip(new Rect(0, center.Y + gap, Bounds.Width, half - gap)))
                {
                    using (context.PushTransform(mb))
                        context.DrawGeometry(ButtonBrush, new Pen(NibBrush, 0.1), ButtonGeometry);
                }
            }
            else
            {
                using (context.PushTransform(mb))
                    context.DrawGeometry(ButtonBrush, new Pen(NibBrush, 0.1), ButtonGeometry);
            }

            // 两个热点落在上下两键中间
            DrawDot(context, new Point(center.X - 9, center.Y - (half + gap) / 2));
            DrawDot(context, new Point(center.X - 9, center.Y + (half + gap) / 2));
        }

        private static void DrawPen(DrawingContext context)
        {
            context.DrawGeometry(BodyBrush, null, SilhouetteGeometry);
            context.DrawGeometry(BandBrush, null, BandGeometry);
            context.DrawGeometry(NibBrush, null, NibGeometry);
            context.DrawGeometry(TailBrush, null, TailGeometry);
        }

        private static void DrawDot(DrawingContext context, Point center)
        {
            context.DrawEllipse(RingBrush, new Pen(Palette.AccentBrush, 1.4), center, 5.5, 5.5);
            context.DrawEllipse(Palette.AccentBrush, null, center, 2.2, 2.2);
        }
    }
}
