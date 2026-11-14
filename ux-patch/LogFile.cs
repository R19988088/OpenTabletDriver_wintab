using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Logging;
using SysDirectory = System.IO.Directory;

namespace OpenTabletDriver.Daemon
{
    public sealed class LogFile : IDisposable
    {
        private const int MAX_LOG_FILES = 20;
        private readonly string _fileName;
        private readonly FileStream _stream;
        private readonly StreamWriter _writer;
        /// <summary>
        /// Only problems reach the file: anything below Warning is dropped, and the queue
        /// is bounded so a slow disk can never turn a logging burst into unbounded memory
        /// growth. Under pressure the oldest entries are discarded, not buffered.
        /// </summary>
        private readonly Channel<LogMessage> _channel = Channel.CreateBounded<LogMessage>(
            new BoundedChannelOptions(2048)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _disposed;

        public string Directory { get; }

        public LogFile(string logDirectory)
        {
            Directory = logDirectory;
            PrepareDirectory(logDirectory);

            int? i = null;
            var currentDate = DateTime.Now;
            string? logFile;

            while (File.Exists(logFile = GetFileName(Directory, currentDate, i)))
                i = (i + 1) ?? 1;

            _fileName = logFile;
            _stream = File.Open(_fileName, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(_stream);

            Task.Factory.StartNew(async () =>
            {
                var token = _cts.Token;
                var writer = _writer;
                await writer.WriteLineAsync("[");
                await foreach (var logMessage in _channel.Reader.ReadAllAsync(token))
                {
                    if (token.IsCancellationRequested)
                        break;

                    var log = JsonConvert.SerializeObject(logMessage, Formatting.Indented) + ",";
                    await writer.WriteLineAsync(log);
                    await writer.FlushAsync();
                }
                await writer.WriteLineAsync("]");
            }, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Write(LogMessage message)
        {
            // Recording every informational line cost more than it explained: bursts of
            // per-report messages filled the file and the writer fell behind. Only
            // warnings, errors and crashes are kept.
            if (message.Level < LogLevel.Warning)
                return;

            _channel.Writer.TryWrite(message);
        }

        public IEnumerable<LogMessage> Read()
        {
            using var file = File.Open(_fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(file);
            using var jsonReader = new JsonTextReader(reader);

            while (jsonReader.Read())
            {
                if (jsonReader.TokenType == JsonToken.StartObject)
                {
                    LogMessage? logMessage = null;
                    try
                    {
                        logMessage = JsonConvert.DeserializeObject<LogMessage>(JObject.Load(jsonReader).ToString());
                    }
                    catch
                    {
                        // ignored
                    }

                    if (logMessage != null)
                        yield return logMessage;
                }
            }
        }

        private static string GetFileName(string directory, DateTime date, int? index = null)
        {
            var fileName = date.ToUniversalTime().ToString("u").Replace(":", "_");
            if (index.HasValue)
                fileName += $"-{index.Value}";
            fileName += ".log.json";
            return Path.Combine(directory, fileName);
        }

        private static void PrepareDirectory(string directory)
        {
            if (!SysDirectory.Exists(directory))
                SysDirectory.CreateDirectory(directory);

            var files = SysDirectory.GetFiles(directory, "*.log.json");
            if (files.Length < MAX_LOG_FILES)
                return;

            Array.Sort(files);

            for (var i = 0; i <= files.Length - MAX_LOG_FILES; i++)
            {
                try
                {
                    File.Delete(files[i]);
                }
                catch
                {
                    // ignored
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _cts.Cancel();
            _writer.Flush();
            _cts.Dispose();
            _writer.Dispose();
            _stream.Dispose();

            _disposed = true;
        }
    }
}
