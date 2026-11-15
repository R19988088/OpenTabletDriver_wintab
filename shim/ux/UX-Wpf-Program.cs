using System;
using System.IO;
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
            {
                _ = new Application(Eto.Platforms.Wpf);
                MessageBox.Show("OpenTabletDriver should not be run with administrator privileges.\n" +
                    "Some features may not work as intended, such as Plugin Manager and Tablet Debugger.\n" +
                    "If you did not manually set OpenTabletDriver to run with administrator privileges please enable UAC to resolve this.\n" +
                    "If it isn't resolved after, you are using the Administrator named account which overrides UAC.", MessageBoxType.Warning);
                Eto.Platform.AllowReinitialize = true;
            }

            App.Run(Eto.Platforms.Wpf, args);
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
