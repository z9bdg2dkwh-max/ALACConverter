using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WColor = Windows.UI.Color;

namespace ALACConverter.Services
{
    public enum AppTheme
    {
        FollowSystem = 0,
        Light = 1,
        Dark = 2
    }

    /// <summary>
    /// 深浅色主题管理：默认跟随系统，用户可手动覆盖，选择持久化到配置文件。
    ///
    /// 弃用 Mica 毛玻璃的原因：Mica 会透出系统的强调色。若用户系统主题为暖色
    /// （如酒红），窗口背景会整体泛红，与界面的中性灰卡片严重冲突，观感脏乱。
    /// 改为纯色背板，浅色/深色各一套中性灰，保证任何系统主题下观感一致。
    ///
    /// 另：本程序以 WindowsPackageType=None 发布（免打包应用），
    /// ApplicationData.Current 会抛 0x80073D54（进程无程序包标识符），
    /// 因此配置持久化走 %LOCALAPPDATA% 下的普通文本文件。
    /// </summary>
    public static class ThemeService
    {
        private static readonly List<Window> Windows = new();

        /// <summary>主题实际变更时触发，供窗口刷新背板与配色。</summary>
        public static event EventHandler<AppTheme>? ThemeChanged;

        /// <summary>浅色模式窗口底色（中性灰白）</summary>
        public static WColor LightBackground => WColor.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

        /// <summary>深色模式窗口底色（中性深灰）</summary>
        public static WColor DarkBackground => WColor.FromArgb(0xFF, 0x20, 0x20, 0x20);

        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ALACConverter", "theme.cfg");

        /// <summary>零散偏好（窗口尺寸/位置等）另存一个文件，不动主题那份单行配置。</summary>
        private static string PrefsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ALACConverter", "prefs.cfg");

        /// <summary>读一个偏好键；不存在或读失败返回空串。</summary>
        public static string Get(string key)
        {
            try
            {
                if (!File.Exists(PrefsPath)) return string.Empty;
                foreach (var line in File.ReadAllLines(PrefsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && line.Substring(0, eq) == key)
                        return line.Substring(eq + 1);
                }
            }
            catch
            {
                // 读不出来按「没有存过」处理
            }
            return string.Empty;
        }

        /// <summary>写一个偏好键。文件只有几行，整读整写即可。</summary>
        public static void Put(string key, string value)
        {
            try
            {
                var dir = Path.GetDirectoryName(PrefsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var map = new Dictionary<string, string>();
                if (File.Exists(PrefsPath))
                {
                    foreach (var line in File.ReadAllLines(PrefsPath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) map[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
                }
                map[key] = value;

                var sb = new System.Text.StringBuilder();
                foreach (var kv in map)
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                File.WriteAllText(PrefsPath, sb.ToString());
            }
            catch
            {
                // 存不下来不影响使用
            }
        }

        public static void Register(Window w)
        {
            if (!Windows.Contains(w)) Windows.Add(w);
        }

        /// <summary>
        /// 窗口关闭时注销。
        /// 静态列表若继续持有已关闭的 Window，整棵 XAML 可视化树都无法被回收，
        /// 反复开关窗口时内存只涨不落。必须在 Closed 里显式移除。
        /// </summary>
        public static void Unregister(Window w) => Windows.Remove(w);

        public static AppTheme Current
        {
            get
            {
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        var text = File.ReadAllText(ConfigPath).Trim();
                        if (Enum.TryParse(text, out AppTheme parsed))
                            return parsed;
                    }
                }
                catch
                {
                    // 配置读取失败时退回默认，不影响启动
                }
                return AppTheme.FollowSystem;
            }
        }

        /// <summary>解析后的实际生效状态（已把「跟随系统」落实为确定值）。</summary>
        public static bool IsDarkEffective => Current switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => Application.Current?.RequestedTheme == ApplicationTheme.Dark
        };

        /// <summary>当前生效的窗口底色。</summary>
        public static WColor CurrentBackground =>
            IsDarkEffective ? DarkBackground : LightBackground;

        /// <summary>在 跟随系统 → 浅色 → 深色 之间循环切换。</summary>
        public static void Cycle() => Apply(Current switch
        {
            AppTheme.FollowSystem => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.FollowSystem
        });

        public static void Apply(AppTheme theme)
        {
            Save(theme);

            var element = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            // 倒序遍历：顺手把已关闭（Content 已释放）的窗口清出列表
            for (int i = Windows.Count - 1; i >= 0; i--)
            {
                var w = Windows[i];
                if (w.Content is FrameworkElement fe)
                {
                    fe.RequestedTheme = element;
                    // WinUI 3 的 Window 与 FrameworkElement 均无 Background 属性，
                    // 底色只能设在 Panel（Grid/StackPanel 等）根元素上
                    if (fe is Microsoft.UI.Xaml.Controls.Panel panel)
                        panel.Background = new SolidColorBrush(CurrentBackground);
                }
                else
                {
                    Windows.RemoveAt(i);
                }
            }

            ThemeChanged?.Invoke(null, theme);
        }

        private static void Save(AppTheme theme)
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath, theme.ToString());
            }
            catch
            {
                // 持久化失败不影响使用
            }
        }
    }
}
