using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;


namespace DeviceBattery
{
    internal sealed partial class SettingsForm : Form
    {
        // ---------------- 设备列表 ----------------

        private void FillDeviceList()
        {
            if (_deviceList == null)
            {
                return;
            }
            string keep = _selectedDeviceId;
            _deviceList.BeginUpdate();
            _deviceList.Items.Clear();
            List<KeyValuePair<string, DeviceRecord>> records = AppSettings.DeviceRecordsSnapshot();
            // 排序：现在连着的排前面（能看到电量），然后按 鼠标 → 耳机 → 键盘 → 其他，
            // 同类里再按名字；这样历史设备只会沉到列表底部，不会夹在在用的设备中间。
            records.Sort(delegate(KeyValuePair<string, DeviceRecord> a, KeyValuePair<string, DeviceRecord> b)
            {
                DeviceState la = FindDevice(a.Key);
                DeviceState lb = FindDevice(b.Key);
                int rankA = (la != null && la.HasValue) ? 0 : 1;
                int rankB = (lb != null && lb.HasValue) ? 0 : 1;
                if (rankA != rankB)
                {
                    return rankA - rankB;
                }
                int catA = DeviceSortCategory(a.Value.Category);
                int catB = DeviceSortCategory(b.Value.Category);
                if (catA != catB)
                {
                    return catA - catB;
                }
                string nameA = string.IsNullOrEmpty(a.Value.Name) ? a.Key : a.Value.Name;
                string nameB = string.IsNullOrEmpty(b.Value.Name) ? b.Key : b.Value.Name;
                return string.Compare(nameA, nameB, StringComparison.CurrentCulture);
            });
            for (int i = 0; i < records.Count; i++)
            {
                string id = records[i].Key;
                DeviceRecord record = records[i].Value;
                DeviceState live = FindDevice(id);
                string name = string.IsNullOrEmpty(record.Name) ? id : record.Name;
                ListViewItem item = new ListViewItem(name);
                item.SubItems.Add(string.IsNullOrEmpty(record.Category) ? "未知" : record.Category);
                item.SubItems.Add(live != null && live.HasValue ? live.PercentOrZero + "%" : "未连接");
                item.Tag = id;
                _deviceList.Items.Add(item);
            }
            _deviceList.EndUpdate();
            if (!string.IsNullOrEmpty(keep))
            {
                for (int i = 0; i < _deviceList.Items.Count; i++)
                {
                    if (string.Equals((string)_deviceList.Items[i].Tag, keep, StringComparison.Ordinal))
                    {
                        _deviceList.Items[i].Selected = true;
                        break;
                    }
                }
            }
            if (_deviceList.SelectedItems.Count == 0 && _deviceList.Items.Count > 0)
            {
                _deviceList.Items[0].Selected = true;
            }
            LoadSelectedDevice();
        }

        /// <summary>设备页排序用的类别权重：鼠标 0、耳机 1、键盘 2、其他 3。</summary>
        private static int DeviceSortCategory(string category)
        {
            if (string.Equals(category, "鼠标", StringComparison.Ordinal)) { return 0; }
            if (string.Equals(category, "耳机", StringComparison.Ordinal)) { return 1; }
            if (string.Equals(category, "键盘", StringComparison.Ordinal)) { return 2; }
            return 3;
        }

        private DeviceState FindDevice(string id)
        {
            if (_snapshot == null || _snapshot.Devices == null)
            {
                return null;
            }
            for (int i = 0; i < _snapshot.Devices.Count; i++)
            {
                DeviceState device = _snapshot.Devices[i];
                if (device != null && string.Equals(device.Id, id, StringComparison.Ordinal))
                {
                    return device;
                }
            }
            return null;
        }

        private void LoadSelectedDevice()
        {
            if (_deviceList == null || _loading)
            {
                return;
            }
            if (_deviceList.SelectedItems.Count == 0)
            {
                _selectedDeviceId = "";
                _deviceName.Text = "";
                _deviceThreshold.SelectedIndex = 0;
                _forceVisible.Checked = false;
                return;
            }
            ListViewItem item = _deviceList.SelectedItems[0];
            string id = (string)item.Tag;
            _selectedDeviceId = id;
            DeviceRecord record = AppSettings.GetDeviceRecord(id);
            _deviceName.Text = record == null || string.IsNullOrEmpty(record.Name)
                ? item.Text
                : record.Name;
            int threshold = AppSettings.GetDeviceAlertThreshold(id);
            _deviceThreshold.SelectedIndex = threshold > 0 ? ThresholdIndex(threshold) : 0;
            _forceVisible.Checked = AppSettings.GetDeviceForceVisible(id);
        }

        /// <summary>
        /// 单台设备的「提醒阈值 / 读不到也显示」即时生效：这两个下拉和勾选以前没有挂事件，
        /// 只有点「重命名」才会顺带保存，所以改完看起来完全没反应。
        /// </summary>
        private void SaveDevicePreferences()
        {
            if (_loading || string.IsNullOrEmpty(_selectedDeviceId))
            {
                return;
            }
            int threshold = _deviceThreshold.SelectedIndex <= 0
                ? 0
                : ThresholdValue(_deviceThreshold.SelectedIndex);
            AppSettings.SetDeviceAlertThreshold(_selectedDeviceId, threshold);
            AppSettings.SetDeviceForceVisible(_selectedDeviceId, _forceVisible.Checked);
            try { AppSettings.Save(); } catch (Exception) { }
            MarkDirty();
            Log.Info("设备设置已保存：" + _selectedDeviceId + " 阈值=" + threshold + "% 读不到也显示=" + _forceVisible.Checked);
        }

        private void SaveDeviceName()
        {
            if (string.IsNullOrEmpty(_selectedDeviceId))
            {
                return;
            }
            string name = _deviceName.Text == null ? "" : _deviceName.Text.Trim();
            AppSettings.SetDeviceName(_selectedDeviceId, name, 0);
            int threshold = _deviceThreshold.SelectedIndex <= 0
                ? 0
                : ThresholdValue(_deviceThreshold.SelectedIndex);
            AppSettings.SetDeviceAlertThreshold(_selectedDeviceId, threshold);
            AppSettings.SetDeviceForceVisible(_selectedDeviceId, _forceVisible.Checked);
            try { AppSettings.Save(); } catch (Exception) { }
            FillDeviceList();
        }

        private void RemoveDevice()
        {
            if (string.IsNullOrEmpty(_selectedDeviceId))
            {
                return;
            }
            if (MessageBox.Show(this, "移除这台设备的记录？下次连上会重新识别。", "VoltPick",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            AppSettings.ForgetDevice(_selectedDeviceId);
            try { AppSettings.Save(); } catch (Exception) { }
            _selectedDeviceId = "";
            FillDeviceList();
        }
    }
}
