using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DeviceBattery
{
    internal static class Native
    {
        public const int DIGCF_PRESENT = 0x00000002;
        public const int DIGCF_DEVICEINTERFACE = 0x00000010;

        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;

        public const uint SPDRP_DEVICEDESC = 0x00000000;
        public const uint SPDRP_FRIENDLYNAME = 0x0000000C;
        public const uint SPDRP_MFG = 0x0000000B;

        public static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        // hidpi.h 的 _HIDP_VALUE_CAPS，字段顺序与 MSDN 一致（72 字节）：
        // USAGE UsagePage(0); UCHAR ReportID(2); BOOLEAN IsAlias(3); USHORT BitField(4);
        // USHORT LinkCollection(6); USAGE LinkUsage(8); USAGE LinkUsagePage(10);
        // BOOLEAN IsRange(12) / IsStringRange(13) / IsDesignatorRange(14) / IsAbsolute(15) / HasNull(16);
        // UCHAR Reserved(17); USHORT BitSize(18); USHORT ReportCount(20); USHORT Reserved2[5](22..31);
        // ULONG UnitsExp(32) / Units(36); LONG LogicalMin(40) / LogicalMax(44) / PhysicalMin(48) / PhysicalMax(52);
        // union(56)：Range{UsageMin,UsageMax,StringMin,StringMax,DesignatorMin,DesignatorMax,DataIndexMin,DataIndexMax}
        //            或 NotRange{Usage,Reserved1,StringIndex,...}，两者都是 8 个 USHORT。
        // 之前这里写的是 HIDP_BUTTON_CAPS 的形状（byte Padding + uint[10] Reserved，88 字节），
        // UsageMin 落在 80 而不是 56，读出来的永远是垃圾值，电量页 0x06 永远匹配不上。
        // 修正后结构体全是值类型，无 ByValArray，是 blittable 的，Marshal.SizeOf 与原生一致。
        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            public byte IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            public byte IsRange;
            public byte IsStringRange;
            public byte IsDesignatorRange;
            public byte IsAbsolute;
            public byte HasNull;
            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;
            public ushort Reserved2a;
            public ushort Reserved2b;
            public ushort Reserved2c;
            public ushort Reserved2d;
            public ushort Reserved2e;
            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            public ushort UsageMin;
            public ushort UsageMax;
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;
            public ushort DataIndexMax;
        }

        /// <summary>HIDP_VALUE_CAPS 的原生大小，用于按步长解析 HidP_GetValueCaps 填的数组。</summary>
        public static readonly int HidpValueCapsSize = Marshal.SizeOf(typeof(HIDP_VALUE_CAPS));

        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, int MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize, ref int RequiredSize, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, StringBuilder DeviceInstanceId, int DeviceInstanceIdSize, out int RequiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, out uint PropertyRegDataType, byte[] PropertyBuffer, int PropertyBufferSize, out int RequiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(IntPtr HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool HidD_GetProductString(IntPtr HidDeviceObject, StringBuilder Buffer, int BufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool HidD_GetManufacturerString(IntPtr HidDeviceObject, StringBuilder Buffer, int BufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool HidD_GetSerialNumberString(IntPtr HidDeviceObject, StringBuilder Buffer, int BufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(IntPtr HidDeviceObject, out IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(IntPtr HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);

        // 厂商协议（雷蛇、赛睿、WLmouse 这一类）都是“写一条 feature 报文、再读回一条”
        // 的问答式交换：hidapi 的 send_feature_report 对应的就是 HidD_SetFeature。
        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(IntPtr HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);

        // 用 IntPtr（原生缓冲区）而不是 HIDP_VALUE_CAPS[]：数组长度由 HidP_GetValueCaps 回填，
        // 显式按 HidpValueCapsSize 步长解析，步长只有一处来源，也便于单测直接喂字节。
        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetValueCaps(int ReportType,
            IntPtr ValueCaps, ref ushort ValueCapsLength, IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetUsageValue(int ReportType, ushort UsagePage,
            ushort LinkCollection, ushort Usage, out uint UsageValue, IntPtr PreparsedData,
            [In] byte[] Report, uint ReportLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string FileName, uint DesiredAccess, uint ShareMode, IntPtr SecurityAttributes, uint CreationDisposition, uint FlagsAndAttributes, IntPtr TemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

        /// <summary>取消指定线程上挂起的同步 I/O（CancelIoEx 对同步句柄通常无效）。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelSynchronousIo(IntPtr hThread);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        /// <summary>THREAD_TERMINATE = 0x0001，CancelSynchronousIo 需要该权限。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int bufferSize, out int returnLength);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

        public static bool IsLowIntegrity()
        {
            try
            {
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out token))
                {
                    return false;
                }
                try
                {
                    int size;
                    GetTokenInformation(token, 25, IntPtr.Zero, 0, out size);
                    if (size <= 0)
                    {
                        return false;
                    }
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (!GetTokenInformation(token, 25, buffer, size, out size))
                        {
                            return false;
                        }
                        IntPtr sid = Marshal.ReadIntPtr(buffer);
                        if (sid == IntPtr.Zero)
                        {
                            return false;
                        }
                        int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                        if (count < 1)
                        {
                            // SID 里一个子权威都没有时，count - 1 会变成 -1，GetSidSubAuthority 直接越界。
                            return false;
                        }
                        int rid = Marshal.ReadInt32(GetSidSubAuthority(sid, count - 1));
                        return rid <= 0x1000;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        public static bool TryGetTrayRect(out RECT rect)
        {
            rect = new RECT();
            try
            {
                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero)
                {
                    return false;
                }
                IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (notify == IntPtr.Zero)
                {
                    return false;
                }
                return GetWindowRect(notify, out rect);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void EnableDpiAwareness()
        {
            // SetProcessDpiAwarenessContext 是 1703+ 的 API，且返回 false 表示没设成功
            // （进程感知级别可能已被外部锁定）。只看异常不看返回值会让界面按系统缩放被拉伸，
            // 全站 UiScale.Px/Scale 的计算随之整体失真，所以失败必须回退到旧 API。
            bool ok;
            try { ok = SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (Exception) { ok = false; }
            if (ok)
            {
                return;
            }
            try { SetProcessDPIAware(); }
            catch (Exception) { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        public static bool IsDisplayOn()
        {
            try
            {
                SYSTEM_POWER_STATUS status;
                if (!GetSystemPowerStatus(out status))
                {
                    return true;
                }
                return (status.SystemStatusFlag & 0x02) == 0;
            }
            catch (Exception)
            {
                return true;
            }
        }

        public static int GetIdleSeconds()
        {
            try
            {
                LASTINPUTINFO info = new LASTINPUTINFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (!GetLastInputInfo(ref info))
                {
                    return 0;
                }
                uint now = unchecked((uint)Environment.TickCount);
                uint elapsed = unchecked(now - info.dwTime);
                if (elapsed > 0x7FFFFFFF)
                {
                    return 0;
                }
                return (int)(elapsed / 1000);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static bool IsFullscreenAppActive()
        {
            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                {
                    return false;
                }

                StringBuilder className = new StringBuilder(128);
                GetClassName(foreground, className, className.Capacity);
                string cls = className.ToString();
                if (string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                RECT window;
                if (!GetWindowRect(foreground, out window))
                {
                    return false;
                }

                IntPtr monitor = MonitorFromWindow(foreground, 2);
                MONITORINFO info = new MONITORINFO();
                info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfo(monitor, ref info))
                {
                    return false;
                }

                RECT screen = info.rcMonitor;
                return window.Left <= screen.Left && window.Top <= screen.Top &&
                       window.Right >= screen.Right && window.Bottom >= screen.Bottom;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
