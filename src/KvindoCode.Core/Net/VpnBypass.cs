using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KvindoCode.Core.Net;

/// <summary>
/// Keeps API traffic off the VPN: when a VPN interface (tun/wg/ppp/…) is up, outgoing API sockets are pinned to the
/// physical default-route interface with SO_BINDTODEVICE (no root needed on Linux ≥ 5.7, no routing-table edits).
/// </summary>
public static class VpnBypass
{
    static readonly string[] VpnPrefixes = { "tun", "tap", "wg", "ppp", "utun", "tailscale", "nordlynx", "zt", "ipsec", "vpn", "proton", "mullvad", "cscotun", "gpd", "ham" };
    static readonly string[] VirtualPrefixes = { "docker", "veth", "br-", "virbr", "mpqemubr", "lxc", "lxd", "vmnet", "vboxnet", "cni", "flannel", "cali", "kube", "dummy" };

    const int SOL_SOCKET = 1, SO_BINDTODEVICE = 25;

    public static bool IsVpnName(string n) => VpnPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    static bool IsVirtual(string n) => n == "lo" || VirtualPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    static bool IsUp(string iface)
    {
        try
        {
            var flags = File.ReadAllText($"/sys/class/net/{iface}/flags").Trim();
            return (Convert.ToInt32(flags, 16) & 1) != 0;      // IFF_UP
        }
        catch { return false; }
    }

    public static List<string> ActiveVpnInterfaces()
    {
        var res = new List<string>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories("/sys/class/net"))
            {
                var n = Path.GetFileName(d);
                if (IsVpnName(n) && IsUp(n)) res.Add(n);
            }
        }
        catch { }
        return res;
    }

    /// <summary>Lowest-metric default-route interface that is not a VPN / container bridge.</summary>
    public static string? PhysicalInterface(string? preferred = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && Directory.Exists($"/sys/class/net/{preferred}")) return preferred;
        try
        {
            var routes = new List<(string iface, int metric)>();
            foreach (var line in File.ReadLines("/proc/net/route").Skip(1))
            {
                var p = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 8) continue;
                if (p[1] != "00000000" || p[7] != "00000000") continue;   // default route only
                routes.Add((p[0], int.TryParse(p[6], out var m) ? m : 0));
            }
            foreach (var (iface, _) in routes.OrderBy(r => r.metric))
                if (!IsVpnName(iface) && !IsVirtual(iface) && IsUp(iface)) return iface;
        }
        catch { }
        return null;
    }

    /// <summary>Human-readable status for the settings dialog.</summary>
    public static string Describe(AppSettings s)
    {
        if (!OperatingSystem.IsLinux()) return "VPN bypass is only implemented on Linux.";
        var vpn = ActiveVpnInterfaces();
        var phys = PhysicalInterface(s.BypassInterface);
        if (!s.BypassVpn) return "Disabled — API traffic follows the system routes.";
        if (phys == null) return "Enabled, but no physical default-route interface was found (traffic uses normal routing).";
        return vpn.Count > 0
            ? $"Active — VPN interface{(vpn.Count > 1 ? "s" : "")} {string.Join(", ", vpn)} detected; API connections are pinned to {phys}."
            : $"Armed — no VPN is up right now; API connections will be pinned to {phys} whenever one is.";
    }

    /// <summary>Returns the interface to pin to right now, or null for normal routing.</summary>
    public static string? ActiveBypassInterface(AppSettings s)
    {
        if (!s.BypassVpn || !OperatingSystem.IsLinux()) return null;
        if (ActiveVpnInterfaces().Count == 0) return null;
        return PhysicalInterface(s.BypassInterface);
    }

    public static SocketsHttpHandler CreateHandler(AppSettings s) => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(20),
        ConnectCallback = (ctx, ct) => ConnectAsync(s, ctx.DnsEndPoint, ct),
    };

    static async ValueTask<Stream> ConnectAsync(AppSettings s, DnsEndPoint ep, CancellationToken ct)
    {
        var iface = ActiveBypassInterface(s);
        IPAddress[] addrs;
        try { addrs = await Dns.GetHostAddressesAsync(ep.Host, ct); }
        catch (Exception e) { throw new HttpRequestException($"DNS lookup for {ep.Host} failed: {e.Message}", e); }
        // IPv4 first: many home/VPN setups have no working IPv6 path
        var ordered = addrs.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();

        Exception? last = null;
        if (iface != null)
        {
            foreach (var a in ordered)
            {
                try { return await ConnectOne(a, ep.Port, iface, ct); }
                catch (Exception e) when (e is SocketException or IOException or InvalidOperationException) { last = e; }
            }
            // pinning failed (interface vanished, firewall…): fall back to normal routing rather than breaking the app
        }
        foreach (var a in ordered)
        {
            try { return await ConnectOne(a, ep.Port, null, ct); }
            catch (SocketException e) { last = e; }
        }
        throw new HttpRequestException($"Could not connect to {ep.Host}:{ep.Port}: {last?.Message}", last);
    }

    static async Task<Stream> ConnectOne(IPAddress addr, int port, string? iface, CancellationToken ct)
    {
        var sock = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (iface != null) sock.SetRawSocketOption(SOL_SOCKET, SO_BINDTODEVICE, Encoding.ASCII.GetBytes(iface + "\0"));
            await sock.ConnectAsync(new IPEndPoint(addr, port), ct);
            return new NetworkStream(sock, ownsSocket: true);
        }
        catch { sock.Dispose(); throw; }
    }
}
