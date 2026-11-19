using System;
using System.Runtime.InteropServices;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 窗口仍然用系统标题栏，只把标题栏刷成浅蓝而不是纯白：
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
        private const int CaptionColor = 0x00FFF1E8; // #E8F1FF 浅蓝
        private const int BorderColor = 0x00F7E4D6;  // #D6E4F7 稍深一点的描边
        private const int TextColor = 0x001F1D1D;     // #1D1D1F 深灰字

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        public static void UseLightTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            // Win10 只能靠这个开关，Win11 上也一样有效
            var off = 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref off, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref off, sizeof(int));

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
