using System;
using System.IO;

namespace MoonDedicated
{
    internal static class SaveSnapshot
    {
        // Called on the game's main thread after its save task completes. The
        // manager only sees the atomically published, immutable directory.
        public static string Create(string source, string runtime)
        {
            string root = Path.Combine(runtime, "snapshots");
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, Guid.NewGuid().ToString("N"));
            string staging = target + ".part";
            try {
                Copy(source, staging);
                Directory.Move(staging, target);
                return target;
            }
            catch { if (Directory.Exists(staging)) Directory.Delete(staging, true); throw; }
        }

        static void Copy(string source, string target)
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Snapshot cannot follow directory links");
            Directory.CreateDirectory(target);
            foreach (string path in Directory.GetFileSystemEntries(source)) {
                var flags = File.GetAttributes(path);
                if ((flags & FileAttributes.ReparsePoint) != 0) throw new IOException("Snapshot cannot follow links");
                string dest = Path.Combine(target, Path.GetFileName(path));
                if ((flags & FileAttributes.Directory) != 0) { Copy(path, dest); continue; }
                var before = new FileInfo(path);
                long length = before.Length; DateTime modified = before.LastWriteTimeUtc;
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                var after = new FileInfo(path);
                if (length != after.Length || modified != after.LastWriteTimeUtc || new FileInfo(dest).Length != length)
                    throw new IOException("World changed while creating snapshot: " + Path.GetFileName(path));
            }
        }
    }
}
