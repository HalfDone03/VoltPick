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

        /// <summary>
        /// 支持范围并入「关于」页：支持信息是静态说明，没必要单独占一个页签。
        /// 三组内容照旧保留，只是挂到关于页的分组列表里，由统一排版处理。
        /// </summary>
        private void BuildSupportedPage()
        {
            SettingsSection canRead = new SettingsSection("能读到电量的设备");
            canRead.AddRow(NewNote("标准 HID 电池页（0x20 / 0x24）：蓝牙、2.4G、USB 外设的通用通道"));
            canRead.AddRow(NewNote("罗技 HID++ 2.0：Unifying / Bolt 接收器、无线键鼠、G 系列耳机"));
            canRead.AddRow(NewNote("雷蛇：鼠标、键盘、耳机（90 字节 feature 报文）"));
            canRead.AddRow(NewNote("赛睿：Nova / Arctis 耳机，Rival / Aerox 鼠标"));
            canRead.AddRow(NewNote("华硕 ROG / TUF：Gladius / Harpe / Chakram / Keris 等无线鼠标"));
            canRead.AddRow(NewNote("HyperX Cloud II 无线、JBL Quantum 910、Corsair Void 与 Bragi 接收器"));
            canRead.AddRow(NewNote("WLmouse / Lamzu / G-Wolves：0x83 查询族"));
            canRead.AddRow(NewNote("MCHOSE：A5 Pro Max、G7、GLW 键盘；VGN / VXE / ATK Evision 键盘"));
            canRead.AddRow(NewNote("Pulsar / VXE / Hitscan / Scyrox / AULA 8K / Endgame Gear 4K"));
            canRead.AddRow(NewNote("ZOWIE DW 电压档位、Redragon / Areson 接收器"));
            canRead.AddRow(NewNote("Xbox / DualSense 手柄；AirPods / Beats 的 BLE 邻近广播"));
            canRead.AddRow(NewNote("蓝牙 LE：标准电量服务 0x180F；Airoha 系耳机还能取左右耳与充电盒"));
            _supported.Add(canRead);

            SettingsSection cannot = new SettingsSection("还读不到的设备");
            cannot.AddRow(NewNote("只支持经典蓝牙 HFP 的耳机（Windows 不暴露电量）"));
            cannot.AddRow(NewNote("有线键鼠：靠 USB 供电，没有可读电量"));
            cannot.AddRow(NewNote("不暴露厂商电量页的 2.4G 接收器"));
            cannot.AddRow(NewNote("Keychron 等尚未实现的私有协议"));
            _supported.Add(cannot);

            SettingsSection rules = new SettingsSection("显示规则");
            rules.AddRow(NewNote("读不到电量的设备默认不显示；需要时可以到「设备」页把记录留成占位或移除。"));
            rules.AddRow(NewNote("同一台设备的多个接口会合并成一张卡片，不会重复出现。"));
            _supported.Add(rules);

            // 支持分组并入关于页；关于页现在自带滚动容器，内容超高时统一滚动。
            _about.AddRange(_supported);
        }
    }
}
