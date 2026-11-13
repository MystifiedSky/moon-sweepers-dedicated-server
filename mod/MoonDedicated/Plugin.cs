using System;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace MoonDedicated
{
    /// One mod for both sides. On a player's PC it adds "join a dedicated server" (driven by the
    /// Moon Sweepers server browser). On the server (started with -moon-dedicated or MOON_DEDICATED=1)
    /// it turns the game into a headless dedicated server.
    [BepInPlugin(Mods.OwnGuid, "Moon Dedicated", BuildVersion.Value)]
    public class Plugin : BasePlugin
    {
        public override void Load()
        {
            global::MoonDedicated.Log.Source = this.Log;
            bool validateOnly = Environment.GetEnvironmentVariable("MOON_VALIDATE_ONLY") == "1";
            bool validated = false;
            try
            {
                Cfg.Load(Paths.BepInExRootPath);
                global::MoonDedicated.Log.FilePath = Path.Combine(Cfg.ModDir, Cfg.Role == Role.Server ? "server.log" : "client.log");
                global::MoonDedicated.Log.Info("Moon Dedicated " + BuildVersion.Value + " loading in " + Cfg.Role + " mode (game " + Cfg.GameVersion + ", protocol " + Cfg.Protocol + ")");
                if (!Il2.Init()) { global::MoonDedicated.Log.Error("could not reach the game's IL2CPP runtime"); return; }
                if (!Game.CheckVersion()) return;
                using var hooks = new Hooks.Transaction();
                if (Cfg.Role == Role.Server) WorldStorage.InstallServer();
                if (!Game.Resolve())
                {
                    global::MoonDedicated.Log.Error("this game build does not match what the mod expects. Missing: " + string.Join(", ", Game.Missing) + ". The mod is disabled.");
                    return;
                }
                if (!validateOnly && Cfg.Role == Role.Client) {
                    try { WorldStorage.InstallClient(); }
                    catch (Exception e) {
                        global::MoonDedicated.Log.Error("World storage is in use or unavailable. Stop other clients/servers using this Windows account before launching: " + e.Message);
                        Environment.Exit(1); // Never continue into an unprotected world.
                    }
                }
                bool installed;
                if (Cfg.Role == Role.Server)
                {
                    Game.DumpClasses(Path.Combine(Cfg.ModDir, "class-dump.txt"));
                    installed = DedicatedServer.Install();
                }
                else { installed = PlayerClient.Install(); if (installed) { MenuButton.Install(); if (!validateOnly) InGameBrowser.Install(this); } }
                if (!installed) throw new InvalidOperationException("required plugin installation failed; hooks rolled back");
                CleaningSync.Install();
                if (validateOnly && Cfg.Role == Role.Server && Environment.GetEnvironmentVariable("MOON_NATIVE_CLEANING_CHECKS") == "1") NativeCleaningChecks.Run();
                validated = true;
                if (!validateOnly) hooks.Commit();
            }
            catch (Exception e) { validated = false; global::MoonDedicated.Log.Error("startup failed: " + e); }
            finally {
                if (validateOnly) {
                    global::MoonDedicated.Log.Info(validated ? "VALIDATION PASSED; hooks rolled back; no world was started" : "VALIDATION FAILED; no world was started");
                    // This diagnostic exits during plugin loading, before gameplay can
                    // write saves. Unity's queued Quit cannot run at this phase.
                    Environment.Exit(validated ? 0 : 1);
                }
                else if (!validated && Cfg.Role == Role.Server)
                {
                    // Rolling back native hooks restores the game's client save
                    // path and Steam client behavior. A failed dedicated install
                    // must never fall through into an ordinary game process.
                    global::MoonDedicated.Log.Error("Dedicated startup aborted before gameplay because required initialization failed");
                    Environment.Exit(1);
                }
            }
        }
    }
}
