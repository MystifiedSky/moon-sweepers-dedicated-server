// Control messages between a player's mod and the dedicated server (Steam messages channel 3).
// The game's own traffic (channels 1 and 2) is untouched.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MoonDedicated
{
    internal enum Msg : ushort { AuthRequest = 1, Roster = 2, Reject = 3, Ping = 4, Goodbye = 5, WaitWorld = 6, GoodbyeAck = 7, PrepareStop = 8 }
    internal enum Why : byte { Generic = 1, NotAllowed = 2, Full = 3, SteamAuth = 4, Mods = 5, Password = 6, Version = 7, NoLicense = 8, Kicked = 9 }

    internal sealed class Roster
    {
        public ulong Server;
        public string Name = "";
        public string Motd = "";
        public List<KeyValuePair<ulong, string>> Players = new List<KeyValuePair<ulong, string>>();
    }

    internal sealed class AuthRequest
    {
        public byte[] Ticket = new byte[0];
        public string Password = "";
        public string Name = "";
        public string GameVersion = "";
        public List<ModInfo> Mods = new List<ModInfo>();
    }

    internal sealed class Reject
    {
        public Why Code;
        public string Message = "";
        public List<string> Details = new List<string>();
    }

    internal static class Protocol
    {
        public const int Channel = 3;
        public const uint Magic = 0x33534444; // "DDS3"
        public const int MaxPayload = 16384;
        static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        static byte[] Finish(MemoryStream stream, BinaryWriter writer)
        {
            writer.Flush();
            if (stream.Length > MaxPayload) throw new InvalidDataException("Login/mod inventory exceeds the 16384-byte protocol limit");
            return stream.ToArray();
        }
        static void Complete(BinaryReader reader) {
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing protocol data");
        }
        static byte[] Exact(BinaryReader reader, int length) {
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException("Truncated protocol field");
            return bytes;
        }

        public static byte[] Frame(Msg type, byte[] payload)
        {
            payload ??= new byte[0];
            if (payload.Length > MaxPayload) throw new InvalidDataException("Control message exceeds the protocol payload limit");
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(Magic); w.Write((ushort)Cfg.Protocol); w.Write((ushort)type); w.Write((uint)payload.Length); w.Write(payload);
            w.Flush();
            return ms.ToArray();
        }

        public static unsafe bool TryParse(byte* data, int size, out Msg type, out byte[] payload)
        {
            type = 0; payload = null;
            if (data == null || size < 12 || size > 12 + MaxPayload) return false;
            var span = new ReadOnlySpan<byte>(data, size);
            uint magic = BitConverter.ToUInt32(span.Slice(0, 4));
            ushort ver = BitConverter.ToUInt16(span.Slice(4, 2));
            ushort t = BitConverter.ToUInt16(span.Slice(6, 2));
            uint len = BitConverter.ToUInt32(span.Slice(8, 4));
            if (magic != Magic || ver != Cfg.Protocol || len != size - 12 || t < 1 || t > (ushort)Msg.PrepareStop) return false;
            type = (Msg)t;
            payload = span.Slice(12).ToArray();
            return true;
        }

        static void Str(BinaryWriter w, string s)
        {
            var b = Utf8.GetBytes(s ?? "");
            if (b.Length > 1024) throw new InvalidDataException("Protocol string exceeds 1024 UTF-8 bytes");
            w.Write((ushort)b.Length); w.Write(b);
        }
        static string Str(BinaryReader r) {
            int length = r.ReadUInt16();
            if (length > 1024) throw new InvalidDataException("Protocol string exceeds 1024 bytes");
            return Utf8.GetString(Exact(r, length));
        }

        public static byte[] Encode(AuthRequest a)
        {
            if (a.Ticket.Length == 0 || a.Ticket.Length > 2048 || a.Mods.Count > 256) throw new InvalidDataException("Ticket or plugin count exceeds protocol limits");
            using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
            w.Write((uint)a.Ticket.Length); w.Write(a.Ticket);
            Str(w, a.Password); Str(w, a.Name); Str(w, a.GameVersion);
            w.Write((ushort)a.Mods.Count);
            foreach (var m in a.Mods) { Str(w, m.Guid); Str(w, m.Version); }
            return Finish(ms, w);
        }
        public static AuthRequest DecodeAuth(byte[] p)
        {
            using var r = new BinaryReader(new MemoryStream(p));
            var a = new AuthRequest();
            uint tl = r.ReadUInt32();
            if (tl == 0 || tl > 2048) throw new InvalidDataException("bad ticket size");
            a.Ticket = Exact(r, (int)tl);
            a.Password = Str(r); a.Name = Str(r); a.GameVersion = Str(r);
            int n = r.ReadUInt16();
            if (n > 256) throw new InvalidDataException("too many mods");
            for (int i = 0; i < n; i++) a.Mods.Add(new ModInfo { Guid = Str(r), Version = Str(r) });
            Complete(r);
            return a;
        }

        public static byte[] Encode(Roster x)
        {
            if (x.Players.Count > 64) throw new InvalidDataException("Too many roster entries");
            using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
            w.Write(x.Server); Str(w, x.Name); Str(w, x.Motd);
            w.Write((ushort)x.Players.Count);
            foreach (var p in x.Players) { w.Write(p.Key); Str(w, p.Value); }
            return Finish(ms, w);
        }
        public static Roster DecodeRoster(byte[] p)
        {
            using var r = new BinaryReader(new MemoryStream(p));
            var x = new Roster { Server = r.ReadUInt64(), Name = Str(r), Motd = Str(r) };
            int n = r.ReadUInt16();
            if (n > 64) throw new InvalidDataException("too many players");
            for (int i = 0; i < n; i++) x.Players.Add(new KeyValuePair<ulong, string>(r.ReadUInt64(), Str(r)));
            Complete(r);
            return x;
        }

        public static byte[] Encode(Reject x)
        {
            using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
            string Short(string value) {
                if (value == null) return "";
                int length = Math.Min(value.Length, 200);
                if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
                return value.Substring(0, length);
            }
            w.Write((byte)x.Code); Str(w, Short(x.Message));
            int count = Math.Min(16, x.Details.Count);
            w.Write((ushort)count);
            for (int i = 0; i < count; i++) Str(w, Short(x.Details[i]));
            return Finish(ms, w);
        }
        public static Reject DecodeReject(byte[] p)
        {
            using var r = new BinaryReader(new MemoryStream(p));
            var x = new Reject { Code = (Why)r.ReadByte(), Message = Str(r) };
            int n = r.ReadUInt16();
            if (n > 256) throw new InvalidDataException("too many details");
            for (int i = 0; i < n; i++) x.Details.Add(Str(r));
            Complete(r);
            return x;
        }

        /// Compare without leaking where the first difference is.
        public static bool SameSecret(string a, string b)
        {
            var x = Encoding.UTF8.GetBytes(a ?? ""); var y = Encoding.UTF8.GetBytes(b ?? "");
            int diff = x.Length ^ y.Length;
            for (int i = 0; i < 256; i++)
            {
                int bx = i < x.Length ? x[i] : 0, by = i < y.Length ? y[i] : 0;
                diff |= bx ^ by;
            }
            return diff == 0 && x.Length <= 256;
        }
    }
}

