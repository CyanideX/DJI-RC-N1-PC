using System.Reflection;
using System.Text.RegularExpressions;
using RCN1Bridge.Core.Device;
using RCN1Bridge.Core.Output;

namespace RCN1Bridge.Core.Tests;

// sdk/rcn1link.h is what readers copy, so it has to agree with the writer down to the last offset
public partial class SdkHeaderTests
{
    // Reader-side timing with no constant of the same name on the writer
    private static readonly HashSet<string> HeaderOnly = ["STALE_MS", "ALIVE_MS", "RETRY_MS"];

    [Fact]
    public void HeaderMatchesTheWriter()
    {
        string header = File.ReadAllText(FindHeader());
        var defines = Define().Matches(header).ToDictionary(
            m => m.Groups[1].Value,
            m => Convert.ToInt64(m.Groups[2].Value, m.Groups[2].Value.StartsWith("0x") ? 16 : 10));
        var constants = typeof(GameLink).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType != typeof(string))
            .ToDictionary(f => Upper().Replace(f.Name, "_").ToUpperInvariant(), f => Convert.ToInt64(f.GetRawConstantValue()));

        Assert.Equal(
            constants.OrderBy(c => c.Key),
            defines.Where(d => !HeaderOnly.Contains(d.Key)).OrderBy(d => d.Key));
        Assert.Contains($"#define RCN1_BLOCK_NAME L\"{GameLink.DefaultName.Replace(@"\", @"\\")}\"", header);
        Assert.Equal((long)BridgeEngine.StallTimeout.TotalMilliseconds, defines["STALE_MS"]);
        Assert.Equal((long)GameLink.ReaderTimeout.TotalMilliseconds, defines["ALIVE_MS"]);
    }

    private static string FindHeader()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "sdk", "rcn1link.h");
            if (File.Exists(path))
                return path;
        }
        throw new FileNotFoundException("sdk/rcn1link.h not found above the test output");
    }

    [GeneratedRegex(@"^#define RCN1_([A-Z0-9_]+) (0x[0-9A-Fa-f]+|\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex Define();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex Upper();
}
