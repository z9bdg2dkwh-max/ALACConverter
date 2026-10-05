using ALACConverter.Services;
using Microsoft.UI.Xaml;

namespace ALACConverter
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            InitializeComponent();

            // 用户选择优先于系统默认
            ThemeService.Apply(ThemeService.Current);

            UnhandledException += (_, e) =>
            {
                System.Diagnostics.Debug.WriteLine(e.Exception);
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }
    }
}
