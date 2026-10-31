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

        private void BuildAboutPage()
        {
            TabPage page = _tabs.TabPages[6];

            SettingsSection about = new SettingsSection("关于 " + AppInfo.DisplayName);
            _versionLabel = NewNote(AppInfo.DisplayName + " 是一个只在本机运行的托盘工具，用来看鼠标、键盘和耳机的电量。"
                + "读数来自 HID、蓝牙 GATT 和 2.4G 接收器，不联网，也不上传任何数据。");
            // 署名单独一行、不换行，宽度铺满，字号小一号与说明文字区分。
            _authorLabel = NewPath();
            _authorLabel.Text = "作者：" + AppInfo.Author;
            _authorLabel.ForeColor = Theme.Text;
            // 主页链接：单行显示，点一下直接开浏览器。
            _githubLabel = NewLink();
            _githubLabel.Text = "GitHub：" + AppInfo.HomePageShort;
            _githubLabel.Click += delegate { OpenLink(AppInfo.HomePage); };
            about.AddRow(_versionLabel);
            about.AddRow(_authorLabel);
            about.AddRow(_githubLabel);
            _about.Add(about);

            SettingsSection feedback = new SettingsSection("反馈");
            // 邮箱整行显示：以前和两个按钮挤在一行，标签列只有 96px，
            // 而 “邮箱：3494384883@qq.com” 需要 151px，被折成了两行。
            // 邮箱直接当链接用：点一下就是写信，省掉一个单独的按钮。
            _contactLabel = NewLink();
            _contactLabel.Text = "邮箱：" + AuthorContact;
            _contactLabel.Click += delegate { OpenMail(); };
            _copyContact = NewButton("复制邮箱", false);
            _copyContact.Click += delegate
            {
                try { Clipboard.SetText(AuthorContact); } catch (Exception) { }
            };
            feedback.AddRow(_contactLabel);
            feedback.AddRow(_copyContact);
            _about.Add(feedback);

            SettingsSection data = new SettingsSection("数据与诊断");
            _dataPath = NewPath();
            _dataPath.Text = "配置文件：" + AppSettings.SettingsPath;
            _openFolder = NewButton("打开目录", false);
            _openFolder.Click += delegate
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe",
                        "\"" + System.IO.Path.GetDirectoryName(AppSettings.SettingsPath) + "\"");
                }
                catch (Exception)
                {
                }
            };
            _reset = NewButton("恢复默认", false);
            _reset.Click += delegate
            {
                if (MessageBox.Show(this, "把所有设置恢复成默认值？", "VoltPick",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }
                AppSettings.ResetToDefaults();
                LoadValues();
            };
            data.AddRow(_dataPath);
            data.AddRow(_openFolder, _reset);
            _about.Add(data);

            AddSections(page, _about);
        }
    }
}
