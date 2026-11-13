using System;
using System.IO;
using BepInEx.Logging;

namespace MoonDedicated
{
    internal static class Log
    {
        public static ManualLogSource Source;
        static readonly object gate = new object();
        public static string FilePath;

        static void Write(string level, string msg)
        {
            var line = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + " " + level + " " + msg;
            if (FilePath != null)
            {
                try { lock (gate) File.AppendAllText(FilePath, line + "\n"); } catch { }
            }
        }
        public static void Info(string m) { Source?.LogInfo(m); Write("INFO", m); }
        public static void Warn(string m) { Source?.LogWarning(m); Write("WARN", m); }
        public static void Error(string m) { Source?.LogError(m); Write("ERROR", m); }
    }
}
