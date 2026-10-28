using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QQAgent.Gui;

public partial class App : Application
{
    private const string SelftestArg = "--selftest";
    private const string ScreenshotArg = "--screenshot";

    private string? _pendingShot;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var args = e.Args ?? Array.Empty<string>();
        MainWindow = new MainWindow();

        var shotPath = ReadOptionValue(args, ScreenshotArg);
        if (shotPath is not null && shotPath.Length != 0)
        {
            _pendingShot = shotPath;
            MainWindow.ContentRendered += OnContentRendered;
        }

        MainWindow.Show();

        if (args.Any(a => string.Equals(a, SelftestArg, StringComparison.OrdinalIgnoreCase)))
        {
            StartSelftestTimer();
        }
    }

    private void StartSelftestTimer()
    {
        var tick = new DispatcherTimer();
        tick.Interval = TimeSpan.FromSeconds(2);
        tick.Tick += delegate
        {
            tick.Stop();
            Shutdown(0);
        };
        tick.Start();
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        var path = _pendingShot;
        _pendingShot = null;
        if (path is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => CaptureAndExit(path)), DispatcherPriority.ApplicationIdle);
    }
    private static string? ReadOptionValue(string[] args, string option)
    {
        for (var i = 0; i <= args.Length - 1; i++)
        {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 <= args.Length - 1 ? args[i + 1] : "";
            }

            var prefix = option + "=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i].Substring(prefix.Length);
            }
        }

        return null;
    }

    private void CaptureAndExit(string path)
    {
        try
        {
            var window = MainWindow;
            if (window is null)
            {
                Shutdown(1);
                return;
            }

            window.UpdateLayout();
            var root = window.Content as FrameworkElement;
            if (root is null)
            {
                throw new InvalidOperationException("窗口没有可渲染的内容");
            }

            var width = (int)Math.Round(root.ActualWidth);
            var height = (int)Math.Round(root.ActualHeight);
            if (width <= 0 || height <= 0)
            {
                width = (int)Math.Round(window.ActualWidth);
                height = (int)Math.Round(window.ActualHeight);
            }

            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("窗口尺寸无效");
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);

            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(full))
            {
                encoder.Save(stream);
            }

            Shutdown(0);
        }
        catch (Exception ex)
        {
            WriteErrorLog(ex);
            Shutdown(1);
        }
    }
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        if (ex is null)
        {
            return;
        }

        Report(ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Report(e.Exception);
        e.SetObserved();
    }

    private void Report(Exception ex)
    {
        WriteErrorLog(ex);
        try
        {
            var text = "程序遇到未处理的异常，详情已写入 gui/error.log\n\n" + ex.Message;
            MessageBox.Show(text, "QQ-AI_Agent 控制台", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 弹窗失败就只留日志
        }
    }

    internal static string ErrorLogPath()
    {
        try
        {
            var root = QQAgent.Gui.Services.BotConfig.TryFindBotRoot();
            if (root is not null)
            {
                return Path.Combine(root, "gui", "error.log");
            }
        }
        catch
        {
            // 定位失败就落到 exe 目录
        }

        return Path.Combine(AppContext.BaseDirectory, "error.log");
    }

    internal static void WriteErrorLog(Exception ex)
    {
        try
        {
            var path = ErrorLogPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var utf8 = new System.Text.UTF8Encoding(false);
            var head = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + ex.GetType().Name + ": " + ex.Message;
            var body = head + "\r\n" + ex + "\r\n\r\n";
            File.AppendAllText(path, body, utf8);
        }
        catch
        {
            // 写日志本身失败就放弃
        }
    }
}
