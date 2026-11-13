// Steamworks flat C API (steam_api64.dll), resolved by export name. These are the
// public, documented Steamworks SDK entry points; no game code is involved.
using System;
using System.Runtime.InteropServices;
using System.IO;

namespace MoonDedicated
{
    internal static unsafe class Steam
    {
        public const uint AppId = 5335510;
        public static IntPtr Module;

        // identity / message layout (Steamworks SDK 1.57)
        public const int IdentitySize = 136;
        public const int IdentityTypeSteamId = 16;

        static IntPtr Exp(string name)
        {
            if (!NativeLibrary.TryGetExport(Module, name, out var p)) throw new EntryPointNotFoundException(name);
            return p;
        }
        static IntPtr OptExp(string name) => NativeLibrary.TryGetExport(Module, name, out var p) ? p : IntPtr.Zero;
        static IntPtr TryIface(params string[] names)
        {
            foreach (var n in names)
                if (NativeLibrary.TryGetExport(Module, n, out var p)) return ((delegate* unmanaged[Cdecl]<IntPtr>)p)();
            return IntPtr.Zero;
        }

        // ---- game server ----
        public static delegate* unmanaged[Cdecl]<uint, ushort, ushort, ushort, int, byte*, byte> GameServerInit;
        public static delegate* unmanaged[Cdecl]<void> GameServerShutdown;
        public static delegate* unmanaged[Cdecl]<int> GameServerPipe;
        public static delegate* unmanaged[Cdecl]<IntPtr, byte*, void> SetProduct, SetGameDescription, SetModDir, SetServerName, SetMapName, SetGameTags, SetGameData;
        public static delegate* unmanaged[Cdecl]<IntPtr, byte, void> SetDedicated, SetAdvertise, SetPasswordProtected;
        public static delegate* unmanaged[Cdecl]<IntPtr, int, void> SetMaxPlayers, SetBotPlayers;
        public static delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, void> SetKeyValue;
        public static delegate* unmanaged[Cdecl]<IntPtr, void> ClearKeyValues, LogOnAnonymous;
        public static delegate* unmanaged[Cdecl]<IntPtr, byte> LoggedOn;
        public static delegate* unmanaged[Cdecl]<IntPtr, ulong> ServerSteamId;
        public static delegate* unmanaged[Cdecl]<IntPtr, void*, int, ulong, int> BeginAuthSession;
        public static delegate* unmanaged[Cdecl]<IntPtr, ulong, void> EndAuthSession;
        public static delegate* unmanaged[Cdecl]<IntPtr, ulong, uint, int> UserHasLicense;
        public static delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, uint, byte> UpdateUserData; // optional
        public static IntPtr Server, ServerMessages, ServerSockets, Utils;

        // ---- networking ----
        public static delegate* unmanaged[Cdecl]<IntPtr, int> InitAuthentication;
        public static delegate* unmanaged[Cdecl]<IntPtr, void> InitRelayNetworkAccess;
        public static delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, uint, int, int, int> MsgSend;       // self, identity*, data, size, flags, channel
        public static delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr*, int, int> MsgReceive;       // self, channel, out, max
        public static delegate* unmanaged[Cdecl]<IntPtr, byte*, byte> MsgAccept, MsgClose;
        public static delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr, byte*, int> MsgInfo;
        public static delegate* unmanaged[Cdecl]<IntPtr, void> MessageRelease;

        // ---- callbacks (manual dispatch) ----
        public static delegate* unmanaged[Cdecl]<void> DispatchInit;
        public static delegate* unmanaged[Cdecl]<int, void> DispatchRunFrame;
        public static delegate* unmanaged[Cdecl]<int, void*, byte> DispatchNext;
        public static delegate* unmanaged[Cdecl]<int, void> DispatchFree;
        public static IntPtr NextCallbackExport;

        // ---- client ----
        public static IntPtr User, ClientMessages;
        public static delegate* unmanaged[Cdecl]<IntPtr, ulong> UserSteamId;
        public static delegate* unmanaged[Cdecl]<IntPtr, void*, int, uint*, byte*, uint> GetAuthTicket;
        public static delegate* unmanaged[Cdecl]<IntPtr, uint, void> CancelAuthTicket;

        public static bool Load()
        {
            if (!NativeLibrary.TryLoad("steam_api64", out Module) && !NativeLibrary.TryLoad("steam_api64.dll", out Module)) {
                string root = Path.GetDirectoryName(Environment.ProcessPath);
                string path = Path.Combine(root, "MoonSweepers_Data", "Plugins", "x86_64", "steam_api64.dll");
                if (!NativeLibrary.TryLoad(path, out Module)) { Log.Error("Steam API could not be loaded from " + path); return false; }
            }
            try
            {
                GameServerInit = (delegate* unmanaged[Cdecl]<uint, ushort, ushort, ushort, int, byte*, byte>)Exp("SteamInternal_GameServer_Init");
                GameServerShutdown = (delegate* unmanaged[Cdecl]<void>)Exp("SteamGameServer_Shutdown");
                GameServerPipe = (delegate* unmanaged[Cdecl]<int>)Exp("SteamGameServer_GetHSteamPipe");
                const string G = "SteamAPI_ISteamGameServer_";
                SetProduct = (delegate* unmanaged[Cdecl]<IntPtr, byte*, void>)Exp(G + "SetProduct");
                SetGameDescription = (delegate* unmanaged[Cdecl]<IntPtr, byte*, void>)Exp(G + "SetGameDescription");
                SetModDir = (delegate* unmanaged[Cdecl]<IntPtr, byte*, void>)Exp(G + "SetModDir");
                SetServerName = (delegate* unmanaged[Cdecl]<IntPtr, byte*, void>)Exp(G + "SetServerName");
                SetMapName = (delegate* unmanaged[Cdecl]<IntPtr, byte*, void>)Exp(G + "SetMapName");
                SetGameTags = (delegate* unmanaged[Cdecl]<IntPtr, byte*, void>)Exp(G + "SetGameTags");
                SetGameData = (delegate* unmanaged[Cdecl]<IntPtr, byte*, void>)Exp(G + "SetGameData");
                SetDedicated = (delegate* unmanaged[Cdecl]<IntPtr, byte, void>)Exp(G + "SetDedicatedServer");
                SetAdvertise = (delegate* unmanaged[Cdecl]<IntPtr, byte, void>)Exp(G + "SetAdvertiseServerActive");
                SetPasswordProtected = (delegate* unmanaged[Cdecl]<IntPtr, byte, void>)Exp(G + "SetPasswordProtected");
                SetMaxPlayers = (delegate* unmanaged[Cdecl]<IntPtr, int, void>)Exp(G + "SetMaxPlayerCount");
                SetBotPlayers = (delegate* unmanaged[Cdecl]<IntPtr, int, void>)Exp(G + "SetBotPlayerCount");
                SetKeyValue = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, void>)Exp(G + "SetKeyValue");
                ClearKeyValues = (delegate* unmanaged[Cdecl]<IntPtr, void>)Exp(G + "ClearAllKeyValues");
                LogOnAnonymous = (delegate* unmanaged[Cdecl]<IntPtr, void>)Exp(G + "LogOnAnonymous");
                LoggedOn = (delegate* unmanaged[Cdecl]<IntPtr, byte>)Exp(G + "BLoggedOn");
                ServerSteamId = (delegate* unmanaged[Cdecl]<IntPtr, ulong>)Exp(G + "GetSteamID");
                BeginAuthSession = (delegate* unmanaged[Cdecl]<IntPtr, void*, int, ulong, int>)Exp(G + "BeginAuthSession");
                EndAuthSession = (delegate* unmanaged[Cdecl]<IntPtr, ulong, void>)Exp(G + "EndAuthSession");
                UserHasLicense = (delegate* unmanaged[Cdecl]<IntPtr, ulong, uint, int>)Exp(G + "UserHasLicenseForApp");
                UpdateUserData = (delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, uint, byte>)OptExp(G + "BUpdateUserData");
                InitAuthentication = (delegate* unmanaged[Cdecl]<IntPtr, int>)Exp("SteamAPI_ISteamNetworkingSockets_InitAuthentication");
                InitRelayNetworkAccess = (delegate* unmanaged[Cdecl]<IntPtr, void>)Exp("SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess");
                const string M = "SteamAPI_ISteamNetworkingMessages_";
                MsgSend = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, uint, int, int, int>)Exp(M + "SendMessageToUser");
                MsgReceive = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr*, int, int>)Exp(M + "ReceiveMessagesOnChannel");
                MsgAccept = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte>)Exp(M + "AcceptSessionWithUser");
                MsgClose = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte>)Exp(M + "CloseSessionWithUser");
                MsgInfo = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr, byte*, int>)Exp(M + "GetSessionConnectionInfo");
                MessageRelease = (delegate* unmanaged[Cdecl]<IntPtr, void>)Exp("SteamAPI_SteamNetworkingMessage_t_Release");
                DispatchInit = (delegate* unmanaged[Cdecl]<void>)Exp("SteamAPI_ManualDispatch_Init");
                DispatchRunFrame = (delegate* unmanaged[Cdecl]<int, void>)Exp("SteamAPI_ManualDispatch_RunFrame");
                DispatchNext = (delegate* unmanaged[Cdecl]<int, void*, byte>)Exp("SteamAPI_ManualDispatch_GetNextCallback");
                DispatchFree = (delegate* unmanaged[Cdecl]<int, void>)Exp("SteamAPI_ManualDispatch_FreeLastCallback");
                NativeLibrary.TryGetExport(Module, "SteamAPI_ManualDispatch_GetNextCallback", out NextCallbackExport);
                UserSteamId = (delegate* unmanaged[Cdecl]<IntPtr, ulong>)Exp("SteamAPI_ISteamUser_GetSteamID");
                GetAuthTicket = (delegate* unmanaged[Cdecl]<IntPtr, void*, int, uint*, byte*, uint>)Exp("SteamAPI_ISteamUser_GetAuthSessionTicket");
                CancelAuthTicket = (delegate* unmanaged[Cdecl]<IntPtr, uint, void>)Exp("SteamAPI_ISteamUser_CancelAuthTicket");
            }
            catch (Exception e) { Log.Error("Steam API export missing: " + e.Message); return false; }
            return true;
        }

        public static bool ResolveServerInterfaces()
        {
            Server = TryIface("SteamAPI_SteamGameServer_v015", "SteamAPI_SteamGameServer_v014");
            ServerMessages = TryIface("SteamAPI_SteamGameServerNetworkingMessages_SteamAPI_v002");
            ServerSockets = TryIface("SteamAPI_SteamGameServerNetworkingSockets_SteamAPI_v012");
            Utils = TryIface("SteamAPI_SteamNetworkingUtils_SteamAPI_v004", "SteamAPI_SteamNetworkingUtils_SteamAPI_v003", "SteamAPI_SteamNetworkingUtils_SteamAPI_v005");
            return Server != IntPtr.Zero && ServerMessages != IntPtr.Zero;
        }

        public static bool ResolveClientInterfaces()
        {
            ClientMessages = TryIface("SteamAPI_SteamNetworkingMessages_SteamAPI_v002");
            User = TryIface("SteamAPI_SteamUser_v023", "SteamAPI_SteamUser_v022", "SteamAPI_SteamUser_v021", "SteamAPI_SteamUser_v020");
            Utils = TryIface("SteamAPI_SteamNetworkingUtils_SteamAPI_v004", "SteamAPI_SteamNetworkingUtils_SteamAPI_v003", "SteamAPI_SteamNetworkingUtils_SteamAPI_v005");
            return User != IntPtr.Zero && ClientMessages != IntPtr.Zero;
        }

        // ---- small helpers ----
        public static void Str(delegate* unmanaged[Cdecl]<IntPtr, byte*, void> f, IntPtr self, string s) { using var c = new CStr(s); f(self, c.P); }

        public static void MakeIdentity(byte* buf, ulong steamId)
        {
            for (int i = 0; i < IdentitySize; i++) buf[i] = 0;
            *(int*)buf = IdentityTypeSteamId;
            *(int*)(buf + 4) = 8;
            *(ulong*)(buf + 8) = steamId;
        }

        public static ulong IdentityToSteamId(byte* identity)
        {
            if (identity == null) return 0;
            return *(int*)identity == IdentityTypeSteamId && *(int*)(identity + 4) == 8 ? *(ulong*)(identity + 8) : 0;
        }

        /// Send flags the game itself uses (decompiled MoonCrewSession.SendBytes, GameAssembly 0x1807abcd0):
        /// reliable = 0x28, unreliable = 0x25. Control messages are always reliable.
        public const int SendReliable = 40 /* 0x28 */, SendUnreliable = 37 /* 0x25 */;
        public const int ControlFlags = SendReliable;

        public static int Send(IntPtr messages, ulong to, byte[] data, int channel)
        {
            byte* id = stackalloc byte[IdentitySize];
            MakeIdentity(id, to);
            fixed (byte* d = data) return MsgSend(messages, id, d, (uint)data.Length, ControlFlags, channel);
        }
    }
}
