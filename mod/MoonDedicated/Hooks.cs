using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;

namespace MoonDedicated
{
    // IL2CPP methods are plain native functions: (this, args..., MethodInfo*) for instance methods,
    // (args..., MethodInfo*) for static ones. bool is one byte.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void D_Inst0(IntPtr self, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void D_Static0(IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte D_Bool0(IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte D_BoolU64(ulong a, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte D_BoolInt(int a, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate ulong D_U64_0(IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr D_Ref0(IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void D_Static2Int(int a, int b, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void D_Static1Int(int a, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void D_Static1Bool(byte a, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte D_BoolObjObj(IntPtr a, IntPtr b, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr D_RefU64(ulong a, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void D_Inst1Obj(IntPtr self, IntPtr a, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void D_Inst1Int(IntPtr self, int a, IntPtr mi);

    // Wrappers in Steamworks.NET that the server redirects to Steam's game-server interfaces.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int D_NetSend(IntPtr identity, IntPtr data, uint size, int flags, int channel, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int D_NetReceive(int channel, IntPtr array, int capacity, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte D_NetIdentity(IntPtr identity, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte D_NetChannel(IntPtr identity, int channel, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int D_NetInfo(IntPtr identity, IntPtr details, IntPtr status, IntPtr mi);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte D_NextCallback(int pipe, IntPtr msg);

    internal static class Hooks
    {
        static readonly List<object> keep = new List<object>();
        public static int Failures;

        public sealed class Transaction : IDisposable
        {
            readonly int start = keep.Count;
            bool committed;
            public void Commit() { committed = true; }
            public void Dispose()
            {
                if (committed) return;
                for (int i = keep.Count - 1; i >= start; i--) {
                    if (keep[i] is IDisposable disposable) disposable.Dispose();
                    keep.RemoveAt(i);
                }
            }
        }

        /// Detour a native function. Returns a delegate that calls the original code.
        public static T Detour<T>(IntPtr code, T replacement, string what) where T : Delegate
        {
            if (code == IntPtr.Zero) { Failures++; throw new MissingMethodException("required hook has no code: " + what); }
            INativeDetour d = null;
            try
            {
                // Use BepInEx's IL2CPP native provider. MonoMod's legacy raw
                // detour generates an undo/call/redo managed proxy, which is
                // unsafe when the game re-enters CoreCLR on these callbacks.
                d = INativeDetour.Create(code, replacement);
                var original = d.GenerateTrampoline<T>();
                d.Apply();
                keep.Add(d);
                keep.Add(replacement);
                return original;
            }
            catch (Exception e) { d?.Dispose(); Failures++; throw new InvalidOperationException("required hook failed: " + what, e); }
        }

        /// Detour a managed (IL2CPP) method given its class, name and argument count.
        public static T Method<T>(IntPtr klass, string name, int argc, T replacement) where T : Delegate
        {
            var m = Il2.Method(klass, name, argc);
            if (m == IntPtr.Zero || Il2.ParamCount(m) != argc) throw new MissingMethodException(name);
            int expected = argc + (Il2.IsStatic(m) ? 1 : 2);
            if (typeof(T).GetMethod("Invoke").GetParameters().Length != expected)
                throw new InvalidOperationException("hook signature mismatch: " + name);
            return Detour(Il2.Code(m), replacement, name);
        }
    }
}
