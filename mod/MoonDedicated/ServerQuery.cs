using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MoonDedicated
{
    // Managed, bounded A2S queries: no Unity calls happen on the worker.
    internal sealed class ServerQuery
    {
        public string Name = "", Version = "";
        public ulong SteamId;
        public int Players, Capacity;
        public bool Password;
        public Dictionary<string, string> Rules = new Dictionary<string, string>();
        static readonly SemaphoreSlim dnsSlots = new SemaphoreSlim(4);

        internal static (string host, int port) Address(string text)
        {
            var uri = new Uri("udp://" + text.Trim());
            if (uri.Host.Length == 0 || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new FormatException("Enter host:query-port, for example play.example.com:27026");
            int port = uri.IsDefaultPort || uri.Port < 0 ? 27026 : uri.Port;
            if (port < 1 || port > 65535) throw new FormatException("The port must be from 1 to 65535");
            return (uri.Host.Trim('[', ']'), port);
        }

        public static async Task<ServerQuery> Fetch(string address)
        {
            long end = Environment.TickCount64 + 4000;
            int Left() { long left=end-Environment.TickCount64; if(left<=0) throw new TimeoutException("Server query timed out"); return (int)left; }
            var (host, port) = Address(address);
            if (port < 1 || port > 65535) throw new FormatException("Invalid query port");
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip)) {
                if (!dnsSlots.Wait(0)) throw new IOException("DNS workers busy; try again shortly");
                Task<IPAddress[]> lookup;
                try { lookup = Dns.GetHostAddressesAsync(host); }
                catch { dnsSlots.Release(); throw; }
                _ = lookup.ContinueWith(t => { _ = t.Exception; dnsSlots.Release(); }, TaskScheduler.Default);
                var ips = await lookup.WaitAsync(TimeSpan.FromMilliseconds(Left())).ConfigureAwait(false);
                ip = ips.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? ips.FirstOrDefault()
                    ?? throw new IOException("No address found");
            }
            using var udp = new UdpClient(ip.AddressFamily);
            udp.Connect(ip, port);
            async Task<byte[]> Receive()
            {
                var fragments = new Dictionary<uint, (int count, int size, byte[][] parts)>();
                int bytes = 0;
                while (true) {
                    var packet = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromMilliseconds(Left())).ConfigureAwait(false);
                    var b = packet.Buffer;
                    if (b.Length >= 5 && BitConverter.ToUInt32(b, 0) == uint.MaxValue) return b.Skip(4).ToArray();
                    if (b.Length < 13 || BitConverter.ToUInt32(b, 0) != 0xfffffffe) throw new IOException("Invalid query packet");
                    uint id = BitConverter.ToUInt32(b, 4); int count = b[8], index = b[9], size = BitConverter.ToUInt16(b, 10);
                    if ((id & 0x80000000) != 0 || count < 1 || count > 128 || index >= count || size < 12 || b.Length > size)
                        throw new IOException("Unsupported split query packet");
                    if (!fragments.TryGetValue(id, out var group)) {
                        if (fragments.Count >= 8) throw new IOException("Too many split replies");
                        group = (count, size, new byte[count][]); fragments.Add(id, group);
                    }
                    if (group.count != count || group.size != size) throw new IOException("Inconsistent split reply");
                    byte[] part = b.Skip(12).ToArray();
                    if (group.parts[index] != null && !group.parts[index].SequenceEqual(part)) throw new IOException("Conflicting query fragments");
                    if (group.parts[index] == null) { bytes += part.Length; group.parts[index] = part; }
                    if (bytes > 1024 * 1024) throw new IOException("Query reply too large");
                    if (group.parts.All(p => p != null)) {
                        var all = group.parts.SelectMany(p => p).ToArray();
                        if (all.Length < 5 || BitConverter.ToUInt32(all, 0) != uint.MaxValue) throw new IOException("Invalid assembled query");
                        return all.Skip(4).ToArray();
                    }
                }
            }
            async Task<byte[]> Ask(byte[] request)
            {
                await udp.SendAsync(request, request.Length).ConfigureAwait(false);
                var answer = await Receive().ConfigureAwait(false);
                if (answer[0] == 0x41) {
                    if (answer.Length != 5) throw new IOException("Invalid query challenge");
                    byte[] retry = request[4] == (byte)'V' ? request.Take(5).Concat(answer.Skip(1)).ToArray() : request.Concat(answer.Skip(1)).ToArray();
                    await udp.SendAsync(retry, retry.Length).ConfigureAwait(false);
                    answer = await Receive().ConfigureAwait(false);
                }
                return answer;
            }
            var info = await Ask(new byte[] {255,255,255,255,(byte)'T'}.Concat(Encoding.ASCII.GetBytes("Source Engine Query\0")).ToArray()).ConfigureAwait(false);
            var result = ParseInfo(info);
            var rules = await Ask(new byte[] {255,255,255,255,(byte)'V',255,255,255,255}).ConfigureAwait(false);
            result.Rules = ParseRules(rules);
            return result;
        }

        static string String(BinaryReader reader)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = reader.ReadByte()) != 0) { if (bytes.Count >= 65535) throw new IOException("Query string too long"); bytes.Add(b); }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        internal static ServerQuery ParseInfo(byte[] bytes)
        {
            using var r = new BinaryReader(new MemoryStream(bytes));
            if (r.ReadByte() != 0x49) throw new IOException("Unexpected info reply");
            r.ReadByte();
            var q = new ServerQuery { Name = String(r) };
            String(r); String(r); String(r); r.ReadUInt16();
            q.Players = r.ReadByte(); q.Capacity = r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte();
            q.Password = r.ReadByte() != 0; r.ReadByte(); q.Version = String(r);
            if (r.BaseStream.Position < bytes.Length) {
                byte flags = r.ReadByte();
                if ((flags & 0x80) != 0) r.ReadUInt16();
                if ((flags & 0x10) != 0) q.SteamId = r.ReadUInt64();
                if ((flags & 0x40) != 0) { r.ReadUInt16(); String(r); }
                if ((flags & 0x20) != 0) String(r);
                if ((flags & 0x01) != 0) r.ReadUInt64();
            }
            return q;
        }
        internal static Dictionary<string, string> ParseRules(byte[] bytes)
        {
            using var r = new BinaryReader(new MemoryStream(bytes));
            if (r.ReadByte() != 0x45) throw new IOException("Unexpected rules reply");
            int count = r.ReadUInt16(); var rules = new Dictionary<string, string>();
            for (int i = 0; i < count; i++) rules.Add(String(r), String(r));
            if (r.BaseStream.Position != bytes.Length) throw new IOException("Trailing rules data");
            return rules;
        }
    }
}
