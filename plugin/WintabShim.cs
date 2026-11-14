using System;
using System.IO;
using OpenTabletDriver.Plugin;

namespace OpenTabletDriver.Wintab
{
    /// <summary>
    /// Locates the native Wintab shim that is shipped beside the application.
    /// The shim must live outside the plugin directory because the plugin loader
    /// treats every DLL in a plugin folder as a managed assembly.
    /// </summary>
    internal static class WintabShim
    {
        public const string FileName = "Wintab32.dll";

        public static string? Resolve()
        {
            var baseDirectory = AppContext.BaseDirectory;

            var bundled = Path.Join(baseDirectory, "WintabNative", FileName);
            if (File.Exists(bundled))
                return bundled;

            var adjacent = Path.Join(baseDirectory, FileName);
            if (File.Exists(adjacent))
                return adjacent;

            return null;
        }

        /// <summary>
        /// Copies the shim to a per-application folder so Wintab consumers can load it.
        /// </summary>
        public static void Install(string targetDirectory)
        {
            var source = Resolve();
            if (source == null)
            {
                Log.Write("Wintab", "Bundled Wintab32.dll was not found; Wintab output will not reach applications.", LogLevel.Warning);
                return;
            }

            try
            {
                Directory.CreateDirectory(targetDirectory);
                var destination = Path.Join(targetDirectory, FileName);
                File.Copy(source, destination, overwrite: true);
                Log.Write("Wintab", $"Installed Wintab32.dll to '{targetDirectory}'");
            }
            catch (Exception ex)
            {
                Log.Write("Wintab", $"Unable to install Wintab32.dll: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
