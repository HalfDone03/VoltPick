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

        // ---------------- 取值映射 ----------------

        private static int ThemeModeIndex(string mode)
        {
            if (string.Equals(mode, "light", StringComparison.Ordinal)) { return 1; }
            if (string.Equals(mode, "dark", StringComparison.Ordinal)) { return 2; }
            return 0;
        }

        private string ThemeModeValue()
        {
            return _theme.SelectedIndex == 1 ? "light" : (_theme.SelectedIndex == 2 ? "dark" : "system");
        }

        private static int DisplayModeIndex(string mode)
        {
            if (string.Equals(mode, "min", StringComparison.Ordinal)) { return 1; }
            if (string.Equals(mode, "single", StringComparison.Ordinal)) { return 2; }
            return 0;
        }

        private string DisplayModeValue()
        {
            return _displayMode.SelectedIndex == 1 ? "min" : (_displayMode.SelectedIndex == 2 ? "single" : "aggregate");
        }

        private static int IconDeviceIndex(string value)
        {
            if (string.Equals(value, "mouse", StringComparison.Ordinal)) { return 1; }
            if (string.Equals(value, "keyboard", StringComparison.Ordinal)) { return 2; }
            if (string.Equals(value, "headset", StringComparison.Ordinal)) { return 3; }
            return 0;
        }

        private string IconDeviceValue()
        {
            switch (_iconDevice.SelectedIndex)
            {
                case 1: return "mouse";
                case 2: return "keyboard";
                case 3: return "headset";
                default: return "auto";
            }
        }

        private static int DigitIndex(int percent)
        {
            if (percent <= 0) { return 0; }
            if (percent < 75) { return 1; }
            if (percent <= 85) { return 2; }
            return 3;
        }

        private int DigitValue()
        {
            switch (_digitSize.SelectedIndex)
            {
                case 0: return 0;
                case 1: return 66;
                case 3: return 100;
                default: return 80;
            }
        }

        private static int IconStyleIndex(string style)
        {
            if (string.Equals(style, "classic", StringComparison.Ordinal)) { return 1; }
            if (string.Equals(style, "rounded", StringComparison.Ordinal)) { return 2; }
            return 0;
        }

        private string IconStyleValue()
        {
            if (_iconStyle.SelectedIndex == 1) { return "classic"; }
            if (_iconStyle.SelectedIndex == 2) { return "rounded"; }
            return "soft";
        }

        private static readonly int[] IntervalValues = new int[] { 1, 2, 5, 10, 30, 60, 120, 240, 480, 720, 1440 };

        private static int IntervalIndex(int minutes)
        {
            // 取最接近的一档：settings.json 里手工写成 90 分钟这种值时，
            // 也不会显示成完全不相关的那一档。
            int best = 3;
            int bestDelta = int.MaxValue;
            for (int i = 0; i < IntervalValues.Length; i++)
            {
                int delta = Math.Abs(IntervalValues[i] - minutes);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = i;
                }
            }
            return best;
        }

        private int IntervalValue()
        {
            int index = _interval.SelectedIndex;
            if (index < 0 || index >= IntervalValues.Length)
            {
                return 10;
            }
            return IntervalValues[index];
        }

        private static readonly int[] ThresholdValues = new int[] { 0, 30, 25, 20, 15, 10, 5 };

        private static int ThresholdIndex(int threshold)
        {
            for (int i = 1; i < ThresholdValues.Length; i++)
            {
                if (ThresholdValues[i] == threshold) { return i; }
            }
            return 0;
        }

        private static int ThresholdValue(int index)
        {
            if (index < 0 || index >= ThresholdValues.Length)
            {
                return 0;
            }
            return ThresholdValues[index];
        }
    }
}
