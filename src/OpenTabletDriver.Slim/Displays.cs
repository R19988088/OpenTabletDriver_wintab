using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 显示器枚举（Win32）。坐标用的是虚拟桌面上的物理像素，和 SendInput 的绝对坐标同一套，
    /// 这样映射到某个显示器时不用再做 DPI 换算。
    /// </summary>
    public static class Displays
    {
        public sealed class Info
        {
            public required string Device { get; init; }
            public required int X { get; init; }
            public required int Y { get; init; }
            public required int Width { get; init; }
            public required int Height { get; init; }
            public required bool Primary { get; init; }

            public string Label => $"{Device.Replace(@"\\.\", "")} · {Width}x{Height}{(Primary ? "（主）" : "")}";
        }

        private const int MONITORINFOF_PRIMARY = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        public static IReadOnlyList<Info> Enum()
        {
            var list = new List<Info>();

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    list.Add(new Info
                    {
                        Device = info.szDevice,
                        X = info.rcMonitor.Left,
                        Y = info.rcMonitor.Top,
                        Width = info.rcMonitor.Right - info.rcMonitor.Left,
                        Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                        Primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0
                    });
                }

                return true;
            }, IntPtr.Zero);

            return list;
        }

        public static Info? Find(string device)
        {
            foreach (var info in Enum())
            {
                if (string.Equals(info.Device, device, StringComparison.OrdinalIgnoreCase))
                    return info;
            }

            return null;
        }

        public static Info? Primary()
        {
            foreach (var info in Enum())
            {
                if (info.Primary)
                    return info;
            }

            return null;
        }
    }
}
