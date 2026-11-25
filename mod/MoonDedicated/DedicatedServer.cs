// The dedicated server: logs in to Steam as an anonymous game server, lets licensed
// players in (password, allow-list, required mods), presents itself to the game as
// the lobby host, keeps the world saved (including when players leave) and reports
// its state to the manager.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MoonDedicated
{
    internal static unsafe class DedicatedServer
    {
        sealed class Peer
        {
            public ulong Id;
            public string Name = "Crewmate";
            public List<ModInfo> Mods = new List<ModInfo>();
            public bool Authed;
            public long Heard, Since;
        }

        static bool started, backendReady, worldReady, shuttingDown, stopAfterSave, quitRequested;
        static ulong serverId;
        static long stopDeadline;
        static bool finalSaveStarted, stopSyncFailed;
        static readonly Dictionary<ulong, long> closingPeers = new();
        static int pipe;
        static string phase = "starting", lastError = "";
        static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        static nuint worldHandle, saveTask;
        static long nextStart, nextHost, nextRoster, nextState, nextAutosave, saveDeadline, lastSaveMs, nextSave1 = -1, nextSave2 = -1, startedAt;
        static bool saveFailed, directLoad, rosterPublished, snapshotRequested, saveTimedOut, saveQueued;
        static string snapshotDir = "", snapshotError = "";
        static string snapshotRequestId = "";
        static long snapshotMs;
        static int startAttempts;
        static ulong publishedHash;
        static string saveDir = "";
        static IntPtr setFrameRate, setVsync;
        static string[] modList;
        static long nextFpsApply;

        public static bool Dedicated => Cfg.Role == Role.Server;

        // ------------------------------------------------------------------ installing the hooks
        static IntPtr findWorld;
        static D_BoolInt origSigned;
        static D_BoolObjObj origShow;
        static D_Inst0 origSteamDestroy, origWorldUpdate, origRobotUpdate;

        public static bool Install()
        {
            if (Cfg.GamePort == Cfg.QueryPort) { phase = "invalid-ports"; Log.Error("MOON_GAME_PORT and MOON_QUERY_PORT must be different"); return false; }
            origWorldUpdate = Hooks.Method(Game.KWorld, "Update", 0, (D_Inst0)((self, mi) => { if (!finalSaveStarted) origWorldUpdate(self, mi); }));
            if (!Steam.Load()) { phase = "steam-missing"; return false; }
            // The native crew packet handler asks SteamFriends for sender names.
            // A dedicated process has server interfaces, not a logged-in Steam client.
            Hooks.Method(Il2.Class(Game.SteamworksImage, "Steamworks", "SteamFriends"), "GetFriendPersonaName", 1,
                (D_RefU64)((id, mi) => Il2.NewString(peers.TryGetValue(id, out var peer) ? peer.Name : Cfg.ServerName)));
            var native = Il2.Class(Game.SteamworksImage, "Steamworks", "NativeMethods");
            var netMsgs = Il2.Class(Game.SteamworksImage, "Steamworks", "SteamNetworkingMessages");
            var user = Il2.Class(Game.SteamworksImage, "Steamworks", "SteamUser");
            if (native == IntPtr.Zero || netMsgs == IntPtr.Zero || user == IntPtr.Zero) { Log.Error("Steamworks.NET classes not found"); return false; }
            if (!ResolveStubs(native)) return false;

            int before = Hooks.Failures;
            var L = Game.KLobby;
            Hooks.Method(L, "get_InLobby", 0, (D_Bool0)LobbyPresent);
            Hooks.Method(L, "get_IsHost", 0, (D_Bool0)LobbyHost);
            Hooks.Method(L, "get_LobbyId", 0, (D_U64_0)LobbyId);
            Hooks.Method(L, "get_HostId", 0, (D_U64_0)LobbyId);
            Hooks.Method(L, "get_ShiftStarted", 0, (D_Bool0)LobbyPresent);
            Hooks.Method(L, "IsMember", 1, (D_BoolU64)IsMember);
            Hooks.Method(L, "InSteamLobby", 1, (D_BoolU64)IsMember);
            Hooks.Method(L, "CanExchangeWith", 1, (D_BoolU64)IsMember);
            foreach (var n in new[] { "RefreshMembers", "RefreshMembersNow", "RefreshLobby" }) Hooks.Method(L, n, 0, (D_Static0)RefreshMembers);
            foreach (var n in new[] { "UpdatePresence", "ShareLook", "InviteFriends", "PublishRemovals" }) Hooks.Method(L, n, 0, (D_Static0)Noop);
            Hooks.Method(L, "Create", 2, (D_Static2Int)NoopCreate);
            Hooks.Method(L, "SetPrivacy", 1, (D_Static1Int)NoopInt);
            Hooks.Method(L, "SetReady", 1, (D_Static1Bool)NoopBool);
            Hooks.Method(L, "StartShift", 0, (D_Static0)StartShiftHook);
            Hooks.Method(L, "Leave", 0, (D_Static0)LeaveHook);
            Hooks.Method(L, "Avatar", 1, (D_RefU64)NoAvatar);

            Hooks.Method(Game.KSteam, "StartSteam", 0, (D_Inst0)StartSteamHook);
            Hooks.Method(Game.KSteam, "Update", 0, (D_Inst0)UpdateHook);
            origSteamDestroy = Hooks.Method(Game.KSteam, "OnDestroy", 0, (D_Inst0)DestroyHook);
            Hooks.Method(user, "GetSteamID", 0, (D_U64_0)ServerSteamIdHook);
            Hooks.Method(netMsgs, "SendMessageToUser", 5, (D_NetSend)NetSend);
            Hooks.Method(netMsgs, "ReceiveMessagesOnChannel", 3, (D_NetReceive)NetReceive);
            Hooks.Method(netMsgs, "AcceptSessionWithUser", 1, (D_NetIdentity)NetAccept);
            Hooks.Method(netMsgs, "CloseSessionWithUser", 1, (D_NetIdentity)NetClose);
            Hooks.Method(netMsgs, "CloseChannelWithUser", 2, (D_NetChannel)NetCloseChannel);
            Hooks.Method(netMsgs, "GetSessionConnectionInfo", 3, (D_NetInfo)NetInfo);

            var unityObject = Il2.Class(Game.CoreImage, "UnityEngine", "Object");
            findWorld = Il2.FindMethod(unityObject, "FindFirstObjectByType", m => Il2.ParamCount(m) == 2 && Il2.ParamType(m, 0) == "System.Type");
            if (findWorld == IntPtr.Zero) throw new MissingMethodException("Unity world lookup is unavailable");
            origSigned = Hooks.Method(Game.KNewHire, "SignedFor", 1, (D_BoolInt)SignedHook);

            Headless.Install();
            origShow = Hooks.Method(Game.KLoading, "Show", 2, (D_BoolObjObj)ShowHook);
            var robotClass = Game.C("MoonRobotVacuum");
            if (robotClass != IntPtr.Zero) origRobotUpdate = Hooks.Method(robotClass, "Update", 0, (D_Inst0)RobotUpdateHook);

            var core = Game.CoreImage;
            var app = Il2.Class(core, "UnityEngine", "Application");
            var quality = Il2.Class(core, "UnityEngine", "QualitySettings");
            setFrameRate = Il2.Method(app, "set_targetFrameRate", 1);
            setVsync = Il2.Method(quality, "set_vSyncCount", 1);

            phase = "waiting-steam";
            Log.Info("server mode: hooks installed" + (Hooks.Failures > before ? " (" + (Hooks.Failures - before) + " failed, see warnings)" : ""));
            if (Cfg.AllowlistInvalid) Log.Warn("MOON_ALLOWED_STEAM_IDS contains an invalid SteamID: nobody can join until it is fixed");
            Log.Info("password " + (Cfg.PasswordEnabled ? "enabled" : "disabled") + ", allow-list " + (Cfg.AllowlistEnabled ? "enabled" : "disabled") + ", mod policy " + Cfg.Policy + ", required mods: " + (Cfg.RequiredMods.Count == 0 ? "none" : string.Join(", ", Cfg.RequiredMods)));
            Log.Info("pause joonas when empty: " + (Cfg.PauseJoonasWhenEmpty ? "enabled" : "disabled"));
            return true;
        }

        // ------------------------------------------------------------------ Steamworks.NET stubs
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, uint, int, int, IntPtr, int> stubSend;
        static delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, int, IntPtr, int> stubReceive;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte> stubAccept, stubClose;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, byte> stubCloseChannel;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int> stubInfo;
        static IntPtr miSend, miReceive, miAccept, miClose, miCloseChannel, miInfo;

        static bool ResolveStubs(IntPtr native)
        {
            IntPtr Stub(string name, int argc, out IntPtr mi) { mi = Il2.Method(native, "ISteamNetworkingMessages_" + name, argc); return Il2.Code(mi); }
            IntPtr a = Stub("SendMessageToUser", 6, out miSend), b = Stub("ReceiveMessagesOnChannel", 4, out miReceive), c = Stub("AcceptSessionWithUser", 2, out miAccept),
                   d = Stub("CloseSessionWithUser", 2, out miClose), e = Stub("CloseChannelWithUser", 3, out miCloseChannel), f = Stub("GetSessionConnectionInfo", 4, out miInfo);
            if (a == IntPtr.Zero || b == IntPtr.Zero || c == IntPtr.Zero || d == IntPtr.Zero || e == IntPtr.Zero || f == IntPtr.Zero) { Log.Error("Steamworks networking stubs not found"); return false; }
            stubSend = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, uint, int, int, IntPtr, int>)a;
            stubReceive = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, int, IntPtr, int>)b;
            stubAccept = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)c;
            stubClose = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)d;
            stubCloseChannel = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, byte>)e;
            stubInfo = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)f;
            return true;
        }

        static int NetSend(IntPtr id, IntPtr data, uint size, int flags, int channel, IntPtr mi) => Steam.ServerMessages == IntPtr.Zero ? 3 : stubSend(Steam.ServerMessages, id, data, size, flags, channel, miSend);
        static int NetReceive(int channel, IntPtr array, int capacity, IntPtr mi)
        {
            if (finalSaveStarted || Steam.ServerMessages == IntPtr.Zero || array == IntPtr.Zero || capacity < 0) return 0;
            long len = Il2.ArrayLength(array);
            if (capacity > len) capacity = (int)len;
            return stubReceive(Steam.ServerMessages, channel, array, capacity, miReceive);
        }
        static byte NetAccept(IntPtr id, IntPtr mi) => Steam.ServerMessages == IntPtr.Zero ? (byte)0 : stubAccept(Steam.ServerMessages, id, miAccept);
        static byte NetClose(IntPtr id, IntPtr mi) => Steam.ServerMessages == IntPtr.Zero ? (byte)0 : stubClose(Steam.ServerMessages, id, miClose);
        static byte NetCloseChannel(IntPtr id, int ch, IntPtr mi) => Steam.ServerMessages == IntPtr.Zero ? (byte)0 : stubCloseChannel(Steam.ServerMessages, id, ch, miCloseChannel);
        static int NetInfo(IntPtr id, IntPtr details, IntPtr status, IntPtr mi) => Steam.ServerMessages == IntPtr.Zero ? 0 : stubInfo(Steam.ServerMessages, id, details, status, miInfo);

        // ------------------------------------------------------------------ the lobby the game sees
        static bool Member(ulong id)
        {
            if (!backendReady) return false;
            return id == serverId || (peers.TryGetValue(id, out var p) && p.Authed);
        }
        static byte LobbyPresent(IntPtr mi) => (byte)(backendReady ? 1 : 0);
        static byte LobbyHost(IntPtr mi) => (byte)(backendReady ? 1 : 0);
        static ulong LobbyId(IntPtr mi) => serverId;
        static ulong ServerSteamIdHook(IntPtr mi) => serverId;
        static byte IsMember(ulong id, IntPtr mi) => (byte)(Member(id) ? 1 : 0);
        static void RefreshMembers(IntPtr mi) { if (backendReady) PublishMembers(false); }
        static void Noop(IntPtr mi) { }
        static void NoopCreate(int a, int b, IntPtr mi) { }
        static void NoopInt(int a, IntPtr mi) { }
        static void NoopBool(byte a, IntPtr mi) { }
        static IntPtr NoAvatar(ulong id, IntPtr mi) => IntPtr.Zero;
        static void StartShiftHook(IntPtr mi) { if (backendReady) Il2.Invoke(Game.M_SessionStartShift, IntPtr.Zero); }
        static void LeaveHook(IntPtr mi) { /* the server never leaves its own lobby */ }
        static void RobotUpdateHook(IntPtr self, IntPtr mi)
        {
            if (Cfg.PauseJoonasWhenEmpty && !peers.Values.Any(p => p.Authed)) return;
            origRobotUpdate?.Invoke(self, mi);
        }
        static void StartSteamHook(IntPtr self, IntPtr mi) { StartServer(); }
        static void DestroyHook(IntPtr self, IntPtr mi) { Shutdown(); origSteamDestroy?.Invoke(self, mi); }
        static byte SignedHook(int slot, IntPtr mi) => slot == Cfg.Slot ? (byte)1 : (origSigned != null ? origSigned(slot, mi) : (byte)0);
        static byte ShowHook(IntPtr scene, IntPtr prelude, IntPtr mi) => prelude == IntPtr.Zero ? (byte)0 : (origShow != null ? origShow(scene, prelude, mi) : (byte)0);

        static void CaptureWorld()
        {
            if (worldHandle != 0 && Il2.Target(worldHandle) != IntPtr.Zero) return;
            int inactive = 0;
            var self = Il2.Invoke(findWorld, IntPtr.Zero, Il2.ReflectionType(Game.KWorld), (IntPtr)(&inactive));
            if (self == IntPtr.Zero) return;
            var progress = Il2.GetRef(self, Game.F_WorldProgress);
            var regions = Il2.GetRef(self, Game.F_WorldRegions);
            if (progress != IntPtr.Zero && regions != IntPtr.Zero && Il2.ArrayLength(regions) > 0)
            {
                if (worldHandle == 0 || Il2.Target(worldHandle) != self)
                {
                    Il2.Unpin(worldHandle);
                    worldHandle = Il2.Pin(self);
                    Log.Info("world initialised");
                }
            }
        }

        static bool WorldBound()
        {
            var world = Il2.Target(worldHandle);
            if (world == IntPtr.Zero) return false;
            var crew = Il2.Invoke(Game.M_CrewInstance, IntPtr.Zero);
            return crew != IntPtr.Zero && Il2.GetRef(crew, Game.F_CrewBound) == world;
        }

        // ------------------------------------------------------------------ per-frame entry
        static void UpdateHook(IntPtr self, IntPtr mi)
        {
            // The dedicated server does not run the game's own Steam client callbacks.
            try { Tick(); }
            catch (Exception e) { Log.Error("tick failed: " + e); }
        }

        static void Tick()
        {
            long now = Environment.TickCount64;
            if (shuttingDown) return;
            if (!started) { if (now >= nextStart) StartServer(); return; }
            Pump(now);
            if (now < nextHost) return;
            nextHost = now + 250;
            LimitFrameRate();
            HostFlow(now);
            NativeSyncDiagnostics.Tick();
            StopTick(now);
            SaveTick(now);
            Commands(now);
            if (now >= nextState) { nextState = now + 1000; WriteState(); }
        }

        static void LimitFrameRate()
        {
            long t = Environment.TickCount64;
            if (t < nextFpsApply) return; // the game's own pacing code may reset it, so re-apply every few seconds
            nextFpsApply = t + 5000;
            int fps = Cfg.ServerFps;
            var f = fps; var z = 0;
            if (setFrameRate != IntPtr.Zero) Il2.Invoke(setFrameRate, IntPtr.Zero, (IntPtr)(&f));
            if (setVsync != IntPtr.Zero) Il2.Invoke(setVsync, IntPtr.Zero, (IntPtr)(&z));
        }

        // ------------------------------------------------------------------ Steam game server
        static void StartServer()
        {
            if (started || shuttingDown) return;
            nextStart = Environment.TickCount64 + 5000;
            Environment.SetEnvironmentVariable("SteamAppId", Steam.AppId.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", Steam.AppId.ToString());
            using var ver = new CStr(Cfg.GameVersion);
            if (Steam.GameServerInit(0, 0, (ushort)Cfg.GamePort, (ushort)(Cfg.ListInSteam ? Cfg.QueryPort : 0), 2, ver.P) == 0)
            {
                phase = "server-init-failed";
                lastError = "SteamGameServer_Init failed. Is steamclient64.dll (Steamworks redistributable) next to the game?";
                Log.Error(lastError);
                return;
            }
            if (!Steam.ResolveServerInterfaces()) { phase = "server-interface-failed"; lastError = "Steam game-server interfaces unavailable"; Log.Error(lastError); Steam.GameServerShutdown(); return; }
            var s = Steam.Server;
            Steam.Str(Steam.SetProduct, s, Steam.AppId.ToString());
            Steam.Str(Steam.SetGameDescription, s, "Moon Sweepers");
            Steam.Str(Steam.SetModDir, s, "Moon Sweepers");
            Steam.Str(Steam.SetServerName, s, Cfg.ServerName);
            Steam.SetDedicated(s, 1);
            Steam.SetAdvertise(s, (byte)(Cfg.ListInSteam ? 1 : 0));
            Steam.SetMaxPlayers(s, Cfg.MaxPlayers);
            Steam.DispatchInit();
            pipe = Steam.GameServerPipe();
            Steam.LogOnAnonymous(s);
            if (Steam.Utils != IntPtr.Zero) Steam.InitRelayNetworkAccess(Steam.Utils);
            started = true;
            phase = "logging-on";
            Log.Info("Steam game server started on game port " + Cfg.GamePort + ", query port " + Cfg.QueryPort + "; logging on anonymously");
        }

        static void Shutdown()
        {
            if (shuttingDown) return;
            shuttingDown = true;
            try
            {
                foreach (var p in peers.Values.ToList())
                {
                    SendTo(p.Id, Msg.Reject, Protocol.Encode(new Reject { Code = Why.Generic, Message = "The server is shutting down" }));
                    if (p.Authed) Steam.EndAuthSession(Steam.Server, p.Id);
                }
                peers.Clear();
                if (started) Steam.GameServerShutdown();
            }
            catch (Exception e) { Log.Warn("shutdown: " + e.Message); }
            started = false; backendReady = false; worldReady = false;
            WriteState();
        }

        static bool Allowed(ulong id)
        {
            if (!Cfg.ValidPlayerId(id)) return false;
            if (Cfg.AllowlistInvalid) return false;
            return !Cfg.AllowlistEnabled || Cfg.AllowedIds.Contains(id);
        }

        static void Pump(long now)
        {
            Steam.DispatchRunFrame(pipe);
            byte* msg = stackalloc byte[24];
            while (Steam.DispatchNext(pipe, msg) != 0)
            {
                int id = *(int*)(msg + 4);
                byte* p = *(byte**)(msg + 8);
                int size = *(int*)(msg + 16);
                try { OnCallback(id, p, size); } catch (Exception e) { Log.Warn("callback " + id + ": " + e.Message); }
                Steam.DispatchFree(pipe);
            }
            if (!backendReady && Steam.LoggedOn(Steam.Server) != 0)
            {
                var id = Steam.ServerSteamId(Steam.Server);
                if (id != 0)
                {
                    serverId = id; backendReady = true;
                    if (Steam.ServerSockets != IntPtr.Zero) Log.Info("networking authentication: " + Steam.InitAuthentication(Steam.ServerSockets));
                    byte yes = 1;
                    Il2.Invoke(Game.M_SetInitialized, IntPtr.Zero, (IntPtr)(&yes));
                    ulong sid = id;
                    Il2.Invoke(Game.M_SetSteamId, IntPtr.Zero, (IntPtr)(&sid));
                    Il2.Invoke(Game.M_SetPersona, IntPtr.Zero, Il2.NewString(Cfg.ServerName));
                    PublishRules();
                    phase = "steam-ready";
                    Log.Info("anonymous Steam game server online, SteamID " + serverId + " (players join by address :" + Cfg.QueryPort + ")");
                }
            }
            ReceiveControl(now);
            if (backendReady)
            {
                if (now >= nextRoster) { nextRoster = now + 5000; BroadcastRoster(); }
                foreach (var p in peers.Values.ToList())
                    if ((!p.Authed && now - p.Since > 30000) || now - p.Heard > Cfg.SessionTimeoutSeconds * 1000L) Drop(p, Why.Generic, "Session timed out");
            }
        }

        static void OnCallback(int id, byte* p, int size)
        {
            if (p == null) return;
            switch (id)
            {
                case 101: phase = "steam-connected"; break;
                case 102:
                case 103:
                    backendReady = false; worldReady = false;
                    foreach (var peer in peers.Values.ToList()) Drop(peer, Why.Generic, "Steam connection lost; please reconnect");
                    serverId = 0;
                    Game.ClearLobbyState();
                    rosterPublished = false; publishedHash = 0;
                    byte no = 0; Il2.Invoke(Game.M_SetInitialized, IntPtr.Zero, (IntPtr)(&no));
                    phase = "steam-disconnected";
                    break;
                case 1251: // a player asks to open a messages session
                    if (size >= Steam.IdentitySize && backendReady && Allowed(Steam.IdentityToSteamId(p)))
                        Steam.MsgAccept(Steam.ServerMessages, p);
                    break;
                case 143: // ticket validation result
                    if (size >= 20)
                    {
                        ulong who = *(ulong*)p; int response = *(int*)(p + 8);
                        if (!peers.TryGetValue(who, out var peer)) break;
                        if (stopAfterSave && !peer.Authed) { Drop(peer, Why.Generic, "The server is stopping"); break; }
                        if (!peer.Authed && Environment.TickCount64 - peer.Since > 30000) { Drop(peer, Why.SteamAuth, "Authentication timed out"); break; }
                        int license = response == 0 ? Steam.UserHasLicense(Steam.Server, who, Steam.AppId) : -1;
                        Log.Info("ticket check for " + who + ": response=" + response + " license=" + license);
                        if (response == 0 && license == 0)
                        {
                            if (!peer.Authed && peers.Values.Count(p => p.Authed) >= Cfg.MaxPlayers) { Drop(peer, Why.Full, "The server is full"); break; }
                            peer.Authed = true; peer.Heard = Environment.TickCount64;
                            Log.Info("player joined: " + peer.Name + " (" + who + ")");
                            OnRosterChanged();
                        }
                        else Drop(peer, response == 0 ? Why.NoLicense : Why.SteamAuth, response == 0 ? "This Steam account does not own Moon Sweepers" : "Steam could not verify your ticket (" + response + ")");
                    }
                    break;
            }
        }

        // ------------------------------------------------------------------ control messages
        static void SendTo(ulong to, Msg type, byte[] payload)
        {
            if (Steam.ServerMessages == IntPtr.Zero) return;
            Steam.Send(Steam.ServerMessages, to, Protocol.Frame(type, payload), Protocol.Channel);
        }

        static void ReceiveControl(long now)
        {
            if (Steam.ServerMessages == IntPtr.Zero) return;
            IntPtr* batch = stackalloc IntPtr[16];
            for (int round = 0; round < 4; round++)
            {
                int n = Steam.MsgReceive(Steam.ServerMessages, Protocol.Channel, batch, 16);
                if (n <= 0 || n > 16) return;
                for (int i = 0; i < n; i++)
                {
                    byte* m = (byte*)batch[i];
                    try
                    {
                        byte* data = *(byte**)m; int size = *(int*)(m + 8); ulong from = Steam.IdentityToSteamId(m + 16);
                        if (from != 0 && Protocol.TryParse(data, size, out var type, out var payload)) Consume(from, type, payload, now);
                    }
                    catch (Exception e) { Log.Warn("bad control message: " + e.Message); }
                    finally { Steam.MessageRelease(batch[i]); }
                }
                if (n < 16) break;
            }
        }

        static void Consume(ulong from, Msg type, byte[] payload, long now)
        {
            if (!backendReady || shuttingDown) return;
            peers.TryGetValue(from, out var peer);
            switch (type)
            {
                case Msg.AuthRequest: OnAuthRequest(from, peer, payload, now); break;
                case Msg.Goodbye:
                    if (peer != null) {
                        Drop(peer, Why.Generic, "Disconnected", quiet: true, deferClose: true);
                        var crew = Il2.Invoke(Game.M_CrewInstance, IntPtr.Zero);
                        if (crew == IntPtr.Zero || !Il2.TryInvoke(Il2.Method(Game.KCrew, "DropMissing", 0), crew, out _)) {
                            stopSyncFailed = true;
                            Refuse(from, Why.Generic, "Vehicle handover could not be confirmed");
                            break;
                        }
                    }
                    SendTo(from, Msg.GoodbyeAck, null);
                    break;
                case Msg.Ping: if (peer != null) { peer.Heard = now; if (peer.Authed) SendRoster(from); } break;
            }
        }

        static void Refuse(ulong to, Why code, string message, params string[] details)
        {
            SendTo(to, Msg.Reject, Protocol.Encode(new Reject { Code = code, Message = message, Details = details.ToList() }));
        }

        static void OnAuthRequest(ulong from, Peer peer, byte[] payload, long now)
        {
            if (CleaningSync.RepairFailed) { Refuse(from, Why.Generic, "World synchronization failed; server restart required"); return; }
            if (stopAfterSave) { Refuse(from, Why.Generic, "The server is stopping"); return; }
            if (!Allowed(from)) { Refuse(from, Why.NotAllowed, "You are not on this server's allow-list"); return; }
            if (peer != null && peer.Authed) { peer.Heard = now; SendRoster(from); return; }
            if (peer != null) return; // already being checked
            var req = Protocol.DecodeAuth(payload);
            if (req.GameVersion != Cfg.GameVersion) { Refuse(from, Why.Version, "This server runs Moon Sweepers " + Cfg.GameVersion + " but you have " + req.GameVersion); return; }
            if (peers.Count >= Cfg.MaxPlayers) { Refuse(from, Why.Full, "The server is full or checking other players"); return; }
            if (Cfg.PasswordEnabled && !Protocol.SameSecret(Cfg.Password, req.Password)) { Refuse(from, Why.Password, "Wrong password"); return; }
            var problems = Mods.Check(req.Mods);
            if (problems.Count > 0) { Log.Info("refused " + from + ": mods (" + string.Join("; ", problems) + ")"); Refuse(from, Why.Mods, "Your mods do not match this server", problems.ToArray()); return; }
            peer = new Peer { Id = from, Name = CleanName(req.Name), Mods = req.Mods, Heard = now, Since = now };
            peers[from] = peer;
            int result;
            fixed (byte* t = req.Ticket) result = Steam.BeginAuthSession(Steam.Server, t, req.Ticket.Length, from);
            Log.Info("BeginAuthSession " + from + " -> " + result);
            if (result != 0) Drop(peer, Why.SteamAuth, "Steam rejected your ticket (" + result + ")");
        }

        static void Drop(Peer p, Why code, string why, bool quiet = false, bool deferClose = false)
        {
            if (p == null || !peers.Remove(p.Id)) return;
            if (!quiet) Refuse(p.Id, code, why);
            if (!deferClose) Steam.EndAuthSession(Steam.Server, p.Id);
            else closingPeers[p.Id] = Environment.TickCount64 + 3000;
            byte* id = stackalloc byte[Steam.IdentitySize];
            Steam.MakeIdentity(id, p.Id);
            if (!deferClose && Steam.ServerMessages != IntPtr.Zero) Steam.MsgClose(Steam.ServerMessages, id);
            Log.Info((p.Authed ? "player left: " : "dropped: ") + p.Name + " (" + p.Id + ") - " + why);
            if (p.Authed) OnPlayerLeft();
            OnRosterChanged();
        }

        static void OnRosterChanged()
        {
            BroadcastRoster();
            PublishRules();
        }

        /// The fix for "vehicles do not save when a player leaves": a player's vehicles are folded
        /// back into the world when they leave, so save again just after (and once more later, in
        /// case the first save raced the hand-over).
        static void OnPlayerLeft()
        {
            long now = Environment.TickCount64;
            nextSave1 = now + 3000;
            nextSave2 = now + 25000;
        }

        static Roster MakeRoster()
        {
            var r = new Roster { Server = serverId, Name = Cfg.ServerName, Motd = Cfg.Motd };
            foreach (var p in peers.Values.Where(p => p.Authed)) r.Players.Add(new KeyValuePair<ulong, string>(p.Id, p.Name));
            return r;
        }

        static void SendRoster(ulong to)
        {
            if (!backendReady) return;
            if (!worldReady) { SendTo(to, Msg.WaitWorld, null); return; }
            SendTo(to, Msg.Roster, Protocol.Encode(MakeRoster()));
        }

        static void BroadcastRoster()
        {
            if (!backendReady) return;
            PublishMembers(false);
            foreach (var p in peers.Values.Where(p => p.Authed)) SendRoster(p.Id);
        }

        static void PublishMembers(bool force)
        {
            if (!backendReady) return;
            ulong h = 1469598103934665603UL ^ serverId;
            foreach (var p in peers.Values.Where(p => p.Authed).OrderBy(p => p.Id))
            {
                h = (h ^ p.Id) * 1099511628211UL;
                foreach (var ch in p.Name) h = (h ^ ch) * 1099511628211UL;
            }
            // Rewriting the list every call would break enumerators the game is in the middle of; only on change.
            if (force || !rosterPublished || h != publishedHash)
            {
                Game.ResetMembers();
                Game.AddMember(serverId, Cfg.ServerName, serverId, serverId);
                foreach (var p in peers.Values.Where(p => p.Authed)) Game.AddMember(p.Id, p.Name, serverId, serverId);
                publishedHash = h;
                rosterPublished = true;
                Game.PublishLobbyState(serverId, true, Cfg.ServerName);
                Game.RaiseLobbyChanged(); // lets the game notice joins/leaves (and release a leaver's vehicles)
                return;
            }
            Game.PublishLobbyState(serverId, true, Cfg.ServerName);
        }

        // ------------------------------------------------------------------ what the server browser sees
        static void PublishRules()
        {
            if (Steam.Server == IntPtr.Zero || !backendReady) return;
            var s = Steam.Server;
            var authed = peers.Values.Where(p => p.Authed).ToList();
            Steam.Str(Steam.SetServerName, s, Cfg.ServerName);
            Steam.Str(Steam.SetMapName, s, "slot " + Cfg.Slot);
            Steam.SetMaxPlayers(s, Cfg.MaxPlayers);
            Steam.SetBotPlayers(s, 0);
            Steam.SetPasswordProtected(s, (byte)(Cfg.PasswordEnabled ? 1 : 0));
            Steam.Str(Steam.SetGameTags, s, "moonds," + (Cfg.PasswordEnabled ? "pw" : "nopw") + ",mods" + Cfg.RequiredMods.Count);
            Steam.ClearKeyValues(s);
            void Rule(string k, string v) { using var a = new CStr(k); using var b = new CStr(v); Steam.SetKeyValue(s, a.P, b.P); }
            Rule("moon_ds", Cfg.Protocol.ToString());
            Rule("moon_ver", Cfg.GameVersion);
            Rule("moon_policy", Cfg.Policy.ToString().ToLowerInvariant());
            Rule("moon_mods", string.Join(",", Cfg.RequiredMods.Select(m => m.ToString())));
            Rule("moon_motd", Cfg.Motd.Length > 200 ? Cfg.Motd.Substring(0, 200) : Cfg.Motd);
            if (Steam.UpdateUserData != null)
                foreach (var p in authed) { using var n = new CStr(p.Name); Steam.UpdateUserData(s, p.Id, n.P, 0); }
        }

        // ------------------------------------------------------------------ starting the world
        static void HostFlow(long now)
        {
            bool menu = Game.InMenu(), inGame = Game.InGame(), loading = Game.Loading();
            if (inGame && !loading) CaptureWorld();
            bool bound = WorldBound();
            if (inGame && !loading && !bound && worldHandle != 0) {
                var crew = Il2.Invoke(Game.M_CrewInstance, IntPtr.Zero);
                if (crew == IntPtr.Zero) {
                    Il2.Invoke(Il2.Method(Game.KCrew, "Create", 0), IntPtr.Zero);
                    crew = Il2.Invoke(Game.M_CrewInstance, IntPtr.Zero);
                }
                if (crew != IntPtr.Zero && Il2.TryInvoke(Il2.Method(Game.KCrew, "Bind", 1), crew, out _, Il2.Target(worldHandle))) {
                    bound = WorldBound();
                    if (bound) Log.Info("Dedicated crew session bound to the loaded world");
                }
            }
            worldReady = backendReady && inGame && !loading && bound;
            if (!backendReady) { return; }
            if (inGame)
            {
                phase = worldReady ? "world-bound" : (loading ? "loading" : "world-scene");
                return;
            }
            if (!menu) return;
            if (startedAt == 0)
            {
                int slot = Cfg.Slot;
                Il2.Invoke(Game.M_SetSlot, IntPtr.Zero, (IntPtr)(&slot));
                Il2.Invoke(Game.M_SessionStartShift, IntPtr.Zero);
                startedAt = now; directLoad = false; phase = "starting-world";
                Log.Info("starting the world from save slot " + Cfg.Slot);
            }
            else if (!directLoad && now - startedAt >= 3000 && !loading)
            {
                directLoad = true;
                var scene = Il2.GetStaticRef(Game.F_GameScene);
                if (scene != IntPtr.Zero && Il2.ReadString(scene).Length is > 0 and < 256)
                {
                    Il2.Invoke(Game.M_Dismiss, IntPtr.Zero);
                    Il2.Invoke(Game.M_Load, IntPtr.Zero, scene);
                    Log.Info("loading the game scene directly");
                }
                else { lastError = "MoonSession.GameScene unavailable"; phase = "error-startup"; }
            }
            else if (now - startedAt > 180000)
            {
                phase = "startup-timeout"; lastError = "the world did not start within 3 minutes";
                if (++startAttempts < 3) startedAt = 0; // try again
            }
        }

        // ------------------------------------------------------------------ saving
        static string CleanName(string name)
        {
            string value = new string((name ?? "").Where(c => !char.IsControl(c) && c != '\u2028' && c != '\u2029').Take(40).ToArray()).Trim();
            return value.Length == 0 ? "Crewmate" : value;
        }

        static string SaveDir()
        {
            if (saveDir.Length > 0) return saveDir;
            try
            {
                var app = Il2.Class(Game.CoreImage, "UnityEngine", "Application");
                var s = Il2.ReadString(Il2.Invoke(Il2.Method(app, "get_persistentDataPath", 0), IntPtr.Zero));
                if (s.Length > 0) saveDir = Path.Combine(s, "MoonSessions", "slot-" + Cfg.Slot);
            }
            catch (Exception e) { Log.Warn("persistentDataPath: " + e.Message); }
            return saveDir;
        }

        static bool TaskFlag(IntPtr task, string name) => Il2.Bool(Il2.Invoke(Il2.Method(Il2.ClassOf(task), name, 0), task));

        static string TaskFailure(IntPtr task)
        {
            if (task == IntPtr.Zero) return "save task was lost";
            var error = Il2.Invoke(Il2.Method(Il2.ClassOf(task), "get_Exception", 0), task);
            if (error != IntPtr.Zero) {
                var text = Il2.ReadString(Il2.Invoke(Il2.Method(Il2.ClassOf(error), "ToString", 0), error));
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            return TaskFlag(task, "get_IsCanceled") ? "world save was cancelled" : "world save task failed without exception details";
        }

        static void RequestSaveNow()
        {
            if (quitRequested) return;
            if (saveTask != 0) { saveQueued = true; return; }
            StartSave();
        }

        static void StartSave()
        {
            if (saveTask != 0) return;
            var world = Il2.Target(worldHandle);
            if (world == IntPtr.Zero || !WorldBound()) { if (stopAfterSave) { saveFailed = true; lastError = "nothing to save: the world is not loaded"; } return; }
            byte complete = 1;
            if (!Il2.TryInvoke(Game.M_SaveProgress, world, out var task, (IntPtr)(&complete)) || task == IntPtr.Zero) {
                saveFailed = true; lastError = "SaveProgress failed: " + Il2.LastError; Log.Error(lastError); return;
            }
            saveTask = Il2.Pin(task);
            saveDeadline = Environment.TickCount64 + 90000;
            saveFailed = false;
            saveTimedOut = false;
        }

        static void StopTick(long now)
        {
            byte* identity = stackalloc byte[Steam.IdentitySize];
            foreach (var entry in closingPeers.ToList()) if (now >= entry.Value) {
                // A reconnect supersedes cleanup of the old session.
                if (!peers.ContainsKey(entry.Key)) {
                    Steam.EndAuthSession(Steam.Server, entry.Key);
                    Steam.MakeIdentity(identity, entry.Key);
                    if (Steam.ServerMessages != IntPtr.Zero) Steam.MsgClose(Steam.ServerMessages, identity);
                }
                closingPeers.Remove(entry.Key);
            }
            if (!stopAfterSave || finalSaveStarted) return;
            if (closingPeers.Count > 0) return;
            if (peers.Values.Any(p => p.Authed) && now < stopDeadline) return;
            if (peers.Values.Any(p => p.Authed)) {
                stopSyncFailed = true;
                foreach (var peer in peers.Values.ToList()) Drop(peer, Why.Generic, "Server stop synchronization timed out");
            }
            var crew = Il2.Invoke(Game.M_CrewInstance, IntPtr.Zero);
            if (crew != IntPtr.Zero && !Il2.TryInvoke(Il2.Method(Game.KCrew, "DropMissing", 0), crew, out _)) stopSyncFailed = true;
            // No members may submit gameplay past this boundary. Pause autonomous
            // simulation before capturing the last snapshot; forced tool completion
            // remains part of SaveProgress(true).
            float zero = 0;
            var time = Il2.Class(Game.CoreImage, "UnityEngine", "Time");
            if (!Il2.TryInvoke(Il2.Method(time, "set_timeScale", 1), IntPtr.Zero, out _, (IntPtr)(&zero))) stopSyncFailed = true;
            finalSaveStarted = true;
            nextSave1 = nextSave2 = -1;
            if (worldReady) RequestSaveNow();
            else { saveFailed = true; lastError = "No loaded world to save during shutdown"; }
        }

        static void SaveTick(long now)
        {
            if (saveTask != 0)
            {
                var task = Il2.Target(saveTask);
                bool done = task != IntPtr.Zero && TaskFlag(task, "get_IsCompleted");
                if (WaitForSave(done, task != IntPtr.Zero, now)) return;
                if (done || task == IntPtr.Zero)
                {
                    bool bad = !done || TaskFlag(task, "get_IsFaulted") || TaskFlag(task, "get_IsCanceled");
                    string failure = bad ? TaskFailure(task) : "";
                    Il2.Unpin(saveTask); saveTask = 0;
                    if (bad) { saveFailed = true; lastError = failure; Log.Error("World save failed: " + lastError); }
                    else {
                        saveFailed = false; saveTimedOut = false; lastError = ""; lastSaveMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); Log.Info("world saved");
                        if (snapshotRequested && !saveQueued) {
                            snapshotRequested = false;
                            try { snapshotDir = SaveSnapshot.Create(SaveDir(), Cfg.RuntimeDir); snapshotMs = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), snapshotMs + 1); snapshotError = ""; }
                            catch (Exception e) { snapshotError = "Snapshot failed: " + e.Message; Log.Error(snapshotError); }
                        }
                    }
                    if (bad && snapshotRequested && !saveQueued) { snapshotRequested = false; snapshotError = lastError; }
                }
            }
            else
            {
                if (nextSave1 >= 0 && now >= nextSave1) { nextSave1 = -1; RequestSaveNow(); }
                else if (nextSave2 >= 0 && now >= nextSave2) { nextSave2 = -1; RequestSaveNow(); }
                else if (Cfg.AutosaveMinutes > 0 && worldReady && string.IsNullOrEmpty(Cfg.InstanceId))
                {
                    // Managed instances already receive autosave commands from
                    // the supervisor. Keep this fallback for standalone hosts.
                    if (nextAutosave == 0) nextAutosave = now + Cfg.AutosaveMinutes * 60000L;
                    if (now >= nextAutosave) { nextAutosave = now + Cfg.AutosaveMinutes * 60000L; RequestSaveNow(); }
                }
            }
            if (saveQueued && saveTask == 0 && !quitRequested)
            {
                saveQueued = false;
                StartSave();
                return;
            }
            if (stopAfterSave && finalSaveStarted && saveTask == 0 && !quitRequested) QuitNow();
        }

        // Timeout is a reported condition, never evidence that an async writer stopped.
        static bool WaitForSave(bool completed, bool exists, long now)
        {
            if (completed || !exists) return false;
            if (now >= saveDeadline && !saveTimedOut) {
                saveTimedOut = true; saveFailed = true; lastError = "World save exceeded 90 seconds; waiting for the original task, no new saves will start";
                if (snapshotRequested) { snapshotRequested = false; snapshotError = lastError; }
                Log.Error(lastError);
            }
            return true;
        }

        static void QuitNow()
        {
            quitRequested = true;
            saveFailed |= stopSyncFailed || CleaningSync.RepairFailed;
            if (stopSyncFailed) lastError = "Final save completed but client synchronization was unconfirmed";
            phase = saveFailed ? "stopped-save-failed" : "stopped";
            Shutdown();
            Il2.Invoke(Game.M_QuitGame, IntPtr.Zero);
        }

        // ------------------------------------------------------------------ manager commands
        static void Commands(long now)
        {
            var dir = Path.Combine(Cfg.RuntimeDir, "commands");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.cmd").OrderBy(x => x))
            {
                string text;
                try { text = File.ReadAllText(f).Trim(); File.Delete(f); } catch { continue; }
                var sp = text.IndexOf(' ');
                var cmd = (sp < 0 ? text : text.Substring(0, sp)).ToLowerInvariant();
                var arg = sp < 0 ? "" : text.Substring(sp + 1).Trim();
                switch (cmd)
                {
                    case "probe-clean":
                        if (Environment.GetEnvironmentVariable("MOON_DS_ENABLE_NATIVE_PROBES") == "1") ProbeClean(arg);
                        else Log.Warn("Native probes are disabled");
                        break;
                    case "save":
                        if (worldReady) { saveFailed = false; RequestSaveNow(); phase = "saving"; }
                        else Log.Warn("save ignored: the world is not ready yet");
                        break;
                    case "snapshot":
                        if (snapshotRequested) { Log.Warn("snapshot already pending; request ignored"); break; }
                        snapshotRequestId = arg;
                        snapshotDir = "";
                        snapshotError = "";
                        if (!worldReady || stopAfterSave) { snapshotError = "World is not ready for a snapshot"; break; }
                        snapshotRequested = true;
                        RequestSaveNow();
                        if (saveFailed) { snapshotRequested = false; snapshotError = lastError; }
                        break;
                    case "stop":
                        if (stopAfterSave) break;
                        stopAfterSave = true; phase = "stopping"; stopDeadline = now + 20000;
                        foreach (var peer in peers.Values.Where(p => p.Authed)) SendTo(peer.Id, Msg.PrepareStop, null);
                        break;
                    case "kick":
                        if (ulong.TryParse(arg, out var id) && peers.TryGetValue(id, out var p)) Drop(p, Why.Kicked, "You were removed by the server");
                        break;
                    case "say":
                        Log.Info("say: " + arg + " (in-game broadcast is not supported yet)");
                        break;
                    default: Log.Warn("unknown command: " + text); break;
                }
            }
        }

        struct ProbeVector { public float X,Y,Z; public ProbeVector(float x,float y,float z) { X=x;Y=y;Z=z; } }
        static void ProbeClean(string argument)
        {
            var world=Il2.Target(worldHandle);
            if (world==IntPtr.Zero || !worldReady) { Log.Warn("Probe requires a ready test world"); return; }
            float z=150;
            if (float.TryParse(argument, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var requested) && float.IsFinite(requested)) z=requested;
            var position=new ProbeVector(100,0,z); var normal=new ProbeVector(0,1,0);
            var surface=Il2.Invoke(Il2.Method(Game.KWorld,"BedrockPoint",2),world,(IntPtr)(&position),(IntPtr)(&normal));
            if (surface!=IntPtr.Zero) position=Il2.Value<ProbeVector>(surface);
            float radius=10, strength=1;
            float before=Il2.Value<float>(Il2.Invoke(Il2.Method(Game.KWorld,"DustAt",1),world,(IntPtr)(&position)));
            bool ok=Il2.TryInvoke(Il2.Method(Game.KWorld,"CleanAt",3),world,out var result,(IntPtr)(&position),(IntPtr)(&radius),(IntPtr)(&strength));
            string error=Il2.LastError;
            float after=Il2.Value<float>(Il2.Invoke(Il2.Method(Game.KWorld,"DustAt",1),world,(IntPtr)(&position)));
            Log.Info("Native cleaning probe: position="+position.X+","+position.Y+","+position.Z+" success="+ok+" changed="+Il2.Int(result)+" dust_before="+before+" dust_after="+after+" error="+error);

        }

        static void WriteState()
        {
            try
            {
                saveFailed |= CleaningSync.RepairFailed;
                if (CleaningSync.RepairFailed) lastError = "Dust synchronization failed; restart required";
                var ready = backendReady && worldReady && !shuttingDown && !stopAfterSave && !saveFailed;
                var state = new
                {
                    schema = 2,
                    instance_id = Cfg.InstanceId,
                    snapshot_dir = snapshotDir,
                    snapshot_unix_ms = snapshotMs,
                    snapshot_error = snapshotError,
                    snapshot_request_id = snapshotRequestId,
                    heartbeat_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    phase,
                    ready,
                    world_in_game = Game.InGame(),
                    world_loading = Game.Loading(),
                    world_found = worldHandle != 0,
                    crew_found = Il2.Invoke(Game.M_CrewInstance, IntPtr.Zero) != IntPtr.Zero,
                    world_bound = WorldBound(),
                    save_pending = saveTask != 0 || saveQueued,
                    save_failed = saveFailed,
                    last_save_unix_ms = lastSaveMs,
                    server_steam_id = serverId.ToString(),
                    players = peers.Values.Where(p => p.Authed).Select(p => new { steam_id = p.Id.ToString(), name = p.Name }).ToArray(),
                    save_dir = SaveDir(),
                    error = lastError,
                    game_port = Cfg.GamePort,
                    query_port = Cfg.QueryPort,
                    mods = modList ??= Mods.Installed().Select(m => m.Guid + "@" + m.Version).ToArray(),
                };
                var tmp = Path.Combine(Cfg.RuntimeDir, "state.json.tmp");
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, Path.Combine(Cfg.RuntimeDir, "state.json"), true);
            }
            catch (Exception e) { Log.Warn("state file: " + e.Message); }
        }
    }
}
