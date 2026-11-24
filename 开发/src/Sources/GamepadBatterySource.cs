using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Devices.Power;
using Windows.Gaming.Input;

namespace DeviceBattery
{
    /// <summary>
    /// 游戏手柄电量：Xbox 走 Windows.Gaming.Input 的真实毫瓦时读数，
    /// 老手柄走 XInput 的粗档位；DualShock 4 / DualSense 的电量在 HID 输入报告里。
    /// 手柄类别是新增的，托盘、面板、设置页都要能认它。
    /// </summary>
    internal sealed class GamepadBatterySource
    {
        private const ushort SonyVid = 0x054C;

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputBatteryInformation
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint dwPacketNumber;
            // Gamepad 结构只有 12 字节，这里只关心有没有手柄占用槽位。
            private byte _reserved;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState(uint userIndex, out XInputState state);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern int XInputGetBatteryInformation(uint userIndex, byte deviceType,
            out XInputBatteryInformation info);

        private const byte BatteryDeviceGamepad = 0;
        private const byte BatteryDeviceHeadset = 1;

        /// <summary>XInput 粗档位换算：Empty/Low/Medium/Full -&gt; 0/25/50/100。</summary>
        internal static bool TryCoarseLevel(byte level, out int percent)
        {
            percent = 0;
            if (level == 0) { percent = 0; return true; }
            if (level == 1) { percent = 25; return true; }
            if (level == 2) { percent = 50; return true; }
            if (level == 3) { percent = 100; return true; }
            // 0xFF = 未知，不能当成 0% 显示。
            return false;
        }

        /// <summary>
        /// DS4 USB 01 字节 30 / BT 11 字节 32：低 4 位是档位，bit4 是线缆。
        /// DualSense USB 01 字节 53 / BT 31 字节 54：低 4 位是 0..10，高 4 位是状态。
        /// </summary>
        internal enum PlayStationReport { Other, Battery, NoController }

        internal static PlayStationReport DecodePlayStation(ushort pid, bool bluetooth, byte[] report,
            out int percent, out bool charging)
        {
            percent = 0;
            charging = false;
            if (report == null || report.Length == 0) return PlayStationReport.Other;
            bool ds4 = pid == 0x05C4 || pid == 0x09CC || pid == 0x0BA0;
            bool dualSense = pid == 0x0CE6 || pid == 0x0DF2;
            if (!ds4 && !dualSense) return PlayStationReport.Other;

            int at;
            if (report[0] == 0x01 && !bluetooth) at = ds4 ? 30 : 53;
            else if (report[0] == (ds4 ? (byte)0x11 : (byte)0x31)) at = ds4 ? 32 : 54;
            else return PlayStationReport.Other;
            if (report.Length <= at) return PlayStationReport.Other;

            byte b = report[at];
            int level = b & 0x0F;
            if (ds4)
            {
                if (pid == 0x0BA0 && b == 0) return PlayStationReport.NoController;
                bool cable = (b & 0x10) != 0;
                percent = cable
                    ? (level >= 11 ? 100 : Math.Min(100, level * 10))
                    : Math.Min(100, level * 10 + 5);
                charging = cable && level < 11;
                return PlayStationReport.Battery;
            }
            int state = b >> 4;
            if (state == 0x2) { percent = 100; charging = true; return PlayStationReport.Battery; }
            percent = Math.Min(100, level * 10 + 5);
            charging = state == 0x1;
            return PlayStationReport.Battery;
        }

        internal static bool IsPlayStationPid(ushort pid)
        {
            return pid == 0x05C4 || pid == 0x09CC || pid == 0x0BA0 || pid == 0x0CE6 || pid == 0x0DF2;
        }

        /// <summary>Xbox 手柄：Windows.Gaming.Input 的真实电量，失败时回落 XInput 粗档位。</summary>
        internal List<BatteryReading> PollXbox()
        {
            List<BatteryReading> result = new List<BatteryReading>();
            // Windows.Gaming.Input 会把整套输入栈加载进进程：只在确实有 Xbox 类手柄时才碰它。
            bool xinputSlotUsed = false;
            XInputState probe;
            for (uint u = 0; u < 4; u++)
            {
                if (XInputGetState(u, out probe) == 0) { xinputSlotUsed = true; break; }
            }
            if (xinputSlotUsed)
            {
                try
                {
                    IReadOnlyList<RawGameController> pads = RawGameController.RawGameControllers;
                    Thread.Sleep(300); // 列表是异步填充的，第一次要等一下
                    for (int i = 0; i < pads.Count; i++)
                    {
                        RawGameController pad = pads[i];
                        ushort vid = (ushort)pad.HardwareVendorId;
                        if (vid != 0x045E && vid != 0x3537) continue;
                        Windows.Devices.Power.BatteryReport report = pad.TryGetBatteryReport();
                        if (report == null) continue;
                        int? remaining = report.RemainingCapacityInMilliwattHours;
                        int? full = report.FullChargeCapacityInMilliwattHours;
                        if (!remaining.HasValue || !full.HasValue || full.Value <= 0) continue;
                        int percent = Math.Max(0, Math.Min(100, remaining.Value * 100 / full.Value));
                        bool charging = report.Status == Windows.System.Power.BatteryStatus.Charging;
                        BatteryReading reading = BatteryReading.Unknown(null, BatterySourceKind.Vendor);
                        reading.DeviceFound = true;
                        reading.Percent = percent;
                        reading.Charging = charging;
                        reading.Confidence = ReadingConfidence.High;
                        reading.Timestamp = DateTime.Now;
                        reading.Raw = "WGI " + vid.ToString("X4") + ":" + pad.HardwareProductId.ToString("X4") +
                            " " + pad.DisplayName;
                        result.Add(reading);
                    }
                }
                catch (Exception)
                {
                    // WinRT 不可用（老系统）时静默回落到 XInput 粗档位。
                }
            }

            // XInput 粗档位：只填 WGI 没给出精确读数的槽位。
            for (uint u = 0; u < 4; u++)
            {
                if (XInputGetState(u, out probe) != 0) continue;
                XInputBatteryInformation info;
                if (XInputGetBatteryInformation(u, BatteryDeviceGamepad, out info) != 0) continue;
                if (info.BatteryType == 0 || info.BatteryType == 2) continue; // 断开 / 有线
                int percent;
                if (!TryCoarseLevel(info.BatteryLevel, out percent)) continue;
                BatteryReading reading = BatteryReading.Unknown(null, BatterySourceKind.Vendor);
                reading.DeviceFound = true;
                reading.Percent = percent;
                reading.Confidence = ReadingConfidence.Low;
                reading.Timestamp = DateTime.Now;
                reading.Raw = "XInput slot " + u + " coarse";
                result.Add(reading);
            }
            return result;
        }
    }
}
