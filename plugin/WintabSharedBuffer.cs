using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using OpenTabletDriver.Plugin;

namespace OpenTabletDriver.Wintab
{
    /// <summary>
    /// Publishes pen samples into the shared ring consumed by <c>Wintab32.dll</c>.
    /// The buffer layout must stay in sync with the Rust shim.
    /// </summary>
    internal sealed class WintabSharedBuffer : IDisposable
    {
        internal const string MappingName = @"Local\OpenTabletDriverWintab";
        private const uint Magic = 0x4F54_5754;
        private const int RingCapacity = 512;

        private readonly MemoryMappedFile? file;
        private readonly MemoryMappedViewAccessor? view;
        // Offsets mirror the SharedState layout in wintab-shim/src/lib.rs:
        // magic 0, version 4, enabled 8, heartbeat 12, width 16, height 20,
        // maxPressure 24, writeIndex 28, packets 32.
        private const long PacketBase = 32;
        private const long WriteIndexOffset = 28;
        private const long HeartbeatOffset = 12;
        private const long EnabledOffset = 8;
        private const long WidthOffset = 16;
        private const long HeightOffset = 20;
        private const long MaxPressureOffset = 24;

        /// <summary>
        /// 环形缓冲里的 X/Y 是不是"已映射到屏幕像素"。
        /// 这件事必须让 shim 知道：它按客户端的 lcSysExt 把像素换算回设备单位，
        /// 否则 SAI 这类把包当成设备单位的应用会按错误的系数再缩一遍。
        /// 标志借用 version 字段的最高位，结构布局不变。
        /// </summary>
        private const uint ScreenSpaceFlag = 0x8000_0000u;

        public static bool ScreenSpace { get; set; } = true;

        public WintabSharedBuffer(int width, int height, uint maxPressure)
        {
            try
            {
                // SharedState declares a single inline packet; the ring needs full capacity.
                var size = PacketBase + (long)RingCapacity * Marshal.SizeOf<SharedPacket>();
                var reused = true;
                try
                {
                    file = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.ReadWrite);
                }
                catch (FileNotFoundException)
                {
                    reused = false;
                    file = MemoryMappedFile.CreateNew(MappingName, size);
                }
                view = file.CreateViewAccessor(0, size);

                view.Write(0, Magic);
                view.Write(4, ScreenSpace ? (1u | ScreenSpaceFlag) : 1u);
                view.Write(EnabledOffset, 1u);
                view.Write(WidthOffset, width);
                view.Write(HeightOffset, height);
                view.Write(MaxPressureOffset, maxPressure);
                view.Write(WriteIndexOffset, 0u);

                Width = width;
                Height = height;
                MaxPressure = maxPressure;

                WintabDiag.Write(
                    $"SHM created name={MappingName} {width}x{height} maxP={maxPressure} " +
                    $"size={size} reusedExisting={reused}");
            }
            catch (Exception ex)
            {
                Log.Write("Wintab", $"Unable to create shared buffer: {ex.Message}", LogLevel.Error);
                WintabDiag.Write("SHM FAILED", ex);
                file = null;
                view = null;
            }
        }

        public int Width { get; }
        public int Height { get; }
        public uint MaxPressure { get; }
        public bool IsValid => view != null;

        private uint sequence;

        public void SetEnabled(bool enabled) => view?.Write(EnabledOffset, enabled ? 1u : 0u);

        private readonly byte[] packetBytes = new byte[Marshal.SizeOf<SharedPacket>()];

        public void Publish(int x, int y, uint pressure, int tiltX, int tiltY, int rotation, uint buttons, bool inRange)
        {
            if (view == null)
                return;

            var index = sequence % RingCapacity;
            sequence++;

            var span = packetBytes.AsSpan();
            BitConverter.TryWriteBytes(span[0..4], sequence);
            BitConverter.TryWriteBytes(span[4..8], 0u);
            BitConverter.TryWriteBytes(span[8..12], x);
            BitConverter.TryWriteBytes(span[12..16], y);
            BitConverter.TryWriteBytes(span[16..20], pressure);
            BitConverter.TryWriteBytes(span[20..24], tiltX);
            BitConverter.TryWriteBytes(span[24..28], tiltY);
            BitConverter.TryWriteBytes(span[28..32], rotation);
            BitConverter.TryWriteBytes(span[32..36], buttons);
            BitConverter.TryWriteBytes(span[36..40], 0u);
            BitConverter.TryWriteBytes(span[40..44], (uint)Environment.TickCount);
            BitConverter.TryWriteBytes(span[44..48], inRange ? 1u : 0u);

            var offset = PacketBase + (long)index * packetBytes.Length;
            view.WriteArray(offset, packetBytes, 0, packetBytes.Length);

            view.Write(HeartbeatOffset, (uint)Environment.TickCount);
            view.Write(WriteIndexOffset, sequence);
        }

        public void Dispose()
        {
            try
            {
                SetEnabled(false);
            }
            catch
            {
                // Ignore teardown failures.
            }
            view?.Dispose();
            file?.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SharedPacket
        {
            public uint Sequence;
            public uint Flags;
            public int X;
            public int Y;
            public uint Pressure;
            public int TiltX;
            public int TiltY;
            public int Rotation;
            public uint Buttons;
            public uint Cursor;
            public uint TimeMs;
            public uint InRange;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SharedState
        {
            public uint Magic;
            public uint Version;
            public uint Enabled;
            public uint HeartbeatMs;
            public int Width;
            public int Height;
            public uint MaxPressure;
            public uint WriteIndex;
            public SharedPacket Packets;
        }
    }
}
