// 性能探针：量面板、设置页、历史页的构造与重绘耗时（毫秒），调性能时手动跑。
//
// 用法（不参与 build.ps1 / test.ps1）：照 开发\test.ps1 里 Build-Harness 的引用与参数自己编一次，
//   把 /main: 换成 PerfCheck、源文件换成 tools\PerfCheck.cs、产物放 开发\tests\，再直接运行。
//
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DeviceBattery;

internal static class PerfCheck
{
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Theme.Apply("dark");

        // 1. 字体创建开销：旧路径每次都 new，新路径走缓存。
        const int rounds = 2000;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < rounds; i++)
        {
            using (Font font = Theme.UiFont(22f, FontStyle.Bold))
            {
                if (font.Height < 0) { Console.WriteLine("bad"); }
            }
        }
        watch.Stop();
        long rawTicks = watch.ElapsedTicks;

        watch = Stopwatch.StartNew();
        for (int i = 0; i < rounds; i++)
        {
            Font font = Theme.PaintFont(22f, FontStyle.Bold);
            if (font.Height < 0) { Console.WriteLine("bad"); }
        }
        watch.Stop();
        long cachedTicks = watch.ElapsedTicks;

        Console.WriteLine("font new x" + rounds + ": " +
            (rawTicks * 1000.0 / Stopwatch.Frequency).ToString("F1") + " ms");
        Console.WriteLine("font cache x" + rounds + ": " +
            (cachedTicks * 1000.0 / Stopwatch.Frequency).ToString("F1") + " ms");

        // 2. 完整面板重绘开销：4 张卡片，模拟一次 hover 引起的 Invalidate。
        BatterySnapshot snapshot = BuildSnapshot();
        using (DevicePanel panel = new DevicePanel())
        {
            panel.SetSnapshot(snapshot);
            panel.StartPosition = FormStartPosition.Manual;
            panel.Location = new Point(-2000, -2000);
            panel.Show();
            Application.DoEvents();

            using (Bitmap bitmap = new Bitmap(panel.Width, panel.Height))
            {
                // 预热，排除首次 JIT 和 GDI 初始化。
                for (int i = 0; i < 5; i++)
                {
                    using (Graphics warm = Graphics.FromImage(bitmap))
                    {
                        panel.DrawToBitmap(bitmap, new Rectangle(0, 0, panel.Width, panel.Height));
                    }
                }

                const int frames = 200;
                watch = Stopwatch.StartNew();
                for (int i = 0; i < frames; i++)
                {
                    panel.DrawToBitmap(bitmap, new Rectangle(0, 0, panel.Width, panel.Height));
                }
                watch.Stop();
                Console.WriteLine("panel repaint x" + frames + ": " +
                    (watch.Elapsed.TotalMilliseconds / frames).ToString("F2") + " ms/frame");
            }
            panel.Hide();
        }
    }

    private static BatterySnapshot BuildSnapshot()
    {
        BatterySnapshot snapshot = new BatterySnapshot();
        snapshot.BluetoothRadioKnown = true;
        snapshot.BluetoothRadioOn = true;

        AddDevice(snapshot, "atk:1", "ATK A9 Ultra Max 2.0 Tri-Mode", DeviceCategory.Mouse,
            TransportKind.Atk24G, 45, 3931);
        AddDevice(snapshot, "usb:2", "ZhuAudio ZG2", DeviceCategory.Headset,
            TransportKind.Usb24G, -1, 0);
        AddDevice(snapshot, "kbd:3", "Keychron K3 Pro", DeviceCategory.Keyboard,
            TransportKind.Usb, 78, 0);
        AddDevice(snapshot, "kbd:4", "WIN 60 HE PRO", DeviceCategory.Keyboard,
            TransportKind.Usb, -1, 0);
        return snapshot;
    }

    private static void AddDevice(BatterySnapshot snapshot, string id, string name,
        DeviceCategory category, TransportKind transport, int percent, int millivolts)
    {
        DeviceState device = new DeviceState();
        device.Id = id;
        device.DisplayName = name;
        device.Category = category;
        device.Transport = transport;
        device.Connected = true;
        device.BatterySupported = percent >= 0;
        if (percent >= 0)
        {
            device.LastReading = new BatteryReading();
            device.LastReading.Percent = percent;
            device.LastReading.Charging = false;
            if (millivolts > 0)
            {
                device.LastReading.Millivolts = millivolts;
            }
            device.LastReading.Timestamp = DateTime.Now;
            device.Status = DeviceStatus.Online;
        }
        else
        {
            device.Status = DeviceStatus.Unsupported;
            device.UnsupportedReason = "2.4G 接收器不提供电量，请用蓝牙";
        }
        snapshot.Devices.Add(device);
    }
}
