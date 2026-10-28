using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QQAgent.WinUI.Models;
using QQAgent.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace QQAgent.WinUI.Pages;

public sealed partial class ErrorsPage : Page
{
    /// <summary>列表展示上限（与 BotRunner.MaxRecentErrors 一致）。</summary>
    private const int MaxItems = 200;

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
        // RecentErrors 是旧在前，列表要新在最上面，故反转后再取上限。
        var items = BotRunner.Current.RecentErrors
            .Reverse()
            .Take(MaxItems)
            .Select(item => new ErrorItem { Time = item.Time, Message = item.Text })
            .ToList();

        ErrorList.ItemsSource = items;

        // 列表始终保持可见（空表只显示居中提示），避免 Collapsed 让 UIA 树里查不到 ListView。
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        Rebuild();
        ShowNotice(InfoBarSeverity.Informational, "已刷新，共 " + BotRunner.Current.RecentErrors.Count + " 条");
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        BotRunner.Current.ClearErrors();
        ErrorList.ItemsSource = null;
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