using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DeviceBattery;

internal static class CompatCheck
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetVersionEx(ref OSVERSIONINFOEX info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OSVERSIONINFOEX
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 运行环境 ===");
        Console.WriteLine("Environment.OSVersion       : " + Environment.OSVersion.VersionString);
        Console.WriteLine("Environment.Version (.NET)   : " + Environment.Version);
        Console.WriteLine("Is64BitOS / Is64BitProcess   : " +
            Environment.Is64BitOperatingSystem + " / " + Environment.Is64BitProcess);

        // Environment.OSVersion 在未声明支持 Win10 时会伪装成 6.2，属于已知行为。
        OSVERSIONINFOEX info = new OSVERSIONINFOEX();
        info.dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEX));
        bool ok = false;
        try { ok = GetVersionEx(ref info); } catch (Exception) { }
        Console.WriteLine("GetVersionEx                 : " + (ok
            ? info.dwMajorVersion + "." + info.dwMinorVersion + " build " + info.dwBuildNumber
            : "不可用"));

        Console.WriteLine();
        Console.WriteLine("=== 关键 API 可用性 ===");
        CheckApi("user32.dll", "SetProcessDpiAwarenessContext",
            "Win10 1703+；缺失时回退 SetProcessDPIAware");
        CheckApi("dwmapi.dll", "DwmSetWindowAttribute",
            "属性 19/20（深色标题栏）需 Win10 1809+，属性 33（面板圆角）需 Win11 22000+；" +
            "缺失时分别退回系统默认标题色与 Region 裁剪圆角");
        CheckApi("user32.dll", "GetLastInputInfo", "空闲检测");
        CheckApi("kernel32.dll", "GetTickCount64", "运行时");

        Console.WriteLine();
        Console.WriteLine("=== 版本判定 ===");
        Console.WriteLine("建议的 Win10/Win11 区分方式：build >= 22000 即 Windows 11");
        Console.WriteLine("程序内 DiagnosticsForm 使用 Environment.OSVersion.VersionString：" +
            Environment.OSVersion.VersionString);
        Console.WriteLine("  → 该值在 Win10/Win11 上都会伪装成 6.2.9200，不能用来区分版本。");
    }

    private static void CheckApi(string module, string entryPoint, string note)
    {
        string result;
        try
        {
            IntPtr moduleHandle = LoadLibrary(module);
            if (moduleHandle == IntPtr.Zero)
            {
                result = "模块缺失";
            }
            else
            {
                result = GetProcAddress(moduleHandle, entryPoint) != IntPtr.Zero
                    ? "可用" : "缺失";
                FreeLibrary(moduleHandle);
            }
        }
        catch (Exception ex)
        {
            result = "检测异常：" + ex.GetType().Name;
        }
        Console.WriteLine(string.Format("{0,-42} {1,-8} {2}", module + "!" + entryPoint, result, note));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);
}
