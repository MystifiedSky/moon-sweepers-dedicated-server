// Exercises actual mod classes. Only logging and BepInEx inventory are stubbed.
using MoonDedicated;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

var tests = new List<string>();
void Assert(bool condition) { if (!condition) throw new Exception("Regression assertion failed"); }
void Check(string name, Action test) { test(); tests.Add(name); Console.WriteLine(name + ": passed"); }
bool Rejects(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } catch (EndOfStreamException) { return true; } }
FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
void Set(Type type, string name, object value) => Field(type, name).SetValue(null, value);
object Get(Type type, string name) => Field(type, name).GetValue(null);
object Call(Type type, string name, params object[] args) => type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);

Check("oversized login is rejected locally", () => {
    var request = new AuthRequest { Ticket = new byte[2048], GameVersion = Cfg.GameVersion };
    for (int i = 0; i < 200; i++) request.Mods.Add(new ModInfo { Guid = "author." + i + new string('x', 60), Version = "1.2.3.1234567890" });
    Assert(Rejects(() => Protocol.Encode(request)));
    Assert(Rejects(() => Protocol.Frame(Msg.AuthRequest, new byte[Protocol.MaxPayload + 1])));
});
Check("auth roundtrip rejects all truncations and trailing data", () => {
    var request = new AuthRequest { Ticket = new byte[] { 1, 2, 3 }, Password = "secret", GameVersion = Cfg.GameVersion };
    request.Mods.Add(new ModInfo { Guid = "test.mod", Version = "1.0" });
    var data = Protocol.Encode(request);
    var decoded = Protocol.DecodeAuth(data);
    Assert(decoded.Password == "secret" && decoded.Mods[0].Guid == "test.mod");
    Assert(Rejects(() => Protocol.DecodeAuth(data.Concat(new byte[] { 0 }).ToArray())));
    for (int i = 0; i < data.Length; i++) Assert(Rejects(() => Protocol.DecodeAuth(data[..i])));
});
Check("truncated final strings are rejected", () => {
    using var bytes = new MemoryStream();
    using (var w = new BinaryWriter(bytes, Encoding.UTF8, true)) {
        w.Write(1u); w.Write((byte)1);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write((ushort)1); w.Write((ushort)4); w.Write(Encoding.UTF8.GetBytes("test"));
        w.Write((ushort)100); w.Write((byte)'v');
    }
    Assert(Rejects(() => Protocol.DecodeAuth(bytes.ToArray())));
});
Check("large rejection stays within frame limits", () => {
    var rejection = new Reject { Message = new string('x', 3000), Details = Enumerable.Repeat(new string('y', 3000), 256).ToList() };
    var data = Protocol.Encode(rejection);
    Assert(Protocol.Frame(Msg.Reject, data).Length <= Protocol.MaxPayload + 12);
    Assert(Protocol.DecodeReject(data).Details.Count == 16);
});
Check("reconnect invalidates the published roster", () => {
    string root = Path.Combine(Path.GetTempPath(), "moon-client-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try {
        Cfg.ModDir = root;
        Set(typeof(PlayerClient), "active", true); Set(typeof(PlayerClient), "connected", false);
        Set(typeof(PlayerClient), "rosterPublished", true); Set(typeof(PlayerClient), "publishedHash", 123UL);
        Set(typeof(PlayerClient), "roster", new Roster());
        Call(typeof(PlayerClient), "Disconnect", "test", false);
        Assert(!(bool)Get(typeof(PlayerClient), "rosterPublished"));
        Assert(Get(typeof(PlayerClient), "roster") == null && (ulong)Get(typeof(PlayerClient), "publishedHash") == 0);
    } finally { Directory.Delete(root, true); }
});
Check("pending authentication reserves capacity", () => {
    var peers = (IDictionary)Get(typeof(DedicatedServer), "peers"); peers.Clear();
    Cfg.MaxPlayers = 1; Cfg.Policy = ModPolicy.Off;
    var peerType = typeof(DedicatedServer).GetNestedType("Peer", BindingFlags.NonPublic);
    peers.Add(76561198000000001UL, Activator.CreateInstance(peerType));
    var payload = Protocol.Encode(new AuthRequest { Ticket = new byte[] { 1 }, GameVersion = Cfg.GameVersion });
    Call(typeof(DedicatedServer), "OnAuthRequest", 76561198000000002UL, null, payload, 1L);
    Assert(peers.Count == 1 && !peers.Contains(76561198000000002UL)); peers.Clear();
});
Check("late ticket validation enforces capacity", () => {
    var peers = (IDictionary)Get(typeof(DedicatedServer), "peers"); peers.Clear();
    var peerType = typeof(DedicatedServer).GetNestedType("Peer", BindingFlags.NonPublic);
    object Peer(ulong id, bool authed) {
        var peer = Activator.CreateInstance(peerType);
        peerType.GetField("Id").SetValue(peer, id); peerType.GetField("Authed").SetValue(peer, authed);
        peerType.GetField("Since").SetValue(peer, Environment.TickCount64);
        return peer;
    }
    peers.Add(76561198000000001UL, Peer(76561198000000001UL, true));
    peers.Add(76561198000000002UL, Peer(76561198000000002UL, false));
    unsafe {
        Steam.UserHasLicense = &NativeStubs.License; Steam.EndAuthSession = &NativeStubs.EndAuth;
        byte* callback = stackalloc byte[24];
        for (int i = 0; i < 24; i++) callback[i] = 0;
        *(ulong*)callback = 76561198000000002UL;
        Call(typeof(DedicatedServer), "OnCallback", 143, Pointer.Box(callback, typeof(byte*)), 24);
    }
    Assert(peers.Count == 1 && !peers.Contains(76561198000000002UL)); peers.Clear();
});
Check("timed out save retains its writer task", () => {
    nuint handle = unchecked((nuint)0x123456789UL);
    Set(typeof(DedicatedServer), "saveTask", handle); Set(typeof(DedicatedServer), "saveDeadline", 90000L);
    Set(typeof(DedicatedServer), "saveTimedOut", false); Set(typeof(DedicatedServer), "saveFailed", false);
    Assert((bool)Call(typeof(DedicatedServer), "WaitForSave", false, true, 89999L));
    Assert(!(bool)Get(typeof(DedicatedServer), "saveFailed"));
    Assert((bool)Call(typeof(DedicatedServer), "WaitForSave", false, true, 90000L));
    Assert((nuint)Get(typeof(DedicatedServer), "saveTask") == handle && (bool)Get(typeof(DedicatedServer), "saveFailed"));
    Assert(!(bool)Call(typeof(DedicatedServer), "WaitForSave", true, true, 100000L));
    Set(typeof(DedicatedServer), "saveTask", (nuint)0);
});
Check("Unity object handles preserve all native pointer bits", () => {
    unsafe {
        void Bind(string field, IntPtr pointer) {
            var setter = new System.Reflection.Emit.DynamicMethod("bind_native_stub", typeof(void), new[] { typeof(IntPtr) }, typeof(Il2), true);
            var il = setter.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Stsfld, Field(typeof(Il2), field));
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            ((Action<IntPtr>)setter.CreateDelegate(typeof(Action<IntPtr>)))(pointer);
        }
        Bind("gchandle_new", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte, nuint>)&NativeStubs.NewHandle);
        Bind("gchandle_get_target", (IntPtr)(delegate* unmanaged[Cdecl]<nuint, IntPtr>)&NativeStubs.HandleTarget);
        Bind("gchandle_free", (IntPtr)(delegate* unmanaged[Cdecl]<nuint, void>)&NativeStubs.FreeHandle);
        try {
            var handle = Il2.Pin((IntPtr)123);
            Assert(handle == unchecked((nuint)0x123456789UL));
            Assert(Il2.Target(handle) == (IntPtr)123);
            Il2.Unpin(handle);
            Assert(NativeStubs.FreedHandle == handle);
        } finally { Bind("gchandle_new", IntPtr.Zero); Bind("gchandle_get_target", IntPtr.Zero); Bind("gchandle_free", IntPtr.Zero); }
    }
});
Check("save request during an active writer queues a fresh snapshot", () => {
    Set(typeof(DedicatedServer), "saveTask", (nuint)42);
    Set(typeof(DedicatedServer), "saveQueued", false);
    Set(typeof(DedicatedServer), "quitRequested", false);
    Call(typeof(DedicatedServer), "RequestSaveNow");
    Assert((bool)Get(typeof(DedicatedServer), "saveQueued"));
    Assert((nuint)Get(typeof(DedicatedServer), "saveTask") == (nuint)42);
    Set(typeof(DedicatedServer), "saveTask", (nuint)0);
    Set(typeof(DedicatedServer), "saveQueued", false);
});
Check("only the supervisor schedules autosaves for managed instances", () => {
    var previousId = Cfg.InstanceId; int previousMinutes = Cfg.AutosaveMinutes;
    try {
        Cfg.InstanceId = "managed-test"; Cfg.AutosaveMinutes = 1;
        Set(typeof(DedicatedServer), "worldReady", true);
        Set(typeof(DedicatedServer), "nextAutosave", 0L);
        Call(typeof(DedicatedServer), "SaveTick", 1000L);
        Assert((long)Get(typeof(DedicatedServer), "nextAutosave") == 0);
        Cfg.InstanceId = "";
        Call(typeof(DedicatedServer), "SaveTick", 1000L);
        Assert((long)Get(typeof(DedicatedServer), "nextAutosave") == 61000L);
    } finally {
        Cfg.InstanceId = previousId; Cfg.AutosaveMinutes = previousMinutes;
        Set(typeof(DedicatedServer), "worldReady", false);
        Set(typeof(DedicatedServer), "nextAutosave", 0L);
    }
});
Check("missing invocation returns explicit failure", () => {
    Assert(!Il2.TryInvoke(IntPtr.Zero, IntPtr.Zero, out var result));
    Assert(result == IntPtr.Zero && Il2.LastError.Length > 0);
});
Check("failed required hook rolls back resources", () => {
    var keep = (IList)typeof(Hooks).GetField("keep", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
    int start = keep.Count; var resource = new TestResource();
    try {
        using var transaction = new Hooks.Transaction(); keep.Add(resource);
        Hooks.Detour(IntPtr.Zero, (D_Static0)(_ => { }), "injected failure");
        throw new Exception("Missing hook was accepted");
    } catch (MissingMethodException) { }
    Assert(resource.Disposed && keep.Count == start);
});
Check("snapshot remains immutable after live files change", () => {
    string root = Path.Combine(Path.GetTempPath(), "moon-snapshot-test-" + Guid.NewGuid().ToString("N"));
    try {
        string save = Path.Combine(root, "save"); Directory.CreateDirectory(save);
        File.WriteAllText(Path.Combine(save, "slot.json"), "before");
        string snapshot = SaveSnapshot.Create(save, Path.Combine(root, "runtime"));
        File.WriteAllText(Path.Combine(save, "slot.json"), "after");
        Assert(File.ReadAllText(Path.Combine(snapshot, "slot.json")) == "before" && !snapshot.EndsWith(".part"));
    } finally { Directory.Delete(root, true); }
});
Check("embedded query rules reject every truncation", () => {
    byte[] bytes = new byte[] {0x45,1,0}.Concat(System.Text.Encoding.UTF8.GetBytes("moon_mods\0required@1\0")).ToArray();
    Assert(ServerQuery.ParseRules(bytes)["moon_mods"] == "required@1");
    for(int i=0;i<bytes.Length;i++) { try { ServerQuery.ParseRules(bytes.Take(i).ToArray()); throw new Exception("Accepted truncation"); } catch(EndOfStreamException) {} catch(IOException) {} }
    Assert(ServerQuery.Address("127.0.0.1:27026").port == 27026);
    Assert(ServerQuery.Address("example.com").port == 27026);
});
Check("names remove console control characters at admission", () => {
    var method=typeof(DedicatedServer).GetMethod("CleanName",BindingFlags.NonPublic|BindingFlags.Static);
    Assert((string)method.Invoke(null,new object[]{"a\n\r\u2028b"}) == "ab");
});
Check("stop waits for authenticated clients before final capture", () => {
    var peers = (IDictionary)Get(typeof(DedicatedServer), "peers"); peers.Clear();
    var pt = typeof(DedicatedServer).GetNestedType("Peer", BindingFlags.NonPublic);
    var peer = Activator.CreateInstance(pt); pt.GetField("Authed").SetValue(peer, true);
    peers.Add(76561198000000001UL, peer);
    Set(typeof(DedicatedServer), "stopAfterSave", true);
    Set(typeof(DedicatedServer), "finalSaveStarted", false);
    Set(typeof(DedicatedServer), "stopDeadline", 20000L);
    try { Call(typeof(DedicatedServer), "StopTick", 1000L); Assert(!(bool)Get(typeof(DedicatedServer), "finalSaveStarted")); }
    finally { peers.Clear(); Set(typeof(DedicatedServer), "stopAfterSave", false); }
});
Check("shutdown handshake messages survive wire parsing", () => {
    unsafe {
        foreach (var kind in new[] { Msg.GoodbyeAck, Msg.PrepareStop }) {
            var frame = Protocol.Frame(kind, null);
            fixed (byte* bytes = frame) Assert(Protocol.TryParse(bytes, frame.Length, out var decoded, out _) && decoded == kind);
        }
    }
});
Check("goodbye acknowledgement is recorded without disconnecting early", () => {
    Set(typeof(PlayerClient), "goodbyeAck", false);
    Call(typeof(PlayerClient), "OnServerMessage", Msg.GoodbyeAck, Array.Empty<byte>(), 1L);
    Assert((bool)Get(typeof(PlayerClient), "goodbyeAck"));
    Set(typeof(PlayerClient), "goodbyeAck", false);
});
Check("native queue inspection failure cannot mean empty", () => {
    unsafe { var setter = new System.Reflection.Emit.DynamicMethod("bind_class_stub", typeof(void), new[] { typeof(IntPtr) }, typeof(Il2), true);
        var il = setter.GetILGenerator(); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Stsfld, Field(typeof(Il2), "object_get_class")); il.Emit(System.Reflection.Emit.OpCodes.Ret);
        ((Action<IntPtr>)setter.CreateDelegate(typeof(Action<IntPtr>)))((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr,IntPtr>)&NativeStubs.NoClass); }
    Set(typeof(PlayerClient), "leaveFailed", false);
    Assert((int)Call(typeof(PlayerClient), "NativeCount", (IntPtr)123, "missingQueue") > 0);
    Assert((bool)Get(typeof(PlayerClient), "leaveFailed"));
    Set(typeof(PlayerClient), "leaveFailed", false);
});
Console.WriteLine(JsonSerializer.Serialize(new { passed = tests.Count, tests }, new JsonSerializerOptions { WriteIndented = true }));
class TestResource : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }
static unsafe class NativeStubs {
    public static nuint FreedHandle;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] public static IntPtr NoClass(IntPtr obj) => IntPtr.Zero;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] public static nuint NewHandle(IntPtr obj, byte pinned) => unchecked((nuint)0x123456789UL);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] public static IntPtr HandleTarget(nuint handle) => handle == unchecked((nuint)0x123456789UL) ? (IntPtr)123 : IntPtr.Zero;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] public static void FreeHandle(nuint handle) { FreedHandle = handle; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] public static int License(IntPtr server, ulong id, uint app) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] public static void EndAuth(IntPtr server, ulong id) { }
}
namespace MoonDedicated {
    internal static class Log { public static void Info(string m) { } public static void Warn(string m) { } public static void Error(string m) { } }
}
namespace BepInEx.Unity.IL2CPP {
    public class IL2CPPChainloader { public static IL2CPPChainloader Instance => null; public Dictionary<string, TestPlugin> Plugins => new(); }
    public class TestPlugin { public TestMetadata Metadata => new(); }
    public class TestMetadata { public string GUID => "test"; public string Name => "test"; public Version Version => new(1, 0); }
}
