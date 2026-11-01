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
    {        // ---------------- 各页内容 ----------------

        private void BuildGeneralPage()
        {
            TabPage page = _tabs.TabPages[0];

            SettingsSection start = new SettingsSection("启动与运行");
            _autoStart = NewCheck("开机自动启动");
            _startWithPanel = NewCheck("启动时打开设备面板");
            _history = NewCheck("记录电量历史（保留 7 天）");
            start.AddRow(_autoStart);
            start.AddRow(_startWithPanel);
            start.AddRow(_history);
            _general.Add(start);

            // 原来「读取」是单独一页，只有两行控件加一段说明，翻页成本比内容还大，合并到这一页。
            SettingsSection auto = new SettingsSection("查询节奏");
            _interval = NewCombo(new string[]
            {
                "1 分钟", "2 分钟", "5 分钟", "10 分钟", "30 分钟",
                "1 小时", "2 小时", "4 小时", "8 小时", "12 小时", "24 小时"
            });
            _interval.SelectedIndexChanged += delegate { OnFieldChanged(); };
            _pauseIdle = NewCheck("息屏或空闲时暂停查询");
            _nextRead = NewNote("");
            auto.AddRow(NewLabel("查询间隔", 96), _interval);
            auto.AddRow(_pauseIdle);
            auto.AddRow(_nextRead);
            _general.Add(auto);

            SettingsSection look = new SettingsSection("外观");
            _theme = NewCombo(new string[] { "跟随系统", "浅色", "深色" });
            look.AddRow(NewLabel("主题", 96), _theme);
            _general.Add(look);

            AddSections(page, _general);
        }    }
}