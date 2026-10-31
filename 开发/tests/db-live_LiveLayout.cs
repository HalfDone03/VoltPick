using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DeviceBattery;

internal static class LiveLayout
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private static uint _target;
    private static readonly List<string> _rows = new List<string>();

    private static bool OnWindow(IntPtr h, IntPtr p)
    {
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid != _target) { return true; }
        StringBuilder cls = new StringBuilder(256), txt = new StringBuilder(256);
        GetClassNameW(h, cls, cls.Capacity); GetWindowTextW(h, txt, txt.Capacity);
        RECT r; GetWindowRect(h, out r);
        _rows.Add("  [" + cls + "] \"" + txt + "\"  " + (r.Right-r.Left) + "x" + (r.Bottom-r.Top) +
            " @ " + r.Left + "," + r.Top + "  可见=" + IsWindowVisible(h));
        return true;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _target = uint.Parse(args[0]);
        EnumWindows(OnWindow, IntPtr.Zero);
        Console.WriteLine("进程 " + _target + " 的顶层窗口 " + _rows.Count + " 个：");
        foreach (string s in _rows) { Console.WriteLine(s); }
        return 0;
    }
}
