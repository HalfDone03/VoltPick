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

        /// <summary>读取设置并入「常规」页：不再单独占一个页签，但控件与行为完全不变。</summary>
        private void BuildRefreshPage()
        {
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
            _readRule = NewNote("读数来自设备自身上报；设备唤醒后才能读到，刚开机时的读数可能偏高。");
            rule.AddRow(_readRule);
            _refresh.Add(rule);

            // 合并到常规页：只把分组加进 _general，页面控件由 BuildGeneralPage 统一挂载，
            // 避免同一分组被 AddSections 两次。
            _general.AddRange(_refresh);
        }
    }
}
