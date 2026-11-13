using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace MoonDedicated
{
    // Opt-in native acceptance fixture. It runs before any world starts and
    // exercises the game's real page constructors, snapshots and restore code.
    internal static unsafe class NativeCleaningChecks
    {
        static IntPtr chunkClass, pageClass;
        static IntPtr New(IntPtr klass) { var obj = Il2.New(klass); Il2.Invoke(Il2.Method(klass, ".ctor", 0), obj); return obj; }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Native cleaning check: " + message); }
        static byte[] Bytes(IntPtr array)
        {
            Require(array != IntPtr.Zero, "missing byte array");
            var result = new byte[Il2.ArrayLength(array)];
            Marshal.Copy(array + (int)Il2.ArrayHeader(), result, 0, result.Length);
            return result;
        }
        static IntPtr Array(byte[] data)
        {
            var array = Il2.NewArray(Il2.CorlibClass("System", "Byte"), data.Length);
            Marshal.Copy(data, 0, array + (int)Il2.ArrayHeader(), data.Length);
            return array;
        }
        static IntPtr Snapshot()
        {
            var chunk = New(chunkClass);
            Il2.SetRef(chunk, Il2.Field(chunkClass, "coarse"), Array(Enumerable.Repeat((byte)255, 129 * 129).ToArray()));
            Il2.SetRef(chunk, Il2.Field(chunkClass, "refined"), Il2.NewArray(Il2.CorlibClass("System", "Int32"), 0));
            return chunk;
        }
        static IntPtr First(IntPtr list) { int zero = 0; return Il2.Invoke(Il2.Method(Il2.ClassOf(list), "get_Item", 1), list, (IntPtr)(&zero)); }
        static void CheckPage(IntPtr chunk, byte[] expected)
        {
            var list = Il2.GetRef(chunk, Il2.Field(chunkClass, "pages"));
            Require(Il2.Int(Il2.Invoke(Il2.Method(Il2.ClassOf(list), "get_Count", 0), list)) == 1, "page count changed");
            var page = First(list);
            foreach (var field in new[] { "data", "raw" })
                Require(Bytes(Il2.GetRef(page, Il2.Field(pageClass, field))).SequenceEqual(expected), field + " pixels changed during save/restore");
        }
        public static void Run()
        {
            chunkClass = Game.C("MoonChunkSnapshot"); pageClass = Game.C("MoonPageSnapshot");
            var maskClass = Game.C("MoonCleanMask");
            var expected = Enumerable.Repeat((byte)255, 257 * 257).ToArray();
            // Three separated narrow marks: the untouched gaps must stay dusty.
            foreach (int x in new[] { 30, 90, 150 })
                for (int y = 50; y < 100; y++) expected[y * 257 + x] = 0;
            var original = Snapshot(); var page = New(pageClass);
            Il2.SetRef(page, Il2.Field(pageClass, "data"), Array(expected));
            Il2.SetRef(page, Il2.Field(pageClass, "raw"), Array(expected));
            var pages = Il2.GetRef(original, Il2.Field(chunkClass, "pages"));
            Il2.Invoke(Il2.Method(Il2.ClassOf(pages), "Add", 1), pages, page);
            var mask = Il2.Invoke(Il2.Method(maskClass, "Restore", 1), IntPtr.Zero, original);
            Require(mask != IntPtr.Zero, "restore failed");
            var root = Il2.Pin(mask);
            try
            {
                int budget = 32;
                Require(Il2.TryInvoke(Il2.Method(maskClass, "Upload", 1), mask, out _, (IntPtr)(&budget)), "headless upload failed");
                var dictionary = Il2.GetRef(mask, Il2.Field(maskClass, "pages"));
                int key = 0;
                var livePage = Il2.Invoke(Il2.Method(Il2.ClassOf(dictionary), "get_Item", 1), dictionary, (IntPtr)(&key));
                var livePageClass = Il2.ClassOf(livePage);
                Require(Bytes(Il2.GetRef(livePage, Il2.Field(livePageClass, "bankSnapshot"))).SequenceEqual(expected), "Upload skipped CPU bank publication");
                Require(Il2.GetField<byte>(livePage, Il2.Field(livePageClass, "dataShared")) != 0, "Upload skipped copy-on-write protection");
                var banks = Il2.Invoke(Il2.Method(maskClass, "SnapshotBankPages", 0), mask);
                Require(Bytes(Il2.GetRef(First(banks), Il2.Field(pageClass, "data"))).SequenceEqual(expected), "bank pixels changed");
                var saved = Snapshot(); Il2.Invoke(Il2.Method(maskClass, "Snapshot", 1), mask, saved); CheckPage(saved, expected);
                var restored = Il2.Invoke(Il2.Method(maskClass, "Restore", 1), IntPtr.Zero, saved);
                Require(restored != IntPtr.Zero, "second restore failed");
                var savedAgain = Snapshot(); Il2.Invoke(Il2.Method(maskClass, "Snapshot", 1), restored, savedAgain); CheckPage(savedAgain, expected);
                Log.Info("NATIVE CLEANING CHECK PASSED: three separated marks and untouched gaps retain exact data/raw pixels through headless upload and two save/restore snapshots");
            }
            finally { Il2.Unpin(root); }
        }
    }
}
