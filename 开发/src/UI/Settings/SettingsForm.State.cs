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
        // ---------------- 读取 / 写回 ----------------

        private void LoadValues()
        {
            _loading = true;
            try
            {
                SettingsData current = AppSettings.Current;
                _autoStart.Checked = AutoStart.IsEnabled();
                _startWithPanel.Checked = current.StartWithPanel;
                _history.Checked = current.HistoryEnabled;

                _theme.SelectedIndex = ThemeModeIndex(current.ThemeMode);

                _displayMode.SelectedIndex = DisplayModeIndex(current.DisplayMode);
                _iconDevice.SelectedIndex = IconDeviceIndex(current.IconDevice);
                _digitSize.SelectedIndex = DigitIndex(current.DigitPercent);
                _iconStyle.SelectedIndex = IconStyleIndex(current.IconStyle);
                _iconFont.SelectedIndex = AppSettings.IconFontIndex(current.IconFont);
                _autoColor.Checked = current.AutoColor;
                _showVoltage.Checked = current.ShowVoltage;
                UpdateColorButton();

                _interval.SelectedIndex = IntervalIndex(current.RefreshMinutes);
                _pauseIdle.Checked = current.PauseWhenIdle;
                UpdateNextRead();

                _lowWarn.Checked = current.LowBatteryWarning;
                _dnd.Checked = current.DndEnabled;
                _dndStart.Text = current.DndStart;
                _dndEnd.Text = current.DndEnd;
                _fullscreen.Checked = current.SuppressFullscreen;

                FillDeviceList();
            }
            finally
            {
                _loading = false;
            }
        }

        private void ApplySettings()
        {
            SettingsData current = AppSettings.Current;
            bool intervalChanged = current.RefreshMinutes != IntervalValue();

            current.StartWithPanel = _startWithPanel.Checked;
            current.HistoryEnabled = _history.Checked;
            current.ThemeMode = ThemeModeValue();
            current.DisplayMode = DisplayModeValue();
            current.IconDevice = IconDeviceValue();
            current.DigitPercent = DigitValue();
            current.IconStyle = IconStyleValue();
            current.IconFont = AppSettings.IconFontKey(_iconFont.SelectedIndex);
            current.AutoColor = _autoColor.Checked;
            current.ShowVoltage = _showVoltage.Checked;
            current.RefreshMinutes = IntervalValue();
            current.PauseWhenIdle = _pauseIdle.Checked;
            current.LowBatteryWarning = _lowWarn.Checked;
            current.DndEnabled = _dnd.Checked;
            current.SuppressFullscreen = _fullscreen.Checked;

            string startText;
            string endText;
            if (TryNormalizeDnd(_dndStart.Text, out startText)) { current.DndStart = startText; }
            if (TryNormalizeDnd(_dndEnd.Text, out endText)) { current.DndEnd = endText; }

            string autoStartError;
            if (!AutoStart.SetEnabled(_autoStart.Checked, out autoStartError) &&
                !string.IsNullOrEmpty(autoStartError))
            {
                MessageBox.Show(this, "设置开机自启动失败。\n\n" + autoStartError, "VoltPick",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            try { AppSettings.Save(); } catch (Exception) { }
            MarkClean();

            NeedsImmediateRefresh = intervalChanged;
            EventHandler handler = Applied;
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); } catch (Exception) { }
            }
        }

        private void UpdateColorButton()
        {
            if (_colorButton == null)
            {
                return;
            }
            _colorButton.Enabled = !_autoColor.Checked;
            try
            {
                _colorButton.Swatch = _autoColor.Checked
                    ? Color.Empty
                    : Color.FromArgb(AppSettings.Current.CustomColorArgb);
            }
            catch (Exception)
            {
                _colorButton.Swatch = Color.Empty;
            }
        }

        private void PickColor()
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.FullOpen = true;
                dialog.Color = Color.FromArgb(AppSettings.Current.CustomColorArgb);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    AppSettings.Current.CustomColorArgb = dialog.Color.ToArgb();
                    UpdateColorButton();
                    ApplySettings();
                }
            }
        }

        private void UpdateNextRead()
        {
            if (_nextRead == null)
            {
                return;
            }
            int minutes = IntervalValue();
            if (minutes <= 0)
            {
                _nextRead.Text = "只在手动刷新时读取。";
                return;
            }
            // 超过一小时按小时显示，免得出现“每 480 分钟读一次”这种不好读的句子。
            string span = minutes >= 60 && minutes % 60 == 0
                ? (minutes / 60) + " 小时"
                : minutes + " 分钟";
            _nextRead.Text = "每 " + span + "读一次；点托盘图标里的「立即刷新」可以马上读。";
        }
    }
}
