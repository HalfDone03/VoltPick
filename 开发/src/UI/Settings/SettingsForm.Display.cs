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
        /// 把三个下拉框的当前取值喂给预览：样式、字体、数字大小任一变化都立刻重画，
        /// 不用先点保存再回托盘确认。
        /// </summary>
        private void UpdateIconPreview()
        {
            if (_iconPreview == null)
            {
                return;
            }

            _iconPreview.SetState(IconStyleValue(), AppSettings.IconFontKey(_iconFont.SelectedIndex),
                DigitValue(), false);
        }

        private void BuildDisplayPage()
        {
            TabPage page = _tabs.TabPages[1];

            // 托盘图标和设备面板原来分成两张卡片，其中「设备面板」只有一行复选框，
            // 却要和托盘一样占一个卡片标题加一段卡片间距，合成一张卡省掉这两处空白。
            SettingsSection tray = new SettingsSection("托盘与面板");
            _displayMode = NewCombo(new string[] { "所有设备合计", "只显示最低电量", "单台设备" });
            _iconDevice = NewCombo(new string[] { "自动选择", "鼠标", "键盘", "耳机" });
            _digitSize = NewCombo(new string[] { "不显示数字（留点）", "较小", "标准", "较大" });
            _iconStyle = NewCombo(new string[] { "柔和（细窄）", "经典（高大）", "圆润（扁矮）" });
            _iconFont = NewCombo(AppSettings.IconFontLabels);
            _iconPreview = new IconStylePreview();
            _iconStyle.SelectedIndexChanged += delegate { UpdateIconPreview(); };
            _digitSize.SelectedIndexChanged += delegate { UpdateIconPreview(); };
            _iconFont.SelectedIndexChanged += delegate { UpdateIconPreview(); };
            _autoColor = NewCheck("按电量自动配色");
            _autoColor.CheckedChanged += delegate { UpdateColorButton(); };
            _colorButton = NewButton("选择颜色…", false);
            _colorButton.Click += delegate { PickColor(); };
            _showVoltage = NewCheck("在设备卡片里显示电压");
            tray.AddRow(NewLabel("显示内容", 96), _displayMode);
            tray.AddRow(NewLabel("图标设备", 96), _iconDevice);
            tray.AddRow(NewLabel("数字大小", 96), _digitSize);
            tray.AddRow(NewLabel("图标样式", 96), _iconStyle);
            tray.AddRow(NewLabel("数字字体", 96), _iconFont);
            tray.AddRow(NewLabel("效果预览", 96), _iconPreview);
            tray.AddRow(_autoColor);
            tray.AddRow(NewLabel("固定颜色", 96), _colorButton);
            tray.AddRow(_showVoltage);
            _display.Add(tray);

            UpdateIconPreview();
            AddSections(page, _display);
        }    }
}