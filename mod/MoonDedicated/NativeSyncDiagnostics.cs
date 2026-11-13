using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace MoonDedicated
{
    // Explicitly enabled, local native acceptance probes. Nothing runs unless
    // the maintainer creates native-tests.enabled and queues a command file.
    internal static unsafe class NativeSyncDiagnostics
    {
        static long nextPoll;
        static bool recordingStarted;
        public static void RecordStroke(IntPtr stroke)
        {
            if (!File.Exists(Path.Combine(Cfg.ModDir, "native-tests.enabled"))) return;
            float* values = (float*)stroke;
            var sample = new float[19];
            for (int i = 0; i < sample.Length; i++) sample[i] = values[i];
            string path = Path.Combine(Cfg.ModDir, "native-tool-strokes.jsonl");
            if (!recordingStarted) { File.WriteAllText(path, ""); recordingStarted = true; }
            File.AppendAllText(path, JsonSerializer.Serialize(sample) + Environment.NewLine);
        }
        public static void Tick()
        {
            long now = Environment.TickCount64;
            if (now < nextPoll) return;
            nextPoll = now + 250;
            if (!File.Exists(Path.Combine(Cfg.ModDir, "native-tests.enabled"))) return;
            string command = Path.Combine(Cfg.ModDir, "native-probe.txt");
            if (!File.Exists(command)) return;
            string text = File.ReadAllText(command).Trim(); File.Delete(command);
            var crew = Il2.Invoke(Game.M_CrewInstance, IntPtr.Zero);
            var world = crew == IntPtr.Zero ? IntPtr.Zero : Il2.GetRef(crew, Game.F_CrewBound);
            if (world == IntPtr.Zero) { Log.Warn("Native sync probe needs a bound world"); return; }
            string[] args = text.Split(' ', 2);
            if (args[0] == "entities") Entities(world, args.Length == 2 ? args[1] : "entities");
            if (args[0] == "dump") Dump(world, args.Length == 2 ? args[1] : "capture");
            if (args[0] == "stroke") Stroke(world);
            if (args[0] == "repair-check") RepairCheck(world);
            if (args[0] == "leave" && Cfg.Role == Role.Client)
                Il2.Invoke(Il2.Method(Game.KSession, "ReturnToMenu", 0), IntPtr.Zero);
        }
        static void Entities(IntPtr world, string label)
        {
            var vehicles = new List<object>();
            var dictionary = Il2.GetRef(world, Il2.Field(Game.KWorld, "ownedVehicles"));
            var values = Il2.Invoke(Il2.Method(Il2.ClassOf(dictionary), "get_Values", 0), dictionary);
            int count = Il2.Int(Il2.Invoke(Il2.Method(Il2.ClassOf(values), "get_Count", 0), values));
            var array = Il2.NewArray(Game.C("MoonOwnedVehicle"), count); int zero = 0;
            if (!Il2.TryInvoke(Il2.Method(Il2.ClassOf(values), "CopyTo", 2), values, out _, array, (IntPtr)(&zero))) throw new InvalidOperationException(Il2.LastError);
            for (int i = 0; i < count; i++) {
                var vehicle = Il2.ArrayRef(array, i); var k = Il2.ClassOf(vehicle);
                var position = Il2.GetField<Vector>(vehicle, Il2.Field(k, "position"));
                vehicles.Add(new {
                    garage = Il2.GetField<int>(vehicle, Il2.Field(k, "garage")), vehicle = Il2.GetField<int>(vehicle, Il2.Field(k, "vehicle")),
                    owner = Il2.ReadString(Il2.GetRef(vehicle, Il2.Field(k, "owner"))), holder = Il2.ReadString(Il2.GetRef(vehicle, Il2.Field(k, "holder"))),
                    inWorld = Il2.GetField<byte>(vehicle, Il2.Field(k, "inWorld")), position = new[] {position.X,position.Y,position.Z},
                    upgrades = Bytes(Il2.GetRef(vehicle, Il2.Field(k, "upgrades")))
                });
            }
            var robots = new List<object>(); var robotClass = Game.C("MoonRobotVacuum");
            var active = Il2.GetStaticRef(Il2.Field(robotClass, "active"));
            count = Il2.Int(Il2.Invoke(Il2.Method(Il2.ClassOf(active), "get_Count", 0), active));
            for (int i = 0; i < count; i++) {
                var robot = Il2.Invoke(Il2.Method(Il2.ClassOf(active), "get_Item", 1), active, (IntPtr)(&i));
                var transform = Il2.Invoke(Il2.Method(Il2.Class(Game.CoreImage, "UnityEngine", "Component"), "get_transform", 0), robot);
                var position = Il2.Value<Vector>(Il2.Invoke(Il2.Method(Il2.Class(Game.CoreImage, "UnityEngine", "Transform"), "get_position", 0), transform));
                robots.Add(new { position = new[] { position.X,position.Y,position.Z },
                    status = Il2.ReadString(Il2.Invoke(Il2.Method(robotClass, "get_StatusText", 0), robot)),
                    dust = Il2.Value<double>(Il2.Invoke(Il2.Method(robotClass, "get_DustKilograms", 0), robot)),
                    hex = Il2.Int(Il2.Invoke(Il2.Method(robotClass, "get_Hex", 0), robot)),
                    progress = Il2.Value<float>(Il2.Invoke(Il2.Method(robotClass, "get_HexProgress", 0), robot)) });
            }
            string safe = new string(label.Where(char.IsLetterOrDigit).Take(40).ToArray());
            File.WriteAllText(Path.Combine(Cfg.ModDir, "entities-" + safe + ".json"), JsonSerializer.Serialize(new { vehicles, robots }, new JsonSerializerOptions { WriteIndented = true }));
            Log.Info("Entity capture " + safe + ": vehicles=" + vehicles.Count + " robots=" + robots.Count);
        }
        static byte[] Bytes(IntPtr array)
        {
            if (array == IntPtr.Zero) return null;
            var bytes = new byte[Il2.ArrayLength(array)];
            Marshal.Copy(array + (int)Il2.ArrayHeader(), bytes, 0, bytes.Length);
            return bytes;
        }
        public static void Capture(IntPtr world, string label)
        {
            if (world != IntPtr.Zero && File.Exists(Path.Combine(Cfg.ModDir, "native-tests.enabled"))) Dump(world, label);
        }
        static void RepairCheck(IntPtr world)
        {
            var chunks = Il2.GetRef(world, Il2.Field(Game.KWorld, "chunks"));
            var listClass = Il2.ClassOf(chunks);
            int count = Il2.Int(Il2.Invoke(Il2.Method(listClass, "get_Count", 0), chunks));
            IntPtr chunk = IntPtr.Zero;
            for (int i = 0; i < count; i++)
            {
                var candidate = Il2.Invoke(Il2.Method(listClass, "get_Item", 1), chunks, (IntPtr)(&i));
                if (Il2.GetRef(candidate, Il2.Field(Il2.ClassOf(candidate), "cleaning")) != IntPtr.Zero) { chunk = candidate; break; }
            }
            if (chunk == IntPtr.Zero) throw new InvalidOperationException("Repair fixture needs a loaded cleaning chunk");
            var mask = Il2.GetRef(chunk, Il2.Field(Il2.ClassOf(chunk), "cleaning"));
            var maskClass = Game.C("MoonCleanMask");
            int key = 0;
            int cases = 0;
            foreach (int width in new[] { 1, 2, 5, 12, 32 })
            foreach (byte value in new byte[] { 0, 64, 128, 224 })
            {
                while (key < 4095 && Il2.Bool(Il2.Invoke(Il2.Method(maskClass, "HasPage", 1), mask, (IntPtr)(&key)))) key++;
                if (key >= 4095) throw new InvalidOperationException("Repair fixture needs unused pages; refuses to overwrite saved progress");
                byte[] expected = Enumerable.Repeat((byte)255, 257 * 257).ToArray();
                // Variable-width closed loops, partial removal, separated marks,
                // and a stroke touching the right edge of the page.
                for (int y = 30; y <= 130; y++)
                for (int x = 30; x <= 130; x++)
                    if (x < 30 + width || x > 130 - width || y < 30 + width || y > 130 - width)
                        expected[y * 257 + x] = value;
                for (int y = 170; y < 190; y++)
                for (int x = 220; x <= 256; x++) expected[y * 257 + x] = value;
                var bytes = Il2.NewArray(Il2.CorlibClass("System", "Byte"), expected.Length);
                Marshal.Copy(expected, 0, bytes + (int)Il2.ArrayHeader(), expected.Length);
                // Repeating a page must be idempotent, and a less-clean stale
                // replay must never fill an already cleaned sample back in.
                for (int repeat = 0; repeat < 2; repeat++)
                    if (!Il2.TryInvoke(Il2.Method(Game.KWorld, "CleanDownTo", 3), world, out _, chunk, (IntPtr)(&key), bytes))
                        throw new InvalidOperationException("Native repair fixture failed: " + Il2.LastError);
                Marshal.Copy(Enumerable.Repeat((byte)255, expected.Length).ToArray(), 0, bytes + (int)Il2.ArrayHeader(), expected.Length);
                if (!Il2.TryInvoke(Il2.Method(Game.KWorld, "CleanDownTo", 3), world, out _, chunk, (IntPtr)(&key), bytes))
                    throw new InvalidOperationException("Native stale page replay failed: " + Il2.LastError);
                var dictionary = Il2.GetRef(mask, Il2.Field(maskClass, "pages"));
                var page = Il2.Invoke(Il2.Method(Il2.ClassOf(dictionary), "get_Item", 1), dictionary, (IntPtr)(&key));
                foreach (var field in new[] { "raw", "data" })
                    if (!Bytes(Il2.GetRef(page, Il2.Field(Il2.ClassOf(page), field))).SequenceEqual(expected))
                        throw new InvalidOperationException("Native repair changed " + field + " samples: width=" + width + " value=" + value);
                key++; cases++;
            }
            Log.Info("NATIVE REPAIR CHECK PASSED: " + cases + " variable-width/depth loop and page-edge cases retain all data/raw samples through repeated native repair");
            Dump(world, "repair-check");
        }
        static void Dump(IntPtr world, string label)
        {
            label = new string(label.Where(c => char.IsLetterOrDigit(c) || c == '-').Take(60).ToArray());
            string output = Path.Combine(Cfg.ModDir, "native-diagnostics", label + "-" + DateTime.UtcNow.Ticks);
            Directory.CreateDirectory(output);
            var rows = new List<object>();
            var chunks = Il2.GetRef(world, Il2.Field(Game.KWorld, "chunks"));
            var listClass = Il2.ClassOf(chunks);
            int count = Il2.Int(Il2.Invoke(Il2.Method(listClass, "get_Count", 0), chunks));
            for (int i = 0; i < count; i++)
            {
                var chunk = Il2.Invoke(Il2.Method(listClass, "get_Item", 1), chunks, (IntPtr)(&i));
                var chunkClass = Il2.ClassOf(chunk);
                var mask = Il2.GetRef(chunk, Il2.Field(chunkClass, "cleaning"));
                if (mask == IntPtr.Zero) continue;
                int id = Il2.Int(Il2.Invoke(Il2.Method(Game.KWorld, "ChunkId", 1), world, chunk));
                var dictionary = Il2.GetRef(mask, Il2.Field(Game.C("MoonCleanMask"), "pages"));
                var dictionaryClass = Il2.ClassOf(dictionary);
                var keys = Il2.Invoke(Il2.Method(dictionaryClass, "get_Keys", 0), dictionary);
                int keyCount = Il2.Int(Il2.Invoke(Il2.Method(Il2.ClassOf(keys), "get_Count", 0), keys));
                var keyArray = Il2.NewArray(Il2.CorlibClass("System", "Int32"), keyCount);
                int zero = 0;
                Il2.Invoke(Il2.Method(Il2.ClassOf(keys), "CopyTo", 2), keys, keyArray, (IntPtr)(&zero));
                for (int p = 0; p < keyCount; p++)
                {
                    int key = *((int*)((byte*)keyArray + Il2.ArrayHeader()) + p);
                    var page = Il2.Invoke(Il2.Method(dictionaryClass, "get_Item", 1), dictionary, (IntPtr)(&key));
                    var pageClass = Il2.ClassOf(page);
                    foreach (var field in new[] { "data", "raw", "bankSnapshot", "published", "savedData", "savedRaw" })
                    {
                        var bytes = Bytes(Il2.GetRef(page, Il2.Field(pageClass, field)));
                        if (bytes == null) continue;
                        string file = id + "-" + key + "-" + field + ".bin";
                        File.WriteAllBytes(Path.Combine(output, file), bytes);
                        rows.Add(new { chunk = id, page = key, field, length = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)), zero = bytes.Count(b => b == 0), full = bytes.Count(b => b == 255), file });
                    }
                }
            }
            File.WriteAllText(Path.Combine(output, "pages.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            Log.Info("Native sync page capture: " + output + " (" + rows.Count + " arrays)");
        }
        struct Vector
        {
            public float X, Y, Z;
            public Vector(float x, float y, float z) { X = x; Y = y; Z = z; }
            public static Vector operator +(Vector a, Vector b) => new Vector(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
            public static Vector operator *(Vector a, float b) => new Vector(a.X*b,a.Y*b,a.Z*b);
        }
        static void Stroke(IntPtr world)
        {
            var center = new Vector(100, 100, -30); var normal = new Vector(0, 1, 0);
            var crew = Il2.Invoke(Game.M_CrewInstance, IntPtr.Zero);
            var local = Il2.GetRef(crew, Il2.Field(Game.KCrew, "local"));
            if (local != IntPtr.Zero)
            {
                var component = Il2.Class(Game.CoreImage, "UnityEngine", "Component");
                var transform = Il2.Invoke(Il2.Method(component, "get_transform", 0), local);
                center = Il2.Value<Vector>(Il2.Invoke(Il2.Method(Il2.Class(Game.CoreImage, "UnityEngine", "Transform"), "get_position", 0), transform));
            }
            Log.Info("Native probe local walker position " + center.X + "," + center.Y + "," + center.Z);
            center += new Vector(10,-1.5f,0);
            float n = MathF.Sqrt(normal.X*normal.X+normal.Z*normal.Z);
            var right = n > .001f ? new Vector(normal.Z/n,0,-normal.X/n) : new Vector(1,0,0);
            var up = new Vector(normal.Y*right.Z-normal.Z*right.Y,normal.Z*right.X-normal.X*right.Z,normal.X*right.Y-normal.Y*right.X);
            float width = .4f, depth = 4, dose = 1, edge = 0;
            var push = new Vector(0,0,0);
            int changed = 0;
            for (int mark = 0; mark < 3; mark++)
            {
                var from = center + right * (mark*4);
                var to = from + up * 3;
                if (!Il2.TryInvoke(Il2.Method(Game.KWorld, "CleanToolStroke", 11), world, out var result,
                    (IntPtr)(&from),(IntPtr)(&to),(IntPtr)(&right),(IntPtr)(&right),(IntPtr)(&width),(IntPtr)(&depth),(IntPtr)(&dose),(IntPtr)(&edge),(IntPtr)(&push),Il2.NewString(""),Il2.NewString("")))
                    throw new InvalidOperationException("Native stroke failed: " + Il2.LastError);
                changed += Il2.Int(result);
            }
            Il2.Invoke(Il2.Method(Game.KCrew, "FlushStroke", 0), crew);
            Log.Info("Native sync test applied three separated tool strokes at " + center.X + "," + center.Y + "," + center.Z + "; changed=" + changed);
        }
    }
}
