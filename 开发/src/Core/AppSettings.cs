using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DeviceBattery
{
    internal sealed class WarnState
    {
        public List<int> Levels = new List<int>();
        public int LastPercent = -1;
        public DateTime Timestamp = DateTime.MinValue;
    }

    internal sealed class DeviceRecord
    {
        public string Name;
        public string Category;
        public string Transport;
        public string LastSeen;
    }

    internal sealed class SettingsData
    {
        public int SchemaVersion = 1;
        public int RefreshMinutes = 10;
        public bool PauseWhenIdle = true;
        public bool LowBatteryWarning = true;
        public List<int> WarnLevels = new List<int>(new int[] { 20, 15, 10, 5 });
        public bool DndEnabled = false;
        public string DndStart = "23:00";
        public string DndEnd = "07:00";
        public bool SuppressFullscreen = true;
        public string DisplayMode = "aggregate";
        public string ThemeMode = "system";
        public string IconDevice = "auto";
        public string TrayDeviceId = "";
        // 80 = 设置页的「标准」档，和旧版默认看上去一样；66/100 分别是较小/较大。
        public int DigitPercent = 80;
        public string IconStyle = "soft";
        // 托盘数字字体：auto = 跟随样式；black / semibold / bold / yahei / verdana / tahoma /
        // arialblack / bahnschrift / impact，见 IconFactory.DigitFont。
        public string IconFont = "auto";
        public bool AutoColor = true;
        public int CustomColorArgb = unchecked((int)0xFF22C55E);
        public bool ShowVoltage = true;
        public bool HistoryEnabled = true;
        public bool PortableMode = false;
        public bool FirstRunHintShown = false;
        public bool StartWithPanel = false;
        public bool PanelPositionSaved = false;
        public int PanelX = 0;
        public int PanelY = 0;
        // 设置窗口上次被拖动出来的大小（0 表示还没拖过，仍按内容自适应）。
        public int SettingsWidth = 0;
        public int SettingsHeight = 0;
        public bool DetailedDiagnostics = false;
        public Dictionary<string, string> DeviceNames = new Dictionary<string, string>();
        public Dictionary<string, int> DeviceOrder = new Dictionary<string, int>();
        public Dictionary<string, int> DeviceAlertThresholds = new Dictionary<string, int>();
        public Dictionary<string, DeviceRecord> DeviceRecords = new Dictionary<string, DeviceRecord>();
        /// <summary>
        /// 被「读不到电量就不显示」规则自动隐藏的设备里，用户手动解除隐藏的那些。
        /// 值为 true 表示强制显示（卡片上照旧写明读不到的原因，不给假数字）。
        /// </summary>
        public Dictionary<string, bool> DeviceForceVisible = new Dictionary<string, bool>();
        public Dictionary<string, AtkIdentityRecord> AtkIdentities = new Dictionary<string, AtkIdentityRecord>();
        public Dictionary<string, WarnState> WarnStates = new Dictionary<string, WarnState>();
    }

    internal static class AppSettings
    {
        private static readonly object Gate = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static bool _knownDevicesDirty;
        private static DateTime _knownDevicesSavedAt = DateTime.MinValue;
        private static bool _dndParseWarned;

        public static SettingsData Current = new SettingsData();

        // 仅供本地测试注入临时配置目录；正常运行时保持 null。
        internal static string ConfigDirectoryOverride = null;

        public static string BaseDirectory
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string PortableMarker
        {
            get { return Path.Combine(BaseDirectory, "portable.txt"); }
        }

        public static bool IsPortable
        {
            get
            {
                try { return File.Exists(PortableMarker); }
                catch (Exception) { return false; }
            }
        }

        public static string SettingsPath
        {
            get
            {
                if (IsPortable)
                {
                    return Path.Combine(BaseDirectory, "settings.json");
                }
                if (!string.IsNullOrEmpty(ConfigDirectoryOverride))
                {
                    return Path.Combine(ConfigDirectoryOverride, "settings.json");
                }
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VoltPick");
                return Path.Combine(folder, "settings.json");
            }
        }

        public static string ConfigDirectory
        {
            get { return Path.GetDirectoryName(SettingsPath); }
        }

        public static int RefreshMinutes
        {
            get { return Current.RefreshMinutes; }
            set { Current.RefreshMinutes = Clamp(value, 1, 1440); }
        }

        public static bool LowBatteryWarning
        {
            get { return Current.LowBatteryWarning; }
            set { Current.LowBatteryWarning = value; }
        }

        public static void Load()
        {
            lock (Gate)
            {
                Current = new SettingsData();
                string path = SettingsPath;
                if (!File.Exists(path))
                {
                    Current.PortableMode = IsPortable;
                    Save();
                    return;
                }

                try
                {
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    Dictionary<string, object> root = Json.DeserializeObject(text) as Dictionary<string, object>;
                    if (root == null)
                    {
                        throw new InvalidDataException("配置根节点不是对象");
                    }
                    ReadRoot(root);
                    Current.PortableMode = IsPortable;
                    Log.Info("设置已载入：" + path);
                }
                catch (Exception ex)
                {
                    Log.Error("设置读取失败，已备份并使用默认值：" + ex.Message);
                    try
                    {
                        string backup = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        File.Copy(path, backup, true);
                    }
                    catch (Exception)
                    {
                    }
                    Current = new SettingsData();
                    Current.PortableMode = IsPortable;
                    Save();
                }
            }
        }

        public static void Save()
        {
            lock (Gate)
            {
                try
                {
                    string path = SettingsPath;
                    string folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }

                    Dictionary<string, object> root = BuildRoot();
                    string text = Json.Serialize(root);
                    string temp = path + ".tmp";
                    File.WriteAllText(temp, text, new UTF8Encoding(false));

                    if (File.Exists(path))
                    {
                        string backup = path + ".bak";
                        try
                        {
                            File.Replace(temp, path, backup, true);
                        }
                        catch (Exception)
                        {
                            File.Copy(temp, path, true);
                            File.Delete(temp);
                        }
                    }
                    else
                    {
                        File.Move(temp, path);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("设置保存失败：" + ex.Message);
                }
            }
        }

        public static void ResetToDefaults()
        {
            lock (Gate)
            {
                bool portable = IsPortable;
                Current = new SettingsData();
                Current.PortableMode = portable;
                Save();
            }
        }

        public static string GetDeviceName(string id)
        {
            lock (Gate)
            {
                string name;
                if (Current.DeviceNames.TryGetValue(id, out name))
                {
                    return name;
                }
                return null;
            }
        }

        public static int GetDeviceOrder(string id, int fallback)
        {
            lock (Gate)
            {
                int order;
                if (Current.DeviceOrder.TryGetValue(id, out order))
                {
                    return order;
                }
                return fallback;
            }
        }

        public static int GetDeviceAlertThreshold(string id)
        {
            lock (Gate)
            {
                int threshold;
                if (!string.IsNullOrEmpty(id) &&
                    Current.DeviceAlertThresholds.TryGetValue(id, out threshold) &&
                    threshold > 0)
                {
                    return threshold;
                }
                return 0;
            }
        }

        /// <summary>
        /// 整体替换所有设备的低电阈值。注意这是**整体替换**：传进来的字典里没有的设备，
        /// 原有阈值会被清掉。设置页只想改一台设备时请用 <see cref="SetDeviceAlertThreshold"/>。
        /// </summary>
        public static void ReplaceDeviceAlertThresholds(Dictionary<string, int> thresholds)
        {
            lock (Gate)
            {
                Current.DeviceAlertThresholds = new Dictionary<string, int>(StringComparer.Ordinal);
                if (thresholds == null)
                {
                    return;
                }
                foreach (KeyValuePair<string, int> pair in thresholds)
                {
                    if (!string.IsNullOrEmpty(pair.Key) && pair.Value > 0)
                    {
                        Current.DeviceAlertThresholds[pair.Key] =
                            Math.Max(1, Math.Min(100, pair.Value));
                    }
                }
            }
        }

        /// <summary>
        /// 只改一台设备的低电阈值（0 表示跟随全局），其余设备不受影响。
        /// 设置页原来用 <see cref="ReplaceDeviceAlertThresholds"/> 只塞一台设备进去，
        /// 结果每改一次别的设备的阈值就全被清空。
        /// </summary>
        public static void SetDeviceAlertThreshold(string id, int threshold)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            lock (Gate)
            {
                if (threshold > 0)
                {
                    Current.DeviceAlertThresholds[id] = Math.Max(1, Math.Min(100, threshold));
                }
                else
                {
                    Current.DeviceAlertThresholds.Remove(id);
                }
            }
        }

        /// <summary>
        /// 该设备是否被用户手动「解除隐藏」。读不到电量的设备默认不显示，
        /// 解除隐藏后仍会显示卡片，但电量位置照旧写明原因，不给猜测值。
        /// </summary>
        public static bool GetDeviceForceVisible(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }
            lock (Gate)
            {
                bool visible;
                return Current.DeviceForceVisible.TryGetValue(id, out visible) && visible;
            }
        }

        /// <summary>设置或取消「解除隐藏」。调用方负责随后统一 Save。</summary>
        public static void SetDeviceForceVisible(string id, bool forceVisible)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            lock (Gate)
            {
                if (forceVisible)
                {
                    Current.DeviceForceVisible[id] = true;
                }
                else
                {
                    Current.DeviceForceVisible.Remove(id);
                }
            }
        }

        /// <summary>
        /// 彻底移除一台设备的全部记录（名称、排序、低电阈值、解除隐藏、已添加设备记录、
        /// 提醒状态、电量历史采样）。用于设置页的「移除记录」。
        /// ATK 型号缓存按接收器序列号存，与设备 id 不同源，故意保留。
        /// </summary>
        public static bool ForgetDevice(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }
            bool removed;
            lock (Gate)
            {
                removed = false;
                removed |= Current.DeviceRecords.Remove(id);
                removed |= Current.DeviceNames.Remove(id);
                removed |= Current.DeviceOrder.Remove(id);
                removed |= Current.DeviceAlertThresholds.Remove(id);
                removed |= Current.DeviceForceVisible.Remove(id);
                removed |= Current.WarnStates.Remove(id);
            }
            // 历史文件的锁独立于 AppSettings.Gate，放在锁外调用，避免两把锁嵌套。
            // 即使设置里没有这台设备的记录，也可能只剩历史采样，所以无条件尝试一次。
            bool historyRemoved = BatteryHistory.ClearSeries(id);
            return removed || historyRemoved;
        }

        public static void SetDeviceName(string id, string name, int order)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            lock (Gate)
            {
                if (string.IsNullOrEmpty(name))
                {
                    Current.DeviceNames.Remove(id);
                }
                else
                {
                    Current.DeviceNames[id] = name.Trim();
                }
                Current.DeviceOrder[id] = order;
                // 由调用方批量设置完后统一 Save，避免设备列表每行都写一次 JSON。
            }
        }

        /// <summary>读取上次成功识别并缓存的 ATK 鼠标型号（按接收器序列号）。</summary>
        public static AtkMouseIdentity FindAtkIdentity(HidDeviceInfo info)
        {
            string key = AtkMouseIdentity.CacheKey(info);
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            lock (Gate)
            {
                AtkIdentityRecord record;
                if (Current.AtkIdentities == null ||
                    !Current.AtkIdentities.TryGetValue(key, out record) ||
                    record == null)
                {
                    return null;
                }
                AtkMouseIdentity identity = new AtkMouseIdentity();
                identity.Cid = record.Cid;
                identity.Mid = record.Mid;
                identity.ModelName = string.IsNullOrEmpty(record.ModelName) ? "ATK 鼠标" : record.ModelName;
                identity.UniqueId = record.UniqueId;
                identity.ModelKnown = !string.Equals(identity.ModelName, "ATK 鼠标", StringComparison.Ordinal);
                return identity;
            }
        }

        /// <summary>缓存真实型号；重复发现同一型号时不再写盘。</summary>
        public static void RememberAtkIdentity(AtkMouseIdentity identity, HidDeviceInfo info)
        {
            string key = AtkMouseIdentity.CacheKey(info);
            if (identity == null || string.IsNullOrEmpty(key))
            {
                return;
            }

            bool changed = false;
            lock (Gate)
            {
                if (Current.AtkIdentities == null)
                {
                    Current.AtkIdentities = new Dictionary<string, AtkIdentityRecord>();
                }
                AtkIdentityRecord record;
                if (!Current.AtkIdentities.TryGetValue(key, out record) || record == null)
                {
                    record = new AtkIdentityRecord();
                    Current.AtkIdentities[key] = record;
                    changed = true;
                }
                if (record.Cid != identity.Cid ||
                    record.Mid != identity.Mid ||
                    !string.Equals(record.ModelName, identity.ModelName, StringComparison.Ordinal) ||
                    !string.Equals(record.UniqueId, identity.UniqueId, StringComparison.Ordinal))
                {
                    record.Cid = identity.Cid;
                    record.Mid = identity.Mid;
                    record.ModelName = identity.ModelName;
                    record.UniqueId = identity.UniqueId;
                    changed = true;
                }
            }
            if (changed)
            {
                Save();
            }
        }

        /// <summary>
        /// 型号查询失败时，从已添加设备记录里找回同一接收器序列号的鼠标型号，
        /// 避免退回接收器哈希 ID 并生成重复记录。
        /// </summary>
        public static AtkMouseIdentity FindKnownAtkIdentity(string serial)
        {
            string normalized = AtkMouseIdentity.NormalizeSerial(serial);
            if (string.IsNullOrEmpty(normalized))
            {
                return null;
            }

            lock (Gate)
            {
                foreach (KeyValuePair<string, DeviceRecord> pair in Current.DeviceRecords)
                {
                    string idSerial;
                    int cid;
                    int mid;
                    if (!TryParseAtkStableId(pair.Key, out idSerial, out cid, out mid) ||
                        !string.Equals(idSerial, normalized, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    AtkMouseIdentity identity = new AtkMouseIdentity();
                    identity.Cid = cid;
                    identity.Mid = mid;
                    identity.ModelName = string.IsNullOrEmpty(pair.Value.Name)
                        ? "ATK 鼠标"
                        : pair.Value.Name;
                    identity.ModelKnown = !string.Equals(identity.ModelName, "ATK 鼠标", StringComparison.Ordinal);
                    return identity;
                }
            }
            return null;
        }

        private static bool TryParseAtkStableId(string id, out string serial, out int cid, out int mid)
        {
            serial = null;
            cid = 0;
            mid = 0;
            if (string.IsNullOrEmpty(id) ||
                !id.StartsWith("atk:", StringComparison.Ordinal))
            {
                return false;
            }

            string body = id.Substring(4);
            int dash = body.LastIndexOf('-');
            if (dash <= 0 || dash >= body.Length - 1)
            {
                return false;
            }
            string serialText = body.Substring(0, dash);
            string version = body.Substring(dash + 1);
            int dot = version.IndexOf('.');
            if (dot <= 0 || dot >= version.Length - 1)
            {
                return false;
            }

            if (!int.TryParse(version.Substring(0, dot), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out cid) ||
                !int.TryParse(version.Substring(dot + 1), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out mid))
            {
                return false;
            }
            serial = serialText;
            return true;
        }

        /// <summary>
        /// 鼠标型号识别后设备 ID 会从接收器哈希升级为“序列号-CID.MID”。
        /// 在首次发现时把旧 ID 的自定义名称、排序、提醒和已知记录迁移过去，
        /// 避免设置里出现重复设备或用户改名丢失。
        /// </summary>
        public static bool MigrateDeviceIdentity(string oldId, string newId)
        {
            if (string.IsNullOrEmpty(oldId) || string.IsNullOrEmpty(newId) ||
                string.Equals(oldId, newId, StringComparison.Ordinal))
            {
                return false;
            }

            bool changed = false;
            lock (Gate)
            {
                bool hasNew = Current.DeviceNames.ContainsKey(newId) ||
                              Current.DeviceOrder.ContainsKey(newId) ||
                              Current.DeviceAlertThresholds.ContainsKey(newId) ||
                              Current.DeviceRecords.ContainsKey(newId) ||
                              Current.WarnStates.ContainsKey(newId);
                if (hasNew)
                {
                    return false;
                }

                string name;
                if (Current.DeviceNames.TryGetValue(oldId, out name))
                {
                    Current.DeviceNames[newId] = name;
                    Current.DeviceNames.Remove(oldId);
                    changed = true;
                }

                int order;
                if (Current.DeviceOrder.TryGetValue(oldId, out order))
                {
                    Current.DeviceOrder[newId] = order;
                    Current.DeviceOrder.Remove(oldId);
                    changed = true;
                }

                int alertThreshold;
                if (Current.DeviceAlertThresholds.TryGetValue(oldId, out alertThreshold))
                {
                    Current.DeviceAlertThresholds[newId] = alertThreshold;
                    Current.DeviceAlertThresholds.Remove(oldId);
                    changed = true;
                }

                bool forceVisible;
                if (Current.DeviceForceVisible.TryGetValue(oldId, out forceVisible))
                {
                    Current.DeviceForceVisible[newId] = forceVisible;
                    Current.DeviceForceVisible.Remove(oldId);
                    changed = true;
                }

                WarnState warn;
                if (Current.WarnStates.TryGetValue(oldId, out warn))
                {
                    Current.WarnStates[newId] = warn;
                    Current.WarnStates.Remove(oldId);
                    changed = true;
                }

                DeviceRecord record;
                if (Current.DeviceRecords.TryGetValue(oldId, out record))
                {
                    DeviceRecord copy = new DeviceRecord();
                    copy.Name = record.Name;
                    copy.Category = record.Category;
                    copy.Transport = record.Transport;
                    copy.LastSeen = record.LastSeen;
                    Current.DeviceRecords[newId] = copy;
                    Current.DeviceRecords.Remove(oldId);
                    changed = true;
                }

                if (string.Equals(Current.TrayDeviceId, oldId, StringComparison.Ordinal))
                {
                    Current.TrayDeviceId = newId;
                    changed = true;
                }

                if (changed)
                {
                    _knownDevicesDirty = true;
                }
            }

            if (changed)
            {
                FlushKnownDevices();
            }
            return changed;
        }

        /// <summary>旧版本只保存过接收器哈希；首次升级时自动迁移唯一的旧 ATK 记录。</summary>
        public static bool MigrateLegacyAtkDevice(string newId)
        {
            string oldId = FindLegacyAtkId(newId);
            return oldId == null ? false : MigrateDeviceIdentity(oldId, newId);
        }

        private static string FindLegacyAtkId(string newId)
        {
            lock (Gate)
            {
                List<string> ids = new List<string>();
                AddLegacyAtkId(ids, newId, Current.DeviceNames.Keys);
                AddLegacyAtkId(ids, newId, Current.DeviceOrder.Keys);
                AddLegacyAtkId(ids, newId, Current.DeviceAlertThresholds.Keys);
                AddLegacyAtkId(ids, newId, Current.DeviceRecords.Keys);
                AddLegacyAtkId(ids, newId, Current.WarnStates.Keys);
                if (!string.IsNullOrEmpty(Current.TrayDeviceId))
                {
                    AddLegacyAtkId(ids, newId, new string[] { Current.TrayDeviceId });
                }

                if (ids.Count != 1)
                {
                    return null;
                }

                DeviceRecord record;
                if (Current.DeviceRecords.TryGetValue(ids[0], out record) &&
                    !string.IsNullOrEmpty(record.Category) &&
                    !string.Equals(record.Category, "鼠标", StringComparison.Ordinal))
                {
                    return null;
                }
                return ids[0];
            }
        }

        private static void AddLegacyAtkId(List<string> ids, string newId,
            IEnumerable<string> keys)
        {
            foreach (string key in keys)
            {
                if (string.IsNullOrEmpty(key) ||
                    key.StartsWith("atk:", StringComparison.Ordinal) == false ||
                    key.IndexOf('-') >= 0 ||
                    string.Equals(key, newId, StringComparison.Ordinal))
                {
                    // 只迁移旧版“接收器哈希”ID（atk:36ee60ea）；
                    // 已带 CID/MID 的新 ID 属于另一只鼠标，不能把偏好带过去。
                    continue;
                }
                bool exists = false;
                for (int i = 0; i < ids.Count; i++)
                {
                    if (string.Equals(ids[i], key, StringComparison.Ordinal))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    ids.Add(key);
                }
            }
        }

        public static void ApplyDevicePreferences(DeviceState device)
        {
            if (device == null || string.IsNullOrEmpty(device.Id))
            {
                return;
            }

            lock (Gate)
            {
                string custom = GetDeviceName(device.Id);
                if (!string.IsNullOrEmpty(custom))
                {
                    device.DisplayName = custom;
                }
                device.Order = GetDeviceOrder(device.Id, device.Order);
                device.ForceVisible = GetDeviceForceVisible(device.Id);

                WarnState state;
                if (Current.WarnStates.TryGetValue(device.Id, out state))
                {
                    // 超过 24 小时的提醒记录不再沿用，避免换了电池/长期关机后永远不提醒。
                    if (state.Timestamp != DateTime.MinValue &&
                        (DateTime.Now - state.Timestamp).TotalHours < 24)
                    {
                        device.WarnedLevels = new HashSet<int>(state.Levels);
                        device.LastWarnPercent = state.LastPercent;
                        device.LastWarnTime = state.Timestamp;
                    }
                }
            }
        }

        /// <summary>把发现过的设备写入“已添加设备”记录，便于断连后仍可在设置中管理。</summary>
        public static void RememberDevice(DeviceState device)
        {
            if (device == null || string.IsNullOrEmpty(device.Id))
            {
                return;
            }

            lock (Gate)
            {
                DeviceRecord record;
                if (!Current.DeviceRecords.TryGetValue(device.Id, out record))
                {
                    record = new DeviceRecord();
                    Current.DeviceRecords[device.Id] = record;
                    _knownDevicesDirty = true;
                }

                string name = string.IsNullOrEmpty(device.DisplayName)
                    ? (string.IsNullOrEmpty(device.Model) ? device.CategoryText : device.Model)
                    : device.DisplayName;
                string category = device.CategoryText;
                string transport = device.TransportText;
                string seen = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                if (!string.Equals(record.Name, name, StringComparison.Ordinal) ||
                    !string.Equals(record.Category, category, StringComparison.Ordinal) ||
                    !string.Equals(record.Transport, transport, StringComparison.Ordinal) ||
                    !string.Equals(record.LastSeen, seen, StringComparison.Ordinal))
                {
                    record.Name = name;
                    record.Category = category;
                    record.Transport = transport;
                    record.LastSeen = seen;
                    _knownDevicesDirty = true;
                }
            }
        }

        /// <summary>发现线程会频繁调用；最多每 10 分钟落盘一次，避免无意义的磁盘写入。</summary>
        public static void SaveKnownDevicesIfDirty()
        {
            bool save = false;
            lock (Gate)
            {
                if (_knownDevicesDirty &&
                    (DateTime.Now - _knownDevicesSavedAt).TotalMinutes >= 10)
                {
                    _knownDevicesDirty = false;
                    _knownDevicesSavedAt = DateTime.Now;
                    save = true;
                }
            }
            if (save)
            {
                Save();
            }
        }

        /// <summary>正常退出时强制落盘一次，避免刚发现的设备记录丢失。</summary>
        public static void FlushKnownDevices()
        {
            bool save = false;
            lock (Gate)
            {
                if (_knownDevicesDirty)
                {
                    _knownDevicesDirty = false;
                    _knownDevicesSavedAt = DateTime.Now;
                    save = true;
                }
            }
            if (save)
            {
                Save();
            }
        }

        public static List<KeyValuePair<string, DeviceRecord>> DeviceRecordsSnapshot()
        {
            lock (Gate)
            {
                return new List<KeyValuePair<string, DeviceRecord>>(Current.DeviceRecords);
            }
        }

        public static DeviceRecord GetDeviceRecord(string id)
        {
            lock (Gate)
            {
                DeviceRecord record;
                if (string.IsNullOrEmpty(id) || !Current.DeviceRecords.TryGetValue(id, out record))
                {
                    return null;
                }
                DeviceRecord copy = new DeviceRecord();
                copy.Name = record.Name;
                copy.Category = record.Category;
                copy.Transport = record.Transport;
                copy.LastSeen = record.LastSeen;
                return copy;
            }
        }

        public static void SaveWarnState(string id, HashSet<int> levels, int lastPercent)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            lock (Gate)
            {
                WarnState state = new WarnState();
                state.Levels = new List<int>(levels);
                state.Levels.Sort();
                state.LastPercent = lastPercent;
                state.Timestamp = DateTime.Now;
                Current.WarnStates[id] = state;
                Save();
            }
        }

        public static void ClearWarnState(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            lock (Gate)
            {
                if (Current.WarnStates.Remove(id))
                {
                    Save();
                }
            }
        }

        public static bool IsInDoNotDisturb()
        {
            if (!Current.DndEnabled)
            {
                return false;
            }
            TimeSpan start;
            TimeSpan end;
            if (!TryParseTime(Current.DndStart, out start) || !TryParseTime(Current.DndEnd, out end))
            {
                // 配置被手工改坏时不能默默当作“不在免打扰”，否则用户会收到本该被抑制的提醒。
                if (!_dndParseWarned)
                {
                    _dndParseWarned = true;
                    Log.Warn("免打扰时段格式无效（" + Current.DndStart + " 至 " + Current.DndEnd +
                             "），本次按不启用免打扰处理。请在 settings.json 里改成 23:00 这样的格式。");
                }
                return false;
            }
            TimeSpan now = DateTime.Now.TimeOfDay;
            if (start <= end)
            {
                return now >= start && now < end;
            }
            return now >= start || now < end;
        }

        private static bool TryParseTime(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            return TimeSpan.TryParseExact(text.Trim(), new string[] { "h\\:mm", "hh\\:mm", "h\\:mm\\:ss", "hh\\:mm\\:ss" },
                CultureInfo.InvariantCulture, out value);
        }

        private static void ReadRoot(Dictionary<string, object> root)
        {
            Current.SchemaVersion = GetInt(root, "schemaVersion", 1);
            Current.RefreshMinutes = NormalizeRefresh(GetInt(root, "refreshMinutes", 10));
            Current.PauseWhenIdle = GetBool(root, "pauseWhenIdle", true);
            Current.LowBatteryWarning = GetBool(root, "lowBatteryWarning", true);
            Current.WarnLevels = NormalizeLevels(GetIntList(root, "warnLevels"));
            Current.DndEnabled = GetBool(root, "dndEnabled", false);
            Current.DndStart = GetString(root, "dndStart", "23:00");
            Current.DndEnd = GetString(root, "dndEnd", "07:00");
            Current.SuppressFullscreen = GetBool(root, "suppressFullscreen", true);
            Current.DisplayMode = NormalizeDisplayMode(GetString(root, "displayMode", "aggregate"));
            Current.ThemeMode = NormalizeThemeMode(GetString(root, "themeMode", "system"));
            Current.IconDevice = NormalizeIconDevice(GetString(root, "iconDevice", "auto"));
            Current.TrayDeviceId = GetString(root, "trayDeviceId", "");
            // 0 = 不显示数字，40..100 = 数字大小三档；旧配置里的 40 仍然有效。
            Current.DigitPercent = Clamp(GetInt(root, "digitPercent", 80), 0, 100);
            Current.IconStyle = NormalizeIconStyle(GetString(root, "iconStyle", "soft"));
            Current.IconFont = NormalizeIconFont(GetString(root, "iconFont", "auto"));
            Current.AutoColor = GetBool(root, "autoColor", true);
            Current.CustomColorArgb = GetInt(root, "customColorArgb", unchecked((int)0xFF22C55E));
            Current.ShowVoltage = GetBool(root, "showVoltage", true);
            Current.HistoryEnabled = GetBool(root, "historyEnabled", true);
            Current.FirstRunHintShown = GetBool(root, "firstRunHintShown", false);
            Current.StartWithPanel = GetBool(root, "startWithPanel", false);
            Current.PanelPositionSaved = GetBool(root, "panelPositionSaved", false);
            Current.PanelX = GetInt(root, "panelX", 0);
            Current.PanelY = GetInt(root, "panelY", 0);
            Current.SettingsWidth = Clamp(GetInt(root, "settingsWidth", 0), 0, 4000);
            Current.SettingsHeight = Clamp(GetInt(root, "settingsHeight", 0), 0, 3000);
            Current.DetailedDiagnostics = GetBool(root, "detailedDiagnostics", false);

            Dictionary<string, object> devices = GetDict(root, "devices");
            if (devices != null)
            {
                Dictionary<string, object> names = GetDict(devices, "names");
                if (names != null)
                {
                    foreach (KeyValuePair<string, object> pair in names)
                    {
                        string value = pair.Value == null ? null : Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                        if (!string.IsNullOrEmpty(value))
                        {
                            Current.DeviceNames[pair.Key] = value;
                        }
                    }
                }

                Dictionary<string, object> orders = GetDict(devices, "order");
                if (orders != null)
                {
                    foreach (KeyValuePair<string, object> pair in orders)
                    {
                        Current.DeviceOrder[pair.Key] = ToInt(pair.Value, 0);
                    }
                }

                Dictionary<string, object> alertThresholds = GetDict(devices, "alertThresholds");
                if (alertThresholds != null)
                {
                    foreach (KeyValuePair<string, object> pair in alertThresholds)
                    {
                        int threshold = ToInt(pair.Value, 0);
                        if (!string.IsNullOrEmpty(pair.Key) && threshold > 0)
                        {
                            Current.DeviceAlertThresholds[pair.Key] = Math.Max(1, Math.Min(100, threshold));
                        }
                    }
                }

                Dictionary<string, object> forceVisible = GetDict(devices, "forceVisible");
                if (forceVisible != null)
                {
                    foreach (KeyValuePair<string, object> pair in forceVisible)
                    {
                        if (!string.IsNullOrEmpty(pair.Key) && ToBool(pair.Value, false))
                        {
                            Current.DeviceForceVisible[pair.Key] = true;
                        }
                    }
                }

                Dictionary<string, object> records = GetDict(devices, "known");
                if (records != null)
                {
                    foreach (KeyValuePair<string, object> pair in records)
                    {
                        Dictionary<string, object> item = pair.Value as Dictionary<string, object>;
                        if (item == null)
                        {
                            continue;
                        }
                        DeviceRecord record = new DeviceRecord();
                        record.Name = GetString(item, "name", "");
                        record.Category = GetString(item, "category", "设备");
                        record.Transport = GetString(item, "transport", "");
                        record.LastSeen = GetString(item, "lastSeen", "");
                        Current.DeviceRecords[pair.Key] = record;
                    }
                }

                Dictionary<string, object> atkIdentities = GetDict(devices, "atkIdentities");
                if (atkIdentities != null)
                {
                    foreach (KeyValuePair<string, object> pair in atkIdentities)
                    {
                        Dictionary<string, object> item = pair.Value as Dictionary<string, object>;
                        if (item == null)
                        {
                            continue;
                        }
                        AtkIdentityRecord record = new AtkIdentityRecord();
                        record.Cid = GetInt(item, "cid", 0);
                        record.Mid = GetInt(item, "mid", 0);
                        record.ModelName = GetString(item, "model", "");
                        record.UniqueId = GetString(item, "uniqueId", "");
                        Current.AtkIdentities[pair.Key] = record;
                    }
                }

                Dictionary<string, object> warnings = GetDict(devices, "warnings");
                if (warnings != null)
                {
                    foreach (KeyValuePair<string, object> pair in warnings)
                    {
                        Dictionary<string, object> warning = pair.Value as Dictionary<string, object>;
                        if (warning == null)
                        {
                            continue;
                        }
                        WarnState state = new WarnState();
                        state.Levels = NormalizeLevels(GetIntList(warning, "levels"));
                        state.LastPercent = GetInt(warning, "lastPercent", -1);
                        state.Timestamp = ParseDate(GetString(warning, "timestamp", ""));
                        Current.WarnStates[pair.Key] = state;
                    }
                }
            }
        }

        private static Dictionary<string, object> BuildRoot()
        {
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["schemaVersion"] = Current.SchemaVersion;
            root["refreshMinutes"] = Current.RefreshMinutes;
            root["pauseWhenIdle"] = Current.PauseWhenIdle;
            root["lowBatteryWarning"] = Current.LowBatteryWarning;
            root["warnLevels"] = Current.WarnLevels.ToArray();
            root["dndEnabled"] = Current.DndEnabled;
            root["dndStart"] = Current.DndStart;
            root["dndEnd"] = Current.DndEnd;
            root["suppressFullscreen"] = Current.SuppressFullscreen;
            root["displayMode"] = Current.DisplayMode;
            root["themeMode"] = NormalizeThemeMode(Current.ThemeMode);
            root["iconDevice"] = Current.IconDevice;
            root["trayDeviceId"] = Current.TrayDeviceId;
            root["digitPercent"] = Current.DigitPercent;
            root["iconStyle"] = NormalizeIconStyle(Current.IconStyle);
            root["iconFont"] = NormalizeIconFont(Current.IconFont);
            root["autoColor"] = Current.AutoColor;
            root["customColorArgb"] = Current.CustomColorArgb;
            root["showVoltage"] = Current.ShowVoltage;
            root["historyEnabled"] = Current.HistoryEnabled;
            root["portableMode"] = IsPortable;
            root["firstRunHintShown"] = Current.FirstRunHintShown;
            root["startWithPanel"] = Current.StartWithPanel;
            root["panelPositionSaved"] = Current.PanelPositionSaved;
            root["panelX"] = Current.PanelX;
            root["panelY"] = Current.PanelY;
            root["settingsWidth"] = Current.SettingsWidth;
            root["settingsHeight"] = Current.SettingsHeight;
            root["detailedDiagnostics"] = Current.DetailedDiagnostics;

            Dictionary<string, object> devices = new Dictionary<string, object>();
            devices["names"] = Current.DeviceNames;
            devices["order"] = Current.DeviceOrder;
            devices["alertThresholds"] = Current.DeviceAlertThresholds;
            Dictionary<string, object> forceVisible = new Dictionary<string, object>();
            foreach (KeyValuePair<string, bool> pair in Current.DeviceForceVisible)
            {
                if (pair.Value)
                {
                    forceVisible[pair.Key] = true;
                }
            }
            devices["forceVisible"] = forceVisible;

            Dictionary<string, object> known = new Dictionary<string, object>();
            foreach (KeyValuePair<string, DeviceRecord> pair in Current.DeviceRecords)
            {
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["name"] = pair.Value.Name;
                item["category"] = pair.Value.Category;
                item["transport"] = pair.Value.Transport;
                item["lastSeen"] = pair.Value.LastSeen;
                known[pair.Key] = item;
            }
            devices["known"] = known;

            Dictionary<string, object> atkIdentities = new Dictionary<string, object>();
            if (Current.AtkIdentities != null)
            {
                foreach (KeyValuePair<string, AtkIdentityRecord> pair in Current.AtkIdentities)
                {
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    item["cid"] = pair.Value.Cid;
                    item["mid"] = pair.Value.Mid;
                    item["model"] = pair.Value.ModelName;
                    item["uniqueId"] = pair.Value.UniqueId;
                    atkIdentities[pair.Key] = item;
                }
            }
            devices["atkIdentities"] = atkIdentities;

            Dictionary<string, object> warnings = new Dictionary<string, object>();
            foreach (KeyValuePair<string, WarnState> pair in Current.WarnStates)
            {
                Dictionary<string, object> warning = new Dictionary<string, object>();
                warning["levels"] = pair.Value.Levels.ToArray();
                warning["lastPercent"] = pair.Value.LastPercent;
                warning["timestamp"] = pair.Value.Timestamp.ToString("o", CultureInfo.InvariantCulture);
                warnings[pair.Key] = warning;
            }
            devices["warnings"] = warnings;
            root["devices"] = devices;
            return root;
        }

        private static int NormalizeRefresh(int minutes)
        {
            if (minutes < 1)
            {
                return 10;
            }
            return Clamp(minutes, 1, 1440);
        }

        private static List<int> NormalizeLevels(List<int> levels)
        {
            List<int> result = new List<int>();
            if (levels != null)
            {
                for (int i = 0; i < levels.Count; i++)
                {
                    int value = levels[i];
                    if (value > 0 && value <= 100 && !result.Contains(value))
                    {
                        result.Add(value);
                    }
                }
            }
            if (result.Count == 0)
            {
                result.AddRange(new int[] { 20, 15, 10, 5 });
            }
            result.Sort();
            result.Reverse();
            return result;
        }

        private static string NormalizeDisplayMode(string value)
        {
            return string.Equals(value, "separate", StringComparison.OrdinalIgnoreCase) ? "separate" : "aggregate";
        }

        private static string NormalizeThemeMode(string value)
        {
            if (string.Equals(value, "light", StringComparison.OrdinalIgnoreCase)) return "light";
            if (string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase)) return "dark";
            return "system";
        }

        private static string NormalizeIconStyle(string value)
        {
            if (string.Equals(value, "classic", StringComparison.OrdinalIgnoreCase)) return "classic";
            if (string.Equals(value, "rounded", StringComparison.OrdinalIgnoreCase)) return "rounded";
            return "soft";
        }

        private static readonly string[] IconFontKeys = new string[]
        {
            "auto", "black", "semibold", "bold", "yahei", "verdana",
            "tahoma", "arialblack", "bahnschrift", "impact"
        };

        private static string NormalizeIconFont(string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                string trimmed = value.Trim();
                for (int i = 0; i < IconFontKeys.Length; i++)
                {
                    if (string.Equals(trimmed, IconFontKeys[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return IconFontKeys[i];
                    }
                }
            }
            return "auto";
        }

        /// <summary>设置页「图标字体」下拉框的选项文字，顺序与键值一一对应。</summary>
        internal static readonly string[] IconFontLabels = new string[]
        {
            "跟随样式", "粗黑（Segoe UI Black）", "半粗（Segoe UI Semibold）", "常规加粗",
            "雅黑加粗", "宽体（Verdana）", "Tahoma", "厚重（Arial Black）",
            "硬朗（Bahnschrift）", "极粗窄（Impact）",
        };

        internal static int IconFontIndex(string value)
        {
            string key = NormalizeIconFont(value);
            for (int i = 0; i < IconFontKeys.Length; i++)
            {
                if (string.Equals(key, IconFontKeys[i], StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return 0;
        }

        internal static string IconFontKey(int index)
        {
            if (index < 0 || index >= IconFontKeys.Length) { return "auto"; }
            return IconFontKeys[index];
        }

        private static string NormalizeIconDevice(string value)
        {
            if (string.Equals(value, "mouse", StringComparison.OrdinalIgnoreCase)) return "mouse";
            if (string.Equals(value, "keyboard", StringComparison.OrdinalIgnoreCase)) return "keyboard";
            if (string.Equals(value, "headset", StringComparison.OrdinalIgnoreCase)) return "headset";
            return "auto";
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static bool GetBool(Dictionary<string, object> dict, string key, bool fallback)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value))
            {
                return ToBool(value, fallback);
            }
            return fallback;
        }

        private static bool ToBool(object value, bool fallback)
        {
            if (value == null) return fallback;
            try
            {
                if (value is bool) return (bool)value;
                return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static int GetInt(Dictionary<string, object> dict, string key, int fallback)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value))
            {
                return ToInt(value, fallback);
            }
            return fallback;
        }

        private static int ToInt(object value, int fallback)
        {
            if (value == null) return fallback;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static string GetString(Dictionary<string, object> dict, string key, string fallback)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value) && value != null)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            return fallback;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value))
            {
                return value as Dictionary<string, object>;
            }
            return null;
        }

        private static List<int> GetIntList(Dictionary<string, object> dict, string key)
        {
            List<int> result = new List<int>();
            object value;
            if (dict == null || !dict.TryGetValue(key, out value) || value == null)
            {
                return result;
            }
            object[] array = value as object[];
            if (array != null)
            {
                for (int i = 0; i < array.Length; i++)
                {
                    result.Add(ToInt(array[i], 0));
                }
            }
            return result;
        }

        private static DateTime ParseDate(string value)
        {
            DateTime parsed;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed;
            }
            return DateTime.MinValue;
        }
    }
}
