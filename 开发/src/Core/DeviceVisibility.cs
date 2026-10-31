using System.Collections.Generic;

namespace DeviceBattery
{
    /// <summary>
    /// 托盘和面板只展示“当前连接且有当前状态”的设备；断连、无响应、休眠、过期设备
    /// 不再显示，只在设置 → 设备的已添加记录里保留。
    /// </summary>
    internal static class DeviceVisibility
    {
        public static bool IsConnected(DeviceState device)
        {
            if (device == null || !device.Connected)
            {
                return false;
            }
            return device.Status != DeviceStatus.Offline &&
                   device.Status != DeviceStatus.BluetoothOff &&
                   device.Status != DeviceStatus.Sleeping &&
                   device.Status != DeviceStatus.Stale;
        }

        /// <summary>
        /// 托盘/面板的显示条件：先要求当前连接，再看能不能真的读出电量。
        /// 读不到电量的设备一律不占卡片——能力缺失（有线键盘、2.4G 接收器、
        /// 经典蓝牙 HFP）和暂时只读到充电盒的情况都算。设备记录仍保留在
        /// 设置 → 设备里，方便重命名和单独设置低电阈值。
        /// <para>
        /// 如果用户在设置里对某台设备选了「解除隐藏」（ForceVisible），
        /// 它会被强制显示，但卡片上照旧写明读不到的原因，不给假数字。
        /// </para>
        /// </summary>
        public static bool IsDisplayable(DeviceState device)
        {
            if (device == null)
            {
                return false;
            }
            // 用户显式要求显示这台设备：即使没连接、读不到电量也保留卡片。
            // 这条必须排在「是否连接」之前，否则断开/休眠的设备永远无法被强制显示，
            // 面板上就会出现「点了显示还是少一台」的情况。
            if (device.ForceVisible)
            {
                return true;
            }
            if (!IsConnected(device))
            {
                return false;
            }
            if (!device.BatterySupported)
            {
                return false;
            }
            if (device.Status == DeviceStatus.Unsupported)
            {
                return false;
            }
            return true;
        }

        public static List<DeviceState> ConnectedDevices(BatterySnapshot snapshot)
        {
            List<DeviceState> result = new List<DeviceState>();
            if (snapshot == null)
            {
                return result;
            }
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                if (IsDisplayable(snapshot.Devices[i]))
                {
                    result.Add(snapshot.Devices[i]);
                }
            }
            return result;
        }

        public static BatterySnapshot ConnectedSnapshot(BatterySnapshot source)
        {
            BatterySnapshot result = new BatterySnapshot();
            if (source == null)
            {
                return result;
            }
            result.CapturedAt = source.CapturedAt;
            result.BluetoothRadioOn = source.BluetoothRadioOn;
            result.BluetoothRadioKnown = source.BluetoothRadioKnown;
            result.BluetoothError = source.BluetoothError;
            for (int i = 0; i < source.Devices.Count; i++)
            {
                DeviceState device = source.Devices[i];
                if (IsDisplayable(device))
                {
                    result.Devices.Add(device);
                }
                else
                {
                    result.HiddenDeviceCount++;
                }
            }
            return result;
        }
    }
}
