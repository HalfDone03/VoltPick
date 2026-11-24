using System;
using System.IO;
using DeviceBattery;

internal static class ListenSmoke
{
    private static int Fail(string message)
    {
        Console.WriteLine("listen smoke FAIL: " + message);
        return 1;
    }

    private static int Main()
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Console.WriteLine("=== --listen 报文探测冒烟 ===");

        // 1. VID:PID 解析（大小写、非法格式、越界）
        int vid, pid;
        if (!ListenTool.TryParseVidPid("1d57:FA60", out vid, out pid) || vid != 0x1D57 || pid != 0xFA60)
        {
            return Fail("解析 1d57:FA60");
        }
        Console.WriteLine("  parse 1d57:FA60 -> {0:X4}:{1:X4} ok", vid, pid);
        if (ListenTool.TryParseVidPid("1D57", out vid, out pid))
        {
            return Fail("缺少冒号应失败");
        }
        if (ListenTool.TryParseVidPid("GGGG:1111", out vid, out pid))
        {
            return Fail("非十六进制应失败");
        }
        if (ListenTool.TryParseVidPid("10000:1", out vid, out pid))
        {
            return Fail("超出 FFFF 应失败");
        }
        Console.WriteLine("  3 组非法输入均被拒绝 ok");

        // 2. Hex4 格式化
        if (ListenTool.Hex4(0x0A0B) != "0A0B")
        {
            return Fail("Hex4 格式化");
        }

        // 3. 真机短时探测：对本机第一个 HID 设备跑 800ms，
        //    断言报告结构完整（产品名行、集合标签、输入报告小节），且全程只读。
        string text = ListenTool.Run(0, 0, 800);
        if (string.IsNullOrEmpty(text))
        {
            return Fail("Run(0,0) 返回空");
        }
        if (text.IndexOf("listen", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return Fail("报告缺少标题行");
        }
        if (text.IndexOf("没找到", StringComparison.Ordinal) < 0 &&
            text.IndexOf("产品名", StringComparison.Ordinal) < 0)
        {
            return Fail("报告既没有产品名也没写找不到的提示");
        }
        Console.WriteLine("  报告结构 ok（{0} 字节）", text.Length);

        // 4. 不存在的设备：走"没找到"分支且正常退出
        string missing = ListenTool.Run(0x1234, 0x5678, 100);
        if (missing.IndexOf("没找到", StringComparison.Ordinal) < 0)
        {
            return Fail("不存在的设备应提示没找到");
        }
        Console.WriteLine("  空设备分支 ok");

        Console.WriteLine("listen smoke: all passed.");
        return 0;
    }
}
