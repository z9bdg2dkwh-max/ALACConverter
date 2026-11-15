using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ALACConverter.Models;
using ALACConverter.Services;
using Windows.Storage.Streams;
using DataPackage = Windows.ApplicationModel.DataTransfer.DataPackage;
using Clipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using DataPackageOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation;
using StandardDataFormats = Windows.ApplicationModel.DataTransfer.StandardDataFormats;
using Windows.System;
using Windows.UI;
using Microsoft.UI.Xaml.Data;
using WinRT.Interop;

namespace ALACConverter
{
    public sealed partial class MainWindow : Window
    {
        private readonly ObservableCollectionShim _jobs = new();
        private readonly ConvertSettings _settings = new();
        private readonly List<string> _logLines = new();
        private CancellationTokenSource? _batchCts;
        private bool _engineReady;

        /// <summary>通知是否注册成功。失败时静默跳过，绝不影响转换主流程。</summary>
        private bool _notificationsReady;

        /// <summary>任务栏进度（ITaskbarList3）。取不到就整段跳过，不影响转换。</summary>
        private ITaskbarList3? _taskbar;
        private IntPtr _hwnd;

        /// <summary>命令行带了 --auto：引擎就绪后自动开转，不用再点按钮。</summary>
        private bool _autoStartFromCommandLine;

        /// <summary>任务栏进度出错只记一次日志，避免高频刷新刷爆日志。</summary>
        private bool _taskbarErrorLogged;

        private static readonly string[] SupportedExtensions =
        {
            ".mp3", ".flac", ".wav", ".aiff", ".aif", ".ape", ".wma", ".m4a", ".aac",
            ".ogg", ".opus", ".alac", ".dsf", ".dff", ".mp4", ".m4v", ".mov", ".mkv",
            ".wavpack", ".wv", ".tta", ".tak", ".mka", ".webm", ".mpc", ".ac3", ".dts"
        };

        public MainWindow()
        {
            // 绑定 UI 线程调度器：转码在后台线程进行，AudioJob 的属性变更通知
            // 必须经此切回 UI 线程，否则抛 0x8001010E（RPC_E_WRONG_THREAD）。
            // 须在任何 AudioJob 实例创建之前调用。
            _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            AudioJob.BindUiThread(_uiQueue);

            InitializeComponent();

            // 使用纯色背板而非 Mica 毛玻璃：Mica 会透出系统强调色，
            // 用户系统为暖色主题时窗口整体泛红，与中性灰卡片冲突。
            SystemBackdrop = null;
            RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(ThemeService.CurrentBackground);
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDrag);
            ApplyWindowIcon();
            ApplyTitleBarMetrics();
            AppWindow.Changed += OnWindowChanged;
            RestoreWindowBounds();   // 有历史尺寸/位置就恢复，没有才按屏幕自适应

            // 记录工作区尺寸，窗口被用户改动大小时按屏幕限制回弹
            _workArea = GetWorkAreaSize();

            ThemeService.Register(this);
            ThemeService.ThemeChanged += OnThemeChanged;

            JobsView.ItemsSource = _jobs.Items;
            RestoreParallelism();
            UpdateEmptyState();
            UpdateThemeGlyph();

            if (RootGrid != null)
                RootGrid.Loaded += OnLoaded;

            // 关闭时必须解开静态引用，否则窗口连同整棵 XAML 树不会被回收
            Closed += OnClosed;
        }

        private void OnThemeChanged(object? sender, AppTheme theme)
        {
            // 纯色背板：随主题刷新内容根元素底色（Window 本身无 Background）
            if (RootGrid != null)
                RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(ThemeService.CurrentBackground);
        }

        /// <summary>
        /// 窗口关闭：解开所有静态挂载点并停掉后台转码。
        ///
        /// ThemeService.Windows 与 ThemeService.ThemeChanged 都是静态成员，
        /// 静态字段持有 Window 等价于把整棵可视化树钉死在内存里，
        /// 只靠运行时 GC 是收不掉的（Windows App SDK 2.6.4 改善的是
        /// 框架侧对不可达 WinUI 对象的清理，应用自己攥的强引用仍需手动放开）。
        /// 同时取消 _batchCts，避免关窗后 ffmpeg 子进程继续写文件。
        /// </summary>
        private void OnClosed(object sender, WindowEventArgs args)
        {
            // 先存几何，再解绑——解绑后窗口句柄可能已经失效
            try { SaveWindowBounds(); } catch { }
            try { AppWindow.Changed -= OnWindowChanged; } catch { }
            try { ThemeService.ThemeChanged -= OnThemeChanged; } catch { }
            try { ThemeService.Unregister(this); } catch { }
            try { _batchCts?.Cancel(); } catch { }
            try { _batchCts?.Dispose(); } catch { }
            _batchCts = null;
        }

        private void UpdateThemeGlyph()
        {
            if (ThemeGlyph == null) return;
            ThemeGlyph.Glyph = ThemeService.Current switch
            {
                AppTheme.Light => GLYPH_SUN,
                AppTheme.Dark => GLYPH_MOON,
                _ => GLYPH_AUTO
            };
            var tip = ThemeService.Current switch
            {
                AppTheme.Light => "当前：浅色（点击切换为深色）",
                AppTheme.Dark => "当前：深色（点击切换为跟随系统）",
                _ => "当前：跟随系统（点击切换为浅色）"
            };
            ToolTipService.SetToolTip(ThemeButton, tip);
        }

        /// <summary>恢复上次选的并行数；没有记录就保持 XAML 里的默认值（4 个）。</summary>
        private void RestoreParallelism()
        {
            try
            {
                var saved = ThemeService.Get("parallel");
                if (!int.TryParse(saved, out int pv) || pv < 1) return;

                foreach (var obj in ParallelBox.Items)
                {
                    if (obj is ComboBoxItem item && item.Tag?.ToString() == pv.ToString())
                    {
                        ParallelBox.SelectedItem = item;
                        return;
                    }
                }
            }
            catch { /* 取不到就用默认值 */ }
        }

        /// <summary>读当前并行数；控件异常时退回 1，等同于旧的串行行为。</summary>
        private int ReadParallelism()
        {
            if (ParallelBox?.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out int n) && n >= 1)
                return n;
            return 1;
        }

        /// <summary>队列为空时显示拖放引导，有内容时隐藏。</summary>
        private void UpdateEmptyState()
        {
            if (EmptyState == null || JobsView == null) return;
            var empty = _jobs.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            JobsView.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------------- 引擎准备 ----------------

        /// <summary>
        /// 按系统标题栏实测高度与按钮区宽度，修正自绘内容的垂直/水平位置。
        /// 不写死数值：不同 Windows 缩放与窗口状态下系统标题栏尺寸会变化。
        /// </summary>
        /// <summary>
        /// 窗口尺寸/状态变化时重新套用标题栏尺寸：
        /// 最大化时系统按钮区会变宽（多了「还原」），最小化/还原也会变。
        /// </summary>
        private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidSizeChange || args.DidPresenterChange)
                ApplyTitleBarMetrics();

            // 用户把窗口拉大到超出屏幕时，拉回工作区内，保证内容始终完整可见
            if (args.DidSizeChange && _workArea.Width > 0)
            {
                int maxW = _workArea.Width - 24;
                int maxH = _workArea.Height - 24;
                if (sender.Size.Width > maxW || sender.Size.Height > maxH)
                    sender.Resize(new Windows.Graphics.SizeInt32(
                        Math.Min(sender.Size.Width, maxW),
                        Math.Min(sender.Size.Height, maxH)));
            }
        }

        private (int Width, int Height) _workArea;

        /// <summary>
        /// 恢复上次关闭时的窗口尺寸与位置；没有记录或记录明显不合理时退回屏幕自适应。
        ///
        /// 位置做了一次粗略范围检查：显示器拔掉后旧坐标可能落在不存在的区域，
        /// 直接 Move 过去窗口就再也看不见了。
        /// </summary>
        private void RestoreWindowBounds()
        {
            try
            {
                var ws = ThemeService.Get("winW");
                var hs = ThemeService.Get("winH");
                if (ws.Length == 0 || hs.Length == 0) { FitWindowToWorkArea(); return; }

                if (!int.TryParse(ws, out int w) || !int.TryParse(hs, out int h))
                {
                    FitWindowToWorkArea();
                    return;
                }

                // 小于设计下限视为脏数据；1000 是文字不被裁切的最小可用宽度
                if (w < 1000 || h < 640) { FitWindowToWorkArea(); return; }

                AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

                if (int.TryParse(ThemeService.Get("winX"), out int x) &&
                    int.TryParse(ThemeService.Get("winY"), out int y) &&
                    x > -20000 && x < 20000 && y > -20000 && y < 20000)
                {
                    AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
                }
            }
            catch (Exception ex)
            {
                App.Trace("恢复窗口尺寸失败：" + ex.Message);
                FitWindowToWorkArea();
            }
        }

        /// <summary>
        /// 记录当前窗口几何。最大化/最小化时不覆盖已存的还原尺寸，
        /// 否则下次启动会得到一个铺满屏幕的窗口，用户还得手动调回来。
        /// </summary>
        private void SaveWindowBounds()
        {
            try
            {
                if (AppWindow.Presenter is not OverlappedPresenter p ||
                    p.State != OverlappedPresenterState.Restored)
                    return;

                ThemeService.Put("winW", AppWindow.Size.Width.ToString());
                ThemeService.Put("winH", AppWindow.Size.Height.ToString());
                ThemeService.Put("winX", AppWindow.Position.X.ToString());
                ThemeService.Put("winY", AppWindow.Position.Y.ToString());
            }
            catch
            {
                // 存不下来不影响关闭
            }
        }

        /// <summary>
        /// 按当前屏幕工作区设置窗口尺寸，保证启动即完整显示、不被屏幕边缘裁切。
        /// 取「预设尺寸」与「工作区减去边距」的较小值——小屏自动收缩，大屏用预设尺寸。
        /// </summary>
        private void FitWindowToWorkArea()
        {
            const int PreferredWidth = 1180;
            const int PreferredHeight = 860;
            const int Margin = 48;   // 四周留白，避免紧贴屏幕边缘

            try
            {
                var area = GetWorkAreaSize();
                int width = Math.Min(PreferredWidth, Math.Max(900, area.Width - Margin));
                int height = Math.Min(PreferredHeight, Math.Max(620, area.Height - Margin));
                AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            }
            catch
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(PreferredWidth, PreferredHeight));
            }
        }

        /// <summary>取当前显示器工作区尺寸（DPI 感知，物理像素）。</summary>
        /// <summary>
        /// 设置窗口图标（标题栏 / Alt-Tab）。
        ///
        /// AppWindow.SetIcon 在 Windows App SDK 1.7 只接受 IconId（由 HICON 转换而来），
        /// 不接受 PNG 数据流或 .ico 路径，故走 Win32 三步：
        /// LoadImage 载入 .ico → GetIconIdFromIcon 转 IconId → SetIcon。
        /// exe 本身的图标由 csproj 的 ApplicationIcon 负责，此处只管窗口。
        /// </summary>
        private void ApplyWindowIcon()
        {
            try
            {
                var icoPath = Path.Combine(AppContext.BaseDirectory, "ALACConverter.ico");
                if (!File.Exists(icoPath)) return;

                // 不指定尺寸时由系统取 ICO 内最大帧
                var hIcon = LoadImage(IntPtr.Zero, icoPath,
                    ImageType.IMAGE_ICON, 0, 0,
                    LoadImageFlags.LR_LOADFROMFILE | LoadImageFlags.LR_DEFAULTSIZE);

                if (hIcon == IntPtr.Zero) return;

                AppWindow.SetIcon(Win32Interop.GetIconIdFromIcon(hIcon));
            }
            catch (Exception ex)
            {
                // 图标加载失败不应影响启动
                System.Diagnostics.Debug.WriteLine("[Icon] " + ex.Message);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr hInst, string name,
            ImageType type, int cx, int cy, LoadImageFlags fuLoad);

        private enum ImageType : uint { IMAGE_ICON = 1 }

        [Flags]
        private enum LoadImageFlags : uint
        {
            LR_DEFAULTSIZE = 0x00000040,
            LR_LOADFROMFILE = 0x00000010
        }

        private (int Width, int Height) GetWorkAreaSize()
        {
            // WinUI 3 中窗口标识来自 AppWindow.Id；WorkArea 本身即 RectInt32
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            var r = area.WorkArea;
            return (r.Width, r.Height);
        }

        private void ApplyTitleBarMetrics()
        {
            try
            {
                // 系统标题栏实际高度（不含 DPI 缩放），通常为 32px
                int sysHeight = AppWindow.TitleBar.Height;
                // 系统最小化/最大化/关闭按钮区总宽度
                double buttonsWidth = AppWindow.TitleBar.RightInset;

                // 标题栏行高与系统一致，使自绘内容与系统按钮同一水平线
                TitleBarDrag.Height = sysHeight;
                // 右侧让出系统按钮区宽度（RightInset）。
                // ThemeButton 已置于 TitleBarDrag 内部且 HorizontalAlignment=Right，
                // 因此自动落在系统最小化/最大化/关闭按钮左侧，不会被遮挡。
                TitleBarDrag.Padding = new Thickness(20, 0, buttonsWidth, 0);

                // 按钮高度跟随标题栏，视觉上与系统按钮等高
                ThemeButton.Height = sysHeight;
                ThemeButton.Width = 36;
            }
            catch
            {
                // 取不到系统尺寸时退回保守值，不影响启动
                TitleBarDrag.Height = 32;
                ThemeButton.Height = 32;
            }
        }

        private void OnToggleTheme(object sender, RoutedEventArgs e)
        {
            ThemeService.Cycle();
            UpdateThemeGlyph();
        }


        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitNotifications();
            InitTaskbar();
            _ = PrepareEngineAsync();
            _ = CaptureSelfIfRequestedAsync();
            _ = NotifySelfTestAsync();
            EnqueueCommandLineFiles();
        }

        /// <summary>
        /// 注册系统通知。批量转换几十个文件时用户不会一直盯着窗口，
        /// 转完弹一条系统通知比界面上的一行字有用得多。
        ///
        /// 免打包（WindowsPackageType=None）应用走 SDK 的自动 AUMID，
        /// 注册可能因系统策略失败——失败就安静降级，不影响任何转换功能。
        /// </summary>
        private void InitNotifications()
        {
            try
            {
                var mgr = AppNotificationManager.Default;
                // 订阅激活事件：不订阅的话点击通知可能让进程处于未处理状态
                mgr.NotificationInvoked += (_, _) => { };
                mgr.Register();
                _notificationsReady = true;
                App.Trace("通知已注册");
            }
            catch (Exception ex)
            {
                _notificationsReady = false;
                App.Trace("通知注册失败：" + ex.Message);
            }
        }

        /// <summary>转换结束发一条通知，内容与界面横幅保持一致。</summary>
        private void NotifyDone(BatchSummary s)
        {
            if (!_notificationsReady) return;

            try
            {
                bool clean = s.Failed == 0 && s.Cancelled == 0;
                var notification = new AppNotificationBuilder()
                    .AddText(clean ? "转换完成" : "转换结束")
                    .AddText(clean
                        ? $"成功 {s.Succeeded} 个，耗时 {s.ElapsedSeconds:0.0} 秒"
                        : $"成功 {s.Succeeded}，失败 {s.Failed}，跳过 {s.Skipped}，取消 {s.Cancelled}")
                    .BuildNotification();

                AppNotificationManager.Default.Show(notification);
                App.Trace($"转换完成通知已发送：成功 {s.Succeeded}，失败 {s.Failed}");
            }
            catch (Exception ex)
            {
                App.Trace("发送通知失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 初始化任务栏进度条。WinUI 3 的 AppWindow 没有进度 API，
        /// 只能走 shell 的 ITaskbarList3；最小化后进度仍可见，这是它的价值所在。
        /// </summary>
        private void InitTaskbar()
        {
            try
            {
                var clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
                var type = Type.GetTypeFromCLSID(clsid);
                if (type == null) return;

                _taskbar = Activator.CreateInstance(type) as ITaskbarList3;
                _taskbar?.HrInit();
                _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            }
            catch (Exception ex)
            {
                _taskbar = null;
                App.Trace("任务栏进度初始化失败：" + ex.Message);
            }
        }

        private void SetTaskbarProgress(int done, int total)
        {
            if (_taskbar == null || _hwnd == IntPtr.Zero) return;
            try
            {
                if (total <= 0)
                {
                    _taskbar.SetProgressState(_hwnd, TaskbarProgressState.NoProgress);
                    return;
                }
                _taskbar.SetProgressState(_hwnd, TaskbarProgressState.Normal);
                _taskbar.SetProgressValue(_hwnd, (ulong)done, (ulong)total);
            }
            catch (Exception ex)
            {
                // 只记第一次：进度条每秒刷新一次，重复记录会把日志刷爆
                if (!_taskbarErrorLogged)
                {
                    _taskbarErrorLogged = true;
                    App.Trace("任务栏进度调用失败：" + ex.Message);
                }
            }
        }

        private void ClearTaskbarProgress()
        {
            if (_taskbar == null || _hwnd == IntPtr.Zero) return;
            try { _taskbar.SetProgressState(_hwnd, TaskbarProgressState.NoProgress); } catch { }
        }

        /// <summary>
        /// 通知自检：设置 ALAC_NOTIFY_TEST=1 启动后真发一条通知，把 Show 的结果写进日志。
        ///
        /// 「Register 成功」只说明注册没报错，不代表通知真能弹出来——
        /// 通知被系统静音、被专注助手拦截、AUMID 没落到开始菜单，
        /// 都表现为「注册成功但什么都看不见」。这个钩子就是用来区分这两种情况的。
        /// </summary>
        private async Task NotifySelfTestAsync()
        {
            var flag = Environment.GetEnvironmentVariable("ALAC_NOTIFY_TEST");
            if (string.IsNullOrEmpty(flag)) return;

            await Task.Delay(2000);

            if (!_notificationsReady)
            {
                App.Trace("通知自检：跳过（未注册成功）");
            }
            else
            {
                try
                {
                    var n = new AppNotificationBuilder()
                        .AddText("ALAC 通知自检")
                        .AddText("看到这条，说明转换完成通知可用")
                        .BuildNotification();
                    AppNotificationManager.Default.Show(n);
                    App.Trace("通知自检：Show 已调用，无异常");
                }
                catch (Exception ex)
                {
                    App.Trace("通知自检：Show 抛异常 —— " + ex.Message);
                }
            }

            await Task.Delay(1500);
            try { Application.Current.Exit(); } catch { }
        }

        /// <summary>
        /// 诊断用：把界面自身渲染成 PNG。
        ///
        /// 与 AvifHeifConverter 保持同一套手法——在看不到窗口的环境里
        /// （脚本启动、远程、CI）核对真实配色与布局，而不是靠文字描述去猜。
        /// 设置环境变量 ALAC_SHOT=&lt;输出路径&gt; 即触发，渲染完自动退出。
        /// </summary>
        private async Task CaptureSelfIfRequestedAsync()
        {
            var shot = Environment.GetEnvironmentVariable("ALAC_SHOT");
            if (string.IsNullOrWhiteSpace(shot)) return;

            // 等首帧排版与引擎状态稳定，否则截到的是空列表、未定位的标题栏
            await Task.Delay(1500);

            try
            {
                var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                await rtb.RenderAsync(RootGrid);
                var buf = await rtb.GetPixelsAsync();

                int w = rtb.PixelWidth, h = rtb.PixelHeight;
                var reader = Windows.Storage.Streams.DataReader.FromBuffer(buf);
                var bgra = new byte[buf.Length];
                reader.ReadBytes(bgra);

                var dir = Path.GetDirectoryName(Path.GetFullPath(shot));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                {
                    var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                        Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                    encoder.SetPixelData(
                        Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                        Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                        (uint)w, (uint)h, 96, 96, bgra);
                    await encoder.FlushAsync();

                    stream.Seek(0);
                    using var outFs = File.Create(shot);
                    await stream.AsStreamForRead().CopyToAsync(outFs);
                }

                AppendLog($"[诊断] 界面已渲染到 {shot}（{w}×{h}）");
            }
            catch (Exception ex)
            {
                AppendLog("[诊断] 渲染失败：" + ex.Message);
            }
            finally
            {
                try { Application.Current.Exit(); } catch { }
            }
        }

        private async Task PrepareEngineAsync()
        {
            EngineBadge.Text = "正在准备内置引擎…";
            EngineBadge.Foreground = new SolidColorBrush(Colors.Gray);
            try
            {
                var path = await FfmpegLocator.EnsureAsync(new Progress<string>(AppendLog));
                _engineReady = true;
                EngineBadge.Text = "内置引擎就绪";
                EngineBadge.Foreground = new SolidColorBrush(Colors.MediumSeaGreen);
                AppendLog("引擎路径：" + path);

                if (_autoStartFromCommandLine && _jobs.Count > 0)
                {
                    AppendLog("[命令行] 引擎就绪，自动开始转换");
                    OnStart(this, new RoutedEventArgs());
                }
            }
            catch (Exception ex)
            {
                _engineReady = false;
                EngineBadge.Text = "引擎准备失败";
                EngineBadge.Foreground = new SolidColorBrush(Colors.IndianRed);
                AppendLog("错误：" + ex.Message);
                ShowBanner("内置引擎加载失败，转换功能不可用：" + ex.Message, isError: true);
            }
        }

        // ---------------- 文件添加 ----------------

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
            e.DragUIOverride.Caption = "释放以添加";
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            var items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (var it in items)
            {
                if (it is Windows.Storage.StorageFile f) paths.Add(f.Path);
                else if (it is Windows.Storage.StorageFolder fd) CollectAudio(fd.Path, paths, 0);
            }
            AddPaths(paths);
        }

        private static void CollectAudio(string dir, List<string> acc, int depth)
        {
            if (depth > 3) return;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { return; }

            foreach (var f in files)
                if (SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    acc.Add(f);

            IEnumerable<string> sub;
            try { sub = Directory.EnumerateDirectories(dir); }
            catch { return; }

            foreach (var d in sub) CollectAudio(d, acc, depth + 1);
        }

        /// <summary>
        /// 把命令行传入的文件/目录加入队列。
        ///
        /// 用途：把文件（或文件夹）直接拖到 exe 图标上就能转，
        /// 也可以给常用目录建快捷方式。参数里的目录会递归展开成音频文件。
        /// 没有参数时整段直接返回，正常双击启动不受影响。
        /// </summary>
        private void EnqueueCommandLineFiles()
        {
            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { return; }
            if (args.Length <= 1) return;

            // --auto：入队完自动开转，配合「拖到 exe 图标」＝一键转换
            _autoStartFromCommandLine = args.Any(a =>
                a.Equals("--auto", StringComparison.OrdinalIgnoreCase));

            var paths = new List<string>();
            foreach (var a in args.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(a) || a.StartsWith("--", StringComparison.Ordinal)) continue;
                try
                {
                    if (File.Exists(a)) paths.Add(a);
                    else if (Directory.Exists(a)) CollectAudio(a, paths, 0);
                }
                catch { /* 单个路径无效不影响其余 */ }
            }

            if (paths.Count == 0) return;

            AddPaths(paths);
            AppendLog($"[命令行] 从启动参数收到 {paths.Count} 个文件");
        }

        private void AddPaths(IEnumerable<string> paths)
        {            var existing = new HashSet<string>(_jobs.Items.Select(j => j.SourcePath), StringComparer.OrdinalIgnoreCase);
            int added = 0;

            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (existing.Contains(p)) continue;
                if (!File.Exists(p)) continue;

                _jobs.Add(new AudioJob(p));
                existing.Add(p);
                added++;
            }

            UpdateSummary();
            UpdateEmptyState();
            AppendLog(added > 0 ? $"已添加 {added} 个文件" : "没有新增文件（可能重复或格式不支持）");
            HideBanner();
        }

        private async void OnPickFiles(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            foreach (var ext in SupportedExtensions) picker.FileTypeFilter.Add(ext);
            picker.FileTypeFilter.Add("*");

            var files = await picker.PickMultipleFilesAsync();
            AddPaths(files.Select(f => f.Path));
        }

        private async void OnPickFolder(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;

            var list = new List<string>();
            CollectAudio(folder.Path, list, 0);
            AddPaths(list);
        }

        private void OnClearAll(object sender, RoutedEventArgs e)
        {
            if (_jobs.IsRunning) return;
            _jobs.Clear();
            UpdateSummary();
            UpdateEmptyState();
            HideBanner();
        }

        // ---------------- 输出设置 ----------------

        private async void OnChooseOutput(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;

            _settings.OutputDirectory = folder.Path;
            OutputPathText.Text = folder.Path;
            OutputPathText.Visibility = Visibility.Visible;
        }

        private void OnSameAsSource(object sender, RoutedEventArgs e)
        {
            _settings.OutputDirectory = null;
            OutputPathText.Visibility = Visibility.Collapsed;
        }

        // ---------------- 执行 ----------------

        private async void OnStart(object sender, RoutedEventArgs e)
        {
            if (!_engineReady) { ShowBanner("内置引擎尚未就绪，请稍候重试。", isError: true); return; }
            if (_jobs.IsRunning) return;

            var pending = _jobs.Items.Where(j => j.Status == JobStatus.Waiting).ToList();
            if (pending.Count == 0)
            {
                ShowBanner(_jobs.Count == 0 ? "请先拖入音频文件。" : "没有待转换的文件。", isError: false);
                return;
            }

            _settings.OverwriteExisting = OverwriteCheck.IsChecked == true;
            _settings.VerifyOutput = VerifyCheck.IsChecked == true;
            _settings.MaxParallelism = ReadParallelism();
            ThemeService.Put("parallel", _settings.MaxParallelism.ToString());

            var enginePath = await FfmpegLocator.EnsureAsync(null);
            var engine = new TranscodeEngine(enginePath);
            var queue = new ConversionQueue(engine, _settings, AppendLog);

            _batchCts = new CancellationTokenSource();
            SetRunningUi(true);

            var progress = new Progress<int>(n =>
            {
                BatchProgress.Value = _jobs.Count == 0 ? 0 : n * 100.0 / _jobs.Count;
                BatchStatusText.Text = _settings.MaxParallelism > 1
                    ? $"正在处理 {n} / {_jobs.Count}（并行 {_settings.MaxParallelism}）"
                    : $"正在处理 {n} / {_jobs.Count}";
                SetTaskbarProgress(n, _jobs.Count);
            });

            try
            {
                var summary = await queue.RunAsync(pending, progress, _batchCts.Token);
                ShowSummary(summary);
            }
            catch (Exception ex)
            {
                ShowBanner("转换过程中发生错误：" + ex.Message, isError: true);
                AppendLog("异常：" + ex);
            }
            finally
            {
                SetRunningUi(false);
                ClearTaskbarProgress();
                _batchCts?.Dispose();
                _batchCts = null;
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            _batchCts?.Cancel();
            BatchStatusText.Text = "正在取消…";
        }

        private void SetRunningUi(bool running)
        {
            StartButton.IsEnabled = !running;
            PickFilesButton.IsEnabled = !running;
            PickFolderButton.IsEnabled = !running;
            ClearButton.IsEnabled = !running;
            CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (PickFilesButton != null) PickFilesButton.IsEnabled = !running;

            if (running)
            {
                BatchProgress.IsIndeterminate = false;
                BatchProgress.Value = 0;
                BatchStatusText.Text = "正在处理 0 / " + _jobs.Count;
                HideBanner();
            }
        }

        // ---------------- 打开产出目录 ----------------

        private void OnOpenOutput(object sender, RoutedEventArgs e)
        {
            var target = _settings.OutputDirectory;
            if (string.IsNullOrWhiteSpace(target))
            {
                var first = _jobs.Items.FirstOrDefault(j => !string.IsNullOrEmpty(j.OutputPath));
                target = first != null ? Path.GetDirectoryName(first.OutputPath) : null;
            }

            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            {
                ShowBanner("尚未生成输出文件。", isError: false);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }

        private void OnRemoveJob(object sender, RoutedEventArgs e)
        {
            if (_jobs.IsRunning) return;
            if (sender is FrameworkElement fe && fe.DataContext is AudioJob job)
            {
                _jobs.Remove(job);
                UpdateSummary();
                UpdateEmptyState();
            }
        }

        // ---------------- 汇总与日志 ----------------

        private void ShowSummary(BatchSummary s)
        {
            UpdateSummary();

            if (s.Failed == 0 && s.Cancelled == 0)
            {
                string extra = s.Skipped > 0 ? $"，跳过 {s.Skipped} 个" : "";
                ShowBanner($"全部完成：成功 {s.Succeeded} 个{extra}，耗时 {s.ElapsedSeconds:0.0} 秒，"
                           + $"共生成 {AudioJob.FormatSize(s.BytesWritten)}。可直接添加到 iTunes / 音乐 App。",
                           isError: false);
            }
            else
            {
                ShowBanner($"完成：成功 {s.Succeeded}，失败 {s.Failed}，跳过 {s.Skipped}，取消 {s.Cancelled}。"
                           + "失败详情见下方日志。", isError: true);
            }

            BatchStatusText.Text = $"已完成 {s.Total} 个任务，耗时 {s.ElapsedSeconds:0.0} 秒";

            // 窗口可能已被最小化或切到后台，补一条系统通知
            NotifyDone(s);
        }

        private const string GLYPH_SUN = "\uE706";
        private const string GLYPH_MOON = "\uE708";
        private const string GLYPH_AUTO = "\uE769";

        private void UpdateSummary()
        {
            JobCountText.Text = _jobs.Count == 0
                ? "队列为空"
                : $"队列 {_jobs.Count} 个文件 · 合计 {AudioJob.FormatSize(_jobs.Items.Sum(j => j.InputBytes))}";
        }

        /// <summary>
        /// 追加一行日志。本方法会被 ffmpeg 的后台输出回调直接调用，
        /// 因此必须先把工作切回 UI 线程——否则写 TextBlock 会抛
        /// 0x8001010E（RPC_E_WRONG_THREAD）。
        /// </summary>
        /// <summary>
        /// UI 线程调度器，在构造函数中于 UI 线程捕获一次。
        /// 不可惰性调用 GetForCurrentThread()——该方法在后台线程会返回 null。
        /// </summary>
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

        private void AppendLog(string line)
        {
            var q = _uiQueue;
            if (q is null || q.HasThreadAccess)
            {
                AppendLogOnUi(line);
                return;
            }

            if (!q.TryEnqueue(() => AppendLogOnUi(line)))
            {
                System.Diagnostics.Debug.WriteLine("[Log] UI 调度失败：" + line);
            }
        }

        /// <summary>在 UI 线程上执行实际的日志写入。</summary>
        private void AppendLogOnUi(string line)
        {
            var ts = DateTime.Now.ToString("HH:mm:ss");
            var entry = $"[{ts}] {line}";
            _logLines.Add(entry);
            if (_logLines.Count > 500) _logLines.RemoveAt(0);
            LogText.Text = string.Join('\n', _logLines);

            // 强制滚动到最新一行
            if (LogScroll != null)
                LogScroll.ChangeView(null, null, null, disableAnimation: true);
        }

        private void ShowBanner(string msg, bool isError)
        {
            var q = _uiQueue;
            if (q is null || q.HasThreadAccess)
            {
                ShowBannerOnUi(msg, isError);
                return;
            }
            q.TryEnqueue(() => ShowBannerOnUi(msg, isError));
        }

        private void ShowBannerOnUi(string msg, bool isError)
        {
            BannerText.Text = msg;
            BannerText.Foreground = new SolidColorBrush(isError ? Colors.Firebrick : Colors.SeaGreen);
            BannerBorder.Visibility = Visibility.Visible;
        }

        private void HideBanner()
        {
            BannerBorder.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 复制日志到剪贴板。
        ///
        /// 免打包应用（WindowsPackageType=None）里操作 Clipboard 必须先把窗口 HWND
        /// 传给 DataPackage，否则 Clipboard.SetContent 会抛
        /// 0x800401E5（RPC_E_DISCONNECTED）或直接闪退。
        /// InitializeWithWindow 是 COM 接口，DataPackage 需显式实现 IInitializeWithWindow
        /// 才能接收 HWND——直接调用静态方法无效，会闪退。
        /// 另：SetContent 失败会抛 COMException，必须捕获，否则整个应用崩溃。
        /// </summary>
        private void OnCopyLog(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = string.Join('\n', _logLines);
                if (string.IsNullOrEmpty(text))
                {
                    ShowBanner("日志为空，无需复制。", isError: false);
                    return;
                }

                // 按 WinUI 官方文档的标准写法：DataPackage 设好内容后直接 SetContent + Flush。
                //
                // 不要对 DataPackage 调用 InitializeWithWindow.Initialize：
                // 该接口用于 FileOpenPicker 等需要父窗口的对象，
                // DataPackage 并未在托管侧暴露 IInitializeWithWindow，强转会抛
                // "Specified cast is not valid"（此前崩溃/报错的主因）。
                var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                dp.SetText(text);

                Clipboard.SetContent(dp);
                Clipboard.Flush();

                ShowBanner($"日志已复制到剪贴板（{_logLines.Count} 行）。", isError: false);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                // 剪贴板可能被其他程序占用
                ShowBanner("复制失败：剪贴板被占用，请稍后重试。", isError: true);
                AppendLog("复制日志失败（COMException）：" + ex.Message);
            }
            catch (Exception ex)
            {
                ShowBanner("复制失败：" + ex.Message, isError: true);
                AppendLog("复制日志失败：" + ex);
            }
        }

        private void OnClearLog(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            LogText.Text = string.Empty;
        }
    }

    /// <summary>轻量集合包装，隔离 UI 层与集合类型的耦合。</summary>
    internal sealed class ObservableCollectionShim
    {
        public System.Collections.ObjectModel.ObservableCollection<AudioJob> Items { get; } = new();
        public bool IsRunning { get; set; }
        public int Count => Items.Count;
        public void Add(AudioJob j) => Items.Add(j);
        public void Remove(AudioJob j) => Items.Remove(j);
        public void Clear() => Items.Clear();
    }

    /// <summary>任务栏进度状态（shellapi.h 的 TBPFLAG）。</summary>
    internal enum TaskbarProgressState
    {
        NoProgress = 0x0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    /// <summary>
    /// ITaskbarList3 的最小投影：只声明到 SetProgressState 为止。
    ///
    /// COM 接口按 vtable 顺序调用，所以方法必须从 ITaskbarList → ITaskbarList2 →
    /// ITaskbarList3 依次列出，**不能只写用到的那两个**，否则调用到的就是别的方法。
    /// 后面的缩略图按钮等方法用不到，可以省略。
    /// </summary>
    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [System.Runtime.InteropServices.InterfaceType(
        System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarList3
    {
        // ---- ITaskbarList ----
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ---- ITaskbarList2 ----
        void MarkFullscreenWindow(IntPtr hwnd,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            bool fFullscreen);

        // ---- ITaskbarList3 ----
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TaskbarProgressState tbpFlags);
    }
}
