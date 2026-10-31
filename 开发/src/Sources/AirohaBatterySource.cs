using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace DeviceBattery
{
    /// <summary>
    /// Airoha 厂商 BLE 服务（RACE 协议）的耳机电量读取。
    /// 标准 0x180F 只给一个总电量；部分耳机（如采用 Airoha 方案的型号）的厂商服务
    /// 在 0x0CD6 响应里按电池索引返回左右耳/充电盒。
    /// </summary>
    internal sealed class AirohaBatterySource
    {
        private static readonly Guid ServiceUuid = new Guid("5052494D-2DAB-0341-6972-6F6861424C45");
        private static readonly Guid TxUuid = new Guid("43484152-2DAB-3241-6972-6F6861424C45");
        private static readonly Guid RxUuid = new Guid("43484152-2DAB-3141-6972-6F6861424C45");

        private const int BatteryCommand = 0x0CD6;

        /// <summary>
        /// 读取 Airoha 厂商服务的电量。<paramref name="serviceMissing"/> 只在本次调用内有效。
        /// 这个类由 BluetoothBatterySource 唯一持有，而 BatteryService 会在线程池里并发查多台设备，
        /// 用共享字段记「这台设备没有该服务」会把 A 设备的结果算到 B 设备头上：缓存里被记成
        /// NotSupported，那台本来有服务的设备之后一小时内都不会再尝试。
        /// </summary>
        public bool TryRead(BluetoothLEDevice device, int timeoutMs, BatteryReading reading,
            out bool serviceMissing)
        {
            serviceMissing = false;
            if (device == null || reading == null || timeoutMs < 300)
            {
                return false;
            }

            GattDeviceService service = null;
            GattCharacteristic tx = null;
            GattCharacteristic rx = null;
            ManualResetEventSlim signal = new ManualResetEventSlim(false);
            List<byte[]> frames = new List<byte[]>();
            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler =
                delegate(GattCharacteristic sender, GattValueChangedEventArgs args)
            {
                try
                {
                    DataReader dataReader = DataReader.FromBuffer(args.CharacteristicValue);
                    byte[] bytes = new byte[args.CharacteristicValue.Length];
                    dataReader.ReadBytes(bytes);
                    lock (frames)
                    {
                        frames.Add(bytes);
                    }
                    signal.Set();
                }
                catch (Exception)
                {
                }
            };

            int deadline = Environment.TickCount + timeoutMs;
            try
            {
                GattDeviceServicesResult services = Wait(
                    device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached),
                    Remaining(deadline, timeoutMs));
                if (services == null || services.Status != GattCommunicationStatus.Success)
                {
                    return false;
                }
                if (services.Services == null || services.Services.Count == 0)
                {
                    serviceMissing = true;
                    return false;
                }

                service = services.Services[0];
                GattCharacteristicsResult characteristics = Wait(
                    service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached),
                    Remaining(deadline, timeoutMs));
                if (characteristics == null || characteristics.Status != GattCommunicationStatus.Success ||
                    characteristics.Characteristics == null)
                {
                    return false;
                }

                foreach (GattCharacteristic characteristic in characteristics.Characteristics)
                {
                    if (characteristic.Uuid == TxUuid) tx = characteristic;
                    if (characteristic.Uuid == RxUuid) rx = characteristic;
                }
                if (tx == null || rx == null)
                {
                    return false;
                }

                rx.ValueChanged += handler;
                GattCommunicationStatus sub = Wait(
                    rx.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify),
                    Remaining(deadline, timeoutMs));
                if (sub != GattCommunicationStatus.Success)
                {
                    return false;
                }

                int left = -1;
                int right = -1;
                int casePercent = -1;

                // 0x0CD6 响应格式：status, index, level。
                // 索引 0/1 分别对应左右耳；部分固件对索引 2 返回充电盒。
                for (byte selector = 0; selector <= 1; selector++)
                {
                    lock (frames) { frames.Clear(); }
                    signal.Reset();
                    if (!Send(tx, Frame(BatteryCommand, selector), deadline, timeoutMs))
                    {
                        continue;
                    }

                    signal.Wait(Math.Min(1200, Remaining(deadline, timeoutMs)));
                    Thread.Sleep(80);

                    List<byte[]> snapshot;
                    lock (frames) { snapshot = new List<byte[]>(frames); }
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        int index;
                        int level;
                        if (!TryParseBatteryFrame(snapshot[i], out index, out level))
                        {
                            continue;
                        }
                        if (index == 0) left = level;
                        else if (index == 1) right = level;
                        else if (index == 2) casePercent = level;
                    }
                }

                if (left < 0 && right < 0)
                {
                    if (casePercent < 0)
                    {
                        return false;
                    }
                    // 只读到充电盒的电量：耳机本体不在盒中，或者固件没有返回左右耳。
                    // 明确写出原因，避免上层把它当成“读取异常”却没有任何说明。
                    reading.CasePercent = casePercent;
                    reading.Error = string.Format(CultureInfo.InvariantCulture,
                        "只读到充电盒电量（C {0}%），未读到耳机本机电量", casePercent);
                    reading.Source = BatterySourceKind.Vendor;
                    reading.Confidence = ReadingConfidence.Low;
                    reading.Timestamp = DateTime.Now;
                    reading.Raw = string.Format(CultureInfo.InvariantCulture,
                        "Airoha RACE L={0} R={1} C={2}", left, right, casePercent);
                    return true;
                }

                if (left >= 0) reading.LeftPercent = left;
                if (right >= 0) reading.RightPercent = right;
                if (casePercent >= 0) reading.CasePercent = casePercent;
                if (left >= 0 && right >= 0)
                {
                    reading.Percent = Math.Min(left, right);
                }
                else if (reading.Percent == null)
                {
                    reading.Percent = left >= 0 ? left : right;
                }

                reading.Source = BatterySourceKind.Vendor;
                reading.Confidence = ReadingConfidence.Medium;
                reading.Charging = null;
                reading.Timestamp = DateTime.Now;
                reading.Error = null;
                reading.Raw = string.Format(CultureInfo.InvariantCulture,
                    "Airoha RACE L={0} R={1} C={2}", left, right, casePercent);
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Airoha 电量读取失败：" + ShortError(ex));
                return false;
            }
            finally
            {
                if (rx != null)
                {
                    try { rx.ValueChanged -= handler; } catch (Exception) { }
                    try
                    {
                        rx.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None).AsTask().Wait(250);
                    }
                    catch (Exception) { }
                }
                if (service != null)
                {
                    try { service.Dispose(); } catch (Exception) { }
                }
                signal.Dispose();
            }
        }

        internal static bool TryParseBatteryFrame(byte[] frame, out int index, out int level)
        {
            index = -1;
            level = -1;
            if (frame == null || frame.Length < 9) return false;
            if (frame[0] != 0x05) return false;
            if (frame[1] != 0x5B && frame[1] != 0x5D) return false;
            int length = frame[2] | (frame[3] << 8);
            int command = frame[4] | (frame[5] << 8);
            if (command != BatteryCommand) return false;
            int payloadLength = length - 2;
            if (payloadLength < 3 || frame.Length < 6 + payloadLength) return false;
            if (frame[6] != 0x00) return false;
            index = frame[7];
            level = frame[8];
            return index >= 0 && index <= 2 && level >= 0 && level <= 100;
        }

        private static byte[] Frame(int command, byte payload)
        {
            return new byte[]
            {
                0x05, 0x5A, 0x03, 0x00,
                (byte)(command & 0xFF), (byte)((command >> 8) & 0xFF),
                payload
            };
        }

        private static bool Send(GattCharacteristic tx, byte[] frame, int deadline, int timeoutMs)
        {
            try
            {
                DataWriter writer = new DataWriter();
                writer.WriteBytes(frame);
                GattWriteResult result = Wait(tx.WriteValueWithResultAsync(writer.DetachBuffer()),
                    Remaining(deadline, timeoutMs));
                return result != null && result.Status == GattCommunicationStatus.Success;
            }
            catch (Exception ex)
            {
                Log.Debug("Airoha 写入失败：" + ShortError(ex));
                return false;
            }
        }

        private static T Wait<T>(IAsyncOperation<T> operation, int timeoutMs)
        {
            if (operation == null)
            {
                throw new InvalidOperationException("Airoha 操作为空");
            }
            Task<T> task = operation.AsTask();
            if (!task.Wait(timeoutMs))
            {
                // 超时后 WinRT 操作仍在后台执行并持有 GATT 服务引用，
                // 不 Cancel 会攒下孤儿操作，拖慢后续每次读取。
                try
                {
                    operation.Cancel();
                }
                catch (Exception ex)
                {
                    Log.Debug("取消 Airoha 操作失败：" + ShortError(ex));
                }
                throw new TimeoutException();
            }
            return task.Result;
        }

        private static int Remaining(int deadline, int fallback)
        {
            int remaining = deadline - Environment.TickCount;
            if (remaining <= 0) return 150;
            return Math.Min(remaining, Math.Max(150, fallback));
        }

        private static string ShortError(Exception ex)
        {
            if (ex == null) return "未知错误";
            string message = ex.Message;
            if (ex.InnerException != null && !string.IsNullOrEmpty(ex.InnerException.Message))
            {
                message += " / " + ex.InnerException.Message;
            }
            return message;
        }
    }
}
