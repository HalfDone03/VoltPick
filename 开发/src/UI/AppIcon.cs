using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeviceBattery
{
    /// <summary>
    /// 窗口图标。
    ///
    /// 图标资源只存一份：build.ps1 用 /win32icon 把 assets\app.ico 打进 exe，
    /// 窗口则从 exe 自己身上把这张图标取回来设置给 Form.Icon。
    /// 这样换图标只要重跑 build.ps1，不用再在源码里塞一份 .ico 或 .resx，
    /// 也不会出现「exe 图标换了、标题栏还是旧的 WinForms 默认图标」这种不一致。
    ///
    /// 为什么不用 new Icon(exePath)：.NET 只认 .ico 文件流，传 exe 路径会抛
    /// ArgumentException（参数"picture"必须是可用作 Icon 的图片），必须走 shell 的关联图标接口。
    /// </summary>
    internal static class AppIcon
    {
        private static Icon _window;
        private static Icon _small;
        private static bool _loaded;

        /// <summary>大图标（32×32 起，随系统 DPI 变化），给窗口和 Alt+Tab 用。</summary>
        internal static Icon Window
        {
            get
            {
                EnsureLoaded();
                return _window;
            }
        }

        /// <summary>小图标（16×16），个别自绘位置要贴标题栏尺寸时用。</summary>
        internal static Icon Small
        {
            get
            {
                EnsureLoaded();
                return _small;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                Icon associated = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (associated != null)
                {
                    // ExtractAssociatedIcon 返回的 Icon 引用了系统句柄，Clone 一份自己持有，
                    // 免得原对象被回收时把句柄一起销毁。
                    _window = (Icon)associated.Clone();
                    associated.Dispose();
                }
            }
            catch (Exception)
            {
                _window = null;
            }

            try
            {
                Icon small = new Icon(_window, SystemInformation.SmallIconSize);
                _small = (Icon)small.Clone();
                small.Dispose();
            }
            catch (Exception)
            {
                _small = _window;
            }
        }

        /// <summary>把 exe 自带的图标套到窗口上。取不到就保持 WinForms 默认图标，不抛。</summary>
        internal static void Apply(Form form)
        {
            if (form == null) return;
            Icon icon = Window;
            if (icon != null) form.Icon = icon;
        }
    }
}
