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

        private void BuildDevicePage()
        {
            TabPage page = _tabs.TabPages[4];

            SettingsSection list = new SettingsSection("已识别的设备");
            _deviceList = new ListView();
            _deviceList.View = View.Details;
            _deviceList.FullRowSelect = true;
            _deviceList.MultiSelect = false;
            _deviceList.HideSelection = false;
            _deviceList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _deviceList.BackColor = Theme.List;
            _deviceList.ForeColor = Theme.Text;
            _deviceList.BorderStyle = BorderStyle.FixedSingle;
            // 列表高度不是写死的：排版时先把高度复原成这个基准值再量内容，
            // 页面剩下的高度会补给列表（见 LayoutOnePage），窗口拉高时列表跟着变高，
            // 窗口按基准高度定高，所以不会出现「列表变高 → 窗口变高 → 列表再变高」的追涨。
            // 基准高度按「至少能看到 8 条记录」给：原来 196 只够 6 条，记录一少整页下半截就空着。
            _deviceListBaseHeight = UiScale.Px(this, 268);
            _deviceList.Height = _deviceListBaseHeight;
            _deviceList.Columns.Add("设备", UiScale.Px(this, 250));
            _deviceList.Columns.Add("类型", UiScale.Px(this, 80));
            _deviceList.Columns.Add("状态", UiScale.Px(this, 96));
            _deviceList.SelectedIndexChanged += delegate { LoadSelectedDevice(); };
            list.AddRow(_deviceList);

            _deviceName = NewText("");
            _renameButton = NewButton("保存名称", false);
            _renameButton.Click += delegate { SaveDeviceName(); };
            list.AddRow(NewLabel("名称", 96), _deviceName, _renameButton);

            _deviceThreshold = NewCombo(new string[] { "跟随全局", "30%", "25%", "20%", "15%", "10%", "5%" });
            _deviceThreshold.SelectedIndexChanged += delegate { SaveDevicePreferences(); };
            _forceVisible = NewCheck("读不到电量也显示（强制）");
            _forceVisible.CheckedChanged += delegate { SaveDevicePreferences(); };

            _removeButton = NewButton("移除记录", false);
            _removeButton.Click += delegate { RemoveDevice(); };
            list.AddRow(NewLabel("提醒阈值", 96), _deviceThreshold, _removeButton);
            list.AddRow(_forceVisible);
            _devices.Add(list);

            SettingsSection me = new SettingsSection("这台设备");
            _deviceNote = NewNote("在列表里选中一台设备后可以改名、单独设阈值、读不到电量时也保留显示，或直接移除记录。");
            me.AddRow(_deviceNote);
            _devices.Add(me);

            AddSections(page, _devices);
        }
    }
}
