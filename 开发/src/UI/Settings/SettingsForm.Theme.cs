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

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            ApplyThemeColors();
            ApplyWindowTheme();
        }

        /// <summary>按当前主题重刷设置页自己的配色（设置页不带自绘，改颜色就够）。</summary>
        private void ApplyThemeColors()
        {
            if (_status != null)
            {
                _status.BackColor = Theme.Background;
                _status.ForeColor = _dirty ? Theme.Blue : Theme.Muted;
            }
            SuspendLayout();
            try
            {
                BackColor = Theme.Background;
                ForeColor = Theme.Text;
                if (_bottom != null)
                {
                if (_supportView != null)
                {
                    _supportView.BackColor = Theme.Background;
                    _supportView.Invalidate();
                }
                if (_supportContent != null)
                {
                    _supportContent.BackColor = Theme.Background;
                }
                _bottom.BackColor = Theme.Background;
                    // 底部三个自绘按钮（取消/应用/确定）不在标签页里，原来的重刷流程走不到它们；
                    // 换主题后它们仍然贴着自己画出来的旧底色，看起来像按钮外围多了一圈方框。
                    RecolorTree(_bottom);
                    _bottom.Invalidate(true);
                }
                if (_tabs != null)
                {
                    for (int i = 0; i < _tabs.TabPages.Count; i++)
                    {
                        RecolorTree(_tabs.TabPages[i]);
                    }
                }
            }
            finally
            {
                ResumeLayout(true);
                Invalidate(true);
            }
        }

        private void RecolorTree(Control parent)
        {
            TabPage page = parent as TabPage;
            if (page != null)
            {
                page.BackColor = Theme.Background;
                page.ForeColor = Theme.Text;
            }
            SettingsSection section = parent as SettingsSection;
            if (section != null)
            {
                // 分组底板和标题都是自绘/普通 Panel，改底色后重画一次。
                section.BackColor = Theme.Card;
                section.Invalidate();
            }
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control c = parent.Controls[i];
                Label label = c as Label;
                if (label != null)
                {
                    int role;
                    if (!_labelRoles.TryGetValue(label, out role))
                    {
                        role = RoleText;
                    }
                    label.ForeColor = role == RoleMuted ? Theme.Muted
                        : (role == RoleAccent ? Theme.Blue : Theme.Text);
                    label.BackColor = Theme.Card;
                }
                else if (c is CheckBox)
                {
                    c.BackColor = Theme.Card;
                    c.ForeColor = Theme.Text;
                }
                else if (c is ComboBox || c is TextBox)
                {
                    c.BackColor = Theme.Input;
                    c.ForeColor = Theme.Text;
                }
                else if (c is ListView)
                {
                    c.BackColor = Theme.List;
                    c.ForeColor = Theme.Text;
                }
                else if (c is Panel || c is SettingsSection)
                {
                    c.BackColor = Theme.Card;
                }
                else if (c is Button)
                {
                    c.Invalidate();
                }
                RecolorTree(c);
            }
        }
    }
}
