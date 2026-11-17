using System;
using System.Diagnostics;
using System.IO;

namespace OpenTabletDriver.Plugin
{
    /// <summary>
    /// Opt-in startup phase timing. Set OTD_TRACE to a file path and every
    /// <see cref="Mark(string)"/> call appends an elapsed-time line, which is how the
    /// real cost of daemon startup was measured. With the variable unset it costs a
    /// single null check.
    /// </summary>
    public static class StartupTrace
    {
        private static readonly Stopwatch Watch = Stopwatch.StartNew();
        private static readonly StreamWriter? Writer = Open();

        private static StreamWriter? Open()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("OTD_TRACE") is not { Length: > 0 } path)
                    return null;
                var writer = new StreamWriter(path, append: true) { AutoFlush = true };
                writer.WriteLine($"# session {DateTime.Now:HH:mm:ss.fff} pid={Environment.ProcessId} exe={Environment.ProcessPath}");
                return writer;
            }
            catch
            {
                return null;
            }
        }

        public static bool Enabled => Writer != null;

        public static void Mark(string label)
        {
            if (Writer == null) return;
            try
            {
                Writer.WriteLine($"+{Watch.ElapsedMilliseconds,6}ms  {label}");
            }
            catch
            {
                // Timing must never break startup.
            }
        }
    }
}
