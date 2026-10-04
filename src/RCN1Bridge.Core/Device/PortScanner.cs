using System.Management;
using System.Text.RegularExpressions;
using RCN1Bridge.Core.Diagnostics;

namespace RCN1Bridge.Core.Device;

public sealed record PortInfo(string PortName, string Name, string? PnpDeviceId)
{
    public bool IsDjiProtocol => Name.Contains("For Protocol", StringComparison.OrdinalIgnoreCase);

    // 2CA3 is DJI's USB vendor ID
    public bool IsDji =>
        Name.Contains("DJI", StringComparison.OrdinalIgnoreCase)
        || (PnpDeviceId?.Contains("VID_2CA3", StringComparison.OrdinalIgnoreCase) ?? false);

    public int Number => int.TryParse(PortName.AsSpan(3), out int n) ? n : int.MaxValue;
}

public static partial class PortScanner
{
    public static IReadOnlyList<PortInfo> Scan()
    {
        var ports = new List<PortInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                {
                    if (item["Name"] is not string name)
                        continue;
                    var match = ComName().Match(name);
                    if (match.Success)
                        ports.Add(new PortInfo(match.Groups[1].Value, name, item["PNPDeviceID"] as string));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log.Warn($"Port scan failed: {ex.Message}");
        }

        ports.Sort((a, b) => a.Number.CompareTo(b.Number));
        return ports;
    }

    [GeneratedRegex(@"\((COM\d+)\)")]
    private static partial Regex ComName();
}
