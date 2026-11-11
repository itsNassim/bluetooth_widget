using System.Diagnostics;
using System.Text;
using HidSharp;

namespace BtBatteryTray;

/// <summary>
/// Read-only probe for vendor-defined HID interfaces (the ones 2.4 GHz dongles use for battery/status):
///  - listens to their input reports for 15 s and logs every different packet
///  - for interfaces that only have feature reports, reads (GET_REPORT) feature report IDs 0-255
/// Nothing is ever written to the devices.
/// </summary>
internal static class HidListen
{
    private const int Seconds = 15;

    public static async Task<string> RunAsync()
    {
        var log = new StringBuilder();
        var sync = new object();
        void Log(string s) { lock (sync) log.AppendLine(s); }

        Log($"HID listen - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({Seconds}s, read-only)");

        var tasks = new List<Task>();
        foreach (var d in DeviceList.Local.GetHidDevices())
        {
            byte[]? desc = null;
            try { desc = d.GetRawReportDescriptor(); } catch { }

            // 06 xx FF = "Usage Page 0xFFxx" = vendor-defined
            bool vendor = desc is { Length: >= 3 } && desc[0] == 0x06 && desc[2] == 0xFF;
            int maxIn = Safe(() => d.GetMaxInputReportLength());
            int maxFeat = Safe(() => d.GetMaxFeatureReportLength());
            string tag = DescribeTag(d);

            if (vendor && maxIn > 0)
                tasks.Add(Task.Run(() => ListenInput(d, tag, maxIn, Log)));
            else if (maxIn == 0 && maxFeat > 0)
                tasks.Add(Task.Run(() => ScanFeature(d, tag, maxFeat, Log)));
        }

        if (tasks.Count == 0) Log("No vendor-defined or feature-only HID interface found.");
        await Task.WhenAll(tasks);

        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "hid-listen.txt");
        File.WriteAllText(path, log.ToString());
        return path;
    }

    private static void ListenInput(HidDevice d, string tag, int maxIn, Action<string> log)
    {
        try
        {
            using var s = d.Open();
            s.ReadTimeout = 1000;
            var buf = new byte[maxIn];
            var sw = Stopwatch.StartNew();
            string? last = null;
            int count = 0;
            while (sw.Elapsed.TotalSeconds < Seconds)
            {
                try
                {
                    int n = s.Read(buf, 0, buf.Length);
                    string hex = BitConverter.ToString(buf, 0, n);
                    if (hex != last)
                    {
                        log($"[{tag}] +{sw.Elapsed.TotalSeconds:0.0}s IN : {hex}");
                        last = hex;
                    }
                    count++;
                }
                catch (TimeoutException) { }
            }
            log($"[{tag}] listen finished, {count} packet(s) received");
        }
        catch (Exception ex)
        {
            log($"[{tag}] cannot open/read: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static void ScanFeature(HidDevice d, string tag, int maxFeat, Action<string> log)
    {
        try
        {
            using var s = d.Open();
            int found = 0;
            for (int id = 0; id < 256; id++)
            {
                var buf = new byte[maxFeat];
                buf[0] = (byte)id;
                try
                {
                    s.GetFeature(buf);
                    log($"[{tag}] FEATURE id {id}: {BitConverter.ToString(buf, 0, Math.Min(buf.Length, 48))}");
                    found++;
                }
                catch { }
            }
            log($"[{tag}] feature scan finished, {found} readable report(s)");
        }
        catch (Exception ex)
        {
            log($"[{tag}] cannot open: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static string DescribeTag(HidDevice d)
    {
        var parts = d.DevicePath.Split('#');
        return parts.Length > 1 ? parts[1] : $"vid_{d.VendorID:x4}&pid_{d.ProductID:x4}";
    }

    private static int Safe(Func<int> f) { try { return f(); } catch { return 0; } }
}
