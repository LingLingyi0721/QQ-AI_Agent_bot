using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QQAgent.WinUI.Services;

namespace QQAgent.WinUI.Dialogs;

/// <summary>人设编辑弹窗：文件名 ≤20 字，内容 ≤200 字（中文字符按 1 个计）。</summary>
public sealed partial class PersonaEditorDialog : ContentDialog
{
    private Brush? _countBrush;

    public PersonaEditorDialog(string title, string? name, string? content)
    {
        InitializeComponent();

        Title = title;
        NameBox.Text = name ?? string.Empty;
        ContentBox.Text = content ?? string.Empty;
        _countBrush = ContentCountText.Foreground;

        UpdateCounters();
    }

    /// <summary>用户填写的文件名（原始输入，写入时再清洗）。</summary>
    public string PersonaName => NameBox.Text ?? string.Empty;

    /// <summary>用户填写的人设内容。</summary>
    public string PersonaContent => ContentBox.Text ?? string.Empty;

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCounters();
    }

    private void OnContentChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCounters();
    }

    private void UpdateCounters()
    {
        var nameLength = (NameBox.Text ?? string.Empty).Length;
        NameCountText.Text = nameLength.ToString() + "/" + PersonaStore.MaxNameLength;

        var contentLength = (ContentBox.Text ?? string.Empty).Length;
        ContentCountText.Text = contentLength.ToString() + "/" + PersonaStore.MaxContentLength;

        var over = contentLength > PersonaStore.MaxContentLength;
        ContentCountText.Foreground = over ? DangerBrush() : _countBrush;
        IsPrimaryButtonEnabled = !over && nameLength > 0;
    }

    private static Brush DangerBrush()
    {
        if (Application.Current.Resources.TryGetValue("DangerBrush", out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 229, 72, 77));
    }
}