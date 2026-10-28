using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QQAgent.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace QQAgent.WinUI.Pages;

public sealed partial class ErrorsPage : Page
{
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(255, 0xFF, 0x8A, 0x8A));

    private bool _subscribed;

    public ErrorsPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            BotRunner.Current.ErrorsChanged += OnErrorsChanged;
            _subscribed = true;
        }

        Rebuild();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            BotRunner.Current.ErrorsChanged -= OnErrorsChanged;
            _subscribed = false;
        }
    }

    /// <summary>错误入列在后台线程触发，这里切回 UI 线程重建。</summary>
    private void OnErrorsChanged()
    {
        DispatcherQueue.TryEnqueue(Rebuild);
    }

    private void Rebuild()
    {
        var errors = BotRunner.Current.RecentErrors;

        ErrorList.Children.Clear();
        foreach (var item in errors)
        {
            ErrorList.Children.Add(new TextBlock
            {
                Text = "[" + item.Time.ToString("HH:mm:ss") + "] " + item.Text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = ErrorBrush,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = true,
            });
        }

        var empty = errors.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ErrorScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        if (!empty)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                ErrorScroller.UpdateLayout();
                ErrorScroller.ChangeView(null, ErrorScroller.ScrollableHeight, null, true);
            });
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        Rebuild();
        ShowNotice(InfoBarSeverity.Informational, "已刷新，共 " + BotRunner.Current.RecentErrors.Count + " 条");
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        BotRunner.Current.ClearErrors();
        Rebuild();
        ShowNotice(InfoBarSeverity.Informational, "已清空报错");
    }

    private void OnCopyAllClicked(object sender, RoutedEventArgs e)
    {
        var errors = BotRunner.Current.RecentErrors;
        if (errors.Count == 0)
        {
            ShowNotice(InfoBarSeverity.Warning, "当前没有报错可复制");
            return;
        }

        var text = new StringBuilder();
        foreach (var item in errors)
        {
            text.Append('[').Append(item.Time.ToString("HH:mm:ss")).Append("] ").AppendLine(item.Text);
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text.ToString());
            Clipboard.SetContent(package);
            Clipboard.Flush();

            ShowNotice(InfoBarSeverity.Success, "已复制 " + errors.Count + " 条报错");
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