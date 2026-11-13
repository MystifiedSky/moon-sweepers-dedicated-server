using System;
using System.IO;

namespace MoonDedicated
{
    internal static class WorldStorage
    {
        static FileStream lease;
        static nuint serverPath;

        public static void InstallServer()
        {
            string path = Environment.GetEnvironmentVariable("MOON_DS_SAVE_ROOT");
            if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(Cfg.RuntimeDir, "..", "world");
            path = Path.GetFullPath(path);
            path = ExtendedPath(path);
            Directory.CreateDirectory(path);
            serverPath = Il2.Pin(Il2.NewString(path));
            var app = Il2.Class(Il2.Image("UnityEngine.CoreModule.dll"), "UnityEngine", "Application");
            Hooks.Method(app, "get_persistentDataPath", 0, (D_Ref0)(_ => Il2.Target(serverPath)));
            string actual = Il2.ReadString(Il2.Invoke(Il2.Method(app, "get_persistentDataPath", 0), IntPtr.Zero));
            if (actual != path) throw new IOException("Could not isolate the server save directory");
            Log.Info("Server world storage isolated at " + actual);
        }

        // Unity's IL2CPP file writer otherwise hits MAX_PATH once the hashed
        // world folder and chunk filename are appended to a long server path.
        internal static string ExtendedPath(string path)
        {
            if (!OperatingSystem.IsWindows() || path.StartsWith(@"\\?\")) return path;
            return path.StartsWith(@"\\") ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        }
        // A client may select any slot. Reserve the shared MoonSessions root for
        // its lifetime, including async writes and transitions back to the menu.
        public static void ReserveClient(string persistentPath)
        {
            if (lease != null) return;
            string root = Path.Combine(persistentPath, "MoonSessions");
            Directory.CreateDirectory(root);
            lease = new FileStream(Path.Combine(root, ".moon-ds-world.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }

        public static void InstallClient()
        {
            var app = Il2.Class(Game.CoreImage, "UnityEngine", "Application");
            string path = Il2.ReadString(Il2.Invoke(Il2.Method(app, "get_persistentDataPath", 0), IntPtr.Zero));
            if (string.IsNullOrEmpty(path)) throw new IOException("Cannot determine world storage path");
            ReserveClient(path);
        }
    }
}
