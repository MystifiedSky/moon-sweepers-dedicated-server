// Player side: when the server browser (or a launch option) asks to join a dedicated server,
// this proves the player's Steam identity and mods to the server and then lets the game's own
// join code download the world. When no join is requested it does nothing: normal play,
// lobbies and invites are untouched.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MoonDedicated
{
    internal static unsafe class PlayerClient
    {
        sealed class Request
        {
            public string address { get; set; }
            public int query_port { get; set; }
            public string steam_id { get; set; }
            public string password { get; set; }
            public string name { get; set; }
            public long created_unix_ms { get; set; }
        }

        static bool active, connected, joinStarted, joinedWorld, interfaces, ticketReady, ticketFailed, modsWritten, rosterPublished;
        static ulong serverId, selfId;
        static string password = "", serverName = "Dedicated server", motd = "";
        static string phase = "idle";
        static uint ticket;
        static readonly byte[] ticketBuf = new byte[2048];
        static uint ticketSize;
        static long ticketAt, nextAuth, nextPing, lastHeard, authStarted, nextPoll;
        static long requestStarted;
        static readonly HashSet<ulong> others = new HashSet<ulong>();
        static ulong publishedHash;
        static long joinDeadline;
        static long nextDiagnostics;
        static int tickCount;
        internal static bool IsDedicated => active;
        internal static bool Leaving { get; private set; }
        static bool quitAfterDrain, executingLeave, goodbyeSent, goodbyeAck, leaveFailed;
        static long leaveStarted, leaveQuietSince;
        static D_Static0 originalReturnToMenu, originalQuit;

        static D_Inst0 origUpdate;
        static D_Bool0 oInLobby, oIsHost, oShift;
        static D_U64_0 oLobbyId, oHostId;
        static D_BoolU64 oIsMember, oInSteamLobby, oCanExchangeWith;
        static D_Static0 oRefresh1, oRefresh2, oRefresh3, oPresence, oShare, oInvite, oPublishRemovals, oLeave;
        static D_NextCallback oNext;

        public static bool Install()
        {
            if (!Steam.Load()) return false;
            var L = Game.KLobby;
            oInLobby = Hooks.Method(L, "get_InLobby", 0, (D_Bool0)(mi => active ? (byte)(connected ? 1 : 0) : oInLobby(mi)));
            oIsHost = Hooks.Method(L, "get_IsHost", 0, (D_Bool0)(mi => active ? (byte)0 : oIsHost(mi)));
            oShift = Hooks.Method(L, "get_ShiftStarted", 0, (D_Bool0)(mi => active ? (byte)(connected ? 1 : 0) : oShift(mi)));
            oLobbyId = Hooks.Method(L, "get_LobbyId", 0, (D_U64_0)(mi => active ? serverId : oLobbyId(mi)));
            oHostId = Hooks.Method(L, "get_HostId", 0, (D_U64_0)(mi => active ? serverId : oHostId(mi)));
            oIsMember = Hooks.Method(L, "IsMember", 1, (D_BoolU64)((id, mi) => active ? (byte)(IsMember(id) ? 1 : 0) : oIsMember(id, mi)));
            oInSteamLobby = Hooks.Method(L, "InSteamLobby", 1, (D_BoolU64)((id, mi) => active ? (byte)(IsMember(id) ? 1 : 0) : oInSteamLobby(id, mi)));
            oCanExchangeWith = Hooks.Method(L, "CanExchangeWith", 1, (D_BoolU64)((id, mi) => active ? (byte)(IsMember(id) ? 1 : 0) : oCanExchangeWith(id, mi)));
            oRefresh1 = Hooks.Method(L, "RefreshMembers", 0, (D_Static0)(mi => { if (active) { if (connected) Publish(); } else oRefresh1(mi); }));
            oRefresh2 = Hooks.Method(L, "RefreshMembersNow", 0, (D_Static0)(mi => { if (active) { if (connected) Publish(); } else oRefresh2(mi); }));
            oRefresh3 = Hooks.Method(L, "RefreshLobby", 0, (D_Static0)(mi => { if (active) { if (connected) Publish(); } else oRefresh3(mi); }));
            oPresence = Hooks.Method(L, "UpdatePresence", 0, (D_Static0)(mi => { if (!active) oPresence(mi); }));
            oShare = Hooks.Method(L, "ShareLook", 0, (D_Static0)(mi => { if (!active) oShare(mi); }));
            oInvite = Hooks.Method(L, "InviteFriends", 0, (D_Static0)(mi => { if (!active) oInvite(mi); }));
            oPublishRemovals = Hooks.Method(L, "PublishRemovals", 0, (D_Static0)(mi => { if (!active) oPublishRemovals(mi); }));
            oLeave = Hooks.Method(L, "Leave", 0, (D_Static0)(mi => { if (active) Disconnect(leaveFailed ? "Left with synchronization unconfirmed." : "You left the server.", !leaveFailed); else oLeave(mi); }));
            originalReturnToMenu = Hooks.Method(Game.KSession, "ReturnToMenu", 0, (D_Static0)(mi => RequestLeave(false, mi)));
            originalQuit = Hooks.Method(Game.KSession, "QuitGame", 0, (D_Static0)(mi => RequestLeave(true, mi)));
            foreach (string method in new[] { "Update", "FixedUpdate", "LateUpdate" })
            {
                D_Inst0 original = null;
                original = Hooks.Method(Game.C("MoonWalker"), method, 0, (D_Inst0)((self, mi) => { if (!Leaving) original(self, mi); }));
            }
            origUpdate = Hooks.Method(Game.KSteam, "Update", 0, (D_Inst0)UpdateHook);
            oNext = Hooks.Detour(Steam.NextCallbackExport, (D_NextCallback)NextCallbackHook, "SteamAPI_ManualDispatch_GetNextCallback");
            Log.Info("player side ready (waiting for a join request)");
            return true;
        }

        static bool IsMember(ulong id) => connected && (id == selfId || id == serverId || others.Contains(id));

        static byte NextCallbackHook(int pipe, IntPtr msg)
        {
            byte r = oNext(pipe, msg);
            if (r != 0 && active && ticket != 0)
            {
                int id = *(int*)((byte*)msg + 4);
                if (id == 163) // GetAuthSessionTicketResponse_t
                {
                    byte* p = *(byte**)((byte*)msg + 8);
                    if (p != null && *(uint*)p == ticket) { int result = *(int*)(p + 4); ticketReady = result == 1; ticketFailed = result != 1; }
                }
            }
            return r;
        }

        static void UpdateHook(IntPtr self, IntPtr mi)
        {
            try { origUpdate?.Invoke(self, mi); Tick(); }
            catch (Exception e) { Log.Error("player tick failed: " + e); if (active) Disconnect("Dedicated server connection failed: " + e.Message, false); }
        }

        // ------------------------------------------------------------------ result file for the server browser
        public static void ClearNotice() => LastNotice = "";
        public static string LastNotice { get; private set; } = "";
        static void Result(bool ok, string message, params string[] details)
        {
            LastNotice = message + (details.Length > 0 ? " — " + string.Join("; ", details) : "");
            try
            {
                File.WriteAllText(Path.Combine(Cfg.ModDir, "last-join.json"),
                    JsonSerializer.Serialize(new { ok, message, phase, server = serverName, details, time_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
            if (ok) Log.Info(message); else Log.Warn(message);
        }

        public static void CancelJoin() => Disconnect("Connection cancelled.", true);

        static void RequestLeave(bool quit, IntPtr mi)
        {
            if (executingLeave || !active || !connected || !joinedWorld) { if (quit) originalQuit(mi); else originalReturnToMenu(mi); return; }
            quitAfterDrain |= quit;
            if (Leaving) return;
            goodbyeSent = goodbyeAck = leaveFailed = false;
            Leaving = true; leaveStarted = Environment.TickCount64; leaveQuietSince = 0;
            phase = "finishing-sync";
            LastNotice = "Finishing server synchronization...";
            var crew = Il2.Invoke(Game.M_CrewInstance, IntPtr.Zero);
            var world = crew == IntPtr.Zero ? IntPtr.Zero : Il2.GetRef(crew, Game.F_CrewBound);
            if (world != IntPtr.Zero) leaveFailed |= !Il2.TryInvoke(Il2.Method(Game.KWorld, "CompletePendingToolStrokes", 0), world, out _);
            if (crew != IntPtr.Zero)
            {
                leaveFailed |= !Il2.TryInvoke(Il2.Method(Game.KCrew, "FlushStroke", 0), crew, out _);
                byte force = 1;
                leaveFailed |= !Il2.TryInvoke(Il2.Method(Game.KCrew, "AskDust", 1), crew, out _, (IntPtr)(&force));
            }
            Log.Info("Finishing native tool strokes and reliable messages before leaving");
        }

        static int NativeCount(IntPtr owner, string field)
        {
            var member = Il2.Field(Il2.ClassOf(owner), field);
            if (member == IntPtr.Zero) { leaveFailed = true; return 1; }
            var collection = Il2.GetRef(owner, member);
            if (collection == IntPtr.Zero) return 0;
            if (!Il2.TryInvoke(Il2.Method(Il2.ClassOf(collection), "get_Count", 0), collection, out var count) || count == IntPtr.Zero) {
                leaveFailed = true; return 1;
            }
            return Il2.Int(count);
        }

        static void DrainLeave(long now)
        {
            if (goodbyeSent) {
                if (!goodbyeAck && now - leaveStarted < 15000) return;
                FinishLeave(!goodbyeAck);
                return;
            }
            var crew = Il2.Invoke(Game.M_CrewInstance, IntPtr.Zero);
            var world = crew == IntPtr.Zero ? IntPtr.Zero : Il2.GetRef(crew, Game.F_CrewBound);
            bool pending = crew == IntPtr.Zero || world == IntPtr.Zero;
            if (!pending)
            {
                leaveFailed |= !Il2.TryInvoke(Il2.Method(Game.KCrew, "FlushStroke", 0), crew, out _);
                var outgoing = Il2.Field(Game.KCrew, "hasOutgoing");
                if (outgoing == IntPtr.Zero) { pending = true; leaveFailed = true; }
                else pending = Il2.GetField<byte>(crew, outgoing) != 0;
                foreach (string field in new[] { "dustAsk", "dustAsking", "dustServe", "dustRetry" }) pending |= NativeCount(crew, field) > 0;
                if (!Il2.TryInvoke(Il2.Method(Game.KWorld, "get_PendingToolStrokes", 0), world, out var count) || count == IntPtr.Zero) { leaveFailed = true; pending = true; }
                else pending |= Il2.Int(count) > 0;
                pending |= CleaningSync.RepairFailed;
            }
            // Public SteamNetConnectionRealTimeStatus_t ABI: reliable bytes at
            // offsets 40 (queued) and 44 (sent, not acknowledged). Checking only
            // the queued counter would discard packets still on the wire.
            byte* identity = stackalloc byte[Steam.IdentitySize]; Steam.MakeIdentity(identity, serverId);
            byte* status = stackalloc byte[512]; new Span<byte>(status, 512).Clear();
            int state = Steam.MsgInfo(Steam.ClientMessages, identity, IntPtr.Zero, status);
            pending |= state != 3 || *(int*)(status + 40) != 0 || *(int*)(status + 44) != 0;
            if (pending) leaveQuietSince = 0;
            else if (leaveQuietSince == 0) leaveQuietSince = now;
            bool ready = now - leaveStarted >= 1000 && leaveQuietSince != 0 && now - leaveQuietSince >= 500;
            bool timedOut = now - leaveStarted >= 15000;
            if (!timedOut) {
                if (!ready || leaveFailed) return;
                if (!goodbyeSent) {
                    Steam.Send(Steam.ClientMessages, serverId, Protocol.Frame(Msg.Goodbye, null), Protocol.Channel);
                    goodbyeSent = true;
                    return;
                }
                if (!goodbyeAck) return;
            }
            FinishLeave(timedOut || !goodbyeAck);
        }

        static void FinishLeave(bool unconfirmed)
        {
            leaveFailed |= unconfirmed;
            bool quit = quitAfterDrain;
            var crew = Il2.Invoke(Game.M_CrewInstance, IntPtr.Zero);
            var world = crew == IntPtr.Zero ? IntPtr.Zero : Il2.GetRef(crew, Game.F_CrewBound);
            NativeSyncDiagnostics.Capture(world, "client-leave-drained");
            Leaving = false; quitAfterDrain = false;
            if (leaveFailed) Log.Warn("Leaving before synchronization was confirmed: native or reliable messages remain pending");
            else Log.Info("Native tool strokes and reliable messages drained before leaving");
            executingLeave = true;
            try { if (quit) originalQuit(IntPtr.Zero); else originalReturnToMenu(IntPtr.Zero); }
            finally { executingLeave = false; }
        }

        static void Disconnect(string message, bool ok)
        {
            if (!active) return;
            try { if (connected && !goodbyeAck) Steam.Send(Steam.ClientMessages, serverId, Protocol.Frame(Msg.Goodbye, null), Protocol.Channel); } catch { }
            if (ticket != 0 && Steam.User != IntPtr.Zero) Steam.CancelAuthTicket(Steam.User, ticket);
            if (Steam.ClientMessages != IntPtr.Zero && serverId != 0) {
                byte* identity = stackalloc byte[Steam.IdentitySize];
                Steam.MakeIdentity(identity, serverId);
                Steam.MsgClose(Steam.ClientMessages, identity);
            }
            ticket = 0; ticketReady = ticketFailed = false;
            Leaving = false; quitAfterDrain = false;
            Game.ClearLobbyState();
            connected = false; joinStarted = false; joinedWorld = false; active = false; password = "";
            roster = null; rosterPublished = false; publishedHash = 0;
            Array.Clear(ticketBuf, 0, ticketBuf.Length);
            others.Clear();
            phase = "idle";
            Result(ok, message);
        }

        // ------------------------------------------------------------------ main loop
        static void Tick()
        {
            NativeSyncDiagnostics.Tick();
            long now = Environment.TickCount64;
            tickCount++;
            if (now >= nextDiagnostics) {
                nextDiagnostics = now + 2000;
                try {
                    string path = Path.Combine(Cfg.ModDir, "client-status.json");
                    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new {
                        phase, active, connected, tick_count = tickCount,
                        steam_ready = Game.SteamReady(), in_menu = Game.InMenu(), in_game = Game.InGame(), loading = Game.Loading(),
                        game_version = Game.RunningVersion, time_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        plugin_version = typeof(PlayerClient).Assembly.GetName().Version?.ToString(),
                        menu_rows_added = MenuButton.RowsAdded, menu_error = MenuButton.LastError, browser_open = InGameBrowser.IsOpen, browser_draw_count = InGameBrowser.DrawCount, browser_error = InGameBrowser.LastError
                    }));
                    File.Move(path + ".tmp", path, true);
                } catch (Exception e) { Log.Warn("client status: " + e.Message); }
            }
            if (!modsWritten && now > 15000) { modsWritten = true; Mods.WriteInstalledFile(); }
            // A new selection must replace a pending request, even while the
            // previous server is responding with WaitWorld indefinitely.
            if (now >= nextPoll) { nextPoll = now + 1000; PollRequest(); }
            if (!active) return;
            if (CleaningSync.RepairFailed && !Leaving) { Disconnect("Dust synchronization failed. Rejoin the server before continuing.", false); return; }
            if (Leaving) { Receive(now); if (active) DrainLeave(now); return; }
            if (!joinedWorld && now - requestStarted > 300000) { Disconnect("Joining took over five minutes. Please select the server again to retry.", false); return; }
            if (!Game.SteamReady()) { phase = "waiting-steam"; return; }
            if (!interfaces)
            {
                if (!Steam.ResolveClientInterfaces()) { Disconnect("Steam is not available (is Steam running and the game started from it?)", false); return; }
                selfId = Steam.UserSteamId(Steam.User);
                if (Steam.Utils != IntPtr.Zero) Steam.InitRelayNetworkAccess(Steam.Utils);
                interfaces = true;
                Log.Info("joining " + serverName + " as " + selfId);
            }
            if (ticket == 0)
            {
                byte* id = stackalloc byte[Steam.IdentitySize];
                Steam.MakeIdentity(id, serverId);
                uint size = 0;
                fixed (byte* buf = ticketBuf) ticket = Steam.GetAuthTicket(Steam.User, buf, ticketBuf.Length, &size, id);
                ticketSize = size; ticketAt = now; ticketReady = ticketFailed = false;
                if (ticket == 0 || size == 0 || size > 2048) { Disconnect("Steam could not create a login ticket for this server.", false); return; }
                phase = "waiting-ticket";
            }
            if (ticketFailed) { Disconnect("Steam rejected the login ticket.", false); return; }
            if (!ticketReady && now - ticketAt > 30000) { Disconnect("Steam did not confirm the login ticket in time.", false); return; }
            if (ticketReady && !connected && now >= nextAuth)
            {
                nextAuth = now + 3000;
                if (authStarted == 0) authStarted = now;
                var req = new AuthRequest { Ticket = ticketBuf.Take((int)ticketSize).ToArray(), Password = password, Name = SafeName(), GameVersion = Cfg.GameVersion, Mods = Mods.Installed() };
                try { Steam.Send(Steam.ClientMessages, serverId, Protocol.Frame(Msg.AuthRequest, Protocol.Encode(req)), Protocol.Channel); }
                catch (InvalidDataException e) { Disconnect("Cannot send login inventory: " + e.Message, false); return; }
                phase = "authenticating";
            }
            Receive(now);
            if (!active) return;
            var heard = lastHeard != 0 ? lastHeard : authStarted;
            if (heard != 0 && now - heard > (connected ? Cfg.SessionTimeoutSeconds * 1000L : 60000)) { Disconnect(connected ? "The server stopped answering." : "The server did not answer. Is the game port open and the server running?", false); return; }
            if (connected && now >= nextPing) { nextPing = now + 5000; Steam.Send(Steam.ClientMessages, serverId, Protocol.Frame(Msg.Ping, null), Protocol.Channel); }
            if (connected && !joinStarted) BeginJoin();
            if (joinStarted && !joinedWorld) {
                if (Game.InGame() && !Game.Loading()) { joinedWorld = true; Result(true, "Joined " + serverName + (motd.Length > 0 ? " - " + motd : "")); }
                else if (now >= joinDeadline) Disconnect("World download did not finish within three minutes. Please try again.", false);
            }
        }

        static string SafeName()
        {
            try { var n = Il2.ReadString(Il2.Invoke(Game.M_GetPersona, IntPtr.Zero)); return n.Length == 0 ? "Crewmate" : n; } catch { return "Crewmate"; }
        }

        static void PollRequest()
        {
            var path = Path.Combine(Cfg.ModDir, "connect.json");
            if (!File.Exists(path)) return;
            if (!Game.InMenu() || Game.Loading() || Game.InGame()) return;
            Request r;
            try { r = JsonSerializer.Deserialize<Request>(File.ReadAllText(path)); File.Delete(path); }
            catch (Exception e) { Log.Warn("bad connect.json: " + e.Message); try { File.Delete(path); } catch { } return; }
            if (r == null || !ulong.TryParse(r.steam_id, out var id) || id == 0) { Result(false, "The join request had no valid server Steam ID."); return; }
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - r.created_unix_ms > 10 * 60 * 1000) { Result(false, "The join request was too old; press Join again."); return; }
            CleaningSync.ResetRepairFailure();
            if (active) Disconnect("Switching to the newly selected server.", true);
            serverId = id; serverName = r.name ?? "Dedicated server"; password = r.password ?? "";
            active = true; connected = false; joinStarted = false; joinedWorld = false; interfaces = false; ticket = 0; authStarted = 0; lastHeard = 0; nextAuth = 0;
            roster = null; rosterPublished = false; publishedHash = 0;
            goodbyeSent = goodbyeAck = leaveFailed = false;
            requestStarted = Environment.TickCount64;
            Game.ResetMembers();
            phase = "waiting-steam";
            Log.Info("join request for " + serverName + " (" + serverId + ")");
        }

        static void Receive(long now)
        {
            IntPtr* batch = stackalloc IntPtr[16];
            for (int round = 0; round < 4 && active; round++)
            {
                int n = Steam.MsgReceive(Steam.ClientMessages, Protocol.Channel, batch, 16);
                if (n <= 0 || n > 16) return;
                for (int i = 0; i < n; i++)
                {
                    byte* m = (byte*)batch[i];
                    try
                    {
                        byte* data = *(byte**)m; int size = *(int*)(m + 8); ulong from = Steam.IdentityToSteamId(m + 16);
                        if (from == serverId && Protocol.TryParse(data, size, out var type, out var payload) && active) OnServerMessage(type, payload, now);
                    }
                    catch (Exception e) { Log.Warn("bad control message: " + e.Message); }
                    finally { Steam.MessageRelease(batch[i]); }
                }
                if (n < 16) break;
            }
        }

        static void OnServerMessage(Msg type, byte[] payload, long now)
        {
            switch (type)
            {
                case Msg.PrepareStop:
                    lastHeard = now;
                    RequestLeave(false, IntPtr.Zero);
                    break;
                case Msg.GoodbyeAck: goodbyeAck = true; break;
                case Msg.WaitWorld: lastHeard = now; phase = "waiting-server-world"; break;
                case Msg.Reject:
                    {
                        var r = Protocol.DecodeReject(payload);
                        var msg = r.Message + (r.Details.Count > 0 ? " - " + string.Join("; ", r.Details) : "");
                        if (r.Code == Why.Mods) msg = "Required mods: " + string.Join("; ", r.Details);
                        Disconnect(msg, false);
                        break;
                    }
                case Msg.Roster:
                    {
                        var r = Protocol.DecodeRoster(payload);
                        if (r.Server != serverId || !r.Players.Any(p => p.Key == selfId)) return;
                        lastHeard = now;
                        serverName = r.Name; motd = r.Motd;
                        others.Clear();
                        foreach (var p in r.Players) if (p.Key != selfId) others.Add(p.Key);
                        Array.Clear(ticketBuf, 0, ticketBuf.Length);
                        password = "";
                        roster = r;
                        connected = true;
                        Publish();
                        phase = "authenticated";
                        break;
                    }
            }
        }
        static Roster roster;

        static void Publish()
        {
            if (roster == null) return;
            ulong h = 1469598103934665603UL ^ serverId ^ selfId;
            foreach (var p in roster.Players.OrderBy(p => p.Key)) { h = (h ^ p.Key) * 1099511628211UL; foreach (var ch in p.Value) h = (h ^ ch) * 1099511628211UL; }
            if (!rosterPublished || h != publishedHash)
            {
                Game.ResetMembers();
                Game.AddMember(serverId, roster.Name, serverId, selfId);
                foreach (var p in roster.Players) Game.AddMember(p.Key, p.Value, serverId, selfId);
                publishedHash = h;
                rosterPublished = true;
                Game.PublishLobbyState(serverId, false, serverName);
                Game.RaiseLobbyChanged();
                return;
            }
            Game.PublishLobbyState(serverId, false, serverName);
        }

        static void BeginJoin()
        {
            if (!Game.InMenu() || Game.NewHireActive()) return;
            var crew = Il2.Invoke(Game.M_CrewInstance, IntPtr.Zero);
            if (crew == IntPtr.Zero || Game.M_JoinCtor == IntPtr.Zero) return;
            var join = Il2.New(Game.KJoin);
            ulong a = serverId, b = serverId;
            if (join == IntPtr.Zero || !Il2.TryInvoke(Game.M_JoinCtor, join, out _, (IntPtr)(&a), (IntPtr)(&b), Il2.NewString(serverName)) ||
                !Il2.TryInvoke(Game.M_BeginJoin, crew, out _, join)) {
                Disconnect("The game could not begin joining: " + Il2.LastError, false); return;
            }
            joinStarted = true;
            joinDeadline = Environment.TickCount64 + 180000;
            phase = "downloading-world";
            Log.Info("Authenticated with " + serverName + "; downloading the world...");
        }
    }
}
