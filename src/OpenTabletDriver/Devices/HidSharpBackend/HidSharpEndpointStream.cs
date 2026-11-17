using HidSharp;
using OpenTabletDriver.Plugin.Devices;

namespace OpenTabletDriver.Devices.HidSharpBackend
{
    public sealed class HidSharpEndpointStream : IDeviceEndpointStream
    {
        internal HidSharpEndpointStream(HidStream stream)
        {
            this.stream = stream;
            // 不能用无限超时：底层事件漏一次，读取线程就永久挂死（实测只拿到一条报告）。
            // 300ms 超时让 Read() 变成"有数据就返回、没数据就空转"，读取线程永远不会被卡住。
            stream.ReadTimeout = 300;
        }

        private HidStream stream;

        public byte[] Read()
        {
            try
            {
                return stream.Read();
            }
            catch (System.TimeoutException)
            {
                return System.Array.Empty<byte>();
            }
        }
        public void Write(byte[] buffer) => stream.Write(buffer);

        public void GetFeature(byte[] buffer) => stream.GetFeature(buffer);
        public void SetFeature(byte[] buffer) => stream.SetFeature(buffer);

        public void Dispose() => stream.Dispose();
    }
}
