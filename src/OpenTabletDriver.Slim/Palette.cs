using Avalonia.Media;

namespace OpenTabletDriver.Slim
{
    /// <summary>浅色主题（贴近常见 macOS 应用：白内容、浅灰侧栏、单个蓝色强调）。</summary>
    internal static class Palette
    {
        public static readonly Color Window = Color.Parse("#FFFFFF");
        public static readonly Color Sidebar = Color.Parse("#F5F5F7");
        public static readonly Color Separator = Color.Parse("#E3E3E5");
        public static readonly Color Field = Color.Parse("#FFFFFF");
        public static readonly Color FieldBorder = Color.Parse("#DCDCDE");
        public static readonly Color PadFill = Color.Parse("#FBFBFC");
        public static readonly Color PadBorder = Color.Parse("#E1E1E3");
        public static readonly Color Ink = Color.Parse("#1D1D1F");
        public static readonly Color InkSoft = Color.Parse("#3C3C3E");
        public static readonly Color InkDim = Color.Parse("#8E8E93");
        public static readonly Color Stroke = Color.Parse("#2C2C2E");
        public static readonly Color Accent = Color.Parse("#0A84FF");
        public static readonly Color Selected = Color.Parse("#E8E8EA");
        public static readonly Color Hover = Color.Parse("#EFEFF1");

        public static readonly IBrush WindowBrush = new SolidColorBrush(Window);
        public static readonly IBrush SidebarBrush = new SolidColorBrush(Sidebar);
        public static readonly IBrush SeparatorBrush = new SolidColorBrush(Separator);
        public static readonly IBrush FieldBrush = new SolidColorBrush(Field);
        public static readonly IBrush FieldBorderBrush = new SolidColorBrush(FieldBorder);
        public static readonly IBrush PadBrush = new SolidColorBrush(PadFill);
        public static readonly IBrush PadBorderBrush = new SolidColorBrush(PadBorder);
        public static readonly IBrush InkBrush = new SolidColorBrush(Ink);
        public static readonly IBrush InkSoftBrush = new SolidColorBrush(InkSoft);
        public static readonly IBrush InkDimBrush = new SolidColorBrush(InkDim);
        public static readonly IBrush StrokeBrush = new SolidColorBrush(Stroke);
        public static readonly IBrush AccentBrush = new SolidColorBrush(Accent);
        public static readonly IBrush SelectedBrush = new SolidColorBrush(Selected);
        public static readonly IBrush HoverBrush = new SolidColorBrush(Hover);
    }
}
