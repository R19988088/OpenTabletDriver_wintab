using System;
using System.Runtime.InteropServices;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 窗口仍然用系统标题栏，只把标题栏刷成深灰（白字）：
    /// 系统默认会跟着"应用深色模式"走，不处理的话浅色窗口会配一条深灰标题栏。
    /// 圆角和投影依旧由 DWM 自己画，这里不碰窗口形状。
    /// </summary>
    internal static class WindowTheme
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
        private const int DWMWA_BORDER_COLOR = 34;

        // COLORREF 是 0x00BBGGRR，不是 0x00RRGGBB
        private const int CaptionColor = 0x003A3A3A; // #3A3A3A 深灰
        private const int BorderColor = 0x002C2C2C;  // #2C2C2C 更深的描边
        private const int TextColor = 0x00FFFFFF;    // 白字

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        public static void ApplyTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            // 深色标题栏要连"深色模式"一起打开，否则最小化/关闭那几个按钮还是黑线，压在深灰上看不清
            var on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));

            // Win10 上这两条 DWM 属性不支持，返回错误码不影响其它调用；
            // Win11 22H2 起才能真正着出颜色。
            var caption = CaptionColor;
            var border = BorderColor;
            var text = TextColor;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }
    }
}
