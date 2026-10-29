using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using QQAgent.WinUI.Dialogs;
using QQAgent.WinUI.Models;
using QQAgent.WinUI.Services;

namespace QQAgent.WinUI.Pages;

/// <summary>日志文件列表页：扫描 logs 目录列出日志文件，点击条目在本程序内弹窗查看内容。</summary>
public sealed partial class ErrorsPage : Page
{
    private static readonly SolidColorBrush CardBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    private static readonly SolidColorBrush HoverBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5));

    private readonly List<LogFileItem> _items = new();

    /// <summary>弹窗正在显示，避免连点重复打开。</summary>
    private bool _dialogOpen;

    public ErrorsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Reload();
    }

    /// <summary>重新扫描日志目录：*.log*（含滚动文件），按修改时间倒序。</summary>
    private void Reload()
    {
        var dir = BotRunner.Current.LogDirectory;
        var list = new List<LogFileItem>();

        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var path in Directory.GetFiles(dir, "*.log*"))
                {
                    FileInfo info;
                    try
                    {
                        info = new FileInfo(path);
                        if (!info.Exists)
                        {
                            continue;
                        }
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    list.Add(new LogFileItem
                    {
                        Name = info.Name,
                        FullPath = info.FullName,
                        SizeText = FormatSize(info.Length),
                        ModifiedText = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        Modified = info.LastWriteTime,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            ShowNotice("扫描日志目录失败：" + ex.Message, OsdSeverity.Error);
        }

        list.Sort((a, b) => b.Modified.CompareTo(a.Modified));

        _items.Clear();
        _items.AddRange(list);

        // 列表始终保持可见（空表只显示居中提示），避免 Collapsed 让 UIA 树里查不到 ListView。
        LogFileList.ItemsSource = null;
        LogFileList.ItemsSource = _items;
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>字节数转显示文本：26 B / 12.3 KB / 1.2 MB。</summary>
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString() + " B";
        }

        if (bytes < 1024L * 1024L)
        {
            return (bytes / 1024d).ToString("0.#") + " KB";
        }

        return (bytes / (1024d * 1024d)).ToString("0.#") + " MB";
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        // 刷新成功不提示：列表本身刷新即是反馈。
        Reload();
    }

    private void OnOpenDirectoryClicked(object sender, RoutedEventArgs e)
    {
        var dir = BotRunner.Current.LogDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            var q = (char)34;
            Process.Start("explorer.exe", q + dir + q);
        }
        catch (Exception ex)
        {
            ShowNotice("打开目录失败：" + ex.Message, OsdSeverity.Error);
        }
    }

    /// <summary>点击条目：本程序内弹窗显示文件内容（不调系统程序、不写文件）。</summary>
    private async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LogFileItem item || _dialogOpen)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var dialog = new LogViewerDialog(item.FullPath)
            {
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            ShowNotice("打开日志失败：" + ex.Message, OsdSeverity.Error);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void OnItemPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = HoverBrush;
        }
    }

    private void OnItemPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = CardBrush;
        }
    }

    /// <summary>统一提示出口：只有 Warning / Error 才显示，成功与普通信息静默。</summary>
    private void ShowNotice(string message, OsdSeverity severity)
    {
        QQAgent.WinUI.Services.Osd.Show(Osd, OsdIcon, OsdText, message, severity);
    }
}
