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

        private void BuildRefreshPage()
        {
            TabPage page = _tabs.TabPages[2];

            SettingsSection auto = new SettingsSection("自动查询");
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
            _refresh.Add(auto);

            SettingsSection rule = new SettingsSection("读取规则");
            _readRule = NewNote("鼠标、键盘走 HID 上报，耳机走蓝牙 GATT 或 2.4G 接收器。"
                + "读数需要设备处于唤醒状态；刚开机后的前几次读数可能偏高，稳定后才是真实电量。");
            rule.AddRow(_readRule);
            _refresh.Add(rule);

            AddSections(page, _refresh);
        }
    }
}
