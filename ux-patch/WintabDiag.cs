using System;
using System.IO;
using System.Text;

namespace OpenTabletDriver.Wintab
{
    /// <summary>
    /// Plain-text diagnostics for the pressure chain, written to
    /// <c>%LOCALAPPDATA%\OTDWintab\otd-wintab-&lt;pid&gt;.log</c>.
    ///
    /// The Wintab32 shim already logs what it does with each packet, but nothing recorded
    /// what this plugin received from the tablet or how much pressure it published. Without
    /// both halves, "no pressure in the application" cannot be told apart from "OTD never
    /// saw pressure" or "the shared ring was never created". One small text file per OTD
    /// process is enough to answer all three, and it survives the tab being closed.
    /// </summary>
    internal static class WintabDiag
    {
        private const long MaxBytes = 4L * 1024 * 1024;

        private static readonly object Gate = new();
        private static string? path;
        private static bool resolved;
        private static bool full;

        private static string? LogPath()
        {
            if (resolved)
                return path;

            resolved = true;
            try
            {
                var dir = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (string.IsNullOrEmpty(dir))
                    return path = null;

                var folder = Path.Combine(dir!, "OTDWintab");
                Directory.CreateDirectory(folder);
                path = Path.Combine(folder, $"otd-wintab-{Environment.ProcessId}.log");
            }
            catch
            {
                path = null;
            }

            return path;
        }

        public static void Write(string message)
        {
            lock (Gate)
            {
                var target = LogPath();
                if (target == null || full)
                    return;

                try
                {
                    var info = new FileInfo(target);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        full = true;
                        File.AppendAllText(target, "LOG FULL, further lines dropped\n", new UTF8Encoding(false));
                        return;
                    }

                    var stamp = Environment.TickCount64;
                    File.AppendAllText(target, $"{stamp,10} {message}\n", new UTF8Encoding(false));
                }
                catch
                {
                    // Diagnostics must never break the output path.
                }
            }
        }

        public static void Write(string message, Exception ex) =>
            Write($"{message}: {ex.GetType().Name}: {ex.Message}");
    }
}
