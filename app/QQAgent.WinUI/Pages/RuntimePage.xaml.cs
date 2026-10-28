using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QQAgent.WinUI.Services;
using Windows.UI;

namespace QQAgent.WinUI.Pages;

public sealed partial class RuntimePage : Page
{
    private static readonly Color IdleColor = Color.FromArgb(255, 0x9A, 0x9A, 0x9A);
    private static readonly Color RunningColor = Color.FromArgb(255, 0x6C, 0xCB, 0x5F);
    private static readonly Color FailedColor = Color.FromArgb(255, 0xFF, 0x6B, 0x6B);

    private readonly SolidColorBrush _idleBrush = new(IdleColor);
    private readonly SolidColorBrush _runningBrush = new(RunningColor);
    private readonly SolidColorBrush _failedBrush = new(FailedColor);

    private bool _subscribed;

    public RuntimePage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            BotRunner.Current.StateChanged += OnRunnerStateChanged;
            _subscribed = true;
        }

        UpdateProcessUi();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            BotRunner.Current.StateChanged -= OnRunnerStateChanged;
            _subscribed = false;
        }
    }

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
}