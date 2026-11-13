using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Unity.IL2CPP;

namespace MoonDedicated
{
    internal sealed class ModInfo
    {
        public string Guid { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
    }

    internal static class Mods
    {
        public const string OwnGuid = "moon.dedicated";

        public static List<ModInfo> Installed()
        {
            var list = new List<ModInfo>();
            try
            {
                var plugins = IL2CPPChainloader.Instance?.Plugins;
                if (plugins != null)
                    foreach (var p in plugins.Values)
                        list.Add(new ModInfo { Guid = p.Metadata.GUID, Name = p.Metadata.Name, Version = p.Metadata.Version.ToString() });
            }
            catch (Exception e) { Log.Warn("could not list installed mods: " + e.Message); }
            return list.OrderBy(m => m.Guid, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// Players see this list in the server browser to know what they have installed.
        public static void WriteInstalledFile()
        {
            try
            {
                File.WriteAllText(Path.Combine(Cfg.ModDir, "installed-mods.json"),
                    JsonSerializer.Serialize(Installed().Select(m => new { guid = m.Guid, name = m.Name, version = m.Version }), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) { Log.Warn("could not write installed-mods.json: " + e.Message); }
        }

        static bool IsInfrastructure(string guid) =>
            guid.Equals(OwnGuid, StringComparison.OrdinalIgnoreCase) || guid.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase);

        /// Returns the problems that keep a player out ("guid@version missing", "guid not allowed").
        public static List<string> Check(IList<ModInfo> player)
        {
            var problems = new List<string>();
            if (Cfg.Policy == ModPolicy.Off) return problems;
            foreach (var req in Cfg.RequiredMods)
            {
                var have = player.FirstOrDefault(m => m.Guid.Equals(req.Guid, StringComparison.OrdinalIgnoreCase));
                if (have == null) problems.Add("missing: " + req);
                else if (req.Version != null && have.Version != req.Version) problems.Add("wrong version: " + req.Guid + " needs " + req.Version + ", you have " + have.Version);
            }
            if (Cfg.Policy == ModPolicy.Strict)
                foreach (var m in player)
                    if (!IsInfrastructure(m.Guid) && !Cfg.RequiredMods.Any(r => r.Guid.Equals(m.Guid, StringComparison.OrdinalIgnoreCase)))
                        problems.Add("not allowed on this server: " + m.Guid);
            return problems;
        }
    }
}
