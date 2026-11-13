// Handles to the game's own types, found by name at run time (namespace MoonCleaners
// in Assembly-CSharp.dll). If an update renames something, Resolve() reports which name is missing.
using System;
using System.Collections.Generic;
using System.IO;

namespace MoonDedicated
{
    internal static unsafe class Game
    {
        public static IntPtr Image, SteamworksImage, CoreImage;
        public static IntPtr KSteam, KSession, KLobby, KMember, KCrew, KJoin, KNewHire, KWorld, KSaveSlots, KMenu, KIntro, KLoading;
        public static IntPtr M_SteamInitialized, M_SetInitialized, M_SetSteamId, M_SetPersona, M_GetPersona;
        public static IntPtr M_InMenu, M_InGame, M_Loading, M_SessionStartShift, M_Load, M_QuitGame;
        public static IntPtr M_InLobby, M_IsHost, M_Busy, M_ShiftStarted;
        public static IntPtr M_SetSlot, M_Dismiss, M_NewHireActive, M_CrewInstance, M_BeginJoin, M_JoinCtor, M_SaveProgress;
        public static IntPtr F_CrewBound, F_GameScene, F_WorldProgress, F_WorldRegions, F_Members;
        public static IntPtr F_MemberId, F_MemberName, F_MemberLook, F_MemberReady, F_MemberHost, F_MemberLocal;
        public static IntPtr ListClear, ListAdd, F_LobbyChanged;
        public static readonly List<string> Missing = new List<string>();

        public static IntPtr C(string name) => Il2.Class(Image, "MoonCleaners", name);

        public static string RunningVersion = "";
        public static bool CheckVersion()
        {
            var core = Il2.Image("UnityEngine.CoreModule.dll");
            var app = Il2.Class(core, "UnityEngine", "Application");
            RunningVersion = Il2.ReadString(Il2.Invoke(Il2.Method(app, "get_version", 0), IntPtr.Zero));
            if (RunningVersion == Cfg.GameVersion) return true;
            Log.Error("Unsupported running game version '" + RunningVersion + "'; this plugin requires " + Cfg.GameVersion + ". No hooks were installed.");
            return false;
        }

        static IntPtr M(IntPtr k, string name, int argc, string label)
        {
            var m = Il2.Method(k, name, argc);
            if (m == IntPtr.Zero || Il2.Code(m) == IntPtr.Zero) Missing.Add(label);
            return m;
        }
        static IntPtr F(IntPtr k, string name, string label)
        {
            var f = Il2.Field(k, name);
            if (f == IntPtr.Zero) Missing.Add(label);
            return f;
        }

        public static bool Resolve()
        {
            Image = Il2.Image("Assembly-CSharp.dll");
            SteamworksImage = Il2.Image("com.rlabrecque.steamworks.net.dll");
            CoreImage = Il2.Image("UnityEngine.CoreModule.dll");
            if (Image == IntPtr.Zero) { Log.Error("Assembly-CSharp.dll is not loaded yet"); return false; }
            KSteam = C("MoonSteam"); KSession = C("MoonSession"); KLobby = C("MoonSteamLobby"); KCrew = C("MoonCrewSession");
            KNewHire = C("MoonNewHire"); KWorld = C("MoonWorld"); KSaveSlots = C("MoonSaveSlots"); KMenu = C("MoonMainMenu");
            KIntro = C("MoonStudioIntro"); KLoading = C("MoonLoadingScreen");
            foreach (var kv in new[] { (KSteam, "MoonSteam"), (KSession, "MoonSession"), (KLobby, "MoonSteamLobby"), (KCrew, "MoonCrewSession"), (KNewHire, "MoonNewHire"), (KWorld, "MoonWorld"), (KSaveSlots, "MoonSaveSlots") })
                if (kv.Item1 == IntPtr.Zero) Missing.Add("class " + kv.Item2);
            if (Missing.Count > 0) return false;

            KMember = Il2.Class(Image, "MoonCleaners", "MoonSteamLobby/Member");
            if (KMember == IntPtr.Zero) KMember = Il2.Nested(KLobby, "Member");
            KJoin = Il2.Nested(KCrew, "CrewJoin");
            if (KMember == IntPtr.Zero) Missing.Add("class MoonSteamLobby.Member");
            if (KJoin == IntPtr.Zero) Missing.Add("class MoonCrewSession.CrewJoin");

            M_SteamInitialized = M(KSteam, "get_Initialized", 0, "MoonSteam.get_Initialized");
            M_SetInitialized = M(KSteam, "set_Initialized", 1, "MoonSteam.set_Initialized");
            M_SetSteamId = M(KSteam, "set_SteamId", 1, "MoonSteam.set_SteamId");
            M_SetPersona = M(KSteam, "set_PersonaName", 1, "MoonSteam.set_PersonaName");
            M_GetPersona = M(KSteam, "get_PersonaName", 0, "MoonSteam.get_PersonaName");
            M_InMenu = M(KSession, "get_InMenu", 0, "MoonSession.get_InMenu");
            M_InGame = M(KSession, "get_InGame", 0, "MoonSession.get_InGame");
            M_Loading = M(KSession, "get_Loading", 0, "MoonSession.get_Loading");
            M_SessionStartShift = M(KSession, "StartShift", 0, "MoonSession.StartShift");
            M_Load = M(KSession, "Load", 1, "MoonSession.Load");
            M_QuitGame = M(KSession, "QuitGame", 0, "MoonSession.QuitGame");
            M_InLobby = M(KLobby, "get_InLobby", 0, "MoonSteamLobby.get_InLobby");
            M_IsHost = M(KLobby, "get_IsHost", 0, "MoonSteamLobby.get_IsHost");
            M_Busy = M(KLobby, "get_Busy", 0, "MoonSteamLobby.get_Busy");
            M_ShiftStarted = M(KLobby, "get_ShiftStarted", 0, "MoonSteamLobby.get_ShiftStarted");
            M_SetSlot = M(KSaveSlots, "set_Active", 1, "MoonSaveSlots.set_Active");
            M_Dismiss = M(KNewHire, "Dismiss", 0, "MoonNewHire.Dismiss");
            M_NewHireActive = M(KNewHire, "get_Active", 0, "MoonNewHire.get_Active");
            M_CrewInstance = M(KCrew, "get_Instance", 0, "MoonCrewSession.get_Instance");
            M_BeginJoin = M(KCrew, "BeginJoin", 1, "MoonCrewSession.BeginJoin");
            M_JoinCtor = KJoin == IntPtr.Zero ? IntPtr.Zero : M(KJoin, ".ctor", 3, "CrewJoin..ctor");
            M_SaveProgress = M(KWorld, "SaveProgress", 1, "MoonWorld.SaveProgress");

            F_CrewBound = F(KCrew, "bound", "MoonCrewSession.bound");
            F_GameScene = F(KSession, "GameScene", "MoonSession.GameScene");
            F_WorldProgress = F(KWorld, "progressStore", "MoonWorld.progressStore");
            F_WorldRegions = F(KWorld, "regionStates", "MoonWorld.regionStates");
            F_Members = F(KLobby, "members", "MoonSteamLobby.members");
            if (KMember != IntPtr.Zero)
            {
                F_MemberId = F(KMember, "id", "Member.id"); F_MemberName = F(KMember, "name", "Member.name"); F_MemberLook = F(KMember, "look", "Member.look");
                F_MemberReady = F(KMember, "ready", "Member.ready"); F_MemberHost = F(KMember, "host", "Member.host"); F_MemberLocal = F(KMember, "local", "Member.local");
            }
            // The game's own roster refresh (GameAssembly 0x18059e1a0) rebuilds the member list and then raises
            // this static Action (static-block offset 0x38) so the crew session notices who joined or left.
            F_LobbyChanged = Il2.Field(KLobby, "Changed");
            if (F_LobbyChanged == IntPtr.Zero) F_LobbyChanged = Il2.StaticFieldAt(KLobby, 56 /* 0x38 */, "System.Action");
            if (F_LobbyChanged == IntPtr.Zero) Log.Warn("MoonSteamLobby 'Changed' event not found: the game may not notice players leaving");
            if (Missing.Count > 0) return false;

            var list = Il2.GetStaticRef(F_Members);
            if (list == IntPtr.Zero) { Missing.Add("MoonSteamLobby.members list is null"); return false; }
            var lk = Il2.ClassOf(list);
            ListClear = Il2.Method(lk, "Clear", 0); ListAdd = Il2.Method(lk, "Add", 1);
            return ListClear != IntPtr.Zero && ListAdd != IntPtr.Zero;
        }

        // ---- reading the game's state ---------------------------------------------------------
        public static bool SteamReady() => Il2.CallBool(M_SteamInitialized);
        public static bool InMenu() => Il2.CallBool(M_InMenu);
        public static bool InGame() => Il2.CallBool(M_InGame);
        public static bool Loading() => Il2.CallBool(M_Loading);
        public static bool NewHireActive() => Il2.CallBool(M_NewHireActive);

        // ---- the lobby the game believes it is in -------------------------------------------
        static void Static(string field, IntPtr value) => Il2.SetStaticRef(Il2.Field(KLobby, field), value);
        static void StaticBool(string field, bool v) => Il2.SetStatic(Il2.Field(KLobby, field), (byte)(v ? 1 : 0));
        static void StaticU64(string field, ulong v) => Il2.SetStatic(Il2.Field(KLobby, field), v);

        public static void AddMember(ulong id, string name, ulong host, ulong self)
        {
            var m = Il2.New(KMember);
            if (m == IntPtr.Zero) return;
            Il2.SetField(m, F_MemberId, id);
            Il2.SetRef(m, F_MemberName, Il2.NewString(name));
            Il2.SetRef(m, F_MemberLook, Il2.NewString(""));
            Il2.SetField(m, F_MemberReady, (byte)1);
            Il2.SetField(m, F_MemberHost, (byte)(id == host ? 1 : 0));
            Il2.SetField(m, F_MemberLocal, (byte)(id == self ? 1 : 0));
            Il2.Invoke(ListAdd, Il2.GetStaticRef(F_Members), m);
        }

        /// Tell the game the member list changed, exactly as its own RefreshMembers does after rebuilding it.
        public static void RaiseLobbyChanged()
        {
            if (F_LobbyChanged == IntPtr.Zero) return;
            var del = Il2.GetStaticRef(F_LobbyChanged);
            if (del == IntPtr.Zero) return;
            Il2.Invoke(Il2.Method(Il2.ClassOf(del), "Invoke", 0), del);
        }

        public static void ResetMembers() => Il2.Invoke(ListClear, Il2.GetStaticRef(F_Members));

        public static void PublishLobbyState(ulong host, bool isHost, string crewName)
        {
            StaticU64("lobby", host); StaticU64("<HostId>k__BackingField", host);
            StaticBool("<IsHost>k__BackingField", isHost); StaticBool("<ShiftStarted>k__BackingField", true); StaticBool("<Busy>k__BackingField", false);
            Il2.SetStatic(Il2.Field(KLobby, "<CurrentPrivacy>k__BackingField"), 2);
            Static("<CrewName>k__BackingField", Il2.NewString(crewName));
            Static("<Status>k__BackingField", Il2.NewString(""));
            StaticBool("<StatusIsError>k__BackingField", false);
        }

        public static void ClearLobbyState()
        {
            ResetMembers();
            StaticU64("lobby", 0); StaticU64("<HostId>k__BackingField", 0);
            StaticBool("<IsHost>k__BackingField", false); StaticBool("<ShiftStarted>k__BackingField", false);
        }

        /// Best-effort listing of the classes behind vehicles, Joonas and saving, for the next debugging session.
        public static void DumpClasses(string path)
        {
            try
            {
                using var w = new StreamWriter(path, false);
                w.WriteLine("# Moon Sweepers class dump " + DateTime.UtcNow.ToString("o"));
                foreach (var n in new[] { "MoonMainMenu", "MoonSimpleKit", "MoonSimpleDialog", "MoonSteam", "MoonCrewSession", "MoonWorld", "MoonOwnedVehicle", "MoonOwnedVehicleSave", "MoonRemoteVehicle", "MoonVehicleStorageSave", "MoonGarage", "MoonGarageSave",
                                          "MoonJoonasMotion", "MoonJoonasRig", "MoonJoonasGauge", "MoonJoonasImporter", "MoonRobotVacuum", "MoonRemotePlayer", "MoonPlayerRecordSave", "MoonProgressStore", "MoonCleanMask", "MoonChunkSnapshot", "MoonPageSnapshot", "CrewStroke", "MoonSteamLobby" })
                {
                    var k = C(n);
                    w.WriteLine(); w.WriteLine(n + (k == IntPtr.Zero ? "  (not found)" : ""));
                    if (k != IntPtr.Zero) w.Write(Il2.Describe(k));
                }
                var choice = Il2.Nested(C("MoonSimpleKit"), "Choice");
                if (choice != IntPtr.Zero) { w.WriteLine("\nMoonSimpleKit.Choice"); w.Write(Il2.Describe(choice)); }
                foreach (var pair in new[] { (KWorld, "Chunk"), (C("MoonCleanMask"), "Page") }) {
                    var nested = Il2.Nested(pair.Item1, pair.Item2);
                    if (nested != IntPtr.Zero) { w.WriteLine("\n" + pair.Item2); w.Write(Il2.Describe(nested)); }
                }
            }
            catch (Exception e) { Log.Warn("class dump failed: " + e.Message); }
        }
    }
}
