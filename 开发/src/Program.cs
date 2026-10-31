using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DeviceBattery
{
    internal static class Program
    {
        public static bool ShowPanelOnStart;
        public static bool ShowSettingsOnStart;

        [STAThread]
        private static void Main()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            ApplyPortableArgument(arguments);

            if (HasArgument(arguments, "--once") || HasArgument(arguments, "--snapshot"))
            {
                Environment.ExitCode = RunOnce();
                return;
            }

            if (HasArgument(arguments, "--autostart"))
            {
                string mode = ValueAfter(arguments, "--autostart");
                Environment.ExitCode = AutoStart.RunFromCommandLine(string.IsNullOrEmpty(mode) ? "on" : mode);
                return;
            }

            if (HasArgument(arguments, "--help") || HasArgument(arguments, "/?"))
            {
                ShowHelp();
                return;
            }

            if (Native.IsLowIntegrity())
            {
                ShowRestrictedStartupHelp();
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
            {
                Log.Error("Unhandled: " + args.ExceptionObject);
            };
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args)
            {
                Log.Error("ThreadException: " + args.Exception);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            ShowPanelOnStart = HasArgument(arguments, "--panel");
            ShowSettingsOnStart = HasArgument(arguments, "--settings");

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "VoltPick.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    SingleInstance.SignalExisting();
                    return;
                }

                SingleInstance.Listen();
                Native.EnableDpiAwareness();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    Application.Run(new TrayApp());
                }
                catch (Exception ex)
                {
                    Log.Error("Fatal: " + ex);
                    throw;
                }
                GC.KeepAlive(mutex);
            }
        }

        private static void ApplyPortableArgument(string[] arguments)
        {
            try
            {
                if (HasArgument(arguments, "--portable"))
                {
                    File.WriteAllText(AppSettings.PortableMarker, "portable", Encoding.UTF8);
                }
                else if (HasArgument(arguments, "--no-portable"))
                {
                    if (File.Exists(AppSettings.PortableMarker))
                    {
                        File.Delete(AppSettings.PortableMarker);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("处理便携模式参数失败：" + ex.Message);
            }
        }

        private static bool HasArgument(string[] arguments, string name)
        {
            for (int i = 1; i < arguments.Length; i++)
            {
                if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string ValueAfter(string[] arguments, string name)
        {
            for (int i = 1; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[i + 1];
                }
            }
            return null;
        }

        private static int RunOnce()
        {
            BatteryService service = null;
            try
            {
                AppSettings.Load();
                service = new BatteryService();
                // Start() 会主动唤醒线程并完成首轮发现/读取；这里不要再排一次手动查询，
                // 否则 --once 会对同一设备读两次。
                service.Start(false);

                DateTime deadline = DateTime.Now.AddSeconds(10);
                BatterySnapshot snapshot = service.GetSnapshot();
                while (DateTime.Now < deadline)
                {
                    Thread.Sleep(250);
                    snapshot = service.GetSnapshot();
                    if (service.HasDiscovered)
                    {
                        // 启动时已经请求过一次；这里只等首轮查询完成，避免同一设备被连读两次。
                        // 等到「每台设备都已经有结论」：读到值、终态（不支持/未连接/蓝牙关），
                        // 或者已经至少查过一次。以前这里对“还没轮到查询”的设备（比如未连接的
                        // 蓝牙耳机要拖满 9 秒超时）空等到 15 秒，--once 因此要 17 秒才返回。
                        DateTime queryDeadline = DateTime.Now.AddSeconds(8);
                        while (DateTime.Now < queryDeadline)
                        {
                            Thread.Sleep(150);
                            snapshot = service.GetSnapshot();
                            if (AllSettledForOnce(snapshot))
                            {
                                break;
                            }
                        }

                        // 只有“在线但还没读出数字”的设备才值得再等一轮（鼠标刚被唤醒时常见）；
                        // 其它情况立刻收工。
                        if (HasPendingOnline(snapshot))
                        {
                            Thread.Sleep(900);
                            snapshot = service.GetSnapshot();
                            if (!HasAnyPercent(snapshot) && HasPendingOnline(snapshot))
                            {
                                service.RequestRefreshAll();
                                Thread.Sleep(1200);
                                snapshot = service.GetSnapshot();
                            }
                        }
                        snapshot = service.GetSnapshot();
                        break;
                    }
                }

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["timestamp"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
                root["bluetoothRadioKnown"] = snapshot.BluetoothRadioKnown;
                root["bluetoothRadioOn"] = snapshot.BluetoothRadioOn;
                root["bluetoothError"] = snapshot.BluetoothError;
                List<object> devices = new List<object>();
                bool anyPercent = false;
                for (int i = 0; i < snapshot.Devices.Count; i++)
                {
                    DeviceState device = snapshot.Devices[i];
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    item["id"] = device.Id;
                    item["name"] = device.DisplayName;
                    item["category"] = device.CategoryText;
                    item["transport"] = device.TransportText;
                    item["status"] = device.StatusText;
                    item["connected"] = device.Connected;
                    item["batterySupported"] = device.BatterySupported;
                    item["percent"] = device.HasValue ? (object)device.PercentOrZero : null;
                    item["charging"] = device.LastReading != null && device.LastReading.Charging.HasValue
                        ? (object)device.LastReading.Charging.Value : null;
                    item["millivolts"] = device.LastReading != null && device.LastReading.Millivolts.HasValue
                        ? (object)device.LastReading.Millivolts.Value : null;
                    item["leftPercent"] = device.LastReading != null && device.LastReading.LeftPercent.HasValue
                        ? (object)device.LastReading.LeftPercent.Value : null;
                    item["rightPercent"] = device.LastReading != null && device.LastReading.RightPercent.HasValue
                        ? (object)device.LastReading.RightPercent.Value : null;
                    item["casePercent"] = device.LastReading != null && device.LastReading.CasePercent.HasValue
                        ? (object)device.LastReading.CasePercent.Value : null;
                    item["source"] = device.SourceText;
                    item["timestamp"] = device.LastReading == null || device.LastReading.Timestamp == DateTime.MinValue
                        ? null
                        : device.LastReading.Timestamp.ToString("o", CultureInfo.InvariantCulture);
                    item["error"] = device.LastError;
                    item["unsupportedReason"] = device.UnsupportedReason;
                    item["queryCount"] = device.QueryCount;
                    item["successCount"] = device.SuccessCount;
                    if (device.HasValue)
                    {
                        anyPercent = true;
                    }
                    devices.Add(item);
                }
                root["devices"] = devices;

                JavaScriptSerializer json = new JavaScriptSerializer();
                string text = json.Serialize(root);
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "device-battery-once.json");
                File.WriteAllText(path, text, new UTF8Encoding(false));
                Console.WriteLine(text);
                return anyPercent ? 0 : (snapshot.Devices.Count > 0 ? 1 : 2);
            }
            catch (Exception ex)
            {
                Log.Error("--once 失败：" + ex);
                try
                {
                    Dictionary<string, object> error = new Dictionary<string, object>();
                    error["error"] = ex.ToString();
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "device-battery-once.json");
                    File.WriteAllText(path, new JavaScriptSerializer().Serialize(error), new UTF8Encoding(false));
                }
                catch (Exception)
                {
                }
                return 3;
            }
            finally
            {
                if (service != null)
                {
                    service.Dispose();
                }
            }
        }

        /// <summary>
        /// --once 的收工条件：所有设备都已有结论（读到值、终态，或至少查过一次）。
        /// 只要还有“在线却既没值也没查过”的设备就继续等。
        /// </summary>
        internal static bool AllSettledForOnce(BatterySnapshot snapshot)
        {
            if (snapshot == null || snapshot.Devices == null || snapshot.Devices.Count == 0)
            {
                return true;
            }
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                DeviceState device = snapshot.Devices[i];
                if (device.HasValue || device.QueryCount > 0)
                {
                    continue;
                }
                if (!device.BatterySupported)
                {
                    continue;
                }
                if (device.Status == DeviceStatus.Unsupported ||
                    device.Status == DeviceStatus.BluetoothOff ||
                    device.Status == DeviceStatus.Offline)
                {
                    continue;
                }
                return false;
            }
            return true;
        }

        /// <summary>还有没有“在线但没读到数字”的设备（这种才值得再补一次查询）。</summary>
        internal static bool HasPendingOnline(BatterySnapshot snapshot)
        {
            if (snapshot == null || snapshot.Devices == null)
            {
                return false;
            }
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                DeviceState device = snapshot.Devices[i];
                if (device.HasValue)
                {
                    continue;
                }
                if (device.Status == DeviceStatus.Online || device.Status == DeviceStatus.Charging)
                {
                    return true;
                }
            }
            return false;
        }
        private static bool HasAnyPercent(BatterySnapshot snapshot)
        {
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                if (snapshot.Devices[i].HasValue)
                {
                    return true;
                }
            }
            return false;
        }

        private static void ShowHelp()
        {
            MessageBox.Show(
                AppInfo.DisplayName + "\n\n" +
                "用法：\n" +
                "  VoltPick.exe                 启动托盘程序\n" +
                "  VoltPick.exe --panel         启动并显示面板\n" +
                "  VoltPick.exe --settings      启动并打开设置\n" +
                    "  VoltPick.exe --once          读取一次，写出 device-battery-once.json\n" +
                "  VoltPick.exe --snapshot      同 --once\n" +
                "  VoltPick.exe --autostart on|off\n" +
                "  VoltPick.exe --portable      使用 exe 同目录的 settings.json\n" +
                "  VoltPick.exe --no-portable   使用 %APPDATA%\\VoltPick\\settings.json\n",
                "VoltPick", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void ShowRestrictedStartupHelp()
        {
            string folder = Path.GetDirectoryName(Application.ExecutablePath);
            MessageBox.Show(
                "这个程序是以「受限权限」启动的，Windows 会拦截托盘图标。\n\n" +
                "请打开资源管理器，进入这个文件夹：\n" + folder + "\n\n" +
                "在资源管理器里双击「VoltPick.exe」运行，不要从聊天窗口/网页直接点开。",
                "VoltPick", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
