using System;
using System.IO;
using System.Text;

namespace NexusNetworkBinder
{
    internal static class AppLogger
    {
        private const long MaxLogBytes = 2 * 1024 * 1024;
        private static readonly object Gate = new();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.UserDataDirectory);
                    RotateIfNeeded();
                    File.AppendAllText(
                        AppPaths.LogPath,
                        $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch
            {
                // O log nunca deve interromper uma operação de rede ou a interface.
            }
        }

        private static void RotateIfNeeded()
        {
            if (!File.Exists(AppPaths.LogPath)) return;
            var info = new FileInfo(AppPaths.LogPath);
            if (info.Length < MaxLogBytes) return;

            var previous = AppPaths.LogPath + ".1";
            try { if (File.Exists(previous)) File.Delete(previous); } catch { }
            File.Move(AppPaths.LogPath, previous, overwrite: true);
        }
    }
}
