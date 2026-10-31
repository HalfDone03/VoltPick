using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using DeviceBattery;

internal static class HistorySmoke
{
    private static object Field(object instance, string name)
    {
        return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
    }

    private static void Pump(int milliseconds)
    {
        DateTime until = DateTime.Now.AddMilliseconds(milliseconds);
        while (DateTime.Now < until)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(30);
        }
    }

    private static Button FindButton(Control parent, string text)
    {
        if (parent == null)
        {
            return null;
        }
        for (int i = 0; i < parent.Controls.Count; i++)
        {
            Control control = parent.Controls[i];
            Button button = control as Button;
            if (button != null && button.Text == text)
            {
                return button;
            }
            button = FindButton(control, text);
            if (button != null)
            {
                return button;
            }
        }
        return null;
    }

    /// <summary>冒烟预览图目录（临时目录，测试结束后一并清理）。</summary>
    private static string PreviewDirectory;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string originalOverride = AppSettings.ConfigDirectoryOverride;
        string tempDirectory = Path.Combine(Path.GetTempPath(),
            "VoltPick.HistorySmoke." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PreviewDirectory = Path.Combine(tempDirectory, "preview");
        Directory.CreateDirectory(PreviewDirectory);
        AppSettings.ConfigDirectoryOverride = tempDirectory;
        try
        {
            AppSettings.Current.HistoryEnabled = true;
            BatteryHistory.ResetForTests();

            DeviceState device = new DeviceState();
            device.Id = "smoke:history";
            device.DisplayName = "ATK A9 Ultra Max 2.0 Tri-Mode";
            device.Category = DeviceCategory.Mouse;
            device.LastReading = new BatteryReading();
            DateTime now = DateTime.Now;
            device.LastReading.Percent = 80;
            device.LastReading.Charging = false;
            BatteryHistory.Record(device, now.AddHours(-5));
            device.LastReading.Percent = 64;
            BatteryHistory.Record(device, now.AddHours(-3));
            device.LastReading.Percent = 51;
            BatteryHistory.Record(device, now.AddHours(-1));

            HistoryForm form = new HistoryForm("smoke:history");
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-2000, -2000);
            form.Show();
            Pump(350);

            ComboBox devices = (ComboBox)Field(form, "_devices");
            ComboBox range = (ComboBox)Field(form, "_range");
            HistoryChart chart = (HistoryChart)Field(form, "_chart");
            Label summary = (Label)Field(form, "_summary");

            bool ok = devices.Items.Count == 1 && chart.Series != null &&
                      chart.Series.Samples.Count == 3 &&
                      summary.Text.IndexOf("最低", StringComparison.Ordinal) >= 0;
            range.SelectedIndex = 1;
            Pump(150);
            ok = ok && chart.Series != null && summary.Text.IndexOf("最低", StringComparison.Ordinal) >= 0;

            // 悬停命中：绘图区内的点应能映射到最近的采样；绘图区外应落空。
            range.SelectedIndex = 0;
            Pump(200);
            Rectangle plot = new Rectangle(48, 16, Math.Max(1, chart.Width - 66), Math.Max(1, chart.Height - 52));
            int hitIndex;
            DateTime hitTime;
            int hitPercent;
            bool hitCharging;
            bool insideHit = chart.TryHitTest(
                new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2),
                out hitIndex, out hitTime, out hitPercent, out hitCharging);
            bool outsideMiss = !chart.TryHitTest(new Point(2, 2),
                out hitIndex, out hitTime, out hitPercent, out hitCharging);
            bool belowMiss = !chart.TryHitTest(new Point(plot.Left + 4, chart.Height - 2),
                out hitIndex, out hitTime, out hitPercent, out hitCharging);
            Console.WriteLine("hover: inside=" + insideHit + " outside=" + outsideMiss +
                              " below=" + belowMiss);
            ok = ok && insideHit && outsideMiss && belowMiss;

            // 让提示框真正绘制出来，抓一张预览图确认视觉正常。
            Point hoverPoint = new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2);
            MethodInfo mouseMove = typeof(HistoryChart).GetMethod("OnMouseMove",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (mouseMove != null)
            {
                mouseMove.Invoke(chart, new object[]
                {
                    new MouseEventArgs(MouseButtons.None, 0, hoverPoint.X, hoverPoint.Y, 0)
                });
                Pump(120);
                // 预览只用于人工核对视觉效果，写到临时目录，不污染源码目录。
                using (Bitmap hoverPreview = new Bitmap(chart.Width, chart.Height))
                {
                    chart.DrawToBitmap(hoverPreview, new Rectangle(0, 0, chart.Width, chart.Height));
                    hoverPreview.Save(Path.Combine(PreviewDirectory, "history-hover.png"));
                }
            }
            range.SelectedIndex = 1;
            Pump(150);

            using (Bitmap preview = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(preview, new Rectangle(0, 0, form.Width, form.Height));
                preview.Save(Path.Combine(PreviewDirectory, "history-preview.png"));
            }

            string summaryText = summary.Text;
            int sampleCount = chart.Series == null ? 0 : chart.Series.Samples.Count;
            int deviceCount = devices.Items.Count;

            string clearMessage = HistoryForm.BuildClearConfirmation(deviceCount, sampleCount);
            bool clearTextOk = clearMessage.IndexOf(sampleCount.ToString(), StringComparison.Ordinal) >= 0 &&
                               clearMessage.IndexOf("不可撤销", StringComparison.Ordinal) >= 0 &&
                               clearMessage.IndexOf(".bak", StringComparison.Ordinal) >= 0;
            ok = ok && clearTextOk;

            Button closeButton = FindButton(form, "关闭");
            if (closeButton == null)
            {
                Console.WriteLine("history smoke failed: 关闭 button not found");
                form.Close();
                form.Dispose();
                return 1;
            }
            closeButton.PerformClick();
            Pump(150);
            ok = ok && !form.Visible;

            form.Dispose();
            Console.WriteLine("history smoke: devices=" + deviceCount +
                              " samples=" + sampleCount +
                              " closed=" + !form.Visible +
                              " clearText=" + clearTextOk +
                              " summary=" + summaryText);
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("history smoke failed: " + ex);
            return 1;
        }
        finally
        {
            AppSettings.ConfigDirectoryOverride = originalOverride;
            BatteryHistory.ResetForTests();
            try { Directory.Delete(tempDirectory, true); } catch (Exception) { }
        }
    }
}
