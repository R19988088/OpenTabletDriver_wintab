using System;
using System.Runtime.InteropServices;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 只读镜像：从 wintab-shim 的共享内存里取最新一笔压力和按键状态。
    /// 布局必须和 wintab-shim 的 SharedState 一致：
    ///   magic 0 / writeIndex 28 / packets 从 32 起，每包 48 字节，
    ///   包内 pressure +16、buttons +32、inRange +44。
    /// </summary>
    public sealed class WintabSharedReader : IDisposable
    {
        public const string MappingName = @"Local\OpenTabletDriverWintab";

        private const uint MagicCode = 0x4F54_5754;
        private const int PacketBase = 32;
        private const int PacketSize = 48;
        private const int RingCapacity = 512;
        private const float WintabMax = 32767f;

        private const int FileMapRead = 0x0004;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenFileMappingW(int desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr fileMapping, int desiredAccess, int offsetHigh, int offsetLow, IntPtr bytesToMap);

        [DllImport("kernel32.dll")]
        private static extern bool UnmapViewOfFile(IntPtr baseAddress);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private IntPtr _handle;
        private IntPtr _view;

        public bool TryOpen()
        {
            if (_view != IntPtr.Zero)
                return true;

            _handle = OpenFileMappingW(FileMapRead, false, MappingName);
            if (_handle == IntPtr.Zero)
                return false;

            _view = MapViewOfFile(_handle, FileMapRead, 0, 0, new IntPtr(PacketBase + PacketSize * RingCapacity));
            if (_view == IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
                return false;
            }

            return true;
        }

        /// <summary>读取最新一包。压力已归一化到 0..1。</summary>
        public bool TryRead(out float pressure, out uint buttons, out bool inRange)
        {
            pressure = 0f;
            buttons = 0;
            inRange = false;

            if (!TryOpen())
                return false;

            if (Marshal.ReadInt32(_view, 0) != MagicCode)
                return false;

            var writeIndex = Marshal.ReadInt32(_view, 28);
            if (writeIndex <= 0)
                return false;

            var slot = (writeIndex - 1) % RingCapacity;
            var at = PacketBase + slot * PacketSize;

            var raw = Marshal.ReadInt32(_view, at + 16);
            pressure = Math.Clamp(raw / WintabMax, 0f, 1f);
            buttons = (uint)Marshal.ReadInt32(_view, at + 32);
            inRange = Marshal.ReadInt32(_view, at + 44) != 0;
            return true;
        }

        public void Dispose()
        {
            if (_view != IntPtr.Zero)
            {
                UnmapViewOfFile(_view);
                _view = IntPtr.Zero;
            }

            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
