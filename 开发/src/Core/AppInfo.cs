namespace DeviceBattery
{
    /// <summary>
    /// 版本号的唯一来源。修改版本时只改这里，避免帮助文本、设置、诊断各处对不上。
    /// （AssemblyInfo.cs 里的程序集版本需要同步修改，那是由编译器读取的程序集属性。）
    /// </summary>
    internal static class AppInfo
    {
        /// <summary>完整版本号，例如 "1.0"。</summary>
        public const string Version = "1.0";

        /// <summary>界面里统一显示的版本文本，例如 "VoltPick v1.0"。</summary>
        public const string DisplayName = "VoltPick v" + Version;


        /// <summary>作者署名。设置页、程序集信息和文档都用这一处。</summary>
        public const string Author = "HalfDone";

        /// <summary>反馈邮箱，复制按钮和写信链接共用。</summary>
        public const string Contact = "3494384883@qq.com";

        /// <summary>GitHub 主页，作者行里的链接直接打开它。</summary>
        public const string HomePage = "https://github.com/HalfDone03";

        /// <summary>GitHub 的简短写法，省版面用（界面里就显示这个）。</summary>
        public const string HomePageShort = "github.com/HalfDone03";
    }
}
