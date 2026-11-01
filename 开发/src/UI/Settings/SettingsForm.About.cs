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
            TabPage page = _tabs.TabPages[4];

            // 关于和反馈合成一张卡：原来「反馈」卡只有邮箱和复制按钮两行，
            // 单独占一个卡片标题和一个卡片间距，看着比内容还重。
            SettingsSection about = new SettingsSection("关于 " + AppInfo.DisplayName);
            _versionLabel = NewNote(AppInfo.DisplayName + " 是一个只在本机运行的托盘工具，用来看鼠标、键盘和耳机的电量。"
                + "读数来自 HID、蓝牙 GATT 和 2.4G 接收器，不联网，也不上传任何数据。");
            // 署名单独一行、不换行，宽度铺满，字号小一号与说明文字区分。
            _authorLabel = NewPath();
            _authorLabel.Text = "作者：" + AppInfo.Author;
            _authorLabel.ForeColor = Theme.Text;
            // 邮箱整行显示、直接当链接用：点一下就是写信，右边再放一个复制按钮。
            _contactLabel = NewLink();
            _contactLabel.Text = "邮箱：" + AuthorContact;
            _contactLabel.Click += delegate { OpenMail(); };
            _copyContact = NewButton("复制邮箱", false);
            _copyContact.Click += delegate
            {
                try { Clipboard.SetText(AuthorContact); } catch (Exception) { }
            };
            // 主页链接：单行显示，点一下直接开浏览器。
            _githubLabel = NewLink();
            _githubLabel.Text = "GitHub：" + AppInfo.HomePageShort;
            _githubLabel.Click += delegate { OpenLink(AppInfo.HomePage); };
            about.AddRow(_versionLabel);
            about.AddRow(_authorLabel);
            about.AddRow(_contactLabel, _copyContact);
            about.AddRow(_githubLabel);
            _about.Add(about);

            // 支持范围原来是单独一个页签、十三条逐品牌说明，翻完要滚三屏。
            // 压成三句放在这一页，逐品牌清单留在 README 里。
            SettingsSection support = new SettingsSection("支持范围");
            support.AddRow(NewNote("能读到：标准 HID 电池页（0x20 / 0x24）、罗技 HID++ 2.0、雷蛇、赛睿、华硕 ROG、"
                + "HyperX 与 JBL、WLmouse / Lamzu / G-Wolves、MCHOSE、Pulsar / VXE / Hitscan、ATK 2.4G、蓝牙 LE。"));
            support.AddRow(NewNote("读不到：只支持经典蓝牙 HFP 的耳机、有线键鼠、"
                + "不暴露厂商电量页的 2.4G 接收器，以及海盗船、Keychron 等尚未实现的私有协议。"));
            support.AddRow(NewNote("逐品牌清单与显示规则见 README。"));
            _about.Add(support);

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
        }    }
}