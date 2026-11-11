using System.Text;
using HidSharp;

namespace BtBatteryTray;

/// <summary>
/// Diagnostics: lists every HID device (VID/PID, names, report descriptor) plus raw XInput values.
/// It does NOT read keystrokes or mouse movement - it only describes the devices.
/// Use it to find out how a given mouse/controller exposes its battery.
/// </summary>
internal static class HidDump
{
    public static string Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"HID dump - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(XInputReader.DescribeRaw());
        sb.AppendLine("GameSir last info reply: " + GameSirReader.LastRaw);
        sb.AppendLine();

        foreach (var d in DeviceList.Local.GetHidDevices().OrderBy(x => x.VendorID).ThenBy(x => x.ProductID))
        {
            sb.AppendLine(new string('-', 70));
            sb.AppendLine($"VID_{d.VendorID:X4}  PID_{d.ProductID:X4}");
            sb.AppendLine("Product:      " + Safe(() => d.GetProductName()));
            sb.AppendLine("Manufacturer: " + Safe(() => d.GetManufacturer()));
            sb.AppendLine("Path:         " + d.DevicePath);
            sb.AppendLine($"MaxInput={Safe(() => d.GetMaxInputReportLength())}  MaxOutput={Safe(() => d.GetMaxOutputReportLength())}  MaxFeature={Safe(() => d.GetMaxFeatureReportLength())}");
            try { sb.AppendLine("ReportDescriptor: " + BitConverter.ToString(d.GetRawReportDescriptor())); }
            catch (Exception ex) { sb.AppendLine("ReportDescriptor: <" + ex.GetType().Name + ">"); }
        }

        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "hid-dump.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static string Safe<T>(Func<T> f)
    {
        try { return f()?.ToString() ?? ""; }
        catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
    }
}
