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

    private static readonly Color IdleColor = Color.FromArgb(255, 0x9A, 0x9A, 0x9A);
    private static readonly Color RunningColor = Color.FromArgb(255, 0x6C, 0xCB, 0x5F);
    private static readonly Color FailedColor = Color.FromArgb(255, 0xFF, 0x6B, 0x6B);

    private readonly List<(string Text, bool IsErr)> _entries = new();
    private readonly SolidColorBrush _idleBrush = new(IdleColor);
    private readonly SolidColorBrush _runningBrush = new(RunningColor);
    private readonly SolidColorBrush _failedBrush = new(FailedColor);
    private readonly SolidColorBrush _normalBrush = new(Color.FromArgb(255, 0xE6, 0xE6, 0xE6));
    private readonly SolidColorBrush _errorBrush = new(Color.FromArgb(255, 0xFF, 0x8A, 0x8A));

    private bool _subscribed;

    public RuntimeLogPage()
    {
        InitializeComponent();
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
            var pid = runner.Pid;
            SetState(_runningBrush, pid is int id && id > 0 ? "运行中 (PID " + id + ")" : "运行中");
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            return;
        }

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        SetState(runner.HasExited ? _failedBrush : _idleBrush, runner.HasExited ? "已退出" : "未运行");
    }

    private void SetState(SolidColorBrush brush, string text)
    {
        StateDot.Fill = brush;
        StateText.Text = text;
    }

    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        ShowResult(InfoBarSeverity.Informational, "正在启动 bot …");

        var result = await Task.Run(BotRunner.Current.Start);

        ShowResult(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error, result.Message);
        UpdateProcessUi();
    }

    private async void OnStopClicked(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        ShowResult(InfoBarSeverity.Informational, "正在停止 bot …");

        var stopped = await Task.Run(BotRunner.Current.Stop);

        // Exited 事件在后台线程触发，给它一点时间把状态推回 UI。
        for (var i = 0; i < 20 && BotRunner.Current.IsRunning; i++)
        {
            await Task.Delay(50);
        }

        ShowResult(
            stopped ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            stopped ? "已停止 bot 进程" : "停止失败：进程不存在或无法结束");
        UpdateProcessUi();
    }

    private void ShowResult(InfoBarSeverity severity, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }

    // ---- 日志 ----

    /// <summary>BotRunner 在后台线程抛日志，这里切回 UI 线程追加。</summary>
    private void OnLogReceived(string text, bool isErr)
    {
        DispatcherQueue.TryEnqueue(() => AppendLine(text, isErr));
    }

    /// <summary>用 BotRunner 的缓存重建全部视图（每次进入页面都以缓存为准）。</summary>
    private void Rebuild()
    {
        _entries.Clear();
        LogText.Blocks.Clear();

        foreach (var line in BotRunner.Current.Recent)
        {
            AppendCore(line.Text, line.IsErr);
        }

        ScrollToEnd();
    }

    private void AppendLine(string text, bool isErr)
    {
        AppendCore(text, isErr);
        if (AutoScrollToggle.IsOn)
        {
            ScrollToEnd();
        }
    }

    private void AppendCore(string text, bool isErr)
    {
        _entries.Add((text, isErr));
        while (_entries.Count > MaxLines)
        {
            _entries.RemoveAt(0);
        }

        LogText.Blocks.Add(CreateBlock(text, isErr));
        while (LogText.Blocks.Count > MaxLines)
        {
            LogText.Blocks.RemoveAt(0);
        }
    }

    /// <summary>stderr 行单独染红，其余保持 #E6E6E6。</summary>
    private Paragraph CreateBlock(string text, bool isErr)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        paragraph.Inlines.Add(new Run
        {
            Text = string.IsNullOrEmpty(text) ? " " : text,
            Foreground = isErr ? _errorBrush : _normalBrush,
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
        ShowResult(InfoBarSeverity.Informational, "已清空日志");
    }

    private async void OnSaveAsClicked(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, _entries.Select(line => line.Text));
        if (text.Length == 0)
        {
            ShowResult(InfoBarSeverity.Warning, "当前没有日志可保存");
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
            ShowResult(InfoBarSeverity.Success, "已保存到 " + file.Path);
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
            ShowResult(InfoBarSeverity.Success, "已保存到 " + path);
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "保存失败：" + ex.Message);
        }
    }
}