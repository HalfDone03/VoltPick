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
        private void BuildNotifyPage()
        {
            TabPage page = _tabs.TabPages[2];

            SettingsSection low = new SettingsSection("低电量提醒");
            _lowWarn = NewCheck("低于阈值时弹通知");
            low.AddRow(_lowWarn);
            low.AddRow(NewNote("默认在 20%、15%、10%、5% 各提醒一次，同一档位不会重复打扰。"));
            _notify.Add(low);

            SettingsSection dnd = new SettingsSection("免打扰时段");
            _dnd = NewCheck("这段时间内不弹通知");
            _dndStart = NewText("23:00");
            _dndStart.Width = UiScale.Px(this, 72);
            _dndEnd = NewText("07:00");
            _dndEnd.Width = UiScale.Px(this, 72);
            Panel range = new Panel();
            range.BackColor = Theme.Card;
            // 高度跟着输入框的实际高度走：只给 26 时，字体放大后文本框下沿会被卡片裁掉。
            range.Height = Math.Max(UiScale.Px(this, 26), _dndStart.PreferredHeight);
            range.Width = UiScale.Px(this, 220);
            _dndStart.Location = new Point(0, 0);
            Label to = NewLabel("至", 24);
            to.Location = new Point(UiScale.Px(this, 78), Math.Max(0, (range.Height - to.Height) / 2));
            _dndEnd.Location = new Point(UiScale.Px(this, 104), 0);
            range.Controls.Add(_dndStart);
            range.Controls.Add(to);
            range.Controls.Add(_dndEnd);
            dnd.AddRow(_dnd);
            // 格式写进标签里，省掉下面那句整行说明。
            dnd.AddRow(NewLabel("时段（时:分）", 96), range);
            _fullscreen = NewCheck("全屏时也不弹提醒");
            dnd.AddRow(_fullscreen);
            _notify.Add(dnd);

            AddSections(page, _notify);
        }    }
}