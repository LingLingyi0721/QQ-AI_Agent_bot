using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using QQAgent.WinUI.Services;

namespace QQAgent.WinUI;

public partial class App : Application
{
    private Window? _window;
    private bool _selfTest;
    private DispatcherTimer? _selfTestTimer;
    private string? _screenshotPath;

    /// <summary>截图前先切页（自检用，参数 --nav &lt;标签&gt;）。</summary>
    private string? _screenshotNav;

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
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();

        _selfTest = argv.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
        _screenshotPath = GetOptionValue(argv, "--screenshot");
        _screenshotNav = GetOptionValue(argv, "--nav");

        Main = new MainWindow();
        _window = Main;
        _window.Activate();

        if (_selfTest)
        {
            _selfTestTimer = new DispatcherTimer();
            _selfTestTimer.Interval = new TimeSpan(0, 0, 2);
            _selfTestTimer.Tick += OnSelfTestTimerTick;
            _selfTestTimer.Start ();
            return;
        }

        if (_screenshotPath is not null)
        {
            _ = RunScreenshotAsync(_screenshotPath);
        }
    }

    /// <summary>取 `--key value` 形式的参数值，缺失返回 null。</summary>
    private static string? GetOptionValue(string[] argv, string key)
    {
        for (var i = 0; i < argv.Length - 1; i++)
        {
            if (string.Equals(argv[i], key, StringComparison.OrdinalIgnoreCase))
            {
                var value = argv[i + 1];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    /// <summary>
    /// 自检截图：等窗口稳定 → 截收起态 → 延时展开侧栏 → 再截展开态 → 退出。
    /// 用于验证侧栏白底是否消失、展开动画是否生效。
    /// </summary>
    private async Task RunScreenshotAsync(string path)
    {
        try
        {
            await Task.Delay(800);

            if (!string.IsNullOrWhiteSpace(_screenshotNav))
            {
                Main?.NavigateForTest(_screenshotNav);
                await Task.Delay(600);
            }

            if (_window?.Content is not UIElement root)
            {
                Environment.Exit(2);
                return;
            }

            await CaptureAsync(root, path);
            await Task.Delay(300);

            if (Main is not null && !Main.IsPaneOpenNow)
            {
                Main.OpenPaneForTest();
            }

            await Task.Delay(500);

            var dir = Path.GetDirectoryName(path);
            var openPath = Path.Combine(
                string.IsNullOrEmpty(dir) ? "." : dir,
                Path.GetFileNameWithoutExtension(path) + "_open.png");
            await CaptureAsync(root, openPath);

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            WriteErrorLog(ex, "Screenshot");
            Environment.Exit(3);
        }
    }

    /// <summary>把元素树渲染成 PNG 落盘（系统背景层不进这张图，因此可用来判定画刷是否透明）。</summary>
    private static async Task CaptureAsync(UIElement root, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);

        var width = (uint)bitmap.PixelWidth;
        var height = (uint)bitmap.PixelHeight;
        var pixels = await bitmap.GetPixelsAsync();

        var bytes = new byte[pixels.Length];
        using (var pixelReader = DataReader.FromBuffer(pixels))
        {
            pixelReader.ReadBytes(bytes);
        }

        using var memory = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, bytes);
        await encoder.FlushAsync();

        var png = new byte[memory.Size];
        using (var reader = new DataReader(memory.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)memory.Size);
            reader.ReadBytes(png);
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(path, png);
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
