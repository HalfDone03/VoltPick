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
        // ---------------- 各页内容 ----------------

        private void BuildGeneralPage()
        {
            TabPage page = _tabs.TabPages[0];

            SettingsSection start = new SettingsSection("启动与运行");
            _autoStart = NewCheck("开机自动启动");
            _startWithPanel = NewCheck("启动时打开设备面板");
            _history = NewCheck("记录电量历史（保留 7 天，整点采样）");
            start.AddRow(_autoStart);
            start.AddRow(_startWithPanel);
            start.AddRow(_history);
            _general.Add(start);

            SettingsSection look = new SettingsSection("外观");
            _theme = NewCombo(new string[] { "跟随系统", "浅色", "深色" });
            look.AddRow(NewLabel("主题", 96), _theme);
            _configPath = NewPath();
            look.AddRow(_configPath);
            _general.Add(look);

            AddSections(page, _general);
        }
    }
}
