using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MoonDedicated
{
    internal enum Role { Client, Server }
    internal enum ModPolicy { Off, Required, Strict }

    internal sealed class ModReq
    {
        public string Guid;
        public string Version; // null = any

        public static ModReq Parse(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return null;
            var i = s.IndexOf('@');
            if (i < 0) return new ModReq { Guid = s };
            var v = s.Substring(i + 1).Trim();
            return new ModReq { Guid = s.Substring(0, i).Trim(), Version = v.Length == 0 ? null : v };
        }
        public override string ToString() => Version == null ? Guid : Guid + "@" + Version;
    }

    /// Settings come from the environment variables the manager sets (MOON_*), the same
    /// names that are used in server.properties.
    internal static class Cfg
    {
        public static Role Role;
        public static string ServerName = "Moon Sweepers Dedicated Server";
        public static string Motd = "";
        public static string Password = "";        // empty = disabled
        public static HashSet<ulong> AllowedIds = new HashSet<ulong>(); // empty = disabled
        public static bool AllowlistInvalid;
        public static int ServerFps = 30;
        public static int GamePort = 27025, QueryPort = 27026, MaxPlayers = 8, Slot = 1, AutosaveMinutes = 10;
        public static List<ModReq> RequiredMods = new List<ModReq>();
        public static ModPolicy Policy = ModPolicy.Required;
        public static bool ListInSteam;
        public static bool PauseJoonasWhenEmpty;
        public static string RuntimeDir;
        public static string ModDir; // BepInEx/moon-ds (client side files)
        public static string InstanceId = "";
        public const string GameVersion = "1.0.6";
        public const int Protocol = 3;
        public const int SessionTimeoutSeconds = 45; // a peer silent this long is dropped

        static string Env(string k) => Environment.GetEnvironmentVariable(k) ?? "";

        public static bool ValidPlayerId(ulong id) => (id >> 56) == 1 && ((id >> 52) & 0xF) == 1 && ((id >> 32) & 0xFFFFF) == 1 && (id & 0xFFFFFFFF) != 0;

        public static void Load(string bepInExRoot)
        {
            var args = Environment.GetCommandLineArgs();
            Role = Env("MOON_DEDICATED") == "1" || args.Any(a => a == "-moon-dedicated") ? Role.Server : Role.Client;
            ModDir = Path.Combine(bepInExRoot, "moon-ds");
            Directory.CreateDirectory(ModDir);
            if (Role != Role.Server) return;

            if (Env("MOON_SERVER_NAME").Trim().Length > 0) ServerName = Env("MOON_SERVER_NAME").Trim();
            if (ServerName.Length > 63) ServerName = ServerName.Substring(0, 63);
            Motd = Env("MOON_MOTD");
            Password = Env("MOON_SERVER_PASSWORD");
            foreach (var part in Env("MOON_ALLOWED_STEAM_IDS").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (ulong.TryParse(part.Trim(), out var id) && ValidPlayerId(id)) AllowedIds.Add(id);
                else AllowlistInvalid = true; // a typo must never silently turn the list off
            }
            GamePort = Int("MOON_GAME_PORT", 27025, 1024, 65535);
            QueryPort = Int("MOON_QUERY_PORT", 27026, 1024, 65535);
            MaxPlayers = Int("MOON_MAX_PLAYERS", 8, 1, 15);
            Slot = Int("MOON_SLOT", 1, 1, 5);
            AutosaveMinutes = Int("MOON_AUTOSAVE_MINUTES", 10, 0, 1440);
            ServerFps = Int("MOON_SERVER_FPS", 30, 10, 120);
            foreach (var m in Env("MOON_REQUIRED_MODS").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var r = ModReq.Parse(m);
                if (r != null) RequiredMods.Add(r);
            }
            switch (Env("MOON_MOD_POLICY").Trim().ToLowerInvariant())
            {
                case "off": case "none": Policy = ModPolicy.Off; break;
                case "strict": case "exact": Policy = ModPolicy.Strict; break;
                default: Policy = ModPolicy.Required; break;
            }
            ListInSteam = Env("MOON_STEAM_LISTING") == "1" || Env("MOON_STEAM_LISTING").Equals("true", StringComparison.OrdinalIgnoreCase);
            PauseJoonasWhenEmpty = Env("MOON_PAUSE_JOONAS_WHEN_EMPTY") == "1" || Env("MOON_PAUSE_JOONAS_WHEN_EMPTY").Equals("true", StringComparison.OrdinalIgnoreCase);
            RuntimeDir = Env("MOON_DS_RUNTIME");
            if (RuntimeDir.Length == 0) RuntimeDir = Path.Combine(ModDir, "runtime");
            Directory.CreateDirectory(RuntimeDir);
            RuntimeDir = Path.GetFullPath(RuntimeDir);
            InstanceId = Env("MOON_DS_INSTANCE_ID");
        }

        static int Int(string key, int def, int lo, int hi)
        {
            if (!int.TryParse(Env(key), out var v)) return def;
            return Math.Max(lo, Math.Min(hi, v));
        }

        public static bool PasswordEnabled => Password.Length > 0;
        public static bool AllowlistEnabled => AllowedIds.Count > 0 || AllowlistInvalid;
    }
}
