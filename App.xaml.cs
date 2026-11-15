using System;
using System.IO;
using ALACConverter.Services;
using Microsoft.UI.Xaml;

namespace ALACConverter
{
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// 启动与崩溃日志。免打包的图形程序没有控制台可看，
        /// 进程无声退出时只能靠这份记录定位卡在哪一步。
        /// </summary>
        internal static void Trace(string msg)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ALACConverter");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "startup.log"),
                    $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
            }
            catch { /* 日志写不了不能影响启动 */ }
        }

        public App()
        {
            Trace("=== App 构造 ===");
            InitializeComponent();

            // 用户选择优先于系统默认
            ThemeService.Apply(ThemeService.Current);
            Trace($"主题已应用：{ThemeService.Current}");

            // 未捕获异常：既写日志也保留调试输出。
            // 不设 e.Handled —— 吞掉异常会让程序停在自相矛盾的状态，
            // 宁可让它按原样退出，也要留下一份可查的现场。
            UnhandledException += (_, e) =>
            {
                Trace("未捕获异常：" + e.Message + Environment.NewLine + e.Exception);
                System.Diagnostics.Debug.WriteLine(e.Exception);
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Trace("OnLaunched 进入");
            _window = new MainWindow();
            Trace("MainWindow 已构造");
            _window.Activate();
            Trace("MainWindow 已 Activate —— 窗口应当可见");
        }
    }
}
