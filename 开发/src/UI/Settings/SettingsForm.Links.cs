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

        private void OpenMail()
        {
            try
            {
                string subject = Uri.EscapeDataString("[VoltPick] 反馈");
                System.Diagnostics.Process.Start("mailto:" + AuthorContact + "?subject=" + subject);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开邮件客户端失败：" + ex.Message, "VoltPick",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
