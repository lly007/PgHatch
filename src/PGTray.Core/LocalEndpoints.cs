using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PGTray.Core;

internal static class LocalEndpoints
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

    // Listener ownership ties readiness to the actual postmaster PID, rather than an arbitrary open port.
    public static List<IPEndPoint>? ForProcess(int pid, int port)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var endpoints = new List<IPEndPoint>();
        foreach (var family in new[] { 2, 23 })
        {
            var size = 0;
            var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (result != 122 || size < 4) return null;
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(memory, ref size, false, family, 3, 0);
                if (result != 0) return null;
                var count = Marshal.ReadInt32(memory);
                var rowSize = family == 2 ? 24 : 56;
                if (count < 0 || (long)count * rowSize + 4 > size) return null;
                for (var row = 0; row < count; row++)
                {
                    var start = IntPtr.Add(memory, 4 + row * rowSize);
                    var owner = Marshal.ReadInt32(start, family == 2 ? 20 : 52);
                    var rawPort = Marshal.ReadInt32(start, family == 2 ? 8 : 20);
                    var actualPort = ((rawPort & 255) << 8) | ((rawPort >> 8) & 255);
                    if (owner != pid || port != actualPort) continue;
                    var bytes = new byte[family == 2 ? 4 : 16];
                    Marshal.Copy(IntPtr.Add(start, family == 2 ? 4 : 0), bytes, 0, bytes.Length);
                    var address = family == 2 ? new IPAddress(bytes) : new IPAddress(bytes, (uint)Marshal.ReadInt32(start, 16));
                    if (address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
                    if (address.Equals(IPAddress.IPv6Any)) address = IPAddress.IPv6Loopback;
                    endpoints.Add(new(address, port));
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        return endpoints.Distinct().OrderBy(e => e.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
    }
}
