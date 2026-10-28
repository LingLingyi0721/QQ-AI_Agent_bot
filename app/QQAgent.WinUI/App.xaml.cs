using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QQAgent.WinUI.Services;

namespace QQAgent.WinUI;

public partial class App : Application
{
    private Window? _window;
    private bool _selfTest;
    private DispatcherTimer? _selfTestTimer;

    public static AppSettings Settings { get; private set; } = new();

    public static string? BotRoot { get; private set; }

    /// <summary>主窗口引用（WinUI 3 的 Application 没有 Windows 集合，只能自己记）。</summary>
    public static MainWindow? Main { get; private set; }

    public App()
    {
        InitializeComponent();
        Settings = AppSettings.Load();
        BotRoot = EnvConfig.FindBotRoot();

        UnhandledException += OnUiUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskUnobservedException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _selfTest = Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));

        Main = new MainWindow();
        _window = Main;
        _window.Activate();

        if (_selfTest)
        {
            _selfTestTimer = new DispatcherTimer();
            _selfTestTimer.Interval = new TimeSpan(0, 0, 2);
            _selfTestTimer.Tick += OnSelfTestTimerTick;
            _selfTestTimer.Start ();
        }
    }

    private void OnSelfTestTimerTick(object? sender, object e)
    {
        if (_selfTestTimer is not null)
        {
            _selfTestTimer.Stop ();
        }

        Environment.Exit(0);
    }

    /// <summary>把主题模式应用到主窗口（页面「外观」卡片即时调用）。</summary>
    public static void ApplyTheme(ThemeMode mode)
    {
        Main?.ApplyTheme(mode);
    }

    private void OnUiUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        WriteErrorLog(e.Exception, "UI.UnhandledException");
        if (_selfTest)
        {
            Environment.Exit(1);
            return;
        }

        ShowErrorDialog(e.Exception);
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString());
        WriteErrorLog(exception, "AppDomain.UnhandledException");
    }

    private void OnTaskUnobservedException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteErrorLog(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    private static string GetErrorLogPath()
    {
        var root = BotRoot ?? EnvConfig.FindBotRoot();
        return root is null
            ? Path.Combine(AppContext.BaseDirectory, "error.log")
            : Path.Combine(root, "app", "error.log");
    }

    private static void WriteErrorLog(Exception exception, string source)
    {
        try
        {
            var path = GetErrorLogPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var text = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ").Append(source).AppendLine()
                .Append(exception).AppendLine()
                .Append(new string('-', 60)).AppendLine()
                .ToString();

            File.AppendAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 日志本身失败就不再连锁
        }
    }

    private async void ShowErrorDialog(Exception exception)
    {
        try
        {
            if (_window?.Content is FrameworkElement root && root.XamlRoot is not null)
            {
                var detail = exception.GetType().Name + ": " + exception.Message
                             + Environment.NewLine + Environment.NewLine
                             + "详细信息已写入 app" + Path.DirectorySeparatorChar + "error.log";
                var dialog = new ContentDialog
                {
                    Title = "发生未处理的错误",
                    Content = detail,
                    CloseButtonText = "关闭",
                    XamlRoot = root.XamlRoot,
                };
                await dialog.ShowAsync ();
            }
        }
        catch (Exception)
        {
            // 弹窗失败不影响进程存活
        }
    }
}
