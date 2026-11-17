// Lets the game run its simulation on a machine without a GPU (-batchmode -nographics):
// the few places where it uploads textures or draws menus are turned into no-ops.
using System;
using System.Runtime.InteropServices;

namespace MoonDedicated
{
    internal static unsafe class Headless
    {
        static IntPtr texArray;
        static D_Inst1Obj origName;
        static D_Inst1Int origFilter, origWrap;

        static void NoopInst(IntPtr self, IntPtr mi) { }
        static void NoopStatic(IntPtr mi) { }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte BoolInst(IntPtr self, IntPtr mi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VisualSnapshot(IntPtr self, IntPtr pages, float time, float duration, IntPtr mi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VisualSnapshotBudget(IntPtr self, IntPtr pages, float time, float duration, int budget, IntPtr mi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void TextureCtor(IntPtr self, int width, int height, int depth, int format, byte mipChain, byte linear, IntPtr mi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void PixelData(IntPtr self, IntPtr data, int mip, int element, int start, IntPtr mi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void TextureApply(IntPtr self, byte mip, byte unreadable, IntPtr mi);
        static bool IsProgressTexture(IntPtr o) => o != IntPtr.Zero && Il2.ClassOf(o) == texArray;
        static void SetName(IntPtr self, IntPtr value, IntPtr mi) { if (!IsProgressTexture(self)) origName?.Invoke(self, value, mi); }
        static void SetFilter(IntPtr self, int value, IntPtr mi) { if (!IsProgressTexture(self)) origFilter?.Invoke(self, value, mi); }
        static void SetWrap(IntPtr self, int value, IntPtr mi) { if (!IsProgressTexture(self)) origWrap?.Invoke(self, value, mi); }

        public static void Install()
        {
            var core = Game.CoreImage;
            texArray = Il2.Class(core, "UnityEngine", "Texture2DArray");
            var texture = Il2.Class(core, "UnityEngine", "Texture");
            var uobject = Il2.Class(core, "UnityEngine", "Object");
            if (texArray == IntPtr.Zero || texture == IntPtr.Zero || uobject == IntPtr.Zero) throw new MissingMethodException("headless Unity texture classes not found");

            // menus and intro draw things a server never needs
            Hooks.Method(Game.KMenu, "OnEnable", 0, (D_Inst0)NoopInst);
            Hooks.Method(Game.KMenu, "Update", 0, (D_Inst0)NoopInst);
            Hooks.Method(Game.KIntro, "Launch", 0, (D_Static0)NoopStatic);

            // A dedicated host must never run a local astronaut's input/tool
            // loops. The world and remote crew have independent update loops.
            // Keep Awake/Start intact: the game initializes the world there.
            var walker = Game.C("MoonWalker");
            foreach (var name in new[] { "Update", "FixedUpdate", "LateUpdate" })
                Hooks.Method(walker, name, 0, (D_Inst0)NoopInst);
            Hooks.Method(Game.KCrew, "SendPose", 0, (D_Inst0)NoopInst);

            // the progress map is a Texture2DArray; without a GPU it cannot be created or filled, but the
            // CPU-side copy of the data is what the simulation and the save use.
            var ctor = Il2.FindMethod(texArray, ".ctor", m => Il2.ParamCount(m) == 6 && Il2.ParamType(m, 3) == "UnityEngine.TextureFormat" && Il2.ParamType(m, 4) == "System.Boolean" && Il2.ParamType(m, 5) == "System.Boolean");
            Hooks.Detour(Il2.Code(ctor), (TextureCtor)((self, w, h, d, f, m, l, mi) => { }), "Texture2DArray ctor");
            origName = Hooks.Method(uobject, "set_name", 1, (D_Inst1Obj)SetName);
            origFilter = Hooks.Method(texture, "set_filterMode", 1, (D_Inst1Int)SetFilter);
            origWrap = Hooks.Method(texture, "set_wrapMode", 1, (D_Inst1Int)SetWrap);

            var generic = Il2.FindMethod(texArray, "SetPixelData", m => Il2.ParamCount(m) == 4 && Il2.ParamType(m, 0).Contains("[]"));
            var floatVersion = generic == IntPtr.Zero ? IntPtr.Zero : Il2.MakeGenericSingle(generic);
            Hooks.Detour(Il2.Code(floatVersion), (PixelData)((self, data, mip, element, start, mi) => { }), "Texture2DArray.SetPixelData<float>");
            Hooks.Method(texArray, "Apply", 2, (TextureApply)((self, mip, unreadable, mi) => { }));
            // World.Update returns early when this readiness check is false.
            // A headless world has no GPU bindings to restore, but it IS ready
            // to simulate. Returning false stranded crew strokes until saving
            // forced them to run, after raw page repair had already occurred.
            Hooks.Method(Game.KWorld, "RestoreRenderingBindings", 0, (BoolInst)((self, mi) => 1));
            // Upload also maintains CPU bank snapshots and copy-on-write flags.
            // Keep that path intact; stop at the narrower visual-upload boundary.
            var mask = Game.C("MoonCleanMask");
            Hooks.Method(mask, "Bind", 1, (D_Inst1Obj)((self, properties, mi) => { }));
            Hooks.Method(mask, "UploadVisualSnapshot", 3, (VisualSnapshot)((self, pages, time, duration, mi) => { }));
            Hooks.Method(mask, "UploadVisualSnapshot", 4, (VisualSnapshotBudget)((self, pages, time, duration, budget, mi) => { }));
            // The footprint renderer continues to update even with -nographics.
            Hooks.Method(Game.C("MoonDustPrints"), "LateUpdate", 0, (D_Inst0)NoopInst);
            // Graphics.RenderMeshInstanced throws "Instancing is not supported" every frame on
            // NullGfxDevice; the resulting log flood freezes the server once canisters are dropped.
            Hooks.Method(Game.C("MoonCanisterStreaming"), "LateUpdate", 0, (D_Inst0)NoopInst);
            Log.Info("headless graphics hooks installed");
        }
    }
}

