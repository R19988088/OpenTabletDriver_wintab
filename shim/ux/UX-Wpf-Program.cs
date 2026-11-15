using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Eto.Forms;

namespace OpenTabletDriver.UX.Wpf
{
    class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            InstallCrashLogging();

            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            if (principal.IsInRole(WindowsBuiltInRole.Administrator))
                WarnAboutElevation();

            // A shim that is not installed looks exactly like a broken shim, so verify the system
            // copies before the panel starts and offer to install them when they are missing.
            WintabInstall.EnsureInstalled();

            App.Run(Eto.Platforms.Wpf, args);
        }

        // Eto's themed message box needs a live application context; asking for one here crashed
        // with ArgumentNullException('path1') inside Path.Combine before the window even existed,
        // which took the whole process down. A plain user32 message box has no such requirement,
        // and a failed warning must never be the reason OTD does not start.
        private static void WarnAboutElevation()
        {
            const string message =
                "OpenTabletDriver should not be run with administrator privileges.\r\n" +
                "Some features may not work as intended, such as Plugin Manager and Tablet Debugger.\r\n" +
                "Run it normally (double-click) instead. Only install-wintab.bat needs elevation.\r\n" +
                "If you did not start it manually as administrator, enable UAC to resolve this.";

            try
            {
                var result = MessageBoxW(IntPtr.Zero, message, "OpenTabletDriver", MB_ICONWARNING);
                Note($"elevated run, warning shown, result={result}");
            }
            catch (Exception ex)
            {
                Note($"elevated run, warning could not be shown: {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
                // Nothing to fall back to: the warning is informational only.
            }

            try
            {
                Console.Error.WriteLine(message);
            }
            catch
            {
            }
        }

        private const uint MB_ICONWARNING = 0x00000030;

        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        private static void Note(string line)
        {
            try
            {
                var directory = Path.Join(AppContext.BaseDirectory, "userdata");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Join(directory, "startup-notes.log"),
                    $"# {DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch
            {
                // A note is optional; never let it break startup.
            }
        }

        // A missing native library used to kill the window with nothing written anywhere, so
        // leave a readable note next to the executable whenever startup fails this way.
        private static void InstallCrashLogging()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                WriteCrashLog(e.ExceptionObject as Exception);

            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (e.Exception is DllNotFoundException or FileNotFoundException or BadImageFormatException)
                    WriteCrashLog(e.Exception);
            };
        }

        private static void WriteCrashLog(Exception? exception)
        {
            try
            {
                var directory = Path.Join(AppContext.BaseDirectory, "userdata");
                Directory.CreateDirectory(directory);
                var builder = new StringBuilder();
                builder.AppendLine($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                builder.AppendLine($"executable: {Environment.ProcessPath}");
                builder.AppendLine($"working directory: {Environment.CurrentDirectory}");
                Append(builder, exception);
                File.AppendAllText(Path.Join(directory, "startup-crash.log"), builder.ToString());
            }
            catch
            {
                // Logging must never be the reason startup fails.
            }
        }

        private static void Append(StringBuilder builder, Exception? exception, int depth = 0)
        {
            if (exception == null || depth > 4) return;
            builder.AppendLine($"{new string(' ', depth * 2)}{exception.GetType().FullName}: {exception.Message}");
            if (exception.StackTrace is { Length: > 0 } stack)
                builder.AppendLine(stack);
            if (exception is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions)
                    Append(builder, inner, depth + 1);
            else
                Append(builder, exception.InnerException, depth + 1);
            builder.AppendLine();
        }
    }
}
