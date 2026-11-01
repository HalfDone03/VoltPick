using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using DeviceBattery;

internal static class TraySmoke
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

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        TrayApp app = new TrayApp();
        Pump(3500);

        DevicePanel panel = (DevicePanel)Field(app, "_panel");
        MethodInfo show = typeof(TrayApp).GetMethod("ShowPanel", BindingFlags.Instance | BindingFlags.NonPublic);
        show.Invoke(app, null);
        // 无人值守跑的时候，ShowPanel 之后偶尔会被“点击外部”定时器立刻收起，
        // 或者机器上残留的二级页还开着；先确认面板可见且停在主视图，再按坐标点击。
        for (int attempt = 0; attempt < 20 && !panel.Visible; attempt++)
        {
            Pump(100);
        }
        if (!panel.Visible)
        {
            show.Invoke(app, null);
            Pump(600);
        }
        if (panel.EmbeddedViewActive)
        {
            panel.CloseEmbeddedView();
            Pump(200);
        }
        // _refreshRect / _historyRect 是 OnPaint 里算出来的。无人值守时窗口刚 Show 完还没画过，
        // 两个矩形都是空，下面按坐标点击全部落空（表现为 refresh=0 → 退出码 3，且随机复现）。
        // 这里先强推绘制，等事件矩形落地再断言；等不到就明确报「没画出来」。
        for (int attempt = 0; attempt < 40 && ((Rectangle)Field(panel, "_refreshRect")).IsEmpty; attempt++)
        {
            panel.Refresh();
            Pump(50);
        }
        if (((Rectangle)Field(panel, "_refreshRect")).IsEmpty)
        {
            Console.WriteLine("panel did not paint: refresh rect is empty");
            return 2;
        }
        // 冒烟测试运行在无人值守环境，系统鼠标可能恰好停在面板外；
        // 把指针放到面板内，避免“外部点击”定时器在断言前把面板关掉。
        // 若面板落在副屏（如 X=2172），鼠标无法移进去，先把它拉回主屏。
        Screen primary = Screen.PrimaryScreen;
        if (primary != null && !primary.WorkingArea.Contains(panel.Bounds))
        {
            panel.Location = new Point(primary.WorkingArea.Left + 40, primary.WorkingArea.Top + 40);
        }
        Cursor.Position = new Point(panel.Left + panel.Width / 2, panel.Top + panel.Height / 2);
        Pump(1200);

        Console.WriteLine("panel visible=" + panel.Visible + " bounds=" + panel.Bounds);
        if (!panel.Visible)
        {
            return 2;
        }

        int refresh = 0;
        int cardRefresh = 0;
        int bluetooth = 0;
        int history = 0;
        panel.RefreshRequested += delegate { refresh++; };
        panel.DeviceRefreshRequested += delegate(string id) { cardRefresh++; };
        panel.OpenBluetoothRequested += delegate { bluetooth++; };
        panel.HistoryRequested += delegate { history++; };

        MethodInfo click = typeof(DevicePanel).GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic);
        Rectangle refreshRect = (Rectangle)Field(panel, "_refreshRect");
        click.Invoke(panel, new object[] { new MouseEventArgs(MouseButtons.Left, 1, refreshRect.Left + 4, refreshRect.Top + 4, 0) });
        Pump(100);

        BatterySnapshot panelSnapshot = (BatterySnapshot)Field(panel, "_snapshot");
        int visibleCards = panelSnapshot.Devices.Count;
        Rectangle bluetoothRect = (Rectangle)Field(panel, "_bluetoothRect");
        bool emptyLinkIsUnhide = (bool)Field(panel, "_emptyLinkIsUnhide");
        bool cardLinkIsHide = (bool)Field(panel, "_cardLinkIsHide");
        // 空面板的“显示被隐藏的设备”会弹出菜单；卡片上的链接在有读数时是「隐藏」，
        // 两者都不能点：前者会卡在弹出菜单里，后者会真的把设备写进隐藏名单。
        bool emptyLinkIsPopup = emptyLinkIsUnhide;
        if (!bluetoothRect.IsEmpty && !emptyLinkIsPopup && !cardLinkIsHide)
        {
            click.Invoke(panel, new object[] { new MouseEventArgs(MouseButtons.Left, 1, bluetoothRect.Left + 4, bluetoothRect.Top + 4, 0) });
        }
        Pump(100);

        if (visibleCards > 0)
        {
            click.Invoke(panel, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 80, 100, 0) });
            Pump(100);
        }

        Rectangle historyRect = (Rectangle)Field(panel, "_historyRect");
        if (!historyRect.IsEmpty)
        {
            click.Invoke(panel, new object[] { new MouseEventArgs(MouseButtons.Left, 1, historyRect.Left + 4, historyRect.Top + 4, 0) });
            Pump(250);
        }

        MethodInfo rebuild = typeof(TrayApp).GetMethod("RebuildTraySourceMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        rebuild.Invoke(app, null);
        ToolStripMenuItem source = (ToolStripMenuItem)Field(app, "_traySourceItem");
        int sourceItems = source.DropDownItems.Count;

        Console.WriteLine("refresh=" + refresh + " cardRefresh=" + cardRefresh + " bluetooth=" + bluetooth +
                          " history=" + history + " sourceItems=" + sourceItems + " visibleCards=" + visibleCards);
        // 蓝牙开启时面板不会出现“打开蓝牙”提示，因此这种情况下不要求蓝牙点击事件。
        // 卡片上的链接在设备有读数时是「隐藏」，点了会改隐藏名单，不算蓝牙事件。
        bool bluetoothExpected = !bluetoothRect.IsEmpty && !emptyLinkIsPopup && !cardLinkIsHide;
        bool cardExpected = visibleCards > 0;
        bool sourceExpected = sourceItems >= 2;
        bool historyExpected = !historyRect.IsEmpty;
        MethodInfo hide = typeof(TrayApp).GetMethod("HidePanel", BindingFlags.Instance | BindingFlags.NonPublic);
        hide.Invoke(app, null);
        Pump(200);

        MethodInfo exit = typeof(TrayApp).GetMethod("ExitApp", BindingFlags.Instance | BindingFlags.NonPublic);
        exit.Invoke(app, null);
        return (refresh > 0 && (!cardExpected || cardRefresh > 0) &&
                (!bluetoothExpected || bluetooth > 0) &&
                (!historyExpected || history > 0) && sourceExpected) ? 0 : 3;
    }
}
