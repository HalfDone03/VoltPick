using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeviceBattery
{
    internal sealed class HidDeviceInfo
    {
        public string Path;
        public string InstanceId;
        public string ProductName;
        public string Manufacturer;
        public string SerialNumber;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
        public ushort UsagePage;
        public ushort Usage;
        public ushort InputReportLength;
        public ushort OutputReportLength;
        public ushort FeatureReportLength;

        public string ShortId
        {
            get
            {
                string source = !string.IsNullOrEmpty(InstanceId) ? InstanceId : Path;
                return Log.HashAddress(HashString(source));
            }
        }

        private static ulong HashString(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            ulong hash = 1469598103934665603UL;
            for (int i = 0; i < text.Length; i++)
            {
                hash ^= text[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }

    internal sealed class HidBatteryDevice
    {
        public HidDeviceInfo Info;
        public int ReportType; // 0 = Input, 2 = Feature
        public byte ReportId;
        public ushort LinkCollection;
        public ushort BitField;
        // 命中 0x06 页的哪个字段：0x0020 StateOfCharge 或 0x0024 RelativeStateOfCharge。
        public ushort Usage;
        public DeviceCategory Category = DeviceCategory.Mouse;
    }

    internal static class HidEnumerator
    {
        private static readonly Guid HidInterfaceGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

        public static List<HidDeviceInfo> FindAll()
        {
            List<HidDeviceInfo> result = new List<HidDeviceInfo>();
            Guid interfaceGuid = HidInterfaceGuid;
            IntPtr deviceInfoSet = Native.SetupDiGetClassDevs(
                ref interfaceGuid,
                IntPtr.Zero,
                IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == new IntPtr(-1) || deviceInfoSet == IntPtr.Zero)
            {
                return result;
            }

            try
            {
                for (int index = 0; ; index++)
                {
                    Native.SP_DEVICE_INTERFACE_DATA interfaceData = new Native.SP_DEVICE_INTERFACE_DATA();
                    interfaceData.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                    if (!Native.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref interfaceGuid, index, ref interfaceData))
                    {
                        break;
                    }

                    IntPtr detail = Marshal.AllocHGlobal(4096);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        Native.SP_DEVINFO_DATA infoData = new Native.SP_DEVINFO_DATA();
                        infoData.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                        int required = 0;
                        bool ok = Native.SetupDiGetDeviceInterfaceDetail(
                            deviceInfoSet, ref interfaceData, detail, 4096, ref required, ref infoData);
                        if (!ok)
                        {
                            continue;
                        }

                        string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path))
                        {
                            continue;
                        }

                        HidDeviceInfo info = Probe(path, deviceInfoSet, ref infoData);
                        if (info != null)
                        {
                            result.Add(info);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return result;
        }

        public static HidDeviceInfo FindAtkMouse()
        {
            return FindAtkMouse(FindAll());
        }

        /// <summary>用调用方已经枚举好的 HID 列表查找，避免每轮发现重复枚举全机 HID 接口。</summary>
        public static HidDeviceInfo FindAtkMouse(List<HidDeviceInfo> devices)
        {
            if (devices == null)
            {
                return null;
            }
            for (int i = 0; i < devices.Count; i++)
            {
                HidDeviceInfo info = devices[i];
                if (info.VendorId == 0x373B &&
                    info.UsagePage == 0xFF02 &&
                    info.Usage == 0x0002 &&
                    info.InputReportLength == 17 &&
                    info.OutputReportLength == 17)
                {
                    return info;
                }
            }
            return null;
        }

        public static List<HidBatteryDevice> FindBatteryDevices()
        {
            return FindBatteryDevices(FindAll());
        }

        /// <summary>用调用方已经枚举好的 HID 列表查找，避免重复枚举全机 HID 接口。</summary>
        public static List<HidBatteryDevice> FindBatteryDevices(List<HidDeviceInfo> all)
        {
            List<HidBatteryDevice> result = new List<HidBatteryDevice>();
            if (all == null)
            {
                return result;
            }
            HashSet<string> seen = new HashSet<string>();
            HashSet<string> mice = new HashSet<string>();
            HashSet<string> keyboards = new HashSet<string>();
            for (int i = 0; i < all.Count; i++)
            {
                HidDeviceInfo info = all[i];
                if (info.UsagePage == 0x0001 && info.Usage == 0x0002)
                {
                    mice.Add(VidPid(info));
                }
                if (info.UsagePage == 0x0001 && info.Usage == 0x0006)
                {
                    keyboards.Add(VidPid(info));
                }
            }

            for (int i = 0; i < all.Count; i++)
            {
                HidDeviceInfo info = all[i];
                // ATK 由专用适配器读取，避免同一接收器出现两张卡片。
                // 罗技不能在这里整体跳过：M590/M720 这类蓝牙罗技只暴露标准
                // 0x06 电池页、没有 HID++ 接口，跳过就再也读不到了。
                if (info.VendorId == 0x373B)
                {
                    continue;
                }
                string id = VidPid(info);
                bool keyboard = keyboards.Contains(id) || LooksLikeKeyboardName(info.ProductName);
                bool mouse = mice.Contains(id) || LooksLikeMouseName(info.ProductName);

                // 只要能真的读出 0x06 页电量就收：不再要求产品名像键鼠，
                // 否则大量国产接收器（名字只有 "2.4G Receiver"）会被漏掉。
                HidBatteryDevice battery = ProbeBattery(info);
                if (battery == null)
                {
                    continue;
                }
                if (keyboard)
                {
                    battery.Category = DeviceCategory.Keyboard;
                }
                else if (mouse)
                {
                    battery.Category = DeviceCategory.Mouse;
                }
                else if (LooksLikeHeadsetName(info.ProductName))
                {
                    battery.Category = DeviceCategory.Headset;
                }
                else
                {
                    battery.Category = DeviceCategory.Mouse;
                }
                string key = string.IsNullOrEmpty(info.InstanceId) ? info.Path : info.InstanceId;
                if (seen.Add(key))
                {
                    result.Add(battery);
                }
            }
            return result;
        }

        /// <summary>
        /// 列出当前连接的键盘（用于“已连接但未暴露电量”的提示），
        /// 不依赖品牌；同一物理键盘的多个 HID 接口会合并为一条。
        /// </summary>
        public static List<HidDeviceInfo> FindKeyboards()
        {
            return FindKeyboards(FindAll());
        }

        /// <summary>用调用方已经枚举好的 HID 列表查找，避免重复枚举全机 HID 接口。</summary>
        public static List<HidDeviceInfo> FindKeyboards(List<HidDeviceInfo> all)
        {
            List<HidDeviceInfo> result = new List<HidDeviceInfo>();
            if (all == null)
            {
                return result;
            }
            HashSet<string> seen = new HashSet<string>();
            for (int i = 0; i < all.Count; i++)
            {
                HidDeviceInfo info = all[i];
                if (info.VendorId == 0x373B || info.VendorId == 0x046D)
                {
                    continue;
                }
                bool keyboard = (info.UsagePage == 0x0001 && info.Usage == 0x0006) ||
                                LooksLikeKeyboardName(info.ProductName);
                if (!keyboard)
                {
                    continue;
                }
                string serial = info.SerialNumber ?? "";
                string key = VidPid(info) + ":" + (serial.Length > 0 ? serial : (info.ProductName ?? ""));
                if (seen.Add(key))
                {
                    result.Add(info);
                }
            }
            return result;
        }

        private static string VidPid(HidDeviceInfo info)
        {
            return info.VendorId.ToString("X4") + ":" + info.ProductId.ToString("X4");
        }

        private static bool LooksLikeMouseName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string lower = name.ToLowerInvariant();
            string[] words = new string[]
            {
                "mouse", "鼠标", "g304", "g502", "g pro", "viper", "deathadder",
                "basilisk", "orochi", "mx master", "m720", "m590", "trackball", "trackpad"
            };
            for (int i = 0; i < words.Length; i++)
            {
                if (lower.IndexOf(words[i], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool LooksLikeHeadsetName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string lower = name.ToLowerInvariant();
            string[] words = new string[]
            {
                "headset", "headphone", "earbud", "earphone", "耳机", "收音",
                "arctis", "cloud", "void", "audio", "sound"
            };
            for (int i = 0; i < words.Length; i++)
            {
                if (lower.IndexOf(words[i], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool LooksLikeKeyboardName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string lower = name.ToLowerInvariant();
            string[] words = new string[]
            {
                "keyboard", "键盘", "kbd", "keychron", "wooting", "nuphy", "k380",
                "k580", "mx keys", "60he", "68he", "75he", "win60", "win68"
            };
            for (int i = 0; i < words.Length; i++)
            {
                if (lower.IndexOf(words[i], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static HidBatteryDevice ProbeBattery(HidDeviceInfo info)
        {
            IntPtr handle = Native.CreateFile(
                info.Path,
                0,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Native.OPEN_EXISTING,
                0,
                IntPtr.Zero);
            if (handle == Native.InvalidHandle)
            {
                return null;
            }

            try
            {
                IntPtr preparsed;
                if (!Native.HidD_GetPreparsedData(handle, out preparsed))
                {
                    return null;
                }
                try
                {
                    Native.HIDP_CAPS caps = new Native.HIDP_CAPS();
                    caps.Reserved = new ushort[17];
                    if (Native.HidP_GetCaps(preparsed, ref caps) != Native.HIDP_STATUS_SUCCESS)
                    {
                        return null;
                    }

                    HidBatteryDevice feature = FindBatteryCap(info, preparsed, 2, caps.NumberFeatureValueCaps);
                    if (feature != null) return feature;
                    return FindBatteryCap(info, preparsed, 0, caps.NumberInputValueCaps);
                }
                finally
                {
                    Native.HidD_FreePreparsedData(preparsed);
                }
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }

        private static HidBatteryDevice FindBatteryCap(HidDeviceInfo info, IntPtr preparsed,
            int reportType, ushort count)
        {
            if (count == 0)
            {
                return null;
            }
            int size = Native.HidpValueCapsSize;
            IntPtr buffer = Marshal.AllocHGlobal(size * count);
            try
            {
                // 驱动应当整块覆盖，先清零只是防止少填时把上一次的残留当成能力项。
                for (int i = 0; i < size * count; i++)
                {
                    Marshal.WriteByte(buffer, i, 0);
                }
                ushort length = count;
                int status = Native.HidP_GetValueCaps(reportType, buffer, ref length, preparsed);
                if (status != Native.HIDP_STATUS_SUCCESS)
                {
                    return null;
                }
                for (int i = 0; i < length; i++)
                {
                    Native.HIDP_VALUE_CAPS cap = ReadValueCaps(buffer, i);
                    ushort usage;
                    if (!TryResolveBatteryUsage(cap, out usage))
                    {
                        continue;
                    }
                    HidBatteryDevice device = new HidBatteryDevice();
                    device.Info = info;
                    device.ReportType = reportType;
                    device.ReportId = cap.ReportID;
                    device.LinkCollection = cap.LinkCollection;
                    device.BitField = cap.BitField;
                    device.Usage = usage;
                    return device;
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// 按 HIDP_VALUE_CAPS 的原生大小（72 字节）从缓冲区里取出第 index 项。
        /// 单点决定布局：结构体字段错了，这里取到的 UsageMin/UsageMax 就是垃圾值。
        /// </summary>
        internal static Native.HIDP_VALUE_CAPS ReadValueCaps(IntPtr buffer, int index)
        {
            IntPtr item = new IntPtr(buffer.ToInt64() + (long)Native.HidpValueCapsSize * index);
            return (Native.HIDP_VALUE_CAPS)Marshal.PtrToStructure(item, typeof(Native.HIDP_VALUE_CAPS));
        }

        /// <summary>测试用：从托管字节缓冲区解析 HIDP_VALUE_CAPS，路径与 ReadValueCaps 一致。</summary>
        internal static Native.HIDP_VALUE_CAPS ParseValueCaps(byte[] raw, int offset)
        {
            GCHandle handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
            try
            {
                return ReadValueCaps(new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset), 0);
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// 0x06 电池页里 0x20 StateOfCharge 最理想，但不少只实现 0x24
        /// RelativeStateOfCharge，两个都要认。
        /// </summary>
        internal static bool TryResolveBatteryUsage(Native.HIDP_VALUE_CAPS cap, out ushort usage)
        {
            usage = 0;
            if (cap.UsagePage != 0x0006)
            {
                return false;
            }
            ushort min = cap.UsageMin;
            ushort max = cap.IsRange != 0 ? cap.UsageMax : cap.UsageMin;
            if (min <= 0x0020 && max >= 0x0020)
            {
                usage = 0x0020;
                return true;
            }
            if (min <= 0x0024 && max >= 0x0024)
            {
                usage = 0x0024;
                return true;
            }
            return false;
        }

        private static HidDeviceInfo Probe(string path, IntPtr deviceInfoSet, ref Native.SP_DEVINFO_DATA infoData)
        {
            IntPtr handle = Native.CreateFile(
                path,
                0,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Native.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle == Native.InvalidHandle)
            {
                return null;
            }

            try
            {
                Native.HIDD_ATTRIBUTES attributes = new Native.HIDD_ATTRIBUTES();
                attributes.Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES));
                if (!Native.HidD_GetAttributes(handle, ref attributes))
                {
                    return null;
                }

                Native.HIDP_CAPS caps = new Native.HIDP_CAPS();
                caps.Reserved = new ushort[17];
                IntPtr preparsed;
                if (!Native.HidD_GetPreparsedData(handle, out preparsed))
                {
                    return null;
                }
                try
                {
                    Native.HidP_GetCaps(preparsed, ref caps);
                }
                finally
                {
                    Native.HidD_FreePreparsedData(preparsed);
                }

                HidDeviceInfo info = new HidDeviceInfo();
                info.Path = path;
                info.VendorId = attributes.VendorID;
                info.ProductId = attributes.ProductID;
                info.VersionNumber = attributes.VersionNumber;
                info.UsagePage = caps.UsagePage;
                info.Usage = caps.Usage;
                info.InputReportLength = caps.InputReportByteLength;
                info.OutputReportLength = caps.OutputReportByteLength;
                info.FeatureReportLength = caps.FeatureReportByteLength;
                info.ProductName = ReadString(handle, true);
                info.Manufacturer = ReadString(handle, false);
                info.SerialNumber = ReadSerial(handle);

                StringBuilder instance = new StringBuilder(512);
                int required;
                if (Native.SetupDiGetDeviceInstanceId(deviceInfoSet, ref infoData, instance, instance.Capacity, out required))
                {
                    info.InstanceId = instance.ToString();
                }

                if (string.IsNullOrEmpty(info.ProductName))
                {
                    info.ProductName = ReadRegistryString(deviceInfoSet, ref infoData, Native.SPDRP_FRIENDLYNAME);
                }
                if (string.IsNullOrEmpty(info.ProductName))
                {
                    info.ProductName = ReadRegistryString(deviceInfoSet, ref infoData, Native.SPDRP_DEVICEDESC);
                }
                if (string.IsNullOrEmpty(info.ProductName))
                {
                    info.ProductName = "HID 设备";
                }

                return info;
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }

        private static string ReadString(IntPtr handle, bool product)
        {
            try
            {
                StringBuilder builder = new StringBuilder(512);
                bool ok = product
                    ? Native.HidD_GetProductString(handle, builder, builder.Capacity * 2)
                    : Native.HidD_GetManufacturerString(handle, builder, builder.Capacity * 2);
                return ok ? builder.ToString().TrimEnd('\0') : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadSerial(IntPtr handle)
        {
            try
            {
                StringBuilder builder = new StringBuilder(512);
                bool ok = Native.HidD_GetSerialNumberString(handle, builder, builder.Capacity * 2);
                return ok ? builder.ToString().TrimEnd('\0') : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadRegistryString(IntPtr deviceInfoSet, ref Native.SP_DEVINFO_DATA infoData, uint property)
        {
            try
            {
                byte[] buffer = new byte[2048];
                uint type;
                int required;
                if (Native.SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref infoData, property, out type, buffer, buffer.Length, out required))
                {
                    string value = Encoding.Unicode.GetString(buffer, 0, Math.Max(0, required - 2));
                    return value.TrimEnd('\0');
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }

    internal sealed class HidConnection : IDisposable
    {
        private IntPtr _handle = Native.InvalidHandle;
        private string _path;
        // 读线程卡在 ReadFile 里时置位：句柄有挂起 I/O，不能再 CloseHandle。
        private bool _poisoned;
        // 毒化时那次读的完成事件：留着等它 signal，收到尾才能复位毒化标记并关句柄。
        private ManualResetEvent _pendingDone;
        // 输入报告长度（-1 = 还没取），ReadFile 缓冲区不能小于它。
        private int _inputReportLength = -1;

        public string Path
        {
            get { return _path; }
        }

        public bool IsOpen
        {
            get { return _handle != Native.InvalidHandle; }
        }

        public bool Open(string path)
        {
            return OpenCore(path, Native.GENERIC_READ | Native.GENERIC_WRITE);
        }

        public bool OpenRead(string path)
        {
            return OpenCore(path, Native.GENERIC_READ);
        }

        private bool OpenCore(string path, uint access)
        {
            Close();
            IntPtr handle = Native.CreateFile(
                path,
                access,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Native.OPEN_EXISTING,
                0,
                IntPtr.Zero);
            if (handle == Native.InvalidHandle)
            {
                return false;
            }
            _handle = handle;
            _path = path;
            return true;
        }

        public bool Write(byte[] buffer)
        {
            if (_handle == Native.InvalidHandle)
            {
                return false;
            }
            int written;
            return Native.WriteFile(_handle, buffer, buffer.Length, out written, IntPtr.Zero) &&
                   written == buffer.Length;
        }

        internal IntPtr Handle
        {
            get { return _handle; }
        }

        public bool GetFeature(byte[] buffer)
        {
            if (_handle == Native.InvalidHandle || buffer == null || buffer.Length == 0)
            {
                return false;
            }
            return Native.HidD_GetFeature(_handle, buffer, buffer.Length);
        }

        /// <summary>
        /// 输入报告长度（含报告 ID）。ReadFile 的缓冲区小于这个值会直接失败，
        /// 所以读之前必须知道；取不到就返回 0，读的时候仍按调用方缓冲区长度走。
        /// </summary>
        private int InputReportLength()
        {
            if (_inputReportLength >= 0)
            {
                return _inputReportLength;
            }
            int length = 0;
            try
            {
                IntPtr preparsed;
                if (Native.HidD_GetPreparsedData(_handle, out preparsed) && preparsed != IntPtr.Zero)
                {
                    try
                    {
                        Native.HIDP_CAPS caps = new Native.HIDP_CAPS();
                        if (Native.HidP_GetCaps(preparsed, ref caps) == Native.HIDP_STATUS_SUCCESS)
                        {
                            length = caps.InputReportByteLength;
                        }
                    }
                    finally
                    {
                        Native.HidD_FreePreparsedData(preparsed);
                    }
                }
            }
            catch (Exception)
            {
                length = 0;
            }
            _inputReportLength = length;
            return length;
        }

        public bool Read(byte[] buffer, int timeoutMs, out int bytesRead)
        {
            bytesRead = 0;
            if (_handle == Native.InvalidHandle || buffer == null || buffer.Length == 0)
            {
                return false;
            }
            if (_poisoned)
            {
                // 上一次读的挂起 I/O 还没收尾时，句柄上再发一次 ReadFile 会让两次完成通知互相串台。
                ReleasePoisonIfFinished();
                if (_poisoned)
                {
                    return false;
                }
            }

            // ReadFile 的缓冲区小于输入报告长度会直接失败（ERROR_INSUFFICIENT_BUFFER 122），
            // 所以按 max(调用方给的, 报告长度) 真正读，再把有效字节拷回调用方缓冲区。
            byte[] target = buffer;
            int reportLength = InputReportLength();
            if (reportLength > buffer.Length)
            {
                target = new byte[reportLength];
            }

            int read = 0;
            uint nativeThreadId = 0;
            ManualResetEvent done = new ManualResetEvent(false);
            Thread thread = new Thread(delegate()
            {
                try
                {
                    // 先把自己的原生线程 ID 交出去，主线程才能用 CancelSynchronousIo
                    // 取消阻塞在这个线程里的同步 ReadFile。
                    Volatile.Write(ref nativeThreadId, Native.GetCurrentThreadId());
                    int count;
                    if (Native.ReadFile(_handle, target, target.Length, out count, IntPtr.Zero))
                    {
                        read = count;
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    try { done.Set(); }
                    catch (ObjectDisposedException) { }
                }
            });
            thread.IsBackground = true;
            thread.Start();

            bool finished = false;
            try
            {
                finished = done.WaitOne(timeoutMs);
                if (!finished)
                {
                    // 句柄是同步打开的，CancelIoEx 对同步 ReadFile 往往不生效，
                    // 必须再取消该线程上挂起的同步 I/O。
                    Native.CancelIoEx(_handle, IntPtr.Zero);
                    uint threadId = Volatile.Read(ref nativeThreadId);
                    if (threadId != 0)
                    {
                        const uint ThreadTerminate = 0x0001;
                        IntPtr hThread = Native.OpenThread(ThreadTerminate, false, threadId);
                        if (hThread != IntPtr.Zero)
                        {
                            try { Native.CancelSynchronousIo(hThread); }
                            finally { Native.CloseHandle(hThread); }
                        }
                    }
                    finished = done.WaitOne(500);
                }
            }
            finally
            {
                try { thread.Join(500); } catch (Exception) { }
                // 读线程还没退出时不能关事件，否则线程里的 done.Set() 会炸；
                // 留着引用，等它收尾后再关（见 ReleasePoisonIfFinished）。
                if (finished)
                {
                    try { done.Close(); } catch (Exception) { }
                }
                else
                {
                    _pendingDone = done;
                }
            }

            if (!finished)
            {
                // 读线程仍阻塞在 ReadFile 里。此时若 CloseHandle，句柄值可能被系统复用，
                // 完成通知会落到别的句柄上。宁可把这个句柄标记为“毒化”暂不关闭，
                // 等挂起的 I/O 真的返回后再复位（ReleasePoisonIfFinished）。
                _poisoned = true;
                Log.Warn("HID 读取超时且无法取消，句柄已标记丢弃：" + (_path ?? "unknown"));
                return false;
            }

            if (target != buffer)
            {
                int copy = read;
                if (copy > buffer.Length)
                {
                    copy = buffer.Length;
                }
                if (copy > 0)
                {
                    Array.Copy(target, 0, buffer, 0, copy);
                }
            }
            bytesRead = read;
            return bytesRead > 0;
        }

        /// <summary>
        /// 挂起 I/O 收尾后复位毒化标记，让句柄重新可用/可关闭；没收到尾就一直保持毒化。
        /// </summary>
        private void ReleasePoisonIfFinished()
        {
            ManualResetEvent pending = _pendingDone;
            if (pending == null)
            {
                return;
            }
            bool signaled;
            try { signaled = pending.WaitOne(0); }
            catch (ObjectDisposedException) { signaled = true; }
            if (!signaled)
            {
                return;
            }
            _pendingDone = null;
            _poisoned = false;
            try { pending.Close(); } catch (Exception) { }
        }

        public void Close()
        {
            if (_handle != Native.InvalidHandle)
            {
                if (_poisoned)
                {
                    // 先给挂起的读一点时间收尾，收尾了就能正常关闭，避免句柄永久泄漏。
                    Native.CancelIoEx(_handle, IntPtr.Zero);
                    ManualResetEvent pending = _pendingDone;
                    if (pending != null)
                    {
                        try { pending.WaitOne(200); } catch (Exception) { }
                    }
                    ReleasePoisonIfFinished();
                }
                if (_poisoned)
                {
                    // 仍卡在 ReadFile 里：关闭会让句柄值被系统复用，只能继续挂着。
                    Native.CancelIoEx(_handle, IntPtr.Zero);
                }
                else
                {
                    Native.CloseHandle(_handle);
                }
                _handle = Native.InvalidHandle;
            }
            _path = null;
            _inputReportLength = -1;
        }

        public void Dispose()
        {
            Close();
        }
    }
}
