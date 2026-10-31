using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace DeviceBattery
{
    internal static class PanelScrollSmoke
    {
        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            BatterySnapshot snapshot = new BatterySnapshot();
            snapshot.BluetoothRadioKnown = true;
            snapshot.BluetoothRadioOn = true;
            for (int i = 0; i < 40; i++)
            {
                DeviceState device = new DeviceState();
                device.Id = "scroll-test:" + i;
                device.Category = i == 1 ? DeviceCategory.Keyboard
                    : (i == 2 ? DeviceCategory.Headset : DeviceCategory.Mouse);
                device.DisplayName = "滚动测试设备 " + (i + 1);
                device.Model = "Scroll Test " + (i + 1);
                device.Transport = i == 2 ? TransportKind.BluetoothLe : TransportKind.Usb;
                device.Status = DeviceStatus.Online;
                device.Connected = true;
                device.BatterySupported = true;
                device.LastReading = new BatteryReading();
                device.LastReading.Percent = 80 - (i % 40);
                device.LastReading.Timestamp = DateTime.Now;
                device.LastReading.Source = BatterySourceKind.GenericHid;
                snapshot.Devices.Add(device);
            }

            using (DevicePanel panel = new DevicePanel())
            {
                panel.SetSnapshot(snapshot);
                panel.StartPosition = FormStartPosition.Manual;
                panel.Location = new Point(-2000, -2000);
                panel.Show();
                Application.DoEvents();

                FieldInfo visible = typeof(DevicePanel).GetField("_scrollBarVisible",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo offset = typeof(DevicePanel).GetField("_scrollOffset",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo scroll = typeof(DevicePanel).GetMethod("ScrollBy",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                bool hasBar = (bool)visible.GetValue(panel);
                scroll.Invoke(panel, new object[] { 180 });
                int scrolled = (int)offset.GetValue(panel);
                scroll.Invoke(panel, new object[] { -10000 });
                int top = (int)offset.GetValue(panel);

                Console.WriteLine("panel scroll: height=" + panel.Height +
                    " scrollVisible=" + hasBar + " scrolled=" + scrolled + " top=" + top);
                panel.Hide();

                if (!hasBar || scrolled <= 0 || top != 0)
                {
                    Console.Error.WriteLine("panel scroll smoke failed");
                    return 1;
                }
            }
            return 0;
        }
    }
}
