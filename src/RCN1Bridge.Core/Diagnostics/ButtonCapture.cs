using System.Text;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Diagnostics;

public sealed class ButtonCapture
{
    public const string Rest = "Rest";

    private readonly Dictionary<string, IReadOnlyList<ByteWindow>> _steps = [];
    private readonly List<string> _order = [];

    public bool Has(string step) => _steps.ContainsKey(step);
    public int Count => _steps.Count;

    public void Store(string step, IReadOnlyList<ByteWindow> windows)
    {
        if (!_steps.ContainsKey(step))
            _order.Add(step);
        _steps[step] = windows;
    }

    public void Clear()
    {
        _steps.Clear();
        _order.Clear();
    }

    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Button capture");
        if (!_steps.TryGetValue(Rest, out var rest))
        {
            sb.AppendLine("  No Rest capture, so nothing to compare against.");
            AppendSlots(sb);
            return sb.ToString();
        }

        foreach (string step in _order.Where(s => s != Rest))
        {
            sb.AppendLine($"  {step}:");
            var lines = Differences(rest, _steps[step]).ToList();
            if (lines.Count == 0)
                sb.AppendLine("    no difference from Rest");
            foreach (string line in lines)
                sb.AppendLine($"    {line}");
        }

        AppendSlots(sb);
        return sb.ToString();
    }

    internal static IEnumerable<string> Differences(IReadOnlyList<ByteWindow> rest, IReadOnlyList<ByteWindow> held)
    {
        foreach (var h in held)
        {
            var r = rest.FirstOrDefault(w => w.Key == h.Key);
            if (r is null)
            {
                yield return $"{h.Key} only appeared here: {Convert.ToHexString(h.Sample)}";
                continue;
            }
            for (int i = 0; i < h.Length; i++)
            {
                if (FrameStats.IsNoiseByte(i, h.Length))
                    continue;
                // Disjoint ranges only; overlapping ranges are stick jitter
                if (h.Max[i] < r.Min[i] || h.Min[i] > r.Max[i])
                    yield return $"{h.Key} byte {i}: rest {r.Min[i]:X2}..{r.Max[i]:X2}, held {h.Min[i]:X2}..{h.Max[i]:X2}";
            }
        }
        foreach (var r in rest.Where(r => held.All(h => h.Key != r.Key)))
            yield return $"{r.Key} stopped arriving";
    }

    private void AppendSlots(StringBuilder sb)
    {
        var slots = new ChannelSlot[StickDecoder.SlotCount];
        bool header = false;
        foreach (string step in _order)
        {
            var frame = _steps[step].FirstOrDefault(w => w.Length == StickDecoder.ExtendedFrameLength && w.CommandId == RcCommands.GetChannels);
            if (frame is null)
                continue;
            int count = StickDecoder.DecodeSlots(frame.Sample, slots);
            if (!header)
            {
                sb.AppendLine("  Stick frame slots (tag:value) at the end of each capture:");
                header = true;
            }
            var values = string.Join("  ", Enumerable.Range(0, count).Select(i => $"{slots[i].Tag:X2}:{slots[i].Value,4}"));
            sb.AppendLine($"    {step,-14} b11={frame.Sample[11]:X2}  {values}");
        }

        header = false;
        foreach (string step in _order)
        {
            var frame = _steps[step].FirstOrDefault(w => w.Length == ButtonDecoder.FrameLength && w.CommandId == RcCommands.GetButtons);
            if (frame is null || !ButtonDecoder.TryDecode(new DumlFrame(frame.Sample), out var b))
                continue;
            if (!header)
            {
                sb.AppendLine("  Button bits (06/27) at the end of each capture:");
                header = true;
            }
            var held = new[] { ("Fn", b.Fn), ("Capture", b.Capture), ("Photo/Video", b.PhotoVideo), ("RTH", b.ReturnHome) }
                .Where(x => x.Item2).Select(x => x.Item1);
            sb.AppendLine($"    {step,-14} {b.Bits:X4}  mode {b.Mode,-7} {string.Join(" ", held)}");
        }
    }
}
