using System;
using System.Collections.Generic;

namespace DeviceBattery
{
    /// <summary>托盘聚合图标的设备选择逻辑，独立出来便于测试。</summary>
    internal static class TrayDeviceSelector
    {
        public static DeviceState Choose(BatterySnapshot snapshot, string selectedId, string fixedMode)
        {
            List<DeviceState> connected = DeviceVisibility.ConnectedDevices(snapshot);
            if (!string.IsNullOrEmpty(selectedId))
            {
                for (int i = 0; i < connected.Count; i++)
                {
                    if (string.Equals(connected[i].Id, selectedId, StringComparison.Ordinal))
                    {
                        return connected[i];
                    }
                }
            }

            DeviceState best = null;
            DeviceState fixedDevice = null;
            for (int i = 0; i < connected.Count; i++)
            {
                DeviceState device = connected[i];
                if (fixedMode == "mouse" && device.Category == DeviceCategory.Mouse)
                {
                    fixedDevice = device;
                }
                else if (fixedMode == "keyboard" && device.Category == DeviceCategory.Keyboard)
                {
                    fixedDevice = device;
                }
                else if (fixedMode == "headset" && device.Category == DeviceCategory.Headset)
                {
                    fixedDevice = device;
                }

                if (device.HasValue &&
                    (best == null || device.PercentOrZero < best.PercentOrZero))
                {
                    best = device;
                }
            }

            if (fixedDevice != null) return fixedDevice;
            if (best != null) return best;
            return connected.Count > 0 ? connected[0] : null;
        }
    }
}
