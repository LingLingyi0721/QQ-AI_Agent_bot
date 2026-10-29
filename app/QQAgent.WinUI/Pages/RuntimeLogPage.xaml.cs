using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using QQAgent.WinUI.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace QQAgent.WinUI.Pages;

public sealed partial class RuntimeLogPage : Page
{
    private const int MaxLines = 3000;

    /// <summary>启动中状态最长等待，超时按失败处理。</summary>
    private const int StartTimeoutMs = 10000;

    /// <summary>BotRunner 推送的系统消息前缀（进程退出等），显示为蓝色。</summary>
    private const string SystemPrefix = "[系统]";

    // 状态圆点配色：未运行灰 / 启动中蓝 / 运行中绿 / 正常退出橙 / 异常退出红。
    private static readonly Color IdleColor = Color.FromArgb(255, 0x8A, 0x8A, 0x8A);
    private static readonly Color StartingColor = Color.FromArgb(255, 0x0F, 0x6C, 0xBD);
    private static readonly Color RunningColor = Color.FromArgb(255, 0x6C, 0xCB, 0x5F);
    private static readonly Color ExitedColor = Color.FromArgb(255, 0xF0, 0xA0, 0x30);
    private static readonly Color FailedColor = Color.FromArgb(255, 0xFF, 0x6B, 0x6B);

    private readonly List<(string Stamp, string Text, bool IsErr)> _entries = new();
    private readonly SolidColorBrush _idleBrush = new(IdleColor);
    private readonly SolidColorBrush _startingBrush = new(StartingColor);
    private readonly SolidColorBrush _runningBrush = new(RunningColor);
    private readonly SolidColorBrush _exitedBrush = new(ExitedColor);
    private readonly SolidColorBrush _failedBrush = new(FailedColor);

    // 日志窗口固定白底，故文字色也固定，不随主题变化。
    private readonly SolidColorBrush _normalBrush = new(Color.FromArgb(255, 0x1F, 0x1F, 0x1F));
    private readonly SolidColorBrush _errorBrush = new(Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
    private readonly SolidColorBrush _systemBrush = new(Color.FromArgb(255, 0x0F, 0x6C, 0xBD));
    private readonly SolidColorBrush _stampBrush = new(Color.FromArgb(255, 0x8A, 0x8A, 0x8A));

    /// <summary>页面唯一的浮动提示。</summary>
    private readonly TipHelper _tip;

    private bool _subscribed;

    /// <summary>启动中：BotRunner 报告运行前一直保持该状态。</summary>
    private bool _starting;

    /// <summary>启动超时计时器（10 秒兜底）。</summary>
    private DispatcherTimer? _startTimer;

    public RuntimeLogPage()
    {
        InitializeComponent();
        _tip = new TipHelper(Tip);
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            BotRunner.Current.LogReceived += OnLogReceived;
            BotRunner.Current.StateChanged += OnRunnerStateChanged;
            _subscribed = true;
        }

        Rebuild();
        UpdateProcessUi();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            BotRunner.Current.LogReceived -= OnLogReceived;
            BotRunner.Current.StateChanged -= OnRunnerStateChanged;
            _subscribed = false;
        }

        StopStartTimer();
    }

    // ---- 进程状态 ----

    /// <summary>BotRunner 的事件在后台线程抛出，这里切回 UI 线程。</summary>
    private void OnRunnerStateChanged(bool running, int? pid)
    {
        DispatcherQueue.TryEnqueue(UpdateProcessUi);
    }

    private void UpdateProcessUi()
    {
        var runner = BotRunner.Current;

        if (runner.IsRunning)
        {
            StopStartTimer();
            _starting = false;

            var pid = runner.Pid;
            SetState(_runningBrush, pid is int id && id > 0 ? "运行中 (PID " + id + ")" : "运行中");
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            return;
        }

        // 启动中：进程还没报告运行，两个按钮都禁用。
        if (_starting)
        {
            SetState(_startingBrush, "启动中…");
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            return;
        }

        if (runner.HasExited)
        {
            // 正常退出橙、异常退出（非 0 退出码）红。
            if (runner.LastExitCode is int code && code != 0)
            {
                SetState(_failedBrush, "异常退出 (退出码 " + code + ")");
            }
            else
            {
                SetState(_exitedBrush, "已退出");
            }
        }
        else
        {
            SetState(_idleBrush, "未运行");
        }

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
    }

    private void SetState(SolidColorBrush brush, string text)
    {
        StateDot.Fill = brush;
        StateText.Text = text;
    }

    /// <summary>统一提示入口：浮层 + 2.5 秒自动消失。</summary>
    private void ShowTip(string title, string message, TipSeverity severity, FrameworkElement? target = null)
    {
        _tip.Show(target ?? StartButton, title, message, severity);
    }

    // ---- 启动 / 停止 ----

    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        var runner = BotRunner.Current;
        if (_starting || runner.IsRunning)
        {
            return;
        }

        // 点击后立即进入启动中，不等 python 预检。
        _starting = true;
        UpdateProcessUi();
        ShowTip("启动", "正在启动 bot …", TipSeverity.Info, StartButton);
        StartStartTimer();

        var result = await Task.Run(runner.Start);

        StopStartTimer();
        _starting = false;
        ShowTip(
            result.Ok ? "启动" : "启动失败",
            result.Message,
            result.Ok ? TipSeverity.Success : TipSeverity.Error,
            StartButton);
        UpdateProcessUi();
    }

    private async void OnStopClicked(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        ShowTip("停止", "正在停止 bot …", TipSeverity.Info, StopButton);

        var stopped = await Task.Run(BotRunner.Current.Stop);

        // Exited 事件在后台线程触发，给它一点时间把状态推回 UI。
        for (var i = 0; i < 20 && BotRunner.Current.IsRunning; i++)
        {
            await Task.Delay(50);
        }

        if (stopped)
        {
            ShowTip("停止", "已停止 bot 进程", TipSeverity.Success, StopButton);
        }

        UpdateProcessUi();

        // 停止失败（进程已不存在）不再弹错误，静默把状态刷成「已退出」。
        if (!stopped && !BotRunner.Current.IsRunning)
        {
            SetState(_exitedBrush, "已退出");
        }
    }

    private void StartStartTimer()
    {
        if (_startTimer is null)
        {
            _startTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StartTimeoutMs) };
            _startTimer.Tick += OnStartTimeout;
        }

        _startTimer.Stop();
        _startTimer.Start();
    }

    private void StopStartTimer()
    {
        _startTimer?.Stop();
    }

    private void OnStartTimeout(object? sender, object e)
    {
        StopStartTimer();
        if (!_starting)
        {
            return;
        }

        _starting = false;
        ShowTip("启动超时", "10 秒内没有收到 bot 的运行状态", TipSeverity.Warning, StartButton);
        UpdateProcessUi();
    }

    // ---- 日志 ----

    /// <summary>BotRunner 在后台线程抛日志，这里切回 UI 线程追加（时间戳在此处生成）。</summary>
    private void OnLogReceived(string text, bool isErr)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        DispatcherQueue.TryEnqueue(() => AppendLine(stamp, text, isErr));
    }

    /// <summary>用 BotRunner 的缓存重建全部视图（每次进入页面都以缓存为准）。</summary>
    private void Rebuild()
    {
        _entries.Clear();
        LogText.Blocks.Clear();

        // BotRunner 缓存不带时间戳（不改其签名），重建时用占位符，避免伪造接收时间。
        foreach (var line in BotRunner.Current.Recent)
        {
            AppendCore("--:--:--", line.Text, line.IsErr);
        }

        ScrollToEnd();
    }

    private void AppendLine(string stamp, string text, bool isErr)
    {
        AppendCore(stamp, text, isErr);
        if (AutoScrollToggle.IsOn)
        {
            ScrollToEnd();
        }
    }

    private void AppendCore(string stamp, string text, bool isErr)
    {
        _entries.Add((stamp, text, isErr));
        while (_entries.Count > MaxLines)
        {
            _entries.RemoveAt(0);
        }

        LogText.Blocks.Add(CreateBlock(stamp, text, isErr));
        while (LogText.Blocks.Count > MaxLines)
        {
            LogText.Blocks.RemoveAt(0);
        }
    }

    /// <summary>灰时间戳 + 正文着色：stderr 红、系统消息蓝、其余黑。</summary>
    private Paragraph CreateBlock(string stamp, string text, bool isErr)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        paragraph.Inlines.Add(new Run
        {
            Text = "[" + stamp + "] ",
            Foreground = _stampBrush,
        });
        paragraph.Inlines.Add(new Run
        {
            Text = string.IsNullOrEmpty(text) ? " " : text,
            Foreground = isErr
                ? _errorBrush
                : (text.StartsWith(SystemPrefix, StringComparison.Ordinal) ? _systemBrush : _normalBrush),
        });
        return paragraph;
    }

    private void ScrollToEnd()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            LogScroller.UpdateLayout();
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null, true);
        });
    }

    private void OnAutoScrollToggled(object sender, RoutedEventArgs e)
    {
        if (AutoScrollToggle.IsOn)
        {
            ScrollToEnd();
        }
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        BotRunner.Current.ClearRecent();
        _entries.Clear();
        LogText.Blocks.Clear();
        ShowTip("清空日志", "已清空日志", TipSeverity.Info, ClearButton);
    }

    private async void OnSaveAsClicked(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, _entries.Select(line => line.Text));
        if (text.Length == 0)
        {
            ShowTip("另存为", "当前没有日志可保存", TipSeverity.Warning, SaveAsButton);
            return;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "bot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            };
            picker.FileTypeChoices.Add("日志文件", new List<string> { ".log" });

            var hwnd = TryGetWindowHandle();
            if (hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法获取窗口句柄");
            }

            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await FileIO.WriteTextAsync(file, text, Windows.Storage.Streams.UnicodeEncoding.Utf8);
            ShowTip("另存为", "已保存到 " + file.Path, TipSeverity.Success, SaveAsButton);
        }
        catch (Exception)
        {
            SaveFallback(text);
        }
    }

    private static IntPtr TryGetWindowHandle()
    {
        try
        {
            if (App.Main is null)
            {
                return IntPtr.Zero;
            }

            return WindowNative.GetWindowHandle(App.Main);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>选择器不可用时的降级落盘：logs\gui-<时间戳>.log。</summary>
    private void SaveFallback(string text)
    {
        try
        {
            var root = App.BotRoot ?? EnvConfig.FindBotRoot();
            var dir = root is null
                ? Path.Combine(AppContext.BaseDirectory, "logs")
                : Path.Combine(root, "logs");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "gui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            ShowTip("另存为", "已保存到 " + path, TipSeverity.Success, SaveAsButton);
        }
        catch (Exception ex)
        {
            ShowTip("另存为", "保存失败：" + ex.Message, TipSeverity.Error, SaveAsButton);
        }
    }
}
