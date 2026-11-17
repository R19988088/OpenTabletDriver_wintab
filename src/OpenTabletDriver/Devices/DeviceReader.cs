using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Devices;
using OpenTabletDriver.Plugin.Tablet;

namespace OpenTabletDriver.Devices
{
    public class DeviceReader<T> : IDisposable where T : class, IDeviceReport
    {
        public DeviceReader(IDeviceEndpoint endpoint, IReportParser<T> reportParser)
        {
            Endpoint = endpoint;
            Parser = reportParser ?? throw new ArgumentNullException(nameof(reportParser));
            workerThread = new Thread(Main)
            {
                Name = "OpenTabletDriver Device Reader",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
        }

        private readonly Thread workerThread;
        private bool initialized, connected;

        /// <summary>
        /// The device endpoint in which is reporting data in the <see cref="ReportStream"/>.
        /// </summary>
        public IDeviceEndpoint Endpoint { protected set; get; }

        /// <summary>
        /// The raw device endpoint report stream.
        /// </summary>
        public IDeviceEndpointStream? ReportStream { protected set; get; }

        /// <summary>
        /// The <see cref="IReportParser{T}"/> in which the device reports will be parsed with.
        /// </summary>
        public IReportParser<T> Parser { private set; get; }

        /// <summary>
        /// Whether to make an extra cloned report with data left unmodified.
        /// </summary>
        public bool RawClone { set; get; }

        /// <summary>
        /// Invoked when a new report comes in from the device.
        /// </summary>
        public event EventHandler<T>? Report;

        /// <summary>
        /// Invoked when a new report comes in from the device.
        /// </summary>
        /// <remarks>
        /// This will only be invoked when <see cref="RawClone"/> is set to true.
        /// This report is not meant in any way to be modified, as it is supposed to represent the original data.
        /// </remarks>
        public event EventHandler<T>? RawReport;

        /// <summary>
        /// Whether the device is actively emitting reports and being parsed.
        /// </summary>
        public bool Connected
        {
            protected set
            {
                connected = value;
                ConnectionStateChanged?.Invoke(this, Connected);
            }
            get => connected;
        }

        /// <summary>
        /// Invoked when <see cref="Connected"/> is changed.
        /// </summary>
        public event EventHandler<bool>? ConnectionStateChanged;

        [MemberNotNullWhen(true, nameof(ReportStream))]
        protected virtual bool Initialize()
        {
            try
            {
                ReportStream = Endpoint.Open();
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return false;
            }
        }

        protected virtual void Start()
        {
            if (!initialized)
                initialized = Initialize();

            if (initialized)
                workerThread.Start();
        }

        protected void Main()
        {
            byte[]? data = null;
            try
            {
                Connected = true;
                while (Connected)
                {
                    var started = Environment.TickCount64;
                    data = ReportStream!.Read();
                    RawTrace(data, Environment.TickCount64 - started);

                    // 空数组代表"这次超时没数据"，不是错误，继续等下一包。
                    if (data.Length == 0) continue;

                    TrackRate();

                    T? parsed;

                    try
                    {
                        parsed = Parser.Parse(data);
                    }
                    catch (IndexOutOfRangeException e)
                    {
                        Log.Write("Device", $"Unable to parse tablet data {Extensions.PrettyPrintHex(data)}", LogLevel.Warning);
                        Log.Exception(e);
                        parsed = new DeviceReport(data) as T;
                    }

                    if (parsed is not { } report) continue;

                    if (RawClone)
                        OnRawReport(report);
                    OnReport(report);
                }
            }
            catch (ObjectDisposedException dex)
            {
                Log.Debug("Device", $"{(string.IsNullOrWhiteSpace(dex.ObjectName) ? "A device stream" : dex.ObjectName)} was disposed.");
            }
            catch (IOException ioex) when (ioex.Message == "I/O disconnected." || ioex.Message == "Operation failed after some time.")
            {
                Log.Write("Device", "Device disconnected.");
            }
            catch (ArgumentOutOfRangeException)
            {
                Log.Write("Device", "Not enough report data returned by the device. Was it disconnected?");
            }
            catch (Exception ex)
            {
                Log.Exception(ex);

                string formattedTabletData = data != null ? Extensions.PrettyPrintHex(data) : "<null>";
                Log.Write("Device", $"Last report read from tablet: {formattedTabletData}");
            }
            finally
            {
                Connected = false;
            }
        }

        private static long _rawLines;

        /// <summary>
        /// 读取层取证：每次 Read() 返回都记一行（耗时 + 长度 + 前 12 字节）。
        /// 用来区分"卡在 Read 里"和"设备根本不发数据"。
        ///
        /// 默认关闭：每包落盘实测 0.38ms/次，200pps 下等于每秒有 75ms 卡在磁盘 I/O 上，
        /// 会把真实上报率压到 120pps 左右（旧版 hiddump 就是被自己写日志拖慢的）。
        /// 需要时把环境变量 OTD_RAW_TRACE 设为 1 再启动。
        /// </summary>
        private static long _rawEmpty;

        private static readonly bool RawTraceEnabled =
            Environment.GetEnvironmentVariable("OTD_RAW_TRACE") == "1";

        private static void RawTrace(byte[]? data, long elapsedMs)
        {
            if (!RawTraceEnabled)
                return;

            // 300ms 超时会让"空读"非常密集，只抽样记一条，避免把有数据的行挤出日志。
            if (data is { Length: 0 })
            {
                if (System.Threading.Interlocked.Increment(ref _rawEmpty) % 50 != 1)
                    return;
            }

            if (System.Threading.Interlocked.Increment(ref _rawLines) > 400)
                return;

            try
            {
                var dir = DataDir();
                System.IO.Directory.CreateDirectory(dir);

                var text = data == null || data.Length == 0
                    ? $"{DateTime.Now:HH:mm:ss.fff} len=0 elapsed={elapsedMs}ms (timeout, no report)"
                    : $"{DateTime.Now:HH:mm:ss.fff} len={data.Length} elapsed={elapsedMs}ms head={Extensions.PrettyPrintHex(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Take(data, 12)))}";

                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, $"raw-{Environment.ProcessId}.log"),
                    text + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static string? _dataDir;

        /// <summary>主程序通过 OTD_DATA_DIR 指定唯一的用户数据目录。</summary>
        private static string DataDir()
        {
            if (_dataDir != null)
                return _dataDir;

            _dataDir = Environment.GetEnvironmentVariable("OTD_DATA_DIR");
            if (string.IsNullOrEmpty(_dataDir))
            {
                _dataDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OTDWintab");
            }

            return _dataDir;
        }

        private long _statWindowStart = Environment.TickCount64;
        private long _statCount;
        private long _statTotal;
        private readonly double[] _statIntervals = new double[256];
        private int _statIntervalCount;
        private int _statIntervalIndex;
        private long _statLastMs;

        /// <summary>
        /// 每秒记一行真实上报率（rate-&lt;pid&gt;.log）：直接给出 pps 和包间隔分位。
        /// 这是给我们自己看的性能证据，200pps 约等于 avg=5.0ms。
        /// </summary>
        private void TrackRate()
        {
            var now = Environment.TickCount64;

            if (_statLastMs > 0)
            {
                var delta = now - _statLastMs;
                if (delta > 0 && delta < 5000)
                {
                    _statIntervals[_statIntervalIndex] = delta;
                    _statIntervalIndex = (_statIntervalIndex + 1) % _statIntervals.Length;
                    if (_statIntervalCount < _statIntervals.Length)
                        _statIntervalCount++;
                }
            }

            _statLastMs = now;
            _statCount++;
            _statTotal++;

            var window = now - _statWindowStart;
            if (window < 1000)
                return;

            if (window < 3000)
                return;

            try
            {
                var avg = 0.0;
                var max = 0.0;
                var p95 = 0.0;

                if (_statIntervalCount > 4)
                {
                    var copy = new double[_statIntervalCount];
                    Array.Copy(_statIntervals, copy, _statIntervalCount);
                    Array.Sort(copy);
                    foreach (var v in copy)
                        avg += v;
                    avg /= copy.Length;
                    max = copy[copy.Length - 1];
                    p95 = copy[(int)(copy.Length * 0.95)];
                }

                var line = $"{DateTime.Now:HH:mm:ss} rate={_statCount * 1000.0 / window:F1}/s " +
                           $"avg={avg:F2}ms p95={p95:F2}ms max={max:F1}ms total={_statTotal}";

                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(DataDir(), $"rate-{Environment.ProcessId}.log"),
                    line + Environment.NewLine);
            }
            catch
            {
            }

            _statWindowStart = now;
            _statCount = 0;
        }

        protected virtual void OnReport(T report) => Report?.Invoke(this, report);
        protected virtual void OnRawReport(T report) => RawReport?.Invoke(this, report);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private bool _isDisposed;

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;

            if (disposing)
            {
                Connected = false;
                ReportStream?.Dispose();
            }

            _isDisposed = true;
        }
    }
}
