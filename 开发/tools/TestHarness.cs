using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using DeviceBattery;

internal static class TestHarness
{
    // AGENTS.md 要求自测零失败。只判 _failed 的话，删掉一条测试也会"通过"，
    // 所以把期望条数也钉死：新增/删除断言后必须同步改这里。
    // 305（基线）+ 23（键盘）+ 38（耳机）+ 23（鼠标）+ 10（手柄）+ 7（AirPods）
    //   + 19（历史统计）+ 5（新增设置项）+ 31（任务栏显示含托盘色）+ 8（托盘类别图标）= 469。
    private const int ExpectedChecks = 469;

    private static int _passed;
    private static int _failed;

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  PASS  " + name);
        }
        else
        {
            _failed++;
            Console.WriteLine("  FAIL  " + name + "  -> " + detail);
        }
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static DeviceState Device(int percent, int unchanged, int fail, bool charging)
    {
        DeviceState device = new DeviceState();
        device.Id = "test";
        device.DisplayName = "Test";
        device.LastReading = new BatteryReading();
        device.LastReading.Percent = percent;
        device.LastReading.Charging = charging;
        device.LastReading.Timestamp = DateTime.Now;
        device.FailCount = fail;
        return device;
    }

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 测试会故意写坏配置来验证回退逻辑；这些日志不能落到用户的真实日志里。
        string realLogDirectory = Log.DirectoryOverride;
        string testLogDirectory = Path.Combine(Path.GetTempPath(),
            "VoltPick.TestLogs." + Guid.NewGuid().ToString("N"));
        Log.DirectoryOverride = testLogDirectory;

        try
        {
            return RunAll();
        }
        finally
        {
            Log.DirectoryOverride = realLogDirectory;
            try
            {
                if (Directory.Exists(testLogDirectory))
                {
                    Directory.Delete(testLogDirectory, true);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private static int RunAll()
    {
        Console.WriteLine("=== ComputeInterval ===");

        int normal = BatteryService.ComputeInterval(Device(80, 0, 0, false), 10);
        Check("80% -> 10 分钟固定间隔", normal == 600, normal + " s");

        int low = BatteryService.ComputeInterval(Device(12, 0, 0, false), 10);
        Check("低电量不额外加速", low == 600, low + " s");

        int charging = BatteryService.ComputeInterval(Device(100, 0, 0, true), 10);
        Check("充满仍按 10 分钟", charging == 600, charging + " s");

        int oneHour = BatteryService.ComputeInterval(Device(80, 0, 0, false), 60);
        Check("60 分钟配置 -> 1 小时", oneHour == 3600, oneHour + " s");

        int twoHours = BatteryService.ComputeInterval(Device(80, 0, 0, false), 120);
        Check("120 分钟配置不再钳到 60 分钟", twoHours == 7200, twoHours + " s");

        int day = BatteryService.ComputeInterval(Device(80, 0, 0, false), 1440);
        Check("24 小时配置 -> 86400 秒", day == 86400, day + " s");

        int overflow = BatteryService.ComputeInterval(Device(80, 0, 0, false), 2000);
        Check("超过 24 小时钳到 24 小时", overflow == 86400, overflow + " s");

        int noAnswer1 = BatteryService.ComputeInterval(null, 10);
        Check("无设备 -> 60 秒", noAnswer1 == 60, noAnswer1 + " s");

        int fail0 = BatteryService.ComputeInterval(new DeviceState { FailCount = 0 }, 10);
        Check("失败 0 次 -> 60 秒", fail0 == 60, fail0 + " s");

        int fail2 = BatteryService.ComputeInterval(new DeviceState { FailCount = 2 }, 10);
        Check("失败 2 次 -> 240 秒", fail2 == 240, fail2 + " s");

        DeviceState staleReading = Device(50, 0, 3, false);
        staleReading.FailCount = 3;
        int staleFail = BatteryService.ComputeFailureInterval(staleReading.FailCount, 10);
        Check("保留旧读数时按失败退避", staleFail == 480, staleFail + " s");

        int failDirect = BatteryService.ComputeFailureInterval(4, 1440);
        Check("失败退避独立于 24 小时间隔", failDirect == 900, failDirect + " s");

        int fail9 = BatteryService.ComputeInterval(new DeviceState { FailCount = 9 }, 60);
        Check("失败退避封顶 15 分钟", fail9 == 900, fail9 + " s");

        int failBase = BatteryService.ComputeInterval(new DeviceState { FailCount = 9 }, 10);
        Check("失败退避不超过设置间隔", failBase == 600, failBase + " s");

        int tiny = BatteryService.ComputeInterval(Device(80, 0, 0, false), 1);
        Check("1 分钟配置 -> 60 秒", tiny == 60, tiny + " s");

        int tinyFail = BatteryService.ComputeInterval(new DeviceState { FailCount = 9 }, 1);
        Check("1 分钟失败退避 -> 60 秒", tinyFail == 60, tinyFail + " s");

        int savedRefresh = AppSettings.Current.RefreshMinutes;
        AppSettings.Current.RefreshMinutes = 1;
        Check("1 分钟发现间隔 -> 60 秒", BatteryService.DiscoveryIntervalSeconds() == 60,
            BatteryService.DiscoveryIntervalSeconds() + " s");
        AppSettings.Current.RefreshMinutes = 10;
        Check("10 分钟发现间隔 -> 150 秒", BatteryService.DiscoveryIntervalSeconds() == 150,
            BatteryService.DiscoveryIntervalSeconds() + " s");
        AppSettings.Current.RefreshMinutes = 60;
        Check("1 小时发现间隔封顶 15 分钟", BatteryService.DiscoveryIntervalSeconds() == 900,
            BatteryService.DiscoveryIntervalSeconds() + " s");
        AppSettings.Current.RefreshMinutes = 1440;
        Check("24 小时发现间隔仍封顶 15 分钟", BatteryService.DiscoveryIntervalSeconds() == 900,
            BatteryService.DiscoveryIntervalSeconds() + " s");
        AppSettings.Current.RefreshMinutes = savedRefresh;

        Console.WriteLine();
        Console.WriteLine("=== Theme 颜色 ===");
        Check("0% 红色", Theme.LevelColor(0) == Theme.Red, Theme.LevelColor(0).ToString());
        Check("10% 红色", Theme.LevelColor(10) == Theme.Red, Theme.LevelColor(10).ToString());
        Check("11% 橙色", Theme.LevelColor(11).R == 249, Theme.LevelColor(11).ToString());
        Check("25% 橙色", Theme.LevelColor(25).R == 249, Theme.LevelColor(25).ToString());
        Check("26% 黄色", Theme.LevelColor(26).R == 245, Theme.LevelColor(26).ToString());
        Check("50% 黄色", Theme.LevelColor(50).R == 245, Theme.LevelColor(50).ToString());
        Check("51% 绿色", Theme.LevelColor(51).G == 197, Theme.LevelColor(51).ToString());
        Check("100% 绿色", Theme.LevelColor(100).G == 197, Theme.LevelColor(100).ToString());
        Check("充电色", Theme.StatusColor(DeviceStatus.Charging, 80) == Theme.Charging, "charging");
        Check("不支持灰色", Theme.StatusColor(DeviceStatus.Unsupported, 0).R == 148, "unsupported");

        string savedThemeMode = Theme.Mode;
        bool savedAutoColor = AppSettings.Current.AutoColor;
        int savedCustomColor = AppSettings.Current.CustomColorArgb;
        Theme.Apply("dark");
        AppSettings.Current.AutoColor = false;
        AppSettings.Current.CustomColorArgb = unchecked((int)0xFF101010);
        Check("固定暗色不再被自动配色覆盖",
            Theme.AccentColor(80).ToArgb() == unchecked((int)0xFF101010),
            Theme.AccentColor(80).ToString());
        Check("充电状态也使用固定色",
            Theme.StatusColor(DeviceStatus.Charging, 80).ToArgb() == unchecked((int)0xFF101010),
            Theme.StatusColor(DeviceStatus.Charging, 80).ToString());
        Check("浅色图标使用深色文字", Theme.ContrastText(Color.White).R < 80,
            Theme.ContrastText(Color.White).ToString());
        Check("深色图标使用白色文字", Theme.ContrastText(Color.Black) == Color.White,
            Theme.ContrastText(Color.Black).ToString());
        AppSettings.Current.AutoColor = savedAutoColor;
        AppSettings.Current.CustomColorArgb = savedCustomColor;
        Theme.Apply(savedThemeMode);

        Console.WriteLine();
        Console.WriteLine("=== 图标生成 ===");
        int iconErrors = 0;
        int[] sizes = new int[] { 8, 16, 20, 24, 32, 64 };
        string[] texts = new string[] { "0", "5", "55", "100", "—", "…" };
        foreach (int size in sizes)
        {
            foreach (string text in texts)
            {
                try
                {
                    using (Icon icon = IconFactory.Build(size, text, Theme.Green, false, 66))
                    {
                        if (icon.Width != size)
                        {
                            iconErrors++;
                            Console.WriteLine("    size=" + size + " text=" + text + " got=" + icon.Width);
                        }
                    }
                    using (Icon icon = IconFactory.Build(size, text, Theme.Red, true, 80, true))
                    {
                        if (icon.Width != size) iconErrors++;
                    }                    foreach (string style in new string[] { "classic", "rounded", "soft" })
                    {
                        using (Icon icon = IconFactory.BuildDigits(size, text, Theme.Blue, false, 66, false, style))
                        {
                            if (icon.Width != size)
                            {
                                iconErrors++;
                                Console.WriteLine("    plain size=" + size + " text=" + text + " style=" + style);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    iconErrors++;
                    Console.WriteLine("    size=" + size + " text=" + text + " -> " + ex.Message);
                }
            }
        }
        Check("所有尺寸/文本/充电组合", iconErrors == 0, iconErrors + " 个异常");

        int whiteIconMin = 255;
        using (Icon icon = IconFactory.Build(64, "88", Color.White, false, 80))
        using (Bitmap bitmap = icon.ToBitmap())
        {
            for (int y = 20; y < 44; y++)
            {
                for (int x = 20; x < 44; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    if (pixel.R < whiteIconMin) whiteIconMin = pixel.R;
                }
            }
        }
        Check("白色图标使用深色文字", whiteIconMin < 100, whiteIconMin.ToString());

        int blackIconMax = 0;
        using (Icon icon = IconFactory.Build(64, "88", Color.Black, false, 80))
        using (Bitmap bitmap = icon.ToBitmap())
        {
            for (int y = 20; y < 44; y++)
            {
                for (int x = 20; x < 44; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    if (pixel.R > blackIconMax) blackIconMax = pixel.R;
                }
            }
        }
        Check("黑色图标使用亮色文字", blackIconMax > 180, blackIconMax.ToString());

        // 托盘图标在 16~32px 上是一像素一笔的尺寸，必须走抗锯齿：1 位提示会把圆弧画成台阶，
        // 缩小后看着就是「毛刺」。这里数一下半透明灰边像素，为 0 说明又退回单比特渲染了。
        int digitSoftEdges = 0;
        using (Icon icon = IconFactory.BuildDigits(32, "80", Color.White, false, 80, false, "soft"))
        using (Bitmap bitmap = icon.ToBitmap())
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int alpha = bitmap.GetPixel(x, y).A;
                    if (alpha > 0 && alpha < 250) digitSoftEdges++;
                }
            }
        }
        Check("托盘数字带抗锯齿灰边", digitSoftEdges >= 20, digitSoftEdges + " 个灰边像素");

        // 16px 是 100% DPI 下托盘槽位的真实尺寸。柔和那档原本用 Segoe UI 常规体，
        // 缩回 16px 后笔芯只剩一成多实心像素，肉眼看是一团灰雾；小尺寸换 Semibold 后
        // 实心比例翻倍。这里卡一个下限，防止以后有人把字重又调回去。
        int digit16Ink = 0;
        int digit16Core = 0;
        using (Icon icon = IconFactory.BuildDigits(16, "80", Color.White, false, 80, false, "soft"))
        using (Bitmap bitmap = icon.ToBitmap())
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int alpha = bitmap.GetPixel(x, y).A;
                    if (alpha == 0)
                    {
                        continue;
                    }

                    digit16Ink++;
                    if (alpha >= 240)
                    {
                        digit16Core++;
                    }
                }
            }
        }
        double digit16Solid = digit16Ink == 0 ? 0.0 : digit16Core / (double)digit16Ink;
        Check("16px 托盘数字笔芯不发虚", digit16Solid >= 0.25,
            string.Format("{0:P1}（{1}/{2}）", digit16Solid, digit16Core, digit16Ink));

        // 设置页的图标预览控件：高度由它自己按缩放系数算，SettingsSection 排版时直接读，
        // 所以必须先确认「基准高度」和「随缩放长高」两件事都成立，否则高 DPI 下会被裁掉一截。
        using (IconStylePreview preview = new IconStylePreview())
        {
            preview.ApplyScale(1f);
            int previewBase = preview.Height;
            preview.ApplyScale(1.5f);
            int previewScaled = preview.Height;
            Check("图标预览基准高度 88", previewBase == 88, previewBase.ToString());
            Check("图标预览随缩放长高", previewScaled >= previewBase + 40,
                string.Format("{0} -> {1}", previewBase, previewScaled));
        }

        // 图标文字测量必须在确定主题下进行：前面的 Theme.Apply(savedThemeMode)
        // 让主题取决于本机系统设置，浅色下深色文字与浅色背景的暗像素判定会失真。
        Theme.Apply("dark");
        int textMinX = 64, textMaxX = -1, textMinY = 64, textMaxY = -1;
        // 「较大」档才应该接近铺满色块；「较小」档本来就是收进去的。
        using (Icon icon = IconFactory.Build(64, "45", Theme.Green, false, 100))
        using (Bitmap bitmap = icon.ToBitmap())
        {
            for (int y = 6; y < 58; y++)
            {
                for (int x = 6; x < 58; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    int luminance = (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
                    if (pixel.A > 180 && luminance < 80)
                    {
                        if (x < textMinX) textMinX = x;
                        if (x > textMaxX) textMaxX = x;
                        if (y < textMinY) textMinY = y;
                        if (y > textMaxY) textMaxY = y;
                    }
                }
            }
        }
        int textWidth = textMaxX - textMinX + 1;
        int textHeight = textMaxY - textMinY + 1;
        float textCenterX = (textMinX + textMaxX) / 2f;
        float textCenterY = (textMinY + textMaxY) / 2f;
        Check("图标数字饱满且居中",
            textWidth >= 48 && textHeight >= 16 &&
            Math.Abs(textCenterX - 32f) <= 2f && Math.Abs(textCenterY - 32f) <= 2f,
            "w=" + textWidth + " h=" + textHeight + " center=" + textCenterX + "," + textCenterY);

        // 「不显示数字」必须一个像素都不画；三档大小必须逐级变大。16px 托盘下三档只差 1~2 像素，
        // 换成抗锯齿之后墨迹盒高度在 16px 上会被量化成同一个整数（灰边让每一档都多出半像素），
        // 高度只能要求不递减，真正的「变大」改用墨迹像素数来判断；64px 上仍按像素数逐级递增。
        int digitErrors = 0;
        int[] levelInk = new int[] { -1, -1, -1, -1 };
        int[] levelDigits = new int[] { 0, 66, 80, 100 };
        for (int i = 0; i < levelDigits.Length; i++)
        {
            try
            {
                levelInk[i] = DigitInkPixels(levelDigits[i]);
            }
            catch (Exception)
            {
                digitErrors++;
            }
        }
        foreach (int extreme in new int[] { -50, 200 })
        {
            try
            {
                using (Icon icon = IconFactory.Build(16, "88", Theme.Red, true, extreme))
                {
                    if (icon.Width != 16) digitErrors++;
                }
            }
            catch (Exception)
            {
                digitErrors++;
            }
        }
        // 无底色图标：四角必须透明（真的没有背景块），数字仍要随档位变大，
        // 「不显示数字」时只留一个小的定位圆点（不是数字，也不是底块）。
        int[] plainInk = new int[4];
        int[] plainWidth = new int[4];
        int[] plainSmall = new int[4];
        int[] plainSmallHeight = new int[4];
        for (int i = 0; i < levelDigits.Length; i++)
        {
            int[] metrics = PlainMetrics(levelDigits[i]);
            plainInk[i] = metrics[0];
            plainWidth[i] = metrics[1];
            int[] smallMetrics = PlainMetricsAt(levelDigits[i], 16);
            plainSmall[i] = smallMetrics[0];
            plainSmallHeight[i] = smallMetrics[3];
            if (metrics[2] != 0)
            {
                digitErrors++;
            }
        }
        // 数字字体：设置页每个可选项都要真的画出字来（系统缺字体时不允许变成空白图标）。
        int fontErrors = 0;
        StringBuilder fontDetail = new StringBuilder();
        for (int fi = 0; fi < AppSettings.IconFontLabels.Length; fi++)
        {
            string fontKey = AppSettings.IconFontKey(fi);
            using (Icon icon = IconFactory.BuildDigits(16, "35", Theme.Green, false, 100, false, "classic", fontKey))
            using (Bitmap bitmap = icon.ToBitmap())
            {
                int ink = 0;
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        if (bitmap.GetPixel(x, y).A > 40) { ink++; }
                    }
                }
                if (ink < 8)
                {
                    fontErrors++;
                    fontDetail.Append(fontKey + "=" + ink + " ");
                }
            }
        }
        Check("数字大小三档递增且「不显示数字」时为空",
            digitErrors == 0 && fontErrors == 0 && levelInk[0] == 0 &&
            levelInk[1] < levelInk[2] && levelInk[2] < levelInk[3] &&
            plainInk[0] > 0 &&
            plainInk[1] < plainInk[2] && plainInk[2] <= plainInk[3] &&
            plainSmallHeight[1] <= plainSmallHeight[2] && plainSmallHeight[2] <= plainSmallHeight[3] &&
            plainSmall[1] > 0 && plainSmall[2] > 0 && plainSmall[3] > 0 &&
            plainSmall[1] < plainSmall[2] && plainSmall[2] < plainSmall[3] &&
            plainWidth[3] >= 56,
            "ink=" + levelInk[0] + "/" + levelInk[1] + "/" + levelInk[2] + "/" + levelInk[3] +
            " plain=" + plainInk[0] + "/" + plainInk[1] + "/" + plainInk[2] + "/" + plainInk[3] +
            " small=" + plainSmall[0] + "/" + plainSmall[1] + "/" + plainSmall[2] + "/" + plainSmall[3] +
            " smallH=" + plainSmallHeight[0] + "/" + plainSmallHeight[1] + "/" + plainSmallHeight[2] + "/" + plainSmallHeight[3] +
            " w=" + plainWidth[3] + " fonts=" + fontDetail.ToString().Trim());

        try
        {
            List<Icon> many = new List<Icon>();
            for (int i = 0; i < 300; i++)
            {
                many.Add(IconFactory.Build(16, (i % 100).ToString(), Theme.Green, false, 66));
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int usable = 0;
            foreach (Icon icon in many)
            {
                using (Bitmap probe = icon.ToBitmap())
                {
                    if (probe.Width == 16) usable++;
                }
            }
            foreach (Icon icon in many) icon.Dispose();
            Check("300 个图标在 GC 后仍可用", usable == 300, usable + "/300");
        }
        catch (Exception ex)
        {
            Check("300 个图标在 GC 后仍可用", false, ex.GetType().Name + ": " + ex.Message);
        }

        Console.WriteLine();
        Console.WriteLine("=== 脱敏 ===");
        string mac = "AA:BB:CC:DD:EE:FF";
        string redacted = Log.Redact("设备 " + mac + " 已连接");
        Check("MAC 被脱敏", redacted.IndexOf("AA:BB:CC:DD:EE:FF", StringComparison.OrdinalIgnoreCase) < 0, redacted);
        Check("替换为 [MAC] 标记", redacted.IndexOf("[MAC]", StringComparison.Ordinal) >= 0, redacted);
        string hash1 = Log.HashAddress(0x112233445566);
        string hash2 = Log.HashAddress(0x112233445566);
        string hash3 = Log.HashAddress(0x112233445567);
        Check("地址哈希稳定且 8 位", hash1 == hash2 && hash1.Length == 8, hash1);
        Check("不同地址哈希不同", hash1 != hash3, hash1 + " vs " + hash3);

        Console.WriteLine();
        Console.WriteLine("=== AutoStart.ExtractPath ===");
        MethodInfo extract = typeof(AutoStart).GetMethod("ExtractPath", BindingFlags.NonPublic | BindingFlags.Static);
        if (extract == null)
        {
            Check("找到 ExtractPath", false, "反射未找到");
        }
        else
        {
            object[][] cases = new object[][]
            {
                new object[] { "\"C:\\Tools\\VoltPick.exe\" --autostart-launch", "C:\\Tools\\VoltPick.exe" },
                new object[] { "\"C:\\Tools\\VoltPick.exe\"", "C:\\Tools\\VoltPick.exe" },
                new object[] { "C:\\Tools\\VoltPick.exe", "C:\\Tools\\VoltPick.exe" },
                new object[] { "C:\\Tools\\VoltPick.exe --autostart-launch", "C:\\Tools\\VoltPick.exe" },
                new object[] { "  \"C:\\A B\\x.exe\"  ", "C:\\A B\\x.exe" }
            };
            foreach (object[] item in cases)
            {
                string input = (string)item[0];
                string expected = (string)item[1];
                string actual = (string)extract.Invoke(null, new object[] { input });
                Check("解析 " + input.Trim(), actual == expected, (actual ?? "null") + " != " + expected);
            }
            Check("空串返回 null", (string)extract.Invoke(null, new object[] { "" }) == null, "非 null");
            Check("null 返回 null", (string)extract.Invoke(null, new object[] { null }) == null, "非 null");
            Check("只有引号返回 null", (string)extract.Invoke(null, new object[] { "\"" }) == null, "非 null");
        }

        Console.WriteLine();
        Console.WriteLine("=== AutoStart.TryParseMode ===");
        // 旧实现是「不是 off 就当开启」，--autostart offf 也会把自启打开，这里锁住白名单行为。
        bool parsedOn;
        Check("on 解析为开启", AutoStart.TryParseMode("on", out parsedOn) && parsedOn, "on");
        bool parsedUpper;
        Check("OFF 大小写无关地解析为关闭",
            AutoStart.TryParseMode("OFF", out parsedUpper) && !parsedUpper, "OFF");
        bool parsedFalse;
        Check("false 解析为关闭", AutoStart.TryParseMode("false", out parsedFalse) && !parsedFalse, "false");
        bool parsedJunk;
        Check("拼错的值被拒绝", !AutoStart.TryParseMode("offf", out parsedJunk), "offf 被接受");
        bool parsedEmpty;
        Check("空值被拒绝", !AutoStart.TryParseMode(null, out parsedEmpty), "null 被接受");

        Console.WriteLine();
        Console.WriteLine("=== 设置 JSON 往返 ===");
        string originalOverride = AppSettings.ConfigDirectoryOverride;
        string tempDirectory = Path.Combine(Path.GetTempPath(),
            "VoltPick.TestHarness." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        AppSettings.ConfigDirectoryOverride = tempDirectory;
        string settingsPath = AppSettings.SettingsPath;
        try
        {
            AppSettings.Load();
            AppSettings.Current.RefreshMinutes = 1440;
            AppSettings.Current.DisplayMode = "separate";
            AppSettings.Current.IconDevice = "headset";
            AppSettings.Current.DigitPercent = 80;
            AppSettings.Current.AutoColor = false;
            AppSettings.Current.CustomColorArgb = unchecked((int)0xFF101010);
            AppSettings.Current.ThemeMode = "dark";
            AppSettings.Current.ShowVoltage = false;
            AppSettings.Current.HistoryEnabled = false;
            Dictionary<string, int> thresholds = new Dictionary<string, int>();
            thresholds["bt:test"] = 25;
            AppSettings.ReplaceDeviceAlertThresholds(thresholds);
            AppSettings.Current.DndEnabled = true;
            AppSettings.Current.DndStart = "22:30";
            AppSettings.Current.DndEnd = "06:15";
            AppSettings.Current.StartWithPanel = true;
            AppSettings.Current.PanelPositionSaved = true;
            AppSettings.Current.PanelX = 123;
            AppSettings.Current.PanelY = 456;
            AppSettings.Current.DeviceNames["test"] = "我的鼠标";
            AppSettings.Current.TrayDeviceId = "bt:test";
            DeviceState remembered = new DeviceState();
            remembered.Id = "bt:test";
            remembered.DisplayName = "我的耳机";
            remembered.Model = "ATK Headset";
            remembered.Category = DeviceCategory.Headset;
            remembered.Transport = TransportKind.BluetoothLe;
            remembered.Connected = true;
            remembered.Status = DeviceStatus.Online;
            AppSettings.RememberDevice(remembered);
            AppSettings.Save();

            string json = File.ReadAllText(settingsPath, Encoding.UTF8);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            Check("JSON 根节点可解析", root != null, "null");
            Check("刷新间隔写入", root != null && Convert.ToInt32(root["refreshMinutes"]) == 1440, json);
            Check("显示方式写入", root != null && Convert.ToString(root["displayMode"]) == "separate", json);
            Check("设备名写入", json.IndexOf("我的鼠标", StringComparison.Ordinal) >= 0, json);
            Check("历史开关写入", root != null && !Convert.ToBoolean(root["historyEnabled"]), json);
            Check("托盘指定设备写入", root != null && Convert.ToString(root["trayDeviceId"]) == "bt:test", json);
            Check("已添加设备写入", json.IndexOf("我的耳机", StringComparison.Ordinal) >= 0, json);

            AppSettings.Load();
            Check("24 小时间隔往返", AppSettings.Current.RefreshMinutes == 1440, AppSettings.Current.RefreshMinutes.ToString());
            Check("显示方式往返", AppSettings.Current.DisplayMode == "separate", AppSettings.Current.DisplayMode);
            Check("固定耳机往返", AppSettings.Current.IconDevice == "headset", AppSettings.Current.IconDevice);
        AppSettings.Current.IconDevice = "keyboard";
        AppSettings.Save();
        AppSettings.Load();
        Check("固定键盘往返", AppSettings.Current.IconDevice == "keyboard", AppSettings.Current.IconDevice);
        AppSettings.Current.IconDevice = "headset";
        AppSettings.Save();
            Check("面板位置往返", AppSettings.Current.PanelPositionSaved &&
                AppSettings.Current.PanelX == 123 && AppSettings.Current.PanelY == 456,
                AppSettings.Current.PanelX + "," + AppSettings.Current.PanelY);
            Check("数字大小往返", AppSettings.Current.DigitPercent == 80, AppSettings.Current.DigitPercent.ToString());
            Check("自动配色往返", !AppSettings.Current.AutoColor, "autoColor");
            Check("暗色固定色往返",
                AppSettings.Current.CustomColorArgb == unchecked((int)0xFF101010),
                AppSettings.Current.CustomColorArgb.ToString("X8"));
            Check("主题模式往返", AppSettings.Current.ThemeMode == "dark", AppSettings.Current.ThemeMode);
            Check("电压开关往返", !AppSettings.Current.ShowVoltage, "showVoltage");
            Check("历史开关往返", !AppSettings.Current.HistoryEnabled, "historyEnabled");
            Check("单设备低电阈值写入",
                json.IndexOf("\"bt:test\":25", StringComparison.Ordinal) >= 0, json);
            Check("单设备低电阈值往返",
                AppSettings.GetDeviceAlertThreshold("bt:test") == 25,
                AppSettings.GetDeviceAlertThreshold("bt:test").ToString());
            Dictionary<string, int> replacedThresholds = new Dictionary<string, int>();
            replacedThresholds["bt:test"] = 35;
            AppSettings.ReplaceDeviceAlertThresholds(replacedThresholds);
            Check("单设备阈值替换",
                AppSettings.GetDeviceAlertThreshold("bt:test") == 35 &&
                AppSettings.Current.DeviceAlertThresholds.Count == 1,
                AppSettings.GetDeviceAlertThreshold("bt:test").ToString());

            // 设置页改一台设备时不能把别的设备的阈值冲掉：那是整体替换接口的老毛病。
            AppSettings.SetDeviceAlertThreshold("bt:other", 15);
            AppSettings.SetDeviceAlertThreshold("bt:test", 20);
            Check("改一台设备阈值不影响另一台",
                AppSettings.GetDeviceAlertThreshold("bt:test") == 20 &&
                AppSettings.GetDeviceAlertThreshold("bt:other") == 15,
                AppSettings.GetDeviceAlertThreshold("bt:test") + "," +
                AppSettings.GetDeviceAlertThreshold("bt:other"));
            AppSettings.SetDeviceAlertThreshold("bt:other", 0);
            Check("阈值设为 0 等于跟随全局",
                AppSettings.GetDeviceAlertThreshold("bt:other") == 0 &&
                !AppSettings.Current.DeviceAlertThresholds.ContainsKey("bt:other"), "zero");
            Check("无阈值设备使用全局",
                AppSettings.GetDeviceAlertThreshold("missing") == 0, "missing");

            // 解除隐藏：写入、往返、以及在设备状态里的落地。
            AppSettings.SetDeviceForceVisible("bt:test", true);
            AppSettings.Save();
            string forceJson = File.ReadAllText(settingsPath, Encoding.UTF8);
            Check("解除隐藏写入 JSON",
                forceJson.IndexOf("forceVisible", StringComparison.Ordinal) >= 0, forceJson);
            AppSettings.Load();
            Check("解除隐藏往返", AppSettings.GetDeviceForceVisible("bt:test"), "forceVisible");
            Check("未设置的设备默认不解除隐藏",
                !AppSettings.GetDeviceForceVisible("missing"), "missing");
            DeviceState prefDevice = new DeviceState();
            prefDevice.Id = "bt:test";
            prefDevice.DisplayName = "耳机";
            AppSettings.ApplyDevicePreferences(prefDevice);
            Check("设备偏好带上解除隐藏", prefDevice.ForceVisible, "forceVisible");

            AppSettings.SetDeviceForceVisible("bt:test", false);
            Check("取消解除隐藏后读回 false",
                !AppSettings.GetDeviceForceVisible("bt:test"), "forceVisible");

            // 移除记录：名称、阈值、解除隐藏、已添加设备记录一起清掉。
            AppSettings.SetDeviceForceVisible("bt:test", true);
            Check("移除前记录存在",
                AppSettings.GetDeviceRecord("bt:test") != null ||
                AppSettings.GetDeviceForceVisible("bt:test"), "before-remove");
            bool forgot = AppSettings.ForgetDevice("bt:test");
            Check("移除记录返回已清除", forgot, "forgot");
            Check("移除后解除隐藏被清掉",
                !AppSettings.GetDeviceForceVisible("bt:test"), "forceVisible");
            Check("移除后低电阈值被清掉",
                AppSettings.GetDeviceAlertThreshold("bt:test") == 0,
                AppSettings.GetDeviceAlertThreshold("bt:test").ToString());
            Check("移除后自定义名被清掉",
                AppSettings.GetDeviceName("bt:test") == null, "name");
            Check("移除后已添加设备记录被清掉",
                AppSettings.GetDeviceRecord("bt:test") == null, "record");

            // 上面的移除把后续断言要用的记录删了，这里恢复回去。
            Dictionary<string, int> restoredThresholds = new Dictionary<string, int>();
            restoredThresholds["bt:test"] = 35;
            AppSettings.ReplaceDeviceAlertThresholds(restoredThresholds);
            AppSettings.RememberDevice(remembered);
            AppSettings.Save();
            Check("移除后可重新登记", AppSettings.GetDeviceRecord("bt:test") != null, "record");
            Check("免打扰往返", AppSettings.Current.DndEnabled && AppSettings.Current.DndStart == "22:30", "dnd");
                Check("设备名往返", AppSettings.GetDeviceName("test") == "我的鼠标", "deviceName");
            Check("托盘指定设备往返", AppSettings.Current.TrayDeviceId == "bt:test",
                AppSettings.Current.TrayDeviceId);
            DeviceRecord known;
            Check("已添加设备往返",
                AppSettings.Current.DeviceRecords.TryGetValue("bt:test", out known) &&
                known.Name == "我的耳机" && known.Category == "耳机",
                "knownDevices");

            HidDeviceInfo cachedAtkInfo = new HidDeviceInfo();
            cachedAtkInfo.SerialNumber = "541505796617";
            AtkMouseIdentity cachedAtk = new AtkMouseIdentity();
            cachedAtk.Cid = 1;
            cachedAtk.Mid = 37;
            cachedAtk.ModelName = "ATK A9 Ultra Max 2.0 Tri-Mode";
            AppSettings.RememberAtkIdentity(cachedAtk, cachedAtkInfo);
            AtkMouseIdentity cachedRead = AppSettings.FindAtkIdentity(cachedAtkInfo);
            Check("ATK 型号缓存往返",
                cachedRead != null && cachedRead.Cid == 1 && cachedRead.Mid == 37 &&
                cachedRead.ModelName == "ATK A9 Ultra Max 2.0 Tri-Mode", "atk cache");
            AppSettings.Load();
            AtkMouseIdentity cachedReloaded = AppSettings.FindAtkIdentity(cachedAtkInfo);
            Check("ATK 型号缓存已落盘",
                cachedReloaded != null && cachedReloaded.ModelName == "ATK A9 Ultra Max 2.0 Tri-Mode",
                "atk cache reload");

            DeviceRecord knownAtk = new DeviceRecord();
            knownAtk.Name = "ATK A9 Ultra Max 2.0 Tri-Mode";
            knownAtk.Category = "鼠标";
            knownAtk.Transport = "2.4G";
            AppSettings.Current.DeviceRecords["atk:541505796617-1.37"] = knownAtk;
            AtkMouseIdentity knownAtkIdentity = AppSettings.FindKnownAtkIdentity("541505796617");
            Check("ATK 已知记录身份回退",
                knownAtkIdentity != null && knownAtkIdentity.Cid == 1 &&
                knownAtkIdentity.Mid == 37, "known atk id");

            AppSettings.Save();
            AppSettings.Load();
            Check("断连记录不会被删除",
                AppSettings.Current.DeviceRecords.ContainsKey("bt:test"), "known removed");

            AppSettings.Current.DeviceNames["atk:old"] = "我的 A9";
            AppSettings.Current.DeviceOrder["atk:old"] = 3;
            AppSettings.Current.TrayDeviceId = "atk:old";
            DeviceRecord oldAtkRecord = new DeviceRecord();
            oldAtkRecord.Name = "Wireless mouse 8k NANO dongle-L";
            oldAtkRecord.Category = "鼠标";
            oldAtkRecord.Transport = "2.4G";
            oldAtkRecord.LastSeen = "2026-10-19 01:00";
            AppSettings.Current.DeviceRecords["atk:old"] = oldAtkRecord;
            bool migrated = AppSettings.MigrateDeviceIdentity("atk:old", "atk:new");
            Check("旧 ATK 身份迁移", migrated &&
                AppSettings.GetDeviceName("atk:new") == "我的 A9" &&
                AppSettings.Current.TrayDeviceId == "atk:new", "migrate");
            Check("旧 ATK 记录迁移",
                AppSettings.Current.DeviceRecords.ContainsKey("atk:new") &&
                !AppSettings.Current.DeviceRecords.ContainsKey("atk:old"), "record");
            AppSettings.Load();
            Check("ATK 身份迁移已落盘",
                AppSettings.Current.DeviceRecords.ContainsKey("atk:new") &&
                AppSettings.GetDeviceName("atk:new") == "我的 A9", "persist");

            // 损坏配置：应备份并回退默认值。
            File.WriteAllText(settingsPath, "{ this is not json", new UTF8Encoding(false));
            AppSettings.Load();
            Check("损坏配置回退默认值", AppSettings.Current.RefreshMinutes == 10, AppSettings.Current.RefreshMinutes.ToString());
            string[] corrupt = Directory.GetFiles(Path.GetDirectoryName(settingsPath), "settings.json.corrupt-*");
            Check("损坏配置已备份", corrupt.Length > 0, "未找到备份");
            foreach (string item in corrupt)
            {
                try { File.Delete(item); } catch (Exception) { }
            }
        }
        finally
        {
            AppSettings.ConfigDirectoryOverride = originalOverride;
            try
            {
                AppSettings.Load();
            }
            catch (Exception ex)
            {
                Console.WriteLine("  恢复设置失败：" + ex.Message);
            }
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  清理临时设置失败：" + ex.Message);
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 连接设备过滤 / 托盘选择 ===");
        DeviceState onlineMouse = Device(55, 0, 0, false);
        onlineMouse.Id = "mouse";
        onlineMouse.DisplayName = "鼠标";
        onlineMouse.Category = DeviceCategory.Mouse;
        onlineMouse.Connected = true;
        onlineMouse.Status = DeviceStatus.Online;

        DeviceState onlineHeadset = Device(35, 0, 0, false);
        onlineHeadset.Id = "headset";
        onlineHeadset.DisplayName = "耳机";
        onlineHeadset.Category = DeviceCategory.Headset;
        onlineHeadset.Connected = true;
        onlineHeadset.Status = DeviceStatus.Charging;

        DeviceState offline = Device(90, 0, 0, false);
        offline.Id = "offline";
        offline.Connected = false;
        offline.Status = DeviceStatus.Offline;

        DeviceState secondMouse = Device(60, 0, 0, false);
        secondMouse.Id = "secondMouse";
        secondMouse.Connected = true;
        secondMouse.Status = DeviceStatus.Online;

        DeviceState sleeping = Device(70, 0, 0, false);
        sleeping.Connected = true;
        sleeping.Status = DeviceStatus.Sleeping;

        BatterySnapshot selection = new BatterySnapshot();
        selection.Devices.Add(onlineMouse);
        selection.Devices.Add(onlineHeadset);
        selection.Devices.Add(offline);
        selection.Devices.Add(secondMouse);
        selection.Devices.Add(sleeping);
        List<DeviceState> visible = DeviceVisibility.ConnectedDevices(selection);
        Check("断连设备不显示", visible.Count == 3, visible.Count.ToString());
        Check("所有连接设备自动显示", visible.Contains(secondMouse), "secondMouse");

        DeviceState btOff = Device(70, 0, 0, false);
        btOff.Connected = true;
        btOff.Status = DeviceStatus.BluetoothOff;
        Check("蓝牙关闭设备不显示", !DeviceVisibility.IsConnected(btOff), "btOff");
        Check("休眠/无响应设备不显示", !DeviceVisibility.IsConnected(sleeping), "sleeping");
        BatterySnapshot panelSnapshot = DeviceVisibility.ConnectedSnapshot(selection);
        Check("面板快照只含连接设备", panelSnapshot.Devices.Count == 3, panelSnapshot.Devices.Count.ToString());
        Check("面板快照记录隐藏数", panelSnapshot.HiddenDeviceCount == 2,
            panelSnapshot.HiddenDeviceCount.ToString());
        Check("原快照不被修改", selection.Devices.Count == 5, selection.Devices.Count.ToString());

        DeviceState wiredKeyboard = Device(0, 0, 0, false);
        wiredKeyboard.Id = "wired-keyboard";
        wiredKeyboard.DisplayName = "有线键盘";
        wiredKeyboard.Category = DeviceCategory.Keyboard;
        wiredKeyboard.Transport = TransportKind.Usb;
        wiredKeyboard.Connected = true;
        wiredKeyboard.Status = DeviceStatus.Unsupported;
        wiredKeyboard.BatterySupported = false;

        DeviceState unsupported2g = Device(0, 0, 0, false);
        unsupported2g.Id = "unsupported-2g";
        unsupported2g.DisplayName = "2.4G 耳机";
        unsupported2g.Category = DeviceCategory.Headset;
        unsupported2g.Transport = TransportKind.Usb24G;
        unsupported2g.Connected = true;
        unsupported2g.Status = DeviceStatus.Unsupported;
        unsupported2g.BatterySupported = false;

        BatterySnapshot displayFilter = new BatterySnapshot();
        displayFilter.Devices.Add(onlineMouse);
        displayFilter.Devices.Add(wiredKeyboard);
        displayFilter.Devices.Add(unsupported2g);
        List<DeviceState> displayable = DeviceVisibility.ConnectedDevices(displayFilter);
        Check("有线无电量键盘不显示", !displayable.Contains(wiredKeyboard), "wiredKeyboard");
        Check("2.4G 不支持设备不显示", !displayable.Contains(unsupported2g), "unsupported2g");
        Check("能读到电量的设备仍显示", displayable.Contains(onlineMouse), "onlineMouse");
        BatterySnapshot filteredSnapshot = DeviceVisibility.ConnectedSnapshot(displayFilter);
        Check("读不到电量的设备保留隐藏计数", filteredSnapshot.HiddenDeviceCount == 2,
            filteredSnapshot.HiddenDeviceCount.ToString());

        // 解除隐藏：用户对单台设备开的例外，只改变是否显示卡片，不改变读数。
        unsupported2g.ForceVisible = true;
        Check("解除隐藏后读不到电量的设备也显示", DeviceVisibility.IsDisplayable(unsupported2g),
            "forceVisible");
        BatterySnapshot forcedSnapshot = DeviceVisibility.ConnectedSnapshot(displayFilter);
        Check("解除隐藏减少隐藏计数", forcedSnapshot.HiddenDeviceCount == 1,
            forcedSnapshot.HiddenDeviceCount.ToString());
        Check("解除隐藏的设备进入面板快照", forcedSnapshot.Devices.Contains(unsupported2g),
            "unsupported2g");
        Check("解除隐藏不改变读数", unsupported2g.PercentOrZero == 0 && !unsupported2g.BatterySupported,
            unsupported2g.PercentOrZero.ToString());
        unsupported2g.ForceVisible = false;
        Check("取消解除隐藏后重新隐藏", !DeviceVisibility.IsDisplayable(unsupported2g), "forceVisible");

        offline.ForceVisible = true;
        // 强制显示是用户显式动作，断连设备也要出卡片（卡片上写「未连接」），
        // 否则「显示全部」永远少一台断开的蓝牙耳机。
        Check("解除隐藏后断连设备也显示", DeviceVisibility.IsDisplayable(offline), "offline");
        offline.ForceVisible = false;

        Check("自动选择最低电量", TrayDeviceSelector.Choose(selection, "", "auto") == onlineHeadset,
            "auto");
        Check("固定鼠标", TrayDeviceSelector.Choose(selection, "", "mouse") == onlineMouse, "mouse");
        DeviceState onlineKeyboard = Device(65, 0, 0, false);
        onlineKeyboard.Id = "keyboard";
        onlineKeyboard.DisplayName = "键盘";
        onlineKeyboard.Category = DeviceCategory.Keyboard;
        onlineKeyboard.Connected = true;
        onlineKeyboard.Status = DeviceStatus.Online;
        BatterySnapshot keyboardSelection = new BatterySnapshot();
        keyboardSelection.Devices.Add(onlineMouse);
        keyboardSelection.Devices.Add(onlineKeyboard);
        Check("固定键盘", TrayDeviceSelector.Choose(keyboardSelection, "", "keyboard") == onlineKeyboard,
            "keyboard");
        Check("指定当前设备", TrayDeviceSelector.Choose(selection, "mouse", "auto") == onlineMouse,
            "selected");
        Check("指定设备断连后回退", TrayDeviceSelector.Choose(selection, "offline", "auto") == onlineHeadset,
            "fallback");
        Check("指定已连接设备优先", TrayDeviceSelector.Choose(selection, "secondMouse", "auto") == secondMouse,
            "selectedDisabled");
        Check("无连接设备返回 null",
            TrayDeviceSelector.Choose(new BatterySnapshot(), "mouse", "auto") == null, "empty");

        Console.WriteLine();
        Console.WriteLine("=== 托盘 tooltip 排序与长度 ===");
        DeviceState tipLow = Device(9, 0, 0, false);
        tipLow.Id = "low";
        tipLow.DisplayName = "低电耳机";
        tipLow.Category = DeviceCategory.Headset;
        tipLow.Status = DeviceStatus.Online;
        DeviceState tipHigh = Device(45, 0, 0, false);
        tipHigh.Id = "high";
        tipHigh.DisplayName = "鼠标";
        tipHigh.Category = DeviceCategory.Mouse;
        tipHigh.Status = DeviceStatus.Online;
        Check("低电量排在前面",
            TrayApp.CompareTooltipPriority(tipLow, tipHigh) < 0, "low first");
        // 真正的无读数设备：不支持读取且没有 LastReading。
        DeviceState tipNoValue = new DeviceState();
        tipNoValue.Id = "novalue";
        tipNoValue.DisplayName = "有线键盘";
        tipNoValue.Category = DeviceCategory.Keyboard;
        tipNoValue.Connected = true;
        tipNoValue.Status = DeviceStatus.Unsupported;
        tipNoValue.BatterySupported = false;
        Check("有值优先于无值",
            TrayApp.CompareTooltipPriority(tipHigh, tipNoValue) < 0, "value first");
        Check("tooltip 状态简写为百分比",
            TrayApp.DescribeForTooltip(tipHigh) == "45%", TrayApp.DescribeForTooltip(tipHigh));
        tipHigh.Status = DeviceStatus.Charging;
        Check("充电状态简写",
            TrayApp.DescribeForTooltip(tipHigh) == "45% 充电", TrayApp.DescribeForTooltip(tipHigh));
        Check("无值设备用状态文本",
            TrayApp.DescribeForTooltip(tipNoValue) == tipNoValue.StatusText,
            TrayApp.DescribeForTooltip(tipNoValue));
        Check("空设备不抛异常",
            TrayApp.DescribeForTooltip(null) == "未知", TrayApp.DescribeForTooltip(null));

        Console.WriteLine();
        Console.WriteLine("=== 设备状态文本 / 克隆 ===");
        DeviceState mouse = Device(45, 0, 0, false);
        mouse.Category = DeviceCategory.Mouse;
        mouse.Transport = TransportKind.Atk24G;
        mouse.Status = DeviceStatus.Online;
        mouse.LastReading.Source = BatterySourceKind.AtkHid;
        Check("鼠标类型文本", mouse.CategoryText == "鼠标", mouse.CategoryText);
        DeviceState keyboard = Device(65, 0, 0, false);
        keyboard.Category = DeviceCategory.Keyboard;
        Check("键盘类型文本", keyboard.CategoryText == "键盘", keyboard.CategoryText);
        Check("2.4G 连接文本", mouse.TransportText == "2.4G", mouse.TransportText);
        Check("在线文本", mouse.StatusText == "在线", mouse.StatusText);
        Check("ATK 来源文本", mouse.SourceText == "厂商 HID", mouse.SourceText);
        string atkModel;
        Check("ATK A9 型号识别",
            AtkModelCatalog.TryResolve(1, 37, out atkModel) &&
            atkModel == "ATK A9 Ultra Max 2.0 Tri-Mode", atkModel);
        Check("ATK 未知型号回退", AtkModelCatalog.Resolve(250, 250) == "ATK 鼠标",
            AtkModelCatalog.Resolve(250, 250));
        Check("ATK 电池查询校验", AtkProtocol.CreateRequest(0x04)[16] == 0x49,
            AtkProtocol.CreateRequest(0x04)[16].ToString("X2"));
        Check("ATK 型号查询校验", AtkProtocol.CreateRequest(0x10)[16] == 0x3D,
            AtkProtocol.CreateRequest(0x10)[16].ToString("X2"));
        AtkMouseIdentity atkIdentity = new AtkMouseIdentity();
        atkIdentity.Cid = 1;
        atkIdentity.Mid = 37;
        atkIdentity.ModelName = "ATK A9 Ultra Max 2.0 Tri-Mode";
        HidDeviceInfo fakeAtkInfo = new HidDeviceInfo();
        fakeAtkInfo.SerialNumber = "541505796617";
        Check("ATK 稳定身份包含型号",
            atkIdentity.BuildStableId(fakeAtkInfo) == "atk:541505796617-1.37",
            atkIdentity.BuildStableId(fakeAtkInfo));
        mouse.LastReading.Source = BatterySourceKind.GenericHid;
        Check("通用 HID 来源文本", mouse.SourceText == "通用 HID", mouse.SourceText);
        mouse.LastReading.Source = BatterySourceKind.AtkHid;
        mouse.LastReading.Source = BatterySourceKind.Vendor;
        Check("厂商来源文本", mouse.SourceText == "厂商协议", mouse.SourceText);
        mouse.LastReading.Source = BatterySourceKind.AtkHid;

        System.Collections.Generic.List<HidBatteryDevice> genericHid = HidEnumerator.FindBatteryDevices();
        bool excludesAtk = true;
        for (int i = 0; i < genericHid.Count; i++)
        {
            if (genericHid[i].Info != null && genericHid[i].Info.VendorId == 0x373B)
            {
                excludesAtk = false;
            }
        }
        Check("通用 HID 排除 ATK", excludesAtk, genericHid.Count + " 个候选");

        int batteryIndex;
        int batteryLevel;
        bool parsed = AirohaBatterySource.TryParseBatteryFrame(
            new byte[] { 0x05, 0x5D, 0x05, 0x00, 0xD6, 0x0C, 0x00, 0x01, 0x64 },
            out batteryIndex, out batteryLevel);
        Check("Airoha 解析右耳", parsed && batteryIndex == 1 && batteryLevel == 100,
            batteryIndex + "/" + batteryLevel);
        parsed = AirohaBatterySource.TryParseBatteryFrame(
            new byte[] { 0x05, 0x5D, 0x05, 0x00, 0xD6, 0x0C, 0x00, 0x00, 0x37 },
            out batteryIndex, out batteryLevel);
        Check("Airoha 解析左耳", parsed && batteryIndex == 0 && batteryLevel == 55,
            batteryIndex + "/" + batteryLevel);
        parsed = AirohaBatterySource.TryParseBatteryFrame(
            new byte[] { 0x05, 0x5D, 0x05, 0x00, 0xD6, 0x0C, 0x01, 0x01, 0x64 },
            out batteryIndex, out batteryLevel);
        Check("Airoha 拒绝错误状态", !parsed, batteryIndex + "/" + batteryLevel);

        Console.WriteLine();
        Console.WriteLine("=== Airoha 能力缓存 ===");
        AirohaCapabilityCache cache = new AirohaCapabilityCache();
        DateTime cacheNow = new DateTime(2026, 10, 18, 12, 0, 0);
        Check("首次尝试厂商服务", cache.ShouldTry("bt:a", cacheNow), "first");
        cache.Record("bt:a", AirohaReadOutcome.NotSupported, cacheNow);
        Check("不支持时跳过重复探测", !cache.ShouldTry("bt:a", cacheNow.AddMinutes(1)), "notSupported");
        Check("30 分钟后重试", cache.ShouldTry("bt:a", cacheNow.AddMinutes(31)), "retry");
        cache.Record("bt:a", AirohaReadOutcome.Success, cacheNow.AddMinutes(31));
        Check("读取成功后继续走厂商路径", cache.ShouldTry("bt:a", cacheNow.AddMinutes(32)), "success");
        cache.Record("bt:b", AirohaReadOutcome.Failed, cacheNow);
        cache.Record("bt:b", AirohaReadOutcome.Failed, cacheNow);
        Check("连续失败短暂退避", !cache.ShouldTry("bt:b", cacheNow.AddMinutes(1)), "failed");
        Check("失败退避后重试", cache.ShouldTry("bt:b", cacheNow.AddMinutes(6)), "retryFailed");

        Console.WriteLine();
        Console.WriteLine("=== 左右耳差异 ===");
        BatteryReading ears = new BatteryReading();
        ears.LeftPercent = 100;
        ears.RightPercent = 80;
        Check("左右耳差异 20% 提醒", DevicePanel.HasLargeEarDifference(ears), "20");
        ears.RightPercent = 81;
        Check("左右耳差异 19% 不提醒", !DevicePanel.HasLargeEarDifference(ears), "19");
        ears.RightPercent = null;
        Check("单耳读数不提醒", !DevicePanel.HasLargeEarDifference(ears), "single");

        Console.WriteLine();
        Console.WriteLine("=== 面板位置边界 ===");
        Rectangle workArea = new Rectangle(0, 0, 1920, 1040);
        Size panelSize = new Size(380, 336);
        Point outside = PanelPlacement.ClampToWorkArea(new Point(-100, -100), panelSize, workArea, 8);
        Check("左上越界校正", outside.X == 8 && outside.Y == 8, outside.ToString());
        Point bottomRight = PanelPlacement.ClampToWorkArea(new Point(1900, 1100), panelSize, workArea, 8);
        Check("右下越界校正", bottomRight.X == 1532 && bottomRight.Y == 696, bottomRight.ToString());
        Point inside = PanelPlacement.ClampToWorkArea(new Point(120, 140), panelSize, workArea, 8);
        Check("正常位置保持", inside.X == 120 && inside.Y == 140, inside.ToString());

        Console.WriteLine();
        Console.WriteLine("=== 电量历史 ===");
        string historyDirectory = Path.Combine(Path.GetTempPath(),
            "VoltPick.History." + Guid.NewGuid().ToString("N"));
        string oldHistoryOverride = AppSettings.ConfigDirectoryOverride;
        bool oldHistoryEnabled = AppSettings.Current.HistoryEnabled;
        Directory.CreateDirectory(historyDirectory);
        try
        {
            AppSettings.ConfigDirectoryOverride = historyDirectory;
            AppSettings.Current.HistoryEnabled = true;
            BatteryHistory.ResetForTests();

            DeviceState historyDevice = new DeviceState();
            historyDevice.Id = "test:history";
            historyDevice.DisplayName = "History Mouse";
            historyDevice.Category = DeviceCategory.Mouse;
            historyDevice.LastReading = new BatteryReading();
            historyDevice.LastReading.Percent = 80;
            historyDevice.LastReading.Charging = false;
            DateTime historyStart = new DateTime(2026, 10, 19, 10, 0, 0);
            BatteryHistory.Record(historyDevice, historyStart);
            BatteryHistory.Record(historyDevice, historyStart.AddMinutes(1));
            Check("历史采样去重", BatteryHistory.CountSamples() == 1,
                BatteryHistory.CountSamples().ToString());

            historyDevice.LastReading.Percent = 79;
            BatteryHistory.Record(historyDevice, historyStart.AddMinutes(2));
            Check("历史电量变化新增采样", BatteryHistory.CountSamples() == 2,
                BatteryHistory.CountSamples().ToString());

            BatteryHistorySeries historySeries = BatteryHistory.GetSeries("test:history");
            Check("历史按设备读取", historySeries != null && historySeries.Samples.Count == 2,
                historySeries == null ? "null" : historySeries.Samples.Count.ToString());
            Check("历史名称保留", historySeries != null && historySeries.Name == "History Mouse",
                historySeries == null ? "null" : historySeries.Name);

            BatteryHistory.ResetForTests();
            Check("历史落盘后可重载", BatteryHistory.CountSamples() == 2,
                BatteryHistory.CountSamples().ToString());

            AppSettings.Current.HistoryEnabled = false;
            historyDevice.LastReading.Percent = 50;
            BatteryHistory.Record(historyDevice, historyStart.AddMinutes(3));
            Check("关闭历史后不新增", BatteryHistory.CountSamples() == 2,
                BatteryHistory.CountSamples().ToString());
            AppSettings.Current.HistoryEnabled = true;

            // 移除设备记录时必须连历史一起清：设置在 AppSettings、采样在 battery-history.json，
            // 两处分开存，只清一处会让「移除记录」后历史图表里还留着曲线。
            historyDevice.LastReading.Percent = 60;
            BatteryHistory.Record(historyDevice, historyStart.AddMinutes(4));
            Check("移除记录前历史存在",
                BatteryHistory.GetSeries("test:history") != null, "before-forget");
            bool historyForgot = AppSettings.ForgetDevice("test:history");
            Check("移除记录后该设备历史被清掉",
                BatteryHistory.GetSeries("test:history") == null, "series");
            Check("移除记录后采样归零",
                BatteryHistory.CountSamples() == 0, BatteryHistory.CountSamples().ToString());
            Check("移除记录返回值反映历史已清", historyForgot, "forgot");

            string historyPath = Path.Combine(historyDirectory, "battery-history.json");
            File.WriteAllText(historyPath + ".bak", "old", Encoding.UTF8);
            File.WriteAllText(historyPath + ".tmp", "tmp", Encoding.UTF8);
            BatteryHistory.Clear();
            Check("清空历史", BatteryHistory.CountSamples() == 0,
                BatteryHistory.CountSamples().ToString());
            Check("清空历史不残留备份",
                !File.Exists(historyPath) && !File.Exists(historyPath + ".bak") &&
                !File.Exists(historyPath + ".tmp"), historyPath);
        }
        finally
        {
            AppSettings.Current.HistoryEnabled = oldHistoryEnabled;
            AppSettings.ConfigDirectoryOverride = oldHistoryOverride;
            BatteryHistory.ResetForTests();
            try { Directory.Delete(historyDirectory, true); } catch (Exception) { }
        }

        Console.WriteLine();
        Console.WriteLine("=== 历史采样去重 ===");
        CheckHistoryGate();

        Console.WriteLine();
        Console.WriteLine("=== 历史保留与清理 ===");
        CheckHistoryRetention();

        Console.WriteLine();
        Console.WriteLine("=== ATK 电量帧解析 ===");
        CheckAtkFrameParsing();

        Console.WriteLine();
        Console.WriteLine("=== 电量大跳变确认 ===");
        CheckPercentJumpConfirmation();

        Console.WriteLine();
        Console.WriteLine("=== 失败重试节奏 ===");
        CheckRetryCadence();

        Console.WriteLine();
        Console.WriteLine("=== 日志轮转 / 刷屏抑制 / 脱敏 ===");
        CheckLogVolume();

        Console.WriteLine();
        Console.WriteLine("=== 低电提醒策略 ===");
        CheckDndParsing();
        LowBatteryPolicyTest();

        Console.WriteLine();
        Console.WriteLine("=== 异常退出标记 ===");
        string markerDirectory = Path.Combine(Path.GetTempPath(),
            "VoltPick.Marker." + Guid.NewGuid().ToString("N"));
        string markerPath = Path.Combine(markerDirectory, "running.marker");
        Directory.CreateDirectory(markerDirectory);
        string markerDetail;
        Check("无标记视为正常", !RunSession.DetectPreviousAbnormalExit(markerPath, out markerDetail), "missing");
        RunSession.MarkRunning(markerPath);
        Check("运行标记已写入", File.Exists(markerPath), markerPath);
        Check("检测到上次异常退出",
            RunSession.DetectPreviousAbnormalExit(markerPath, out markerDetail) &&
            !string.IsNullOrEmpty(markerDetail), markerDetail);
        RunSession.MarkCleanExit(markerPath);
        Check("正常退出清除标记", !File.Exists(markerPath), markerPath);
        try { Directory.Delete(markerDirectory, true); } catch (Exception) { }

        Console.WriteLine();
        Console.WriteLine("=== 读取失败分类 ===");
        Check("打不开设备 -> 断开", BatteryService.ClassifyFailure("无法打开无线接收器（可能被其它程序占用）", false)
            == BatteryService.FailureKind.Disconnected, "classify");
        Check("只读到充电盒 -> 临时", BatteryService.ClassifyFailure(
            "只读到充电盒电量（C 80%），未读到耳机本机电量", true) == BatteryService.FailureKind.CaseOnly, "classify");
        Check("未暴露电量接口 -> 能力缺失",
            BatteryService.ClassifyFailure("未暴露标准 HID 电量接口", true) == BatteryService.FailureKind.CapabilityMissing,
            "classify");
        Check("HFP 占用 -> 能力缺失",
            BatteryService.ClassifyFailure("经典蓝牙 HFP 通道被占用", true) == BatteryService.FailureKind.CapabilityMissing,
            "classify");
        Check("鼠标没有响应 -> 无响应",
            BatteryService.ClassifyFailure("鼠标没有响应（可能休眠中）", true) == BatteryService.FailureKind.NoResponse,
            "classify");
        Check("空错误 -> 其它失败",
            BatteryService.ClassifyFailure(null, true) == BatteryService.FailureKind.Transient, "classify");
        Check("空错误不会显示成无原因异常",
            BatteryService.ClassifyFailure("", true) == BatteryService.FailureKind.Transient, "classify");

        Console.WriteLine();
        Console.WriteLine("=== HID 单次枚举的 null 安全 ===");
        Check("FindKeyboards(null) 返回空表", HidEnumerator.FindKeyboards(null).Count == 0, "null guard");
        Check("FindBatteryDevices(null) 返回空表", HidEnumerator.FindBatteryDevices(null).Count == 0, "null guard");
        Check("FindAtkMouse(null) 返回 null", HidEnumerator.FindAtkMouse((List<HidDeviceInfo>)null) == null, "null guard");

        // HIDP_VALUE_CAPS 是 72 字节（hidpi.h：IsAbsolute/HasNull/Reserved/BitSize/ReportCount/
        // Reserved2[5] 之后才是 UnitsExp..PhysicalMax，union 落在 56）。一旦按 BOOLEAN IsRange/IsString/
        // IsDesignator + Reserved[10] 的老形状声明（88 字节），UsageMin/UsageMax 就错位到 80/82，
        // 驱动填在 56/58 的用法区间永远读不到 —— 标准 HID 电量通道等于彻底失效。
        Console.WriteLine();
        Console.WriteLine("=== HIDP_VALUE_CAPS 原生布局（hidpi.h：72 字节） ===");
        Type capsType = typeof(Native.HIDP_VALUE_CAPS);
        int capsSize = Marshal.SizeOf(capsType);
        Check("结构大小 = 72 字节", capsSize == 72, capsSize + " 字节");
        int isRangeOffset = Marshal.OffsetOf(capsType, "IsRange").ToInt32();
        Check("IsRange 偏移 = 12", isRangeOffset == 12, isRangeOffset.ToString());
        int bitSizeOffset = Marshal.OffsetOf(capsType, "BitSize").ToInt32();
        Check("BitSize 偏移 = 18", bitSizeOffset == 18, bitSizeOffset.ToString());
        int usageMinOffset = Marshal.OffsetOf(capsType, "UsageMin").ToInt32();
        Check("UsageMin 偏移 = 56", usageMinOffset == 56, usageMinOffset.ToString());
        int usageMaxOffset = Marshal.OffsetOf(capsType, "UsageMax").ToInt32();
        Check("UsageMax 偏移 = 58", usageMaxOffset == 58, usageMaxOffset.ToString());

        // 用驱动真实回填的字节布局造样本：UsagePage=0x0006、IsRange=1、区间 0x0020..0x0024
        byte[] rawCaps = new byte[72];
        WriteUInt16(rawCaps, 0, 0x0006);
        rawCaps[12] = 1;
        WriteUInt16(rawCaps, 56, 0x0020);
        WriteUInt16(rawCaps, 58, 0x0024);
        Native.HIDP_VALUE_CAPS parsedCaps = HidEnumerator.ParseValueCaps(rawCaps, 0);
        Check("原生缓冲区能解析出 UsagePage", parsedCaps.UsagePage == 0x0006,
            "0x" + parsedCaps.UsagePage.ToString("X4"));
        Check("区间 0x20..0x24 命中 StateOfCharge",
            parsedCaps.UsageMin == 0x0020 && parsedCaps.UsageMax == 0x0024,
            "0x" + parsedCaps.UsageMin.ToString("X4") + "..0x" + parsedCaps.UsageMax.ToString("X4"));

        ushort resolvedUsage;
        bool resolved = HidEnumerator.TryResolveBatteryUsage(parsedCaps, out resolvedUsage);
        Check("Range 型能力项解析为 0x20", resolved && resolvedUsage == 0x0020, resolvedUsage.ToString("X4"));

        byte[] rawSingle = new byte[72];
        WriteUInt16(rawSingle, 0, 0x0006);
        rawSingle[12] = 0;
        WriteUInt16(rawSingle, 56, 0x0024);
        resolvedUsage = 0;
        resolved = HidEnumerator.TryResolveBatteryUsage(HidEnumerator.ParseValueCaps(rawSingle, 0), out resolvedUsage);
        Check("NonRange 型能力项解析为 0x24", resolved && resolvedUsage == 0x0024, resolvedUsage.ToString("X4"));

        byte[] rawOtherPage = new byte[72];
        WriteUInt16(rawOtherPage, 0, 0x0001);
        WriteUInt16(rawOtherPage, 56, 0x0020);
        Check("非电池页不被当成电量",
            !HidEnumerator.TryResolveBatteryUsage(HidEnumerator.ParseValueCaps(rawOtherPage, 0), out resolvedUsage),
            "page 0x0001");

        Console.WriteLine();
        Console.WriteLine("=== 窗口图标 ===");
        // 标题栏图标只能从 exe 里取（build.ps1 的 /win32icon 把 app.ico 打进去），
        // 所以这里钉两件事：图标取得回来、Apply 之后窗口用的是同一个对象而不是每次新做一份。
        Check("AppIcon.Window 不为空", AppIcon.Window != null, "icon");
        Check("AppIcon.Small 不为空", AppIcon.Small != null, "small icon");
        System.Windows.Forms.Form iconProbe = new System.Windows.Forms.Form();
        AppIcon.Apply(iconProbe);
        Check("窗口套上了程序图标", object.ReferenceEquals(iconProbe.Icon, AppIcon.Window), "form.Icon");
        iconProbe.Dispose();
        DeviceState clone = mouse.Clone();
        clone.LastReading.Percent = 10;
        Check("克隆不共享读数", mouse.PercentOrZero == 45, mouse.PercentOrZero.ToString());

        CheckKeyboardProtocols();
        CheckHeadsetProtocols();
        CheckMouseProtocols();
        CheckGamepadSupport();
        CheckAirPodsBroadcast();
        CheckBatteryStats();
        CheckNewSettingsRoundTrip();
        CheckTaskbarDisplay();

        Console.WriteLine();
        Console.WriteLine(string.Format("结果：{0} 通过，{1} 失败（期望 {2} 条）", _passed, _failed, ExpectedChecks));
        if (_failed > 0)
        {
            return 1;
        }
        if (_passed != ExpectedChecks)
        {
            Console.WriteLine(string.Format("断言条数不符：期望 {0}，实际 {1}；改动测试后请同步 TestHarness.ExpectedChecks。",
                ExpectedChecks, _passed));
            return 1;
        }
        return 0;
    }

    /// <summary>2.4G 键盘协议：全部用公开资料里的实际回包帧，解码是纯函数，不需要硬件。</summary>
    private static void CheckKeyboardProtocols()
    {
        Console.WriteLine();
        Console.WriteLine("=== 2.4G 键盘协议帧解析 ===");

        // ---- Evision（VGN / VXE / ATK）----
        string name;
        bool vgn;
        Check("Evision 型号 VGN V98 PRO 2.4G Dongle",
            EvisionProtocol.TryModel("VGN V98 PRO 2.4G Dongle ", out name, out vgn) &&
            name == "VGN V98 PRO" && vgn, name + "/" + vgn);
        Check("Evision 型号 ATK V75X 非 VGN 变体",
            EvisionProtocol.TryModel("ATK V75X", out name, out vgn) && name == "ATK V75X" && !vgn,
            name + "/" + vgn);
        Check("Evision 未知型号不认领",
            !EvisionProtocol.TryModel("Some Other Dongle", out name, out vgn), "unknown");
        byte[] evReq = EvisionProtocol.BuildRequest(0x0102, true);
        Check("Evision 请求帧头 04 02 01 1A 06",
            evReq[0] == 0x04 && evReq[1] == 0x02 && evReq[2] == 0x01 &&
            evReq[3] == 0x1A && evReq[4] == 0x06 && evReq.Length == 32, evReq.Length.ToString());
        byte[] evReply = new byte[64];
        evReply[0] = 0x04; evReply[1] = 0x05; evReply[2] = 0x00; evReply[3] = 0x1A;
        evReply[4] = 0x06; evReply[8] = 93; evReply[9] = 0;
        int lvl; bool chg;
        Check("Evision 回包 93% 未充电",
            EvisionProtocol.TryParse(evReply, 5, out lvl, out chg) && lvl == 93 && !chg, lvl + "/" + chg);
        evReply[9] = 2;
        Check("Evision 回包充电状态 2 也算充电",
            EvisionProtocol.TryParse(evReply, 5, out lvl, out chg) && lvl == 93 && chg, lvl + "/" + chg);
        Check("Evision 序号不匹配不认",
            !EvisionProtocol.TryParse(evReply, 6, out lvl, out chg), "seq 6");
        evReply[8] = 101;
        Check("Evision 越界电量不认",
            EvisionProtocol.TryParse(evReply, 5, out lvl, out chg) == false, "101%");

        // ---- AULA / MCHOSE BY 接收器 ----
        byte[] aulaReq = AulaKeyboardProtocol.BuildRequest(0);
        byte aulaSum = 0;
        for (int i = 0; i < 19; i++) { aulaSum = unchecked((byte)(aulaSum + aulaReq[i])); }
        Check("AULA 请求帧校验和正确", aulaReq[19] == aulaSum, aulaReq[19].ToString("X2"));
        byte[] aulaReply = new byte[20];
        aulaReply[0] = 0x13; aulaReply[1] = 0x4A; aulaReply[5] = 76; aulaReply[6] = 0x01;
        aulaSum = 0;
        for (int i = 0; i < 19; i++) { aulaSum = unchecked((byte)(aulaSum + aulaReply[i])); }
        aulaReply[19] = aulaSum;
        Check("AULA 回包 76% 未充电",
            AulaKeyboardProtocol.TryParse(aulaReply, out lvl, out chg) && lvl == 76 && !chg, lvl + "/" + chg);
        aulaReply[1] = 0xCA; aulaReply[5] = 54; aulaReply[6] = 0x10;
        aulaSum = 0;
        for (int i = 0; i < 19; i++) { aulaSum = unchecked((byte)(aulaSum + aulaReply[i])); }
        aulaReply[19] = aulaSum;
        Check("AULA 回包高半字节 1 为充电",
            AulaKeyboardProtocol.TryParse(aulaReply, out lvl, out chg) && lvl == 54 && chg, lvl + "/" + chg);
        Check("AULA 请求回显被跳过",
            !AulaKeyboardProtocol.TryParse(AulaKeyboardProtocol.BuildRequest(0), out lvl, out chg), "echo");
        byte[] mchoseReply = new byte[20];
        mchoseReply[0] = 0x13; mchoseReply[1] = 0x4A; mchoseReply[2] = 1;
        mchoseReply[3] = 0; mchoseReply[5] = 90; mchoseReply[6] = 0; mchoseReply[19] = 0;
        Check("MCHOSE 单包帧校验位 0 也认",
            AulaKeyboardProtocol.TryParse(mchoseReply, out lvl, out chg) && lvl == 90 && !chg, lvl + "/" + chg);
        aulaReply[5] = 0;
        aulaSum = 0;
        for (int i = 0; i < 19; i++) { aulaSum = unchecked((byte)(aulaSum + aulaReply[i])); }
        aulaReply[19] = aulaSum;
        Check("AULA 0% 电量不认", !AulaKeyboardProtocol.TryParse(aulaReply, out lvl, out chg), "0%");

        // ---- MCHOSE GLW ----
        byte[] glwReq = MchoseGlwProtocol.BuildRequest();
        Check("GLW 请求帧 00 55 05 00 38 38",
            glwReq[0] == 0 && glwReq[1] == 0x55 && glwReq[2] == 0x05 &&
            glwReq[3] == 0 && glwReq[4] == 56 && glwReq[5] == 56, "req");
        byte[] glwReply = new byte[64];
        glwReply[0] = 0xAA; glwReply[1] = 0x05; glwReply[4] = 56;
        glwReply[8 + 3] = 0x12; glwReply[8 + 32] = 85;
        byte glwSum = 0;
        for (int i = 4; i < 8; i++) { glwSum = unchecked((byte)(glwSum + glwReply[i])); }
        for (int i = 0; i < 56; i++) { glwSum = unchecked((byte)(glwSum + glwReply[8 + i])); }
        glwReply[3] = glwSum;
        Check("GLW 回包 85% 未充电",
            MchoseGlwProtocol.TryParse(glwReply, out lvl, out chg) && lvl == 85 && !chg, lvl + "/" + chg);
        glwReply[8 + 32] = (byte)(0x80 | 60);
        glwSum = 0;
        for (int i = 4; i < 8; i++) { glwSum = unchecked((byte)(glwSum + glwReply[i])); }
        for (int i = 0; i < 56; i++) { glwSum = unchecked((byte)(glwSum + glwReply[8 + i])); }
        glwReply[3] = glwSum;
        Check("GLW 回包最高位为充电",
            MchoseGlwProtocol.TryParse(glwReply, out lvl, out chg) && lvl == 60 && chg, lvl + "/" + chg);
        glwReply[3] ^= 1;
        Check("GLW 校验和错误不认", !MchoseGlwProtocol.TryParse(glwReply, out lvl, out chg), "bad sum");
        Check("GLW 空帧不认",
            !MchoseGlwProtocol.TryParse(new byte[64], out lvl, out chg), "empty");

        // ---- SONiX ----
        byte[] sonixReq = SonixKeyboardProtocol.BuildRequest();
        Check("SONiX 请求帧 33 字节、20 01 开头、校验 0x21",
            sonixReq.Length == 33 && sonixReq[1] == 0x20 && sonixReq[2] == 0x01 && sonixReq[32] == 0x21,
            sonixReq[32].ToString("X2"));
        byte[] sonixReply = new byte[32];
        sonixReply[0] = 0x20; sonixReply[1] = 0x01; sonixReply[2] = 0x00; sonixReply[3] = 87;
        int sonixLevel;
        Check("SONiX 回包 87%",
            SonixKeyboardProtocol.TryParse(sonixReply, out sonixLevel) && sonixLevel == 87, sonixLevel.ToString());
        sonixReply[3] = 0;
        Check("SONiX 0% 表示关机（解析仍成功）",
            SonixKeyboardProtocol.TryParse(sonixReply, out sonixLevel) && sonixLevel == 0, sonixLevel.ToString());
        sonixReply[1] = 0x02;
        Check("SONiX 命令不匹配不认",
            !SonixKeyboardProtocol.TryParse(sonixReply, out sonixLevel), "cmd 02");
    }

    /// <summary>无线耳机协议：全部用公开资料里的实际回包帧做解码断言。</summary>
    private static void CheckHeadsetProtocols()
    {
        Console.WriteLine();
        Console.WriteLine("=== 无线耳机协议帧解析 ===");
        int level; bool charging, full, linked;
        int[] raw = new int[2];

        // ---- Corsair Void ----
        byte[] c = new byte[] { 0x64, 0x00, 60, 177, 1 };
        Check("Corsair Void 60% 未充电",
            CorsairVoidProtocol.TryParse(c, out level, out charging, out full, out linked) &&
            linked && level == 60 && !charging, level + "/" + charging);
        c = new byte[] { 0x64, 0x00, 60, 51, 1 };
        Check("Corsair Void 断连状态 51",
            CorsairVoidProtocol.TryParse(c, out level, out charging, out full, out linked) && !linked, "51");
        c = new byte[] { 0x64, 0x00, 88, 177, 5 };
        Check("Corsair Void 状态 5 充电",
            CorsairVoidProtocol.TryParse(c, out level, out charging, out full, out linked) && charging && !full, "5");
        c = new byte[] { 0x64, 0x00, 100, 177, 4 };
        Check("Corsair Void 状态 4 充满",
            CorsairVoidProtocol.TryParse(c, out level, out charging, out full, out linked) && full, "4");
        c = new byte[] { 0x66, 0x01, 0x02, 0x03, 0x04 };
        Check("Corsair Void 非状态报告不认",
            !CorsairVoidProtocol.TryParse(c, out level, out charging, out full, out linked), "0x66");

        // ---- Corsair Bragi ----
        byte[] bragiReq = CorsairBragiProtocol.BuildRequest(0x0F);
        Check("Bragi 请求帧 02 09 02 0F",
            bragiReq[0] == 0x02 && bragiReq[1] == 0x09 && bragiReq[2] == 0x02 && bragiReq[3] == 0x0F, "req");
        byte[] bragi = new byte[64];
        bragi[0] = 0x02; bragi[1] = 0x01; bragi[2] = 0x02; bragi[3] = 0;
        bragi[4] = 107; bragi[5] = 3; // 875 = 0x36B
        Check("Bragi 回包千分比 875",
            CorsairBragiProtocol.TryParse(bragi, 0x01, out raw[0]) && raw[0] == 875, raw[0].ToString());
        bragi[3] = 5;
        Check("Bragi 错误状态 value=-1",
            CorsairBragiProtocol.TryParse(bragi, 0x01, out raw[0]) && raw[0] == -1, raw[0].ToString());
        bragi[3] = 0; bragi[1] = 0x00;
        Check("Bragi 接收器自己的回包不认",
            !CorsairBragiProtocol.TryParse(bragi, 0x01, out raw[0]), "src 0");
        int pct;
        Check("Bragi 875 -> 88%",
            CorsairBragiProtocol.TryPercent(875, out pct) && pct == 88, pct.ToString());
        Check("Bragi 1000 -> 100%",
            CorsairBragiProtocol.TryPercent(1000, out pct) && pct == 100, pct.ToString());
        Check("Bragi 1001 越界不认", !CorsairBragiProtocol.TryPercent(1001, out pct), "1001");

        // ---- HyperX Flight ----
        byte[] flight = new byte[20];
        flight[0] = 0x21; flight[1] = 0xFF; flight[2] = 0x05; flight[3] = 0x0E; flight[4] = 0xD8;
        int mv; bool charging2, linked2;
        Check("Flight 3800mV 解析",
            HyperXFlightProtocol.TryParse(flight, out mv, out charging2, out linked2) &&
            linked2 && mv == 3800 && !charging2, mv.ToString());
        Check("Flight 4000mV -> 100%", HyperXFlightProtocol.VoltagePercent(4000) == 100, "4000");
        Check("Flight 3600mV -> 4%", HyperXFlightProtocol.VoltagePercent(3600) == 4, "3600");
        flight[3] = 0x10; flight[4] = 0x20; // 4128mV
        Check("Flight 超过 0x100BmV 为充电",
            HyperXFlightProtocol.TryParse(flight, out mv, out charging2, out linked2) && charging2, "4128");
        flight[3] = 0; flight[4] = 0;
        Check("Flight 0mV 表示没连上",
            HyperXFlightProtocol.TryParse(flight, out mv, out charging2, out linked2) && !linked2, "0mV");

        // ---- HyperX Stinger ----
        byte[] stinger = new byte[64];
        stinger[0] = 0xFF; stinger[9] = 0x03;
        stinger[11] = 0x41; stinger[12] = 0x0F; // 3905mV little endian
        Check("Stinger 3905mV 放电 -> 70%",
            HyperXStingerProtocol.TryParse(stinger, out level, out charging, out full) &&
            level == 70 && !charging, level + "/" + charging);
        stinger[9] = 0x06;
        Check("Stinger 状态 06 充满 100%",
            HyperXStingerProtocol.TryParse(stinger, out level, out charging, out full) && full && level == 100, "6");
        stinger[9] = 0x05; stinger[11] = 0xA0; stinger[12] = 0x0F; // 4000mV
        Check("Stinger 状态 05 充电 -> 70%",
            HyperXStingerProtocol.TryParse(stinger, out level, out charging, out full) && charging && level == 70, level + "/" + charging);
        stinger[9] = 0x01;
        Check("Stinger 未知状态不认", !HyperXStingerProtocol.TryParse(stinger, out level, out charging, out full), "1");

        // ---- HyperX Alpha ----
        byte[] alpha = new byte[31];
        alpha[0] = 0x21; alpha[1] = 0xBB; alpha[2] = 0x0B; alpha[3] = 77;
        int av;
        Check("Alpha 命令 0B 电量 77%",
            HyperXAlphaProtocol.TryParse(alpha, 0x0B, out av) && av == 77, av.ToString());
        alpha[2] = 0x0C;
        Check("Alpha 命令 0C 请求不匹配不认",
            !HyperXAlphaProtocol.TryParse(alpha, 0x0B, out av), "0C");

        // ---- Razer BlackShark V3 ----
        byte[] bsReq = RazerBlackSharkV3Protocol.BuildRequest();
        Check("BlackShark V3 请求校验位正确",
            bsReq[10] == 0x21 && bsReq[62] == HeadsetMath.Xor(bsReq, 0, 62), "req");
        byte[] bs = new byte[64];
        bs[0] = 0x02; bs[10] = 0x21; bs[11] = 0x01; bs[12] = 0x01; bs[13] = 80;
        RazerBlackSharkV3Protocol.Kind kind3 = RazerBlackSharkV3Protocol.TryParse(bs, out raw[0]);
        Check("BlackShark V3 回包 80%",
            kind3 == RazerBlackSharkV3Protocol.Kind.Level && raw[0] == 80, raw[0].ToString());
        bs[10] = 0x2A; bs[13] = 1;
        kind3 = RazerBlackSharkV3Protocol.TryParse(bs, out raw[0]);
        Check("BlackShark V3 充电推送",
            kind3 == RazerBlackSharkV3Protocol.Kind.Charging && raw[0] == 1, raw[0].ToString());
        bs[10] = 0x21; bs[13] = 0xFF;
        Check("BlackShark V3 FF 表示未知不认",
            RazerBlackSharkV3Protocol.TryParse(bs, out raw[0]) == RazerBlackSharkV3Protocol.Kind.None, "FF");

        // ---- Razer BlackShark V2 Pro ----
        byte[] v2q = RazerBlackSharkV2ProProtocol.BuildRequest(0x21);
        Check("BlackShark V2 Pro 查询帧头 02 80 08",
            v2q[0] == 0x02 && v2q[1] == 0x80 && v2q[2] == 0x08 && v2q[10] == 0x21, "req");
        byte[] v2 = new byte[64];
        v2[0] = 0x02; v2[12] = 0x21; v2[13] = 0x01; v2[15] = 65;
        Check("BlackShark V2 Pro 回包 65%",
            RazerBlackSharkV2ProProtocol.TryParse(v2, 0x21, out raw[0]) && raw[0] == 65, raw[0].ToString());
        v2[13] = 0;
        Check("BlackShark V2 Pro 非 ACK 不认",
            !RazerBlackSharkV2ProProtocol.TryParse(v2, 0x21, out raw[0]), "ACK=0");

        // ---- Razer Barracuda ----
        byte[] baReq = RazerBarracudaProtocol.BuildRequest(0x21);
        Check("Barracuda 请求校验位正确",
            baReq[10] == 0x21 && baReq[62] == HeadsetMath.Xor(baReq, 0, 62), "req");
        byte[] ba = new byte[64];
        byte[] baHead = new byte[] { 0x02, 0x02, 0x60, 0, 0, 0, 0x05, 0, 0x80, 0x80, 0x21, 0x01, 0x01 };
        Array.Copy(baHead, ba, baHead.Length);
        ba[13] = 72;
        ba[62] = HeadsetMath.Xor(ba, 1, 61);
        Check("Barracuda 回包 72%",
            RazerBarracudaProtocol.TryParse(ba, 0x21, out raw[0]) && raw[0] == 72, raw[0].ToString());
        ba[62] ^= 1;
        Check("Barracuda 校验错误不认",
            !RazerBarracudaProtocol.TryParse(ba, 0x21, out raw[0]), "bad xor");

        // ---- Logitech G533 ----
        byte[] g533 = new byte[20];
        g533[0] = 0x11; g533[1] = 0xFF; g533[2] = 0x07; g533[3] = 0x01;
        g533[4] = 0x0F; g533[5] = 0xA0; // 4000mV
        int gmv; bool gchg;
        Check("G533 回包 4000mV",
            LogitechG533Protocol.TryParse(g533, out gmv, out gchg) == LogitechG533Protocol.Kind.Battery &&
            gmv == 4000 && !gchg, gmv.ToString());
        Check("G533 4250mV -> 100%", LogitechG533Protocol.VoltagePercent(4250) == 100, "4250");
        Check("G533 3850mV -> 50%", LogitechG533Protocol.VoltagePercent(3850) == 50, "3850");
        Check("G533 3330mV -> 0%", LogitechG533Protocol.VoltagePercent(3330) == 0, "3330");
        g533[2] = 0xFF; g533[3] = 0x07;
        Check("G533 FF 07 为离线",
            LogitechG533Protocol.TryParse(g533, out gmv, out gchg) == LogitechG533Protocol.Kind.Offline, "offline");
    }

    /// <summary>2.4G 鼠标协议：AULA 8K / Endgame 4K / ZOWIE / Areson 的录帧断言。</summary>
    private static void CheckMouseProtocols()
    {
        Console.WriteLine();
        Console.WriteLine("=== 2.4G 鼠标协议帧解析 ===");
        int level; bool charging; int value;

        // ---- AULA 8K ----
        byte[] aulaFrame = new byte[64];
        aulaFrame[0] = 0x04; aulaFrame[3] = 0x1A; aulaFrame[8] = 0x59; aulaFrame[9] = 0;
        Check("AULA 8K 回包 89% 未充电",
            AulaMouseProtocol.Decode(aulaFrame, out value) == AulaMouseProtocol.FrameKind.Battery &&
            (value & 0xFF) == 89 && (value & 0x100) == 0, value.ToString());
        aulaFrame[8] = 0x63; aulaFrame[9] = 1;
        Check("AULA 8K 状态 1 充电",
            AulaMouseProtocol.Decode(aulaFrame, out value) == AulaMouseProtocol.FrameKind.Battery &&
            (value & 0xFF) == 99 && (value & 0x100) != 0, value.ToString());
        aulaFrame[8] = 0x64; aulaFrame[9] = 2;
        Check("AULA 8K 状态 2 充满",
            AulaMouseProtocol.Decode(aulaFrame, out value) == AulaMouseProtocol.FrameKind.Battery &&
            (value & 0xFF) == 100 && (value & 0x100) != 0, value.ToString());
        aulaFrame[8] = 0; aulaFrame[9] = 0;
        Check("AULA 8K 0% 表示暂无读数",
            AulaMouseProtocol.Decode(aulaFrame, out value) == AulaMouseProtocol.FrameKind.Battery && value == 0,
            value.ToString());
        byte[] aulaLink = new byte[64];
        aulaLink[0] = 0x04; aulaLink[1] = 0xAA; aulaLink[3] = 0xAA; aulaLink[8] = 0xFF;
        Check("AULA 8K 链路推送 FF=在线",
            AulaMouseProtocol.Decode(aulaLink, out value) == AulaMouseProtocol.FrameKind.Link && value == 1,
            value.ToString());

        // ---- Endgame Gear 4K ----
        byte[] egReq = Endgame4KProtocol.BuildRequest(0xB4);
        Check("Endgame 请求帧 A1 B4 00",
            egReq[0] == 0xA1 && egReq[1] == 0xB4 && egReq[2] == 0, "req");
        byte[] eg = new byte[64];
        eg[0] = 0xA1; eg[1] = 0x01; eg[16] = 73;
        Check("Endgame 回包 73%",
            Endgame4KProtocol.ParseBattery(eg, out level) == Endgame4KProtocol.Battery.Level && level == 73,
            level.ToString());
        eg[1] = 0x08;
        Check("Endgame 状态 08 休眠",
            Endgame4KProtocol.ParseBattery(eg, out level) == Endgame4KProtocol.Battery.Asleep, "08");
        eg[1] = 0x03;
        Check("Endgame 状态 03 忙不认",
            Endgame4KProtocol.ParseBattery(eg, out level) == Endgame4KProtocol.Battery.None, "03");
        eg[1] = 0x01; eg[16] = 0x67; eg[17] = 0x33; eg[18] = 0x82; eg[19] = 0x19;
        int mousePid;
        Check("Endgame 信息帧 PID 0x1982",
            Endgame4KProtocol.ParseInfo(eg, out mousePid) && mousePid == 0x1982,
            mousePid.ToString("X4"));

        // ---- ZOWIE ----
        byte[] zReq = ZowieProtocol.BuildRequest();
        Check("ZOWIE 请求帧 08 B4 15",
            zReq[0] == 0x08 && zReq[1] == 0xB4 && zReq[2] == 0x15, "req");
        byte[] z = new byte[16];
        z[0] = 0x09; z[1] = 0x01; z[2] = 0x86; z[4] = 0xC8;
        Check("ZOWIE 回包 3900mV", ZowieProtocol.ParseVoltage(z) == 3900, z[1] + "/" + z[2]);
        z[1] = 0; z[2] = 0;
        Check("ZOWIE 0mV 表示没连上", ZowieProtocol.ParseVoltage(z) == 0, "0mV");
        z[0] = 0x08;
        Check("ZOWIE 非回报报告号不认", ZowieProtocol.ParseVoltage(z) < 0, "report 08");
        Check("ZOWIE 3890mV -> 50% 档位",
            ZowieProtocol.VoltagePercent(3890) == 50, ZowieProtocol.VoltagePercent(3890).ToString());
        Check("ZOWIE 3680mV -> 10% 档位",
            ZowieProtocol.VoltagePercent(3680) == 10, ZowieProtocol.VoltagePercent(3680).ToString());

        // ---- Areson / Redragon ----
        byte[] arReq = AresonProtocol.BuildRequest();
        Check("Areson 请求帧 08 04 ... 49",
            arReq[0] == 0x08 && arReq[1] == 0x04 && arReq[16] == 0x49, arReq[16].ToString("X2"));
        byte[] ar = new byte[17];
        byte[] arHead = new byte[] { 0x09, 0x04, 0x00, 0x00, 0x00, 0x02, 0x08, 0x00 };
        Array.Copy(arHead, ar, arHead.Length);
        ar[16] = 0x3E; // 整帧和为 0x55
        Check("Areson 回包 80% 未充电",
            AresonProtocol.TryParse(ar, out level, out charging) && level == 80 && !charging,
            level + "/" + charging);
        byte[] ar2 = new byte[17];
        Array.Copy(arHead, ar2, arHead.Length);
        ar2[6] = 5; ar2[7] = 1;
        int sum = 0;
        for (int i = 0; i < 16; i++) { sum += ar2[i]; }
        ar2[16] = (byte)((0x55 - sum) & 0xFF);
        Check("Areson 档位 5=50% 且充电",
            AresonProtocol.TryParse(ar2, out level, out charging) && level == 50 && charging,
            level + "/" + charging);
        ar2[6] = 11;
        sum = 0;
        for (int i = 0; i < 16; i++) { sum += ar2[i]; }
        ar2[16] = (byte)((0x55 - sum) & 0xFF);
        Check("Areson 档位 11 越界不认", !AresonProtocol.TryParse(ar2, out level, out charging), "11");
        ar[16] ^= 1;
        Check("Areson 校验和错误不认", !AresonProtocol.TryParse(ar, out level, out charging), "bad sum");

        // ---- CompX 扩展型号（同一 Pulsar 解码器）----
        byte[] compReq = PulsarProtocol.BuildRequest();
        Check("CompX 请求帧 08 04 ... 校验",
            compReq[0] == 0x08 && compReq[1] == 0x04 &&
            compReq[16] == PulsarProtocol.Checksum(compReq, 16), "req");
        Check("CompX 校验 85 - 前16字节和", PulsarProtocol.Checksum(compReq, 16) == 0x49, "0x49");
    }

    /// <summary>手柄：Xbox 粗档位换算与 DualShock/DualSense 输入报告解码。</summary>
    private static void CheckGamepadSupport()
    {
        Console.WriteLine();
        Console.WriteLine("=== 手柄电量解析 ===");
        int pct; bool chg;

        // XInput 粗档位
        Check("XInput 档位 0 = 0%", GamepadBatterySource.TryCoarseLevel(0, out pct) && pct == 0, pct.ToString());
        Check("XInput 档位 1 = 25%", GamepadBatterySource.TryCoarseLevel(1, out pct) && pct == 25, pct.ToString());
        Check("XInput 档位 2 = 50%", GamepadBatterySource.TryCoarseLevel(2, out pct) && pct == 50, pct.ToString());
        Check("XInput 档位 3 = 100%", GamepadBatterySource.TryCoarseLevel(3, out pct) && pct == 100, pct.ToString());
        Check("XInput 0xFF 未知不认", !GamepadBatterySource.TryCoarseLevel(0xFF, out pct), "0xFF");

        // DS4 USB report 01 byte30 = 0x05：档位 5 未插线 -> 55%
        byte[] ds4 = new byte[64];
        ds4[0] = 0x01; ds4[30] = 0x05;
        Check("DS4 USB 0x05 -> 55% 未充电",
            GamepadBatterySource.DecodePlayStation(0x05C4, false, ds4, out pct, out chg) ==
                GamepadBatterySource.PlayStationReport.Battery && pct == 55 && !chg, pct + "/" + chg);
        ds4[30] = 0x1A; // bit4=线缆、档位 10 -> 100%
        Check("DS4 USB 有线满电",
            GamepadBatterySource.DecodePlayStation(0x05C4, false, ds4, out pct, out chg) ==
                GamepadBatterySource.PlayStationReport.Battery && pct == 100 && chg, pct + "/" + chg);

        // DualSense USB report 01 byte53 = 0x43：档位 3 -> 35%，状态 4（未充电）
        byte[] ds5 = new byte[64];
        ds5[0] = 0x01; ds5[53] = 0x43;
        Check("DualSense USB 0x43 -> 35%",
            GamepadBatterySource.DecodePlayStation(0x0CE6, false, ds5, out pct, out chg) ==
                GamepadBatterySource.PlayStationReport.Battery && pct == 35 && !chg, pct + "/" + chg);
        ds5[53] = 0x21; // 状态 2 且满电
        Check("DualSense 状态 2 满电",
            GamepadBatterySource.DecodePlayStation(0x0CE6, false, ds5, out pct, out chg) ==
                GamepadBatterySource.PlayStationReport.Battery && pct == 100 && chg, pct + "/" + chg);
        Check("索尼 PID 识别", GamepadBatterySource.IsPlayStationPid(0x0CE6) &&
            GamepadBatterySource.IsPlayStationPid(0x09CC) && !GamepadBatterySource.IsPlayStationPid(0x045E), "pids");
    }

    /// <summary>苹果耳机 BLE 邻近配对广播：只解析不发送，用公开资料里的真实帧。</summary>
    private static void CheckAirPodsBroadcast()
    {
        Console.WriteLine();
        Console.WriteLine("=== AirPods BLE 广播解析 ===");
        AppleBleBatterySource.Advert advert;

        // AirPods Pro 2：左耳广播(status 0x20)，左 90% 右 80% 盒 50% 且盒充电。
        byte[] msg = new byte[27];
        msg[0] = 0x07; msg[1] = 0x19; msg[2] = 0x01;
        msg[3] = 0x14; msg[4] = 0x20; msg[5] = 0x20; msg[6] = 0x89; msg[7] = 0x45;
        Check("AirPods Pro2 左90 右80 盒50",
            AppleBleBatterySource.TryParseProximity(msg, out advert) &&
            advert.Left == 90 && advert.Right == 80 && advert.Case == 50 && advert.CaseCharging,
            advert == null ? "null" : advert.Left + "/" + advert.Right + "/" + advert.Case);

        // 右耳广播、另一侧未知(0xF)：左 -1 右 70%、盒未知。
        msg[5] = 0x00; msg[6] = 0xF7; msg[7] = 0x0F;
        Check("AirPods 右耳70 另一侧未知",
            AppleBleBatterySource.TryParseProximity(msg, out advert) &&
            advert.Left == -1 && advert.Right == 70 && advert.Case == -1,
            advert == null ? "null" : advert.Left + "/" + advert.Right);

        // AirPods Max：单电池型号 0x200A，低半字节 6=60%，充电位 0。
        msg[3] = 0x0A; msg[4] = 0x20; msg[5] = 0x00; msg[6] = 0x06; msg[7] = 0x90;
        Check("AirPods Max 单电池 60% 充电",
            AppleBleBatterySource.TryParseProximity(msg, out advert) &&
            advert.Left == 60 && advert.LeftCharging,
            advert == null ? "null" : advert.Left.ToString());

        // 非邻近配对消息 / 非音频型号应拒绝。
        msg[0] = 0x10;
        Check("非 0x07 消息不认", !AppleBleBatterySource.TryParseProximity(msg, out advert), "type 0x10");
        msg[0] = 0x07; msg[3] = 0x34; msg[4] = 0x12;
        Check("非 0x20xx 型号不认", !AppleBleBatterySource.TryParseProximity(msg, out advert), "model 0x1234");
        Check("短帧不认", !AppleBleBatterySource.TryParseProximity(new byte[8], out advert), "8 bytes");

        // TLV 前面有别的字段时也能走到 0x07。
        byte[] wrapped = new byte[32];
        wrapped[0] = 0x01; wrapped[1] = 0x02; wrapped[2] = 0xAA; wrapped[3] = 0xBB;
        msg[0] = 0x07; msg[3] = 0x14; msg[4] = 0x20; msg[5] = 0x20; msg[6] = 0x89; msg[7] = 0x45;
        Array.Copy(msg, 0, wrapped, 4, 27);
        Check("前面有其他 TLV 时仍能解析",
            AppleBleBatterySource.TryParseManufacturerData(wrapped, out advert) &&
            advert.Left == 90 && advert.Right == 80, "wrapped");
    }

    /// <summary>新增设置项（任务栏小组件）能正确读写 settings.json，且默认关闭。</summary>
    /// <summary>任务栏显示：字形映射、状态色、Piece 语义与命中区间，全部为纯函数断言。</summary>
    private static void CheckTaskbarDisplay()
    {
        Console.WriteLine();
        Console.WriteLine("=== 任务栏显示 ===");
        string glyph; Color boltColor;
        Color darkFg = Theme.TaskbarForeground(false);
        Color lightFg = Theme.TaskbarForeground(true);
        Check("任务栏前景色按明暗区分",
            darkFg.ToArgb() == Color.White.ToArgb() && lightFg.ToArgb() == Color.Black.ToArgb(),
            darkFg + "/" + lightFg);
        Check("低电深色为 255,90,90",
            Theme.TaskbarLow(false).R == 255 && Theme.TaskbarLow(false).G == 90, "dark");
        Check("低电浅色为 196,30,40",
            Theme.TaskbarLow(true).R == 196 && Theme.TaskbarLow(true).G == 30, "light");
        Check("充电绿与满电蓝不同",
            Theme.TaskbarCharging(false).ToArgb() != Theme.TaskbarFull(false).ToArgb(), "bolt");

        Check("鼠标字形 E962", TaskbarStripStyle.GlyphFor(DeviceCategory.Mouse) == "\uE962", "mouse");
        Check("键盘字形 E765", TaskbarStripStyle.GlyphFor(DeviceCategory.Keyboard) == "\uE765", "kbd");
        Check("耳机字形 E7F6", TaskbarStripStyle.GlyphFor(DeviceCategory.Headset) == "\uE7F6", "headset");
        Check("手柄字形 E7FC", TaskbarStripStyle.GlyphFor(DeviceCategory.Gamepad) == "\uE7FC", "pad");
        Check("未知字形 E83F", TaskbarStripStyle.GlyphFor(DeviceCategory.Unknown) == "\uE83F", "other");
        Check("闪电字形 E945", TaskbarStripStyle.GlyphBolt == "\uE945", "bolt");

        DeviceState dev = Device(12, 0, 0, false);
        dev.Id = "taskbar:low";
        Check("12% 判低电", TaskbarStripStyle.IsLow(dev), "12%");
        dev = Device(60, 0, 0, false);
        dev.Id = "taskbar:high";
        Check("60% 不判低电", !TaskbarStripStyle.IsLow(dev), "60%");
        dev = Device(12, 0, 0, true);
        dev.Status = DeviceStatus.Charging;
        Check("充电时不判低电", !TaskbarStripStyle.IsLow(dev), "charging");
        dev = Device(60, 0, 0, false);
        dev.Status = DeviceStatus.Sleeping;
        Check("休眠状态识别", TaskbarStripStyle.IsAsleep(dev), "sleeping");

        dev = Device(80, 0, 0, true);
        dev.Status = DeviceStatus.Charging;
        Check("充电中给绿闪电",
            TaskbarStripStyle.TryBolt(dev, false, out glyph, out boltColor) &&
            glyph == "\uE945" && boltColor.ToArgb() == Theme.TaskbarCharging(false).ToArgb(), "charging bolt");
        dev = Device(100, 0, 0, true);
        dev.Status = DeviceStatus.Charging;
        Check("充满给蓝闪电",
            TaskbarStripStyle.TryBolt(dev, false, out glyph, out boltColor) &&
            boltColor.ToArgb() == Theme.TaskbarFull(false).ToArgb(), "full bolt");
        dev = Device(80, 0, 0, false);
        dev.Status = DeviceStatus.Online;
        Check("未充电无闪电", !TaskbarStripStyle.TryBolt(dev, false, out glyph, out boltColor), "none");

        List<TaskbarPiece> pieces = new List<TaskbarPiece>();
        dev = Device(9, 0, 0, false);
        dev.Id = "taskbar:pieces";
        dev.Status = DeviceStatus.Online;
        TaskbarStripStyle.AddDevice(pieces, dev, 0, false);
        Check("低电设备产生字形+数字两段", pieces.Count == 2, pieces.Count.ToString());
        Check("低电数字为红色", pieces[1].Color.ToArgb() == Theme.TaskbarLow(false).ToArgb(), "color");
        Check("字形与数字间距 5px", pieces[0].GapAfter == 5, pieces[0].GapAfter.ToString());

        pieces.Clear();
        dev = Device(100, 0, 0, true);
        dev.Status = DeviceStatus.Charging;
        TaskbarStripStyle.AddDevice(pieces, dev, 0, false);
        Check("满电设备产生三段（含闪电）", pieces.Count == 3, pieces.Count.ToString());
        Check("数字与闪电间距 2px", pieces[1].GapAfter == 2, pieces[1].GapAfter.ToString());

        Check("任务栏条间距常量正确",
            TaskbarStripStyle.Pad == 8 && TaskbarStripStyle.GapBetweenDevices == 14, "layout");
        TaskbarStripRenderer.Layout empty = new TaskbarStripRenderer.Layout();
        empty.Hits.Add(new int[] { 10, 40, 0 });
        empty.Hits.Add(new int[] { 50, 90, 1 });
        Check("命中区间定位第 0 台", TaskbarStripRenderer.HitTest(empty, 20) == 0, "hit0");
        Check("命中区间定位第 1 台", TaskbarStripRenderer.HitTest(empty, 60) == 1, "hit1");
        Check("区间外返回 -1", TaskbarStripRenderer.HitTest(empty, 45) == -1, "gap");

        // 托盘类别图标：16px 以上才画，左右半区都要有墨迹，四种类别都能画出来。
        Check("16px 可画类别图标", IconFactory.CanDrawCategoryGlyph(16, DeviceCategory.Mouse), "16 mouse");
        Check("8px 不画类别图标", !IconFactory.CanDrawCategoryGlyph(8, DeviceCategory.Mouse), "8 mouse");
        Check("未知类别不画图标", !IconFactory.CanDrawCategoryGlyph(16, DeviceCategory.Unknown), "unknown");
        using (Icon withCat = IconFactory.BuildDigitsWithCategory(16, "88", Theme.Green, false, 80,
            false, "classic", "auto", DeviceCategory.Mouse))
        using (Icon plain = IconFactory.BuildDigits(16, "88", Theme.Green, false, 80, false, "classic", "auto"))
        using (Bitmap catBmp = withCat.ToBitmap())
        using (Bitmap plainBmp = plain.ToBitmap())
        {
            Check("类别图标保持 16px 尺寸", withCat.Width == 16 && withCat.Height == 16,
                withCat.Width + "x" + withCat.Height);
            int leftInk = 0;
            int rightInk = 0;
            int diff = 0;
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    if (catBmp.GetPixel(x, y).A > 40)
                    {
                        if (x < 5) leftInk++; else rightInk++;
                    }
                    if (catBmp.GetPixel(x, y).ToArgb() != plainBmp.GetPixel(x, y).ToArgb()) diff++;
                }
            }
            Check("左侧类别图标有墨迹", leftInk >= 3, leftInk.ToString());
            Check("右侧数字有墨迹", rightInk >= 5, rightInk.ToString());
            Check("带类别图标与纯数字位图不同", diff > 5, diff.ToString());
        }
        bool allCategoriesOk = true;
        foreach (DeviceCategory cat in new DeviceCategory[]
            { DeviceCategory.Mouse, DeviceCategory.Keyboard, DeviceCategory.Headset, DeviceCategory.Gamepad })
        {
            using (Icon icon = IconFactory.BuildDigitsWithCategory(16, "55", Theme.Blue, false, 80,
                false, "soft", "auto", cat))
            using (Bitmap bitmap = icon.ToBitmap())
            {
                int ink = 0;
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 5; x++)
                    {
                        if (bitmap.GetPixel(x, y).A > 40) ink++;
                    }
                }
                if (ink < 3) allCategoriesOk = false;
            }
        }
        Check("四类设备图标都有墨迹", allCategoriesOk, "categories");

        // 托盘图标状态色：显式固定色优先，自动配色才按低电/充电/满电分级。
        bool savedAutoColor = AppSettings.Current.AutoColor;
        int savedCustom = AppSettings.Current.CustomColorArgb;
        try
        {
            DeviceState tray = Device(9, 0, 0, false);
            tray.Id = "tray:low";
            AppSettings.Current.AutoColor = false;
            AppSettings.Current.CustomColorArgb = unchecked((int)0xFF112233);
            Check("固定色优先于低电红",
                TaskbarStripStyle.TrayIconColor(tray).ToArgb() == unchecked((int)0xFF112233), "fixed");
            AppSettings.Current.AutoColor = true;
            Check("自动配色低电为红",
                TaskbarStripStyle.TrayIconColor(tray).ToArgb() == Theme.TaskbarLow(Theme.IsSystemTaskbarDark()).ToArgb(), "low");
            tray = Device(80, 0, 0, true);
            tray.Status = DeviceStatus.Charging;
            Check("自动配色充电为绿",
                TaskbarStripStyle.TrayIconColor(tray).ToArgb() == Theme.TaskbarCharging(Theme.IsSystemTaskbarDark()).ToArgb(), "charge");
            tray = Device(100, 0, 0, true);
            tray.Status = DeviceStatus.Charging;
            Check("自动配色满电为蓝",
                TaskbarStripStyle.TrayIconColor(tray).ToArgb() == Theme.TaskbarFull(Theme.IsSystemTaskbarDark()).ToArgb(), "full");
            tray = Device(60, 0, 0, false);
            tray.Status = DeviceStatus.Sleeping;
            Check("自动配色休眠变淡",
                TaskbarStripStyle.TrayIconColor(tray).ToArgb() == Theme.Faint.ToArgb(), "sleep");
        }
        finally
        {
            AppSettings.Current.AutoColor = savedAutoColor;
            AppSettings.Current.CustomColorArgb = savedCustom;
        }
    }

    private static void CheckNewSettingsRoundTrip()
    {
        Console.WriteLine();
        Console.WriteLine("=== 新增设置项 ===");
        SettingsData defaults = new SettingsData();
        Check("任务栏小组件默认关闭", !defaults.TaskbarWidgetEnabled &&
            defaults.TaskbarOffsetX == 0, "default");
        Check("电压估算默认关闭", !defaults.AllowVoltageEstimate, "default");

        string original = AppSettings.SettingsPath;
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "VoltPick.SettingsTest." + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            AppSettings.ConfigDirectoryOverride = dir;
            AppSettings.Current.TaskbarWidgetEnabled = true;
            AppSettings.Current.TaskbarOffsetX = -42;
            AppSettings.Current.AllowVoltageEstimate = true;
            AppSettings.Save();
            AppSettings.Current = new SettingsData();
            AppSettings.Load();
            Check("小组件开关往返", AppSettings.Current.TaskbarWidgetEnabled, "enabled");
            Check("小组件偏移往返", AppSettings.Current.TaskbarOffsetX == -42,
                AppSettings.Current.TaskbarOffsetX.ToString());
            Check("电压估算开关往返", AppSettings.Current.AllowVoltageEstimate, "enabled");
        }
        finally
        {
            AppSettings.ConfigDirectoryOverride = null;
            AppSettings.Current = new SettingsData();
            try { System.IO.Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    private static BatteryHistorySample Sample(long minutes, int percent, int charging)
    {
        BatteryHistorySample s = new BatteryHistorySample();
        s.Time = minutes * 60;
        s.Percent = percent;
        s.Charging = charging;
        return s;
    }

    /// <summary>历史统计：速率/续航/剩余时间、断点不跨段、数据不足不给猜测值。</summary>
    private static void CheckBatteryStats()
    {
        Console.WriteLine();
        Console.WriteLine("=== 电量历史统计 ===");
        BatteryStats st;

        st = BatteryStats.Compute(new BatteryHistorySample[0], null, null);
        Check("空历史无速率", !st.DischargeRatePerHour.HasValue && st.Readings == 0, "empty");

        // 数据太少：20 分钟掉 1% 不足以算速率。
        st = BatteryStats.Compute(new BatteryHistorySample[]
            { Sample(0, 80, 0), Sample(20, 79, 0) }, 79, false);
        Check("数据不足不给速率", !st.DischargeRatePerHour.HasValue && !st.RemainingHours.HasValue, "thin");

        // 100% -> 80% 用了 4 小时：5 %/小时、20 小时续航、当前 80% 剩 16 小时。
        BatteryHistorySample[] discharge = new BatteryHistorySample[]
        {
            Sample(0, 100, 0), Sample(60, 95, 0), Sample(120, 90, 0),
            Sample(180, 85, 0), Sample(240, 80, 0)
        };
        st = BatteryStats.Compute(discharge, 80, false);
        Check("放电速率 5%/小时", st.DischargeRatePerHour.HasValue &&
            Math.Abs(st.DischargeRatePerHour.Value - 5.0) < 1e-9,
            st.DischargeRatePerHour.HasValue ? st.DischargeRatePerHour.Value.ToString() : "null");
        Check("满电续航 20 小时", st.BatteryLifeHours.HasValue &&
            Math.Abs(st.BatteryLifeHours.Value - 20.0) < 1e-9, "life");
        Check("当前剩余 16 小时", st.RemainingHours.HasValue &&
            Math.Abs(st.RemainingHours.Value - 16.0) < 1e-9, "left");
        Check("监测总时长 4 小时", st.MonitoredSeconds == 240 * 60, st.MonitoredSeconds.ToString());
        st = BatteryStats.Compute(discharge, 80, true);
        Check("充电时不给剩余时间", !st.RemainingHours.HasValue, "charging");

        // 断点：中间关机 10 小时，断点期间的掉电不算进速率。
        BatteryHistorySample[] gap = new BatteryHistorySample[]
        {
            Sample(0, 50, 0), Sample(60, 45, 0), Sample(660, 20, 0), Sample(720, 15, 0)
        };
        st = BatteryStats.Compute(gap, null, null);
        Check("断点切成两段", st.Discharges.Count == 2, st.Discharges.Count.ToString());
        Check("断点不计入速率", st.DischargeRatePerHour.HasValue &&
            Math.Abs(st.DischargeRatePerHour.Value - 5.0) < 1e-9, "rate");
        Check("断点期间不算监测时长", st.MonitoredSeconds == 120 * 60, st.MonitoredSeconds.ToString());

        // 充电：50% -> 100% 用 1 小时，50 %/小时，充满 2 小时，当前 60% 还需 0.8 小时。
        BatteryHistorySample[] charge = new BatteryHistorySample[]
        {
            Sample(0, 60, 0), Sample(60, 50, 0), Sample(61, 50, 1),
            Sample(91, 80, 1), Sample(121, 100, 1), Sample(200, 100, 1), Sample(201, 100, 0)
        };
        st = BatteryStats.Compute(charge, 60, true);
        Check("充电区间数 1", st.Charges.Count == 1, st.Charges.Count.ToString());
        Check("充电区间从50到100用1小时", st.Charges.Count == 1 &&
            st.Charges[0].From == 50 && st.Charges[0].To == 100 &&
            st.Charges[0].Seconds == 60 * 60, "session");
        Check("充满标记为完成", st.Charges.Count == 1 && st.Charges[0].Completed, "completed");
        Check("充电速率 50%/小时", st.ChargeRatePerHour.HasValue &&
            Math.Abs(st.ChargeRatePerHour.Value - 50.0) < 1e-9, "rate");
        Check("充满需要 2 小时", st.FullChargeHours.HasValue &&
            Math.Abs(st.FullChargeHours.Value - 2.0) < 1e-9, "full");
        Check("当前还需 0.8 小时", st.ToFullHours.HasValue &&
            Math.Abs(st.ToFullHours.Value - 0.8) < 1e-9, "toFull");
        Check("最近充电时间", st.LastCharged.HasValue && st.LastCharged.Value == 121 * 60, "last");

        // 格式化
        Check("时长格式 30 分钟", BatteryStats.FormatHours(0.5) == "30 分钟", "fmt");
        Check("速率格式 4.3 %/小时", BatteryStats.FormatRate(4.26) == "4.3 %/小时", "fmt");
    }

    private static void CheckHistoryGate()
    {
        long t = 1792000000;

        HistorySampleGate keepAlive = new HistorySampleGate();
        Check("首个读数落点",
            keepAlive.Evaluate(80, 0, t) == HistorySampleDecision.Record, "first");
        Check("同值 1 分钟内不落点",
            keepAlive.Evaluate(80, 0, t + 60) == HistorySampleDecision.SkipDuplicate, "dup");
        Check("同值 10 分钟仍不落点",
            keepAlive.Evaluate(80, 0, t + 600) == HistorySampleDecision.SkipDuplicate, "dup10");
        Check("同值 29 分钟仍不落点",
            keepAlive.Evaluate(80, 0, t + 1740) == HistorySampleDecision.SkipDuplicate, "dup29");
        Check("同值满 30 分钟续一个点",
            keepAlive.Evaluate(80, 0, t + HistorySampleGate.SameValueKeepAliveSeconds)
                == HistorySampleDecision.Record, "keepAlive");
        Check("续点后同值继续去重",
            keepAlive.Evaluate(80, 0, t + HistorySampleGate.SameValueKeepAliveSeconds + 60)
                == HistorySampleDecision.SkipDuplicate, "afterKeepAlive");

        HistorySampleGate changed = new HistorySampleGate();
        changed.Evaluate(80, 0, t);
        Check("掉 1% 立即落点",
            changed.Evaluate(79, 0, t + 60) == HistorySampleDecision.Record, "1%");
        Check("已确认的大跳变直接落点（判定在上游）",
            changed.Evaluate(55, 0, t + 120) == HistorySampleDecision.Record, "jump");
        Check("回升同样立即落点",
            changed.Evaluate(79, 0, t + 180) == HistorySampleDecision.Record, "up");

        HistorySampleGate chargingFlag = new HistorySampleGate();
        chargingFlag.Evaluate(80, 0, t);
        Check("充电状态变化落点",
            chargingFlag.Evaluate(80, 1, t + 60) == HistorySampleDecision.Record, "chargeFlag");
        Check("同值同充电状态仍去重",
            chargingFlag.Evaluate(80, 1, t + 120) == HistorySampleDecision.SkipDuplicate, "chargeDup");

        HistorySampleGate rollback = new HistorySampleGate();
        rollback.Evaluate(80, 0, t);
        Check("时间回拨不写倒序点",
            rollback.Evaluate(70, 0, t - 60) == HistorySampleDecision.SkipDuplicate, "rollback");
        Check("时间恢复后继续落点",
            rollback.Evaluate(70, 0, t + 60) == HistorySampleDecision.Record, "recover");

        HistorySampleGate seeded = new HistorySampleGate();
        seeded.Seed(70, 0, t);
        Check("重新载入历史后沿用最后一个点去重",
            seeded.Evaluate(70, 0, t + 60) == HistorySampleDecision.SkipDuplicate, "seed");
        Check("重新载入历史后同值满 30 分钟仍续点",
            seeded.Evaluate(70, 0, t + HistorySampleGate.SameValueKeepAliveSeconds)
                == HistorySampleDecision.Record, "seedKeepAlive");
    }

    private static void CheckHistoryRetention()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "VoltPick.Retention." + Guid.NewGuid().ToString("N"));
        string oldOverride = AppSettings.ConfigDirectoryOverride;
        bool oldEnabled = AppSettings.Current.HistoryEnabled;
        Directory.CreateDirectory(directory);
        try
        {
            AppSettings.ConfigDirectoryOverride = directory;
            AppSettings.Current.HistoryEnabled = true;
            BatteryHistory.ResetForTests();

            DeviceState mouse = new DeviceState();
            mouse.Id = "atk:retention";
            mouse.DisplayName = "ATK A9";
            mouse.Category = DeviceCategory.Mouse;
            mouse.LastReading = new BatteryReading();
            mouse.LastReading.Charging = false;
            DateTime start = new DateTime(2026, 10, 19, 10, 0, 0);

            // 每 10 分钟读到同一个值：只留第一个点（0/10/20 分钟三轮；
            // 满 30 分钟属于下面的“同值续点”，不能混进这一段）。
            for (int i = 0; i < 3; i++)
            {
                mouse.LastReading.Percent = 40;
                BatteryHistory.Record(mouse, start.AddMinutes(i * 10));
            }
            Check("同值不重复落点", BatteryHistory.CountSamples() == 1,
                BatteryHistory.CountSamples().ToString());

            // 满 30 分钟续一个点。
            mouse.LastReading.Percent = 40;
            BatteryHistory.Record(mouse, start.AddMinutes(30));
            Check("同值满 30 分钟续点", BatteryHistory.CountSamples() == 2,
                BatteryHistory.CountSamples().ToString());

            // 数值变化立刻落点。
            mouse.LastReading.Percent = 39;
            BatteryHistory.Record(mouse, start.AddMinutes(40));
            Check("数值变化立即落点", BatteryHistory.CountSamples() == 3,
                BatteryHistory.CountSamples().ToString());

            // 7 天保留：8 天后的点落库时把老点裁掉。
            mouse.LastReading.Percent = 38;
            BatteryHistory.Record(mouse, start.AddDays(8));
            Check("7 天以前的采样被裁掉", BatteryHistory.CountSamples() == 1,
                BatteryHistory.CountSamples().ToString());

            BatteryHistorySeries series = BatteryHistory.GetSeries("atk:retention");
            Check("裁剪后只留最新真实读数",
                series != null && series.Samples.Count == 1 && series.Samples[0].Percent == 38,
                series == null ? "null" : series.Samples.Count + " 点");

            // 关闭历史后不再新增。
            AppSettings.Current.HistoryEnabled = false;
            mouse.LastReading.Percent = 30;
            BatteryHistory.Record(mouse, start.AddDays(8).AddMinutes(10));
            Check("关闭历史后不再新增", BatteryHistory.CountSamples() == 1,
                BatteryHistory.CountSamples().ToString());
        }
        finally
        {
            AppSettings.Current.HistoryEnabled = oldEnabled;
            AppSettings.ConfigDirectoryOverride = oldOverride;
            BatteryHistory.ResetForTests();
            try { Directory.Delete(directory, true); } catch (Exception) { }
        }
    }

    private static void CheckRetryCadence()
    {
        Check("短暂失败保留卡片次数 3", BatteryService.TransientFailuresBeforeHidden == 3,
            BatteryService.TransientFailuresBeforeHidden.ToString());
        Check("首次失败 30 秒后重试", BatteryService.ComputeRetrySeconds(0) == 30,
            BatteryService.ComputeRetrySeconds(0) + " s");
        Check("第 1 次失败 30 秒后重试", BatteryService.ComputeRetrySeconds(1) == 30,
            BatteryService.ComputeRetrySeconds(1) + " s");
        Check("第 2 次失败 60 秒后重试", BatteryService.ComputeRetrySeconds(2) == 60,
            BatteryService.ComputeRetrySeconds(2) + " s");
        Check("第 3 次失败 120 秒后重试", BatteryService.ComputeRetrySeconds(3) == 120,
            BatteryService.ComputeRetrySeconds(3) + " s");
        Check("第 4 次失败 240 秒后重试", BatteryService.ComputeRetrySeconds(4) == 240,
            BatteryService.ComputeRetrySeconds(4) + " s");
        Check("重试间隔封顶 5 分钟", BatteryService.ComputeRetrySeconds(5) == 300 &&
            BatteryService.ComputeRetrySeconds(9) == 300,
            BatteryService.ComputeRetrySeconds(9) + " s");
    }

    private static void CheckLogVolume()
    {
        string error;
        Log.ResetForTests();
        Log.Clear(out error);

        Log.Info("同一行重复内容");
        Log.Info("同一行重复内容");
        Log.Info("同一行重复内容");
        Log.Info("同一行重复内容");
        string[] lines = File.ReadAllLines(Log.FilePath, Encoding.UTF8);
        Check("重复内容只记一条", lines.Length == 1, lines.Length + " 行");

        Log.Info("换一条内容");
        lines = File.ReadAllLines(Log.FilePath, Encoding.UTF8);
        Check("重复结束后补一条省略说明",
            lines.Length == 3 && lines[1].IndexOf("重复 3 次", StringComparison.Ordinal) >= 0,
            lines.Length + " 行 / " + (lines.Length > 1 ? lines[1] : "无"));

        Log.ResetForTests();
        Log.Clear(out error);
        Log.Info(new string('x', 1050 * 1024));
        long bigLength = new FileInfo(Log.FilePath).Length;
        Log.Info("轮转探针");
        bool rotated = File.Exists(Log.FilePath + ".1");
        long smallLength = new FileInfo(Log.FilePath).Length;
        Check("单文件超过 1MB 自动轮转",
            rotated && bigLength > 1024 * 1024 && smallLength < 1024 * 1024,
            "big=" + bigLength + " new=" + smallLength + " rotated=" + rotated);

        Check("短横线 MAC 被脱敏",
            Log.Redact("aa-bb-cc-dd-ee-ff").IndexOf("aa-bb-cc-dd-ee-ff", StringComparison.Ordinal) < 0,
            Log.Redact("aa-bb-cc-dd-ee-ff"));
        Check("无分隔符 MAC 被脱敏",
            Log.Redact("bt:AABBCCDDEEFF").IndexOf("AABBCCDDEEFF", StringComparison.Ordinal) < 0,
            Log.Redact("bt:AABBCCDDEEFF"));
        Check("纯数字接收器序列号不被当成 MAC",
            Log.Redact("接收器 541505796617") == "接收器 541505796617",
            Log.Redact("接收器 541505796617"));
        Check("写入的日志已脱敏",
            Log.Redact("设备 AA:BB:CC:DD:EE:FF 连接") == "设备 [MAC] 连接",
            Log.Redact("设备 AA:BB:CC:DD:EE:FF 连接"));

        Log.Clear(out error);
    }

    private static void CheckDndParsing()
    {
        string normalized;
        Check("免打扰接受 9:05", SettingsForm.TryNormalizeDnd("9:05", out normalized) && normalized == "09:05",
            normalized);
        Check("免打扰接受 23:00", SettingsForm.TryNormalizeDnd("23:00", out normalized) && normalized == "23:00",
            normalized);
        Check("免打扰接受全角冒号", SettingsForm.TryNormalizeDnd("22：30", out normalized) && normalized == "22:30",
            normalized);
        Check("免打扰拒绝 25:99", !SettingsForm.TryNormalizeDnd("25:99", out normalized), normalized);
        Check("免打扰拒绝乱码", !SettingsForm.TryNormalizeDnd("晚上", out normalized), normalized);
        Check("免打扰拒绝空值", !SettingsForm.TryNormalizeDnd("   ", out normalized), normalized);
    }

    private static void LowBatteryPolicyTest()
    {
        List<int> globalLevels = new List<int>(new int[] { 20, 15, 10, 5 });
        HashSet<int> warned = new HashSet<int>();

        Check("全局档位命中 20", LowBatteryPolicy.ChooseLevel(18, 0, globalLevels, warned) == 20,
            LowBatteryPolicy.ChooseLevel(18, 0, globalLevels, warned).ToString());
        LowBatteryPolicy.MarkWarned(18, 0, globalLevels, warned);
        Check("全局档位已提醒不重复",
            LowBatteryPolicy.ChooseLevel(19, 0, globalLevels, warned) == -1,
            LowBatteryPolicy.ChooseLevel(19, 0, globalLevels, warned).ToString());
        Check("全局跨档后提醒 15",
            LowBatteryPolicy.ChooseLevel(14, 0, globalLevels, warned) == 15,
            LowBatteryPolicy.ChooseLevel(14, 0, globalLevels, warned).ToString());
        List<int> unsortedLevels = new List<int>(new int[] { 5, 20, 10, 15 });
        Check("乱序档位命中 20",
            LowBatteryPolicy.ChooseLevel(18, 0, unsortedLevels, new HashSet<int>()) == 20,
            LowBatteryPolicy.ChooseLevel(18, 0, unsortedLevels, new HashSet<int>()).ToString());
        Check("乱序档位命中 15",
            LowBatteryPolicy.ChooseLevel(14, 0, unsortedLevels, new HashSet<int>()) == 15,
            LowBatteryPolicy.ChooseLevel(14, 0, unsortedLevels, new HashSet<int>()).ToString());

        Check("自定义阈值未达不提醒",
            LowBatteryPolicy.ChooseLevel(31, 30, globalLevels, new HashSet<int>()) == -1,
            "31/30");
        Check("自定义阈值命中",
            LowBatteryPolicy.ChooseLevel(30, 30, globalLevels, new HashSet<int>()) == 30,
            "30/30");
        HashSet<int> customWarned = new HashSet<int>();
        LowBatteryPolicy.MarkWarned(30, 30, globalLevels, customWarned);
        Check("自定义阈值只提醒一次",
            LowBatteryPolicy.ChooseLevel(30, 30, globalLevels, customWarned) == -1,
            "repeat");
    }

    /// <summary>把 "08-04-.." 形式的十六进制串变成字节数组。</summary>
    private static byte[] Hex(string text)
    {
        string[] parts = text.Split('-');
        byte[] bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            bytes[i] = Convert.ToByte(parts[i], 16);
        }
        return bytes;
    }

    /// <summary>按协议公式给测试帧补上正确的校验和。</summary>
    private static void FixChecksum(byte[] frame)
    {
        frame[16] = AtkFrameParser.Checksum(frame, 17);
    }

    private static void CheckAtkFrameParsing()
    {
        // 真实帧：2026-10-22 01:17 从 atk:541505796617-1.37（HID\VID_373B&PID_11FE&MI_01&COL05）
        // 连续 30+ 次原样读到的两帧，仅电压相差 4mV。
        byte[] frame40 = Hex("08-04-00-00-00-02-28-00-0F-57-00-00-00-00-00-00-B9");
        byte[] frame40b = Hex("08-04-00-00-00-02-28-00-0F-5B-00-00-00-00-00-00-B5");
        // 55% 帧：历史里出现过 55%（=0x37），与 40% 是同一个字节 [6] 的两个取值。
        // 帧布局完全一致，校验和按协议公式复算得 0xAA。
        byte[] frame55 = Hex("08-04-00-00-00-02-37-00-0F-57-00-00-00-00-00-00-AA");

        Check("ATK 校验和公式对 40% 帧成立（0xB9）",
            AtkFrameParser.Checksum(frame40, 17) == 0xB9,
            AtkFrameParser.Checksum(frame40, 17).ToString("X2"));
        Check("ATK 校验和公式对 3931mV 帧成立（0xB5）",
            AtkFrameParser.Checksum(frame40b, 17) == 0xB5,
            AtkFrameParser.Checksum(frame40b, 17).ToString("X2"));
        Check("ATK 校验和公式对 55% 帧成立（0xAA）",
            AtkFrameParser.Checksum(frame55, 17) == 0xAA,
            AtkFrameParser.Checksum(frame55, 17).ToString("X2"));
        Check("真实帧 40% 校验和自校验通过",
            AtkFrameParser.HasValidChecksum(frame40, 17) &&
            AtkFrameParser.HasValidChecksum(frame40b, 17), "checksum");

        AtkBatteryFrame parsed = AtkFrameParser.ParseBattery(frame40, 17);
        Check("解析 40% 帧：有效", parsed.Valid, parsed.Error);
        Check("解析 40% 帧：百分比 40", parsed.Valid && parsed.Percent == 40,
            parsed.Percent.ToString());
        Check("解析 40% 帧：未充电", parsed.Valid && !parsed.Charging, parsed.Charging.ToString());
        Check("解析 40% 帧：电压 3927mV", parsed.Valid && parsed.Millivolts == 3927,
            parsed.Millivolts.ToString());

        AtkBatteryFrame parsedB = AtkFrameParser.ParseBattery(frame40b, 17);
        Check("解析第二帧：电压 3931mV 同百分比 40",
            parsedB.Valid && parsedB.Millivolts == 3931 && parsedB.Percent == 40,
            parsedB.Millivolts + "/" + parsedB.Percent);

        AtkBatteryFrame parsed55 = AtkFrameParser.ParseBattery(frame55, 17);
        Check("解析 55% 帧：有效且百分比 55", parsed55.Valid && parsed55.Percent == 55,
            parsed55.Error ?? parsed55.Percent.ToString());
        Check("解析 55% 帧：电压仍为 3927mV",
            parsed55.Valid && parsed55.Millivolts == 3927, parsed55.Millivolts.ToString());

        // 坏校验和：把真实帧的校验字节改掉。
        byte[] badChecksum = Hex("08-04-00-00-00-02-28-00-0F-57-00-00-00-00-00-00-B8");
        AtkBatteryFrame rejectedChecksum = AtkFrameParser.ParseBattery(badChecksum, 17);
        Check("坏校验和帧被拒", !rejectedChecksum.Valid, "accepted");
        Check("坏校验和帧给出原因", rejectedChecksum.Error != null &&
            rejectedChecksum.Error.IndexOf("校验和", StringComparison.Ordinal) >= 0,
            rejectedChecksum.Error);

        // 状态字节 [2] 不是 0。
        byte[] badStatus = Hex("08-04-00-00-00-02-28-00-0F-57-00-00-00-00-00-01-00");
        badStatus[2] = 0x01;
        FixChecksum(badStatus);
        AtkBatteryFrame rejectedStatus = AtkFrameParser.ParseBattery(badStatus, 17);
        Check("[2] 状态异常被拒", !rejectedStatus.Valid, "accepted");
        Check("[2] 状态异常给出原因", rejectedStatus.Error != null &&
            rejectedStatus.Error.IndexOf("状态", StringComparison.Ordinal) >= 0, rejectedStatus.Error);

        // 载荷长度 [5] 不是 2。
        byte[] badLength = Hex("08-04-00-00-00-02-28-00-0F-57-00-00-00-00-00-00-00");
        badLength[5] = 0x03;
        FixChecksum(badLength);
        AtkBatteryFrame rejectedLength = AtkFrameParser.ParseBattery(badLength, 17);
        Check("[5] 长度异常被拒", !rejectedLength.Valid, "accepted");
        Check("[5] 长度异常给出原因", rejectedLength.Error != null &&
            rejectedLength.Error.IndexOf("载荷长度", StringComparison.Ordinal) >= 0, rejectedLength.Error);

        // 命令字节不对（例如把 0x10 型号响应当电量）。
        byte[] wrongCommand = Hex("08-04-00-00-00-02-28-00-0F-57-00-00-00-00-00-00-00");
        wrongCommand[1] = 0x10;
        FixChecksum(wrongCommand);
        Check("命令不是 0x04 的帧被拒", !AtkFrameParser.ParseBattery(wrongCommand, 17).Valid,
            "accepted");

        // 报告号不对。
        byte[] wrongReport = Hex("08-04-00-00-00-02-28-00-0F-57-00-00-00-00-00-00-00");
        wrongReport[0] = 0x09;
        FixChecksum(wrongReport);
        Check("报告号不是 0x08 的帧被拒", !AtkFrameParser.ParseBattery(wrongReport, 17).Valid,
            "accepted");

        // 长度不足（半截帧）。
        AtkBatteryFrame shortFrame = AtkFrameParser.ParseBattery(frame40, 10);
        Check("半截帧被拒", !shortFrame.Valid, "accepted");
        Check("null 帧被拒", !AtkFrameParser.ParseBattery(null, 0).Valid, "accepted");

        // 百分比越界（0xE8 = 232）。
        byte[] badPercent = Hex("08-04-00-00-00-02-28-00-0F-57-00-00-00-00-00-00-00");
        badPercent[6] = 0xE8;
        FixChecksum(badPercent);
        AtkBatteryFrame rejectedPercent = AtkFrameParser.ParseBattery(badPercent, 17);
        Check("百分比 >100 被拒", !rejectedPercent.Valid, "accepted");
        Check("百分比越界给出原因", rejectedPercent.Error != null &&
            rejectedPercent.Error.IndexOf("0–100", StringComparison.Ordinal) >= 0,
            rejectedPercent.Error);

        // 电压越界：保留 2500-5000mV 防御。
        byte[] lowVoltage = Hex("08-04-00-00-00-02-28-00-00-00-00-00-00-00-00-00-00");
        FixChecksum(lowVoltage);
        AtkBatteryFrame rejectedVoltage = AtkFrameParser.ParseBattery(lowVoltage, 17);
        Check("电压 0mV 被拒", !rejectedVoltage.Valid, "accepted");
        Check("电压越界给出原因", rejectedVoltage.Error != null &&
            rejectedVoltage.Error.IndexOf("mV", StringComparison.Ordinal) >= 0,
            rejectedVoltage.Error);

        byte[] highVoltage = Hex("08-04-00-00-00-02-28-00-00-00-00-00-00-00-00-00-00");
        highVoltage[8] = 0x13;
        highVoltage[9] = 0x89;   // 0x1389 = 5001mV：5000 是上限，超过要拒
        FixChecksum(highVoltage);
        Check("电压超出上限被拒", !AtkFrameParser.ParseBattery(highVoltage, 17).Valid, "accepted");

        // 协议常量的边界值（防止以后把范围校验放宽或去掉）。
        Check("百分比范围常量 0-100",
            AtkFrameParser.MinPercent == 0 && AtkFrameParser.MaxPercent == 100, "range");
        Check("电压范围常量 2500-5000mV",
            AtkFrameParser.MinMillivolts == 2500 && AtkFrameParser.MaxMillivolts == 5000, "range");
        Check("期望状态字节 0x00", AtkFrameParser.ExpectedStatus == 0x00, "status");
        Check("期望载荷长度 0x02", AtkFrameParser.ExpectedPayloadLength == 0x02, "length");
        Check("帧长常量 17", AtkFrameParser.FrameLength == 17, "length");

        // 每个字节都被校验保护：把 [10..15] 中任意一个改掉，校验和就不再匹配。
        byte[] tampered = Hex("08-04-00-00-00-02-28-00-0F-57-00-00-00-00-00-00-B9");
        tampered[13] = 0x01;
        Check("载荷被篡改后校验和不通过",
            !AtkFrameParser.HasValidChecksum(tampered, 17), "tamper");
    }

    private static void CheckPercentJumpConfirmation()
    {
        Check("大跳变阈值 10 个百分点", BatteryService.PercentJumpThreshold == 10,
            BatteryService.PercentJumpThreshold.ToString());
        Check("待确认时重读间隔不超过 120 秒",
            BatteryService.PendingConfirmRetrySeconds == 120,
            BatteryService.PendingConfirmRetrySeconds.ToString());

        int pending;
        int count;

        Check("首次读数直接采信",
            BatteryService.EvaluatePercentUpdate(false, 0, -1, 0, 55, out pending, out count) ==
                BatteryService.PercentUpdateDecision.Accept && pending == -1 && count == 0,
            pending + "/" + count);

        Check("掉 1% 立即生效",
            BatteryService.EvaluatePercentUpdate(true, 40, -1, 0, 39, out pending, out count) ==
                BatteryService.PercentUpdateDecision.Accept, pending + "/" + count);
        Check("掉 9% 立即生效",
            BatteryService.EvaluatePercentUpdate(true, 40, -1, 0, 31, out pending, out count) ==
                BatteryService.PercentUpdateDecision.Accept, pending + "/" + count);
        Check("涨 9% 立即生效",
            BatteryService.EvaluatePercentUpdate(true, 40, -1, 0, 49, out pending, out count) ==
                BatteryService.PercentUpdateDecision.Accept, pending + "/" + count);

        // 40 -> 55：第一次挂起，不采信、不落库。
        Check("40 -> 55 先挂起待确认",
            BatteryService.EvaluatePercentUpdate(true, 40, -1, 0, 55, out pending, out count) ==
                BatteryService.PercentUpdateDecision.AwaitConfirmation &&
            pending == 55 && count == 1, pending + "/" + count);

        // 连续第二次读到 55：确认生效。
        Check("连续两次 55 才确认",
            BatteryService.EvaluatePercentUpdate(true, 40, 55, 1, 55, out pending, out count) ==
                BatteryService.PercentUpdateDecision.Accept && pending == -1 && count == 0,
            pending + "/" + count);

        // 又跳回原值：说明刚才是抖动，立刻采信原值并清掉挂起。
        Check("跳回原值立即可信并清挂起",
            BatteryService.EvaluatePercentUpdate(true, 40, 55, 1, 40, out pending, out count) ==
                BatteryService.PercentUpdateDecision.Accept && pending == -1,
            pending + "/" + count);

        // 55 -> 40 同样要确认（两个方向对称）。
        Check("55 -> 40 同样先挂起",
            BatteryService.EvaluatePercentUpdate(true, 55, -1, 0, 40, out pending, out count) ==
                BatteryService.PercentUpdateDecision.AwaitConfirmation && pending == 40,
            pending + "/" + count);

        // 挂起期间又出现第三个值：替换挂起值，计数从头开始。
        Check("新的跳变值替换挂起并重新计数",
            BatteryService.EvaluatePercentUpdate(true, 40, 55, 1, 60, out pending, out count) ==
                BatteryService.PercentUpdateDecision.AwaitConfirmation &&
            pending == 60 && count == 1, pending + "/" + count);

        // 40 -> 55 -> 55 -> 40：确认 55 之后掉回 40 又要重新确认一次。
        BatteryService.EvaluatePercentUpdate(true, 40, -1, 0, 55, out pending, out count);
        BatteryService.EvaluatePercentUpdate(true, 40, pending, count, 55, out pending, out count);
        Check("确认 55 后掉回 40 仍要再确认",
            BatteryService.EvaluatePercentUpdate(true, 55, -1, 0, 40, out pending, out count) ==
                BatteryService.PercentUpdateDecision.AwaitConfirmation, pending + "/" + count);

        // DeviceState 上要能带着待确认值走（发现流程每轮都会重建 DeviceState）。
        DeviceState jump = Device(40, 0, 0, false);
        jump.PendingPercent = 55;
        jump.ConfirmCount = 1;
        DeviceState jumpClone = jump.Clone();
        Check("克隆保留待确认跳变",
            jumpClone.PendingPercent == 55 && jumpClone.ConfirmCount == 1,
            jumpClone.PendingPercent + "/" + jumpClone.ConfirmCount);
        DeviceState fresh = new DeviceState();
        Check("新设备默认没有挂起值", fresh.PendingPercent == -1 && fresh.ConfirmCount == 0,
            fresh.PendingPercent.ToString());

        jump.ForceVisible = true;
        DeviceState forceClone = jump.Clone();
        Check("克隆保留解除隐藏", forceClone.ForceVisible, "forceVisible");
        Check("新设备默认不解除隐藏", !fresh.ForceVisible, "forceVisible");
    }

    /// <summary>无底色图标：返回 { 不透明像素数, 墨迹宽度, 四角不透明像素数 }，用于验证「没有底块」。</summary>
    private static int[] PlainMetrics(int digitPercent)
    {
        return PlainMetricsAt(digitPercent, 64);
    }

    private static int[] PlainMetricsAt(int digitPercent, int size)
    {
        using (Icon icon = IconFactory.BuildDigits(size, "88", Theme.Green, false, digitPercent, false, "classic"))
        using (Bitmap bitmap = icon.ToBitmap())
        {
            int ink = 0;
            int minX = bitmap.Width;
            int maxX = -1;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).A > 40)
                    {
                        ink++;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                    }
                }
            }
            int corners = 0;
            int last = bitmap.Width - 1;
            int[] xs = new int[] { 0, last, 0, last };
            int[] ys = new int[] { 0, 0, last, last };
            for (int i = 0; i < 4; i++)
            {
                if (bitmap.GetPixel(xs[i], ys[i]).A > 0) corners++;
            }
            int minY = bitmap.Height;
            int maxY = -1;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).A > 40)
                    {
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            return new int[] { ink, maxX >= minX ? maxX - minX + 1 : 0, corners, maxY >= minY ? maxY - minY + 1 : 0 };
        }
    }

    /// <summary>64px 图标里数字实际占了多少像素：用「有字」和「空字」两张图对比得出。</summary>
    private static int DigitInkPixels(int digitPercent)
    {
        using (Icon withText = IconFactory.Build(64, "88", Theme.Green, false, digitPercent))
        using (Icon without = IconFactory.Build(64, "", Theme.Green, false, digitPercent))
        using (Bitmap withTextBitmap = withText.ToBitmap())
        using (Bitmap withoutBitmap = without.ToBitmap())
        {
            int ink = 0;
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    Color a = withTextBitmap.GetPixel(x, y);
                    Color b = withoutBitmap.GetPixel(x, y);
                    if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 90)
                    {
                        ink++;
                    }
                }
            }
            return ink;
        }
    }
}
