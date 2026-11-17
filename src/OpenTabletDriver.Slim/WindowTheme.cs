using System;
using System.Runtime.InteropServices;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 窗口仍然用系统标题栏，只把标题栏刷成浅色：
    /// 系统默认会跟着"应用深色模式"走，不处理的话浅色窗口会配一条深灰标题栏。
    /// 圆角和投影依旧由 DWM 自己画，这里不碰窗口形状。
    /// </summary>
    internal static class WindowTheme
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

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

            // Win11 上光有关不够：某些主题下标题栏仍是灰的，直接把颜色指定成白色/黑字。
            // COLORREF 是 0x00BBGGRR。
            var caption = 0x00FFFFFF;
            var text = 0x00000000;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }
    }
}
