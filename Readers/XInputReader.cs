using System.Runtime.InteropServices;
using System.Text;

namespace BtBatteryTray;

/// <summary>
/// Any XInput gamepad (Xbox-compatible, including many 2.4 GHz dongle controllers).
/// Windows only gives 4 coarse levels, and only if the controller/dongle reports its battery.
/// </summary>
internal sealed class XInputReader : IBatteryReader
{
    public string Name => "XInput";

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInfo { public byte BatteryType; public byte BatteryLevel; }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetBatteryInformation(uint userIndex, byte devType, out BatteryInfo info);

    public Task<List<BatteryDevice>> ReadAsync()
    {
        var list = new List<BatteryDevice>();
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                if (XInputGetBatteryInformation(i, 0, out var info) != 0) continue; // slot empty
                if (info.BatteryType is not (2 or 3)) continue;                    // only alkaline / NiMH = real battery
                int level = info.BatteryLevel switch { 0 => 5, 1 => 25, 2 => 60, 3 => 100, _ => -1 };
                if (level < 0) continue;
                list.Add(new BatteryDevice($"xinput{i}", $"Controller {i + 1}", level, Name, Approx: true));
            }
        }
        catch (DllNotFoundException) { }
        return Task.FromResult(list);
    }

    /// <summary>Raw values for the diagnostics file (so we can see what the controller really reports).</summary>
    public static string DescribeRaw()
    {
        var sb = new StringBuilder();
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                uint r = XInputGetBatteryInformation(i, 0, out var info);
                sb.AppendLine(r == 0
                    ? $"XInput slot {i}: BatteryType={info.BatteryType} (0=none,1=wired,2=alkaline,3=NiMH,255=unknown) BatteryLevel={info.BatteryLevel} (0=empty..3=full)"
                    : $"XInput slot {i}: no controller (code {r})");
            }
        }
        catch (Exception ex) { sb.AppendLine("XInput unavailable: " + ex.Message); }
        return sb.ToString();
    }
}
