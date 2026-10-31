using System;
using System.Collections.Generic;

namespace DeviceBattery
{
    internal enum DeviceCategory
    {
        Unknown = 0,
        Mouse = 1,
        Headset = 2,
        Keyboard = 3
    }

    internal enum TransportKind
    {
        Unknown = 0,
        Atk24G = 1,
        Usb24G = 2,
        BluetoothLe = 3,
        BluetoothClassic = 4,
        Usb = 5
    }

    internal enum DeviceStatus
    {
        Unknown = 0,
        Online = 1,
        Charging = 2,
        Sleeping = 3,
        Stale = 4,
        Offline = 5,
        Unsupported = 6,
        BluetoothOff = 7,
        Error = 8
    }

    internal enum BatterySourceKind
    {
        Unknown = 0,
        AtkHid = 1,
        GenericHid = 2,
        BleGatt = 3,
        Hfp = 4,
        Vendor = 5
    }

    internal enum ReadingConfidence
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3
    }

    internal sealed class BatteryReading
    {
        public bool DeviceFound;
        public int? Percent;
        public bool? Charging;
        public int? Millivolts;
        public int? LeftPercent;
        public int? RightPercent;
        public int? CasePercent;
        public DateTime Timestamp = DateTime.MinValue;
        public BatterySourceKind Source = BatterySourceKind.Unknown;
        public ReadingConfidence Confidence = ReadingConfidence.None;
        public string Raw;
        public string Error;

        public bool HasPercent
        {
            get { return Percent.HasValue; }
        }

        public static BatteryReading Unknown(string error, BatterySourceKind source)
        {
            BatteryReading reading = new BatteryReading();
            reading.Source = source;
            reading.Error = error;
            reading.Confidence = ReadingConfidence.None;
            return reading;
        }

        public BatteryReading Clone()
        {
            BatteryReading copy = new BatteryReading();
            copy.DeviceFound = DeviceFound;
            copy.Percent = Percent;
            copy.Charging = Charging;
            copy.Millivolts = Millivolts;
            copy.LeftPercent = LeftPercent;
            copy.RightPercent = RightPercent;
            copy.CasePercent = CasePercent;
            copy.Timestamp = Timestamp;
            copy.Source = Source;
            copy.Confidence = Confidence;
            copy.Raw = Raw;
            copy.Error = Error;
            return copy;
        }
    }

    internal sealed class DeviceState
    {
        public string Id;
        public DeviceCategory Category = DeviceCategory.Unknown;
        public string DisplayName;
        public string Model;
        public TransportKind Transport = TransportKind.Unknown;
        public ushort VendorId;
        public ushort ProductId;
        public string HardwareId;
        public ulong BluetoothAddress;
        public string BluetoothDeviceId;
        public string AddressHash;
        public bool Connected;

        public int Order;
        public BatteryReading LastReading;
        public DateTime LastSeen = DateTime.MinValue;
        public DateTime LastAttempt = DateTime.MinValue;
        public DateTime NextDue = DateTime.MinValue;
        public DeviceStatus Status = DeviceStatus.Unknown;
        public string StatusDetail;
        public string LastError;
        public string UnsupportedReason;
        public string SourceDescription;
        public bool BatterySupported = true;
        public int QueryCount;
        public int SuccessCount;
        public int FailCount;

        public HashSet<int> WarnedLevels = new HashSet<int>();
        public int LastWarnPercent = -1;
        public DateTime LastWarnTime = DateTime.MinValue;
        public int CurrentIntervalSeconds = 600;

        /// <summary>
        /// 待确认的跳变值（-1 表示没有挂起）。设备侧电量计会在两个稳定状态之间
        /// 切换（实测 ATK 鼠标 40% / 55%），大跳变要连续两次读到同一个新值才生效。
        /// </summary>
        public int PendingPercent = -1;

        /// <summary>这个待确认值已经读到过几次。</summary>
        public int ConfirmCount;

        /// <summary>
        /// 用户在设置里对这台设备选了「解除隐藏」：即使读不到电量也照旧显示卡片，
        /// 电量位置写明原因（不支持读取 / 未检测到电量接口等），不给猜测值。
        /// </summary>
        public bool ForceVisible;

        public DeviceState()
        {
            LastReading = new BatteryReading();
        }

        public bool HasValue
        {
            get { return LastReading != null && LastReading.Percent.HasValue; }
        }

        public int PercentOrZero
        {
            get { return LastReading != null && LastReading.Percent.HasValue ? LastReading.Percent.Value : 0; }
        }

        public string CategoryText
        {
            get
            {
                if (Category == DeviceCategory.Mouse) return "鼠标";
                if (Category == DeviceCategory.Keyboard) return "键盘";
                if (Category == DeviceCategory.Headset) return "耳机";
                return "设备";
            }
        }

        public string TransportText
        {
            get
            {
                switch (Transport)
                {
                    case TransportKind.Atk24G: return "2.4G";
                    case TransportKind.Usb24G: return "2.4G";
                    case TransportKind.BluetoothLe: return "蓝牙 LE";
                    case TransportKind.BluetoothClassic: return "经典蓝牙";
                    case TransportKind.Usb: return "USB";
                    default: return "未知连接";
                }
            }
        }

        public string SourceText
        {
            get
            {
                if (LastReading == null) return "未知来源";
                switch (LastReading.Source)
                {
                    case BatterySourceKind.AtkHid: return "厂商 HID";
                    case BatterySourceKind.GenericHid: return "通用 HID";
                    case BatterySourceKind.BleGatt: return "蓝牙 GATT";
                    case BatterySourceKind.Hfp: return "HFP";
                    case BatterySourceKind.Vendor: return "厂商协议";
                    default: return "未知来源";
                }
            }
        }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case DeviceStatus.Online: return "在线";
                    case DeviceStatus.Charging: return "充电中";
                    case DeviceStatus.Sleeping: return "等待响应";
                    case DeviceStatus.Stale: return "数据过期";
                    case DeviceStatus.Offline: return "未连接";
                    case DeviceStatus.Unsupported: return "不支持读取";
                    case DeviceStatus.BluetoothOff: return "蓝牙已关闭";
                    case DeviceStatus.Error: return "读取异常";
                    default: return "未知";
                }
            }
        }

        public DeviceState Clone()
        {
            DeviceState copy = new DeviceState();
            copy.Id = Id;
            copy.Category = Category;
            copy.DisplayName = DisplayName;
            copy.Model = Model;
            copy.Transport = Transport;
            copy.VendorId = VendorId;
            copy.ProductId = ProductId;
            copy.HardwareId = HardwareId;
            copy.BluetoothAddress = BluetoothAddress;
            copy.BluetoothDeviceId = BluetoothDeviceId;
            copy.AddressHash = AddressHash;
            copy.Connected = Connected;

            copy.Order = Order;
            copy.LastReading = LastReading == null ? new BatteryReading() : LastReading.Clone();
            copy.LastSeen = LastSeen;
            copy.LastAttempt = LastAttempt;
            copy.NextDue = NextDue;
            copy.Status = Status;
            copy.StatusDetail = StatusDetail;
            copy.LastError = LastError;
            copy.UnsupportedReason = UnsupportedReason;
            copy.SourceDescription = SourceDescription;
            copy.BatterySupported = BatterySupported;
            copy.QueryCount = QueryCount;
            copy.SuccessCount = SuccessCount;
            copy.FailCount = FailCount;

            copy.WarnedLevels = new HashSet<int>(WarnedLevels);
            copy.LastWarnPercent = LastWarnPercent;
            copy.LastWarnTime = LastWarnTime;
            copy.CurrentIntervalSeconds = CurrentIntervalSeconds;
            copy.PendingPercent = PendingPercent;
            copy.ConfirmCount = ConfirmCount;
            copy.ForceVisible = ForceVisible;
            return copy;
        }
    }

    internal sealed class BatterySnapshot
    {
        public DateTime CapturedAt = DateTime.Now;
        public List<DeviceState> Devices = new List<DeviceState>();
        public int HiddenDeviceCount;
        public bool BluetoothRadioOn;
        public bool BluetoothRadioKnown;
        public string BluetoothError;

        public DeviceState Find(string id)
        {
            for (int i = 0; i < Devices.Count; i++)
            {
                if (string.Equals(Devices[i].Id, id, StringComparison.Ordinal))
                {
                    return Devices[i];
                }
            }
            return null;
        }
    }

    internal sealed class LowBatteryEventArgs : EventArgs
    {
        public DeviceState Device;
        public int Percent;
        public int Level;
    }
}
