using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeviceBattery
{
    /// <summary>只用来接收 WM_DEVICECHANGE 的隐藏窗口。</summary>
    internal sealed class HiddenHost : Form
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        public event EventHandler DeviceChanged;

        public HiddenHost()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-32000, -32000, 1, 1);
            Opacity = 0.01;
            Show();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE)
            {
                int code = m.WParam.ToInt32();
                if (code == DBT_DEVNODES_CHANGED || code == DBT_DEVICEARRIVAL || code == DBT_DEVICEREMOVECOMPLETE)
                {
                    EventHandler handler = DeviceChanged;
                    if (handler != null)
                    {
                        try
                        {
                            handler(this, EventArgs.Empty);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("设备变化事件异常：" + ex.Message);
                        }
                    }
                }
            }
            base.WndProc(ref m);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return parameters;
            }
        }
    }
}
