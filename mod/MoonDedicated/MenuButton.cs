using System;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;

namespace MoonDedicated
{
    internal static unsafe class MenuButton
    {
        static D_Inst0 originalBuild;
        static D_Inst0 originalDestroy, originalUpdate;
        static IntPtr rowsField, homeRow, layout, choiceClass;
        static object actionRoot;
        static IntPtr actionPointer;
        static nuint menuHandle;
        public static int RowsAdded { get; private set; }
        public static string LastError { get; private set; } = "";

        public static void Install()
        {
            rowsField = Il2.Field(Game.KMenu, "homeRows");
            homeRow = Il2.Method(Game.KMenu, "HomeRow", 3);
            layout = Il2.Method(Game.KMenu, "LayoutHome", 0);
            choiceClass = Il2.Nested(Game.C("MoonSimpleKit"), "Choice");
            if (rowsField == IntPtr.Zero || homeRow == IntPtr.Zero || layout == IntPtr.Zero || choiceClass == IntPtr.Zero)
                throw new MissingMethodException("Main menu row builder is not available");
            var actionType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Il2CppSystem.Action")).FirstOrDefault(t => t != null)
                ?? Assembly.Load("Il2Cppmscorlib").GetType("Il2CppSystem.Action", true);
            var convert = typeof(DelegateSupport).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == "ConvertDelegate" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            actionRoot = convert.MakeGenericMethod(actionType).Invoke(null, new object[] { (Action)OpenBrowser });
            actionPointer = (IntPtr)actionRoot.GetType().GetProperty("Pointer").GetValue(actionRoot);
            originalBuild = Hooks.Method(Game.KMenu, "BuildHome", 0, (D_Inst0)BuildHome);
            originalDestroy = Hooks.Method(Game.KMenu, "OnDestroy", 0, (D_Inst0)Destroy);
            originalUpdate = Hooks.Method(Game.KMenu, "Update", 0, (D_Inst0)((self, mi) => { if (!InGameBrowser.IsOpen) originalUpdate(self, mi); }));
        }

        static void BuildHome(IntPtr self, IntPtr method)
        {
            originalBuild(self, method);
            try {
                Il2.Unpin(menuHandle); menuHandle = Il2.Pin(self);
                var rows = Il2.GetRef(self, rowsField);
                int count = (int)Il2.ArrayLength(rows);
                if (rows == IntPtr.Zero || count < 1 || count > 12) throw new InvalidOperationException("Unexpected main menu rows");
                var row = Il2.Invoke(homeRow, self, Il2.NewString("JOIN SERVER"), actionPointer, (IntPtr)(&count));
                if (row == IntPtr.Zero) throw new InvalidOperationException("Could not create JOIN SERVER row: " + Il2.LastError);
                nuint rowHandle = Il2.Pin(row);
                nuint rowsHandle = Il2.Pin(rows);
                try {
                    var expanded = Il2.NewArray(choiceClass, count + 1);
                    if (expanded == IntPtr.Zero) throw new InvalidOperationException("Could not expand menu rows");
                    for (int i = 0; i < count; i++) Il2.ArrayRef(expanded, i, Il2.ArrayRef(rows, i));
                    Il2.ArrayRef(expanded, count, row);
                    Il2.SetRef(self, rowsField, expanded);
                    if (!Il2.TryInvoke(layout, self, out _)) throw new InvalidOperationException(Il2.LastError);
                    RowsAdded++;
                    LastError = "";
                    Log.Info("JOIN SERVER menu row added");
                } finally { Il2.Unpin(rowsHandle); Il2.Unpin(rowHandle); }
            } catch (Exception e) { LastError = e.Message; Log.Error("Menu button: " + e); }
        }

        static void Destroy(IntPtr self, IntPtr method)
        {
            try { if (InGameBrowser.IsOpen) InGameBrowser.Close(); originalDestroy(self, method); }
            finally { if (Il2.Target(menuHandle) == self) { Il2.Unpin(menuHandle); menuHandle = 0; } }
        }

        public static void ShowHome(bool visible)
        {
            var menu = Il2.Target(menuHandle);
            if (menu == IntPtr.Zero) return;
            var home = Il2.GetRef(menu, Il2.Field(Game.KMenu, "home"));
            if (home == IntPtr.Zero) return;
            var component = Il2.Class(Game.CoreImage, "UnityEngine", "Component");
            var go = Il2.Invoke(Il2.Method(component, "get_gameObject", 0), home);
            byte active = (byte)(visible ? 1 : 0);
            Il2.Invoke(Il2.Method(Il2.ClassOf(go), "SetActive", 1), go, (IntPtr)(&active));
        }

        static void OpenBrowser()
        {
            try { InGameBrowser.Open(); LastError = ""; }
            catch (Exception e) { LastError=e.Message; Log.Error("Could not open in-game server browser: " + e); }
        }
    }
}

