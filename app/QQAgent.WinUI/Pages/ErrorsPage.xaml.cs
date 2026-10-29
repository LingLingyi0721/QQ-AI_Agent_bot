using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using QQAgent.WinUI.Models;
using QQAgent.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace QQAgent.WinUI.Pages;

public sealed partial class ErrorsPage : Page
{
    /// <summary>列表展示上限（只读文件末尾这么多行）。</summary>
    private const int MaxItems = 200;

    /// <summary>错误日志行格式：[HH:mm:ss] 内容；不匹配的行按纯文本条目显示。</summary>
    private static readonly Regex LineRegex = new Regex("^\\[(\\d{2}:\\d{2}:\\d{2})\\]\\s?(.*)$", RegexOptions.Compiled);

    /// <summary>文件不存在时写这一行，保证「打开日志文件」不会打开失败。</summary>
    private const string EmptyLogPlaceholder = "（暂无错误记录）";

    private readonly List<ErrorItem> _items = new();

    private bool _subscribed;

    public ErrorsPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Reload();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            BotRunner.Current.ErrorsChanged += OnErrorsChanged;
            _subscribed = true;
        }

        Reload();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            BotRunner.Current.ErrorsChanged -= OnErrorsChanged;
            _subscribed = false;
        }
    }

    /// <summary>错误入列在后台线程触发，这里切回 UI 线程重读文件。</summary>
    private void OnErrorsChanged()
    {
        DispatcherQueue.TryEnqueue(Reload);
    }

    /// <summary>从错误日志文件末尾读取条目（最新在最上面）。</summary>
    private void Reload()
    {
        var logPath = BotRunner.Current.ErrorLogPath;
        string[] lines;
        try
        {
            lines = File.Exists(logPath) ? File.ReadAllLines(logPath, Encoding.UTF8) : Array.Empty<string>();
        }
        catch (Exception ex)
        {
            lines = Array.Empty<string>();
            ShowNotice(InfoBarSeverity.Error, "读取错误日志失败：" + ex.Message);
        }

        _items.Clear();
        foreach (var line in lines.Skip(Math.Max(0, lines.Length - MaxItems)))
        {
            var match = LineRegex.Match(line);
            _items.Add(match.Success
                ? new ErrorItem { TimeText = match.Groups[1].Value, Message = match.Groups[2].Value }
                : new ErrorItem { TimeText = string.Empty, Message = line });
        }

        // 文件里最旧在上，列表要最新在最上面。
        _items.Reverse();

        ErrorList.ItemsSource = null;
        ErrorList.ItemsSource = _items;

        // 列表始终保持可见（空表只显示居中提示），避免 Collapsed 让 UIA 树里查不到 ListView。
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        Reload();
        ShowNotice(InfoBarSeverity.Informational, "已刷新，共 " + _items.Count + " 条");
    }

    private async void OnClearClicked(object sender, RoutedEventArgs e)
    {
        // 先清内存缓冲（文件是独立档案，默认保留）。
        BotRunner.Current.ClearErrors();
        _items.Clear();
        ErrorList.ItemsSource = null;
        ErrorList.ItemsSource = _items;
        EmptyText.Visibility = Visibility.Visible;

        var dialog = new ContentDialog();
        dialog.XamlRoot = XamlRoot;
        dialog.Title = "清空列表";
        dialog.Content = "是否同时清空错误日志文件？清除后不可恢复。";
        dialog.PrimaryButtonText = "是";
        dialog.SecondaryButtonText = "否";
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Close;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            BotRunner.Current.ClearErrorLogFile();
            Reload();
            ShowNotice(InfoBarSeverity.Success, "已清空列表与日志文件");
        }
        else if (result == ContentDialogResult.Secondary)
        {
            ShowNotice(InfoBarSeverity.Informational, "已清空列表，日志文件保留");
        }
        else
        {
            Reload();
            ShowNotice(InfoBarSeverity.Informational, "已取消，列表已从文件恢复");
        }
    }

    private async void OnOpenLogClicked(object sender, RoutedEventArgs e)
    {
        var logPath = BotRunner.Current.ErrorLogPath;
        try
        {
            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            if (!File.Exists(logPath))
            {
                File.WriteAllText(logPath, EmptyLogPlaceholder + Environment.NewLine, new UTF8Encoding(false));
            }

            var logFile = await StorageFile.GetFileFromPathAsync(logPath);
            await Launcher.LaunchFileAsync(logFile);

            Reload();
            ShowNotice(InfoBarSeverity.Success, "已用系统默认程序打开错误日志");
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, "打开日志文件失败：" + ex.Message);
        }
    }

    private void OnCopyAllClicked(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            ShowNotice(InfoBarSeverity.Warning, "当前没有报错可复制");
            return;
        }

        var text = new StringBuilder();
        foreach (var item in _items)
        {
            if (item.TimeText.Length > 0)
            {
                text.Append("[").Append(item.TimeText).Append("] ");
            }

            text.AppendLine(item.Message);
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text.ToString());
            Clipboard.SetContent(package);
            Clipboard.Flush();

            ShowNotice(InfoBarSeverity.Success, "已复制 " + _items.Count + " 条报错");
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, "复制失败：" + ex.Message);
        }
    }

    private void ShowNotice(InfoBarSeverity severity, string message)
    {
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }
}