using System;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using QQAgent.WinUI.Services;

namespace QQAgent.WinUI.Dialogs;

/// <summary>人设编辑窗：文件名 ≤20 字、内容 ≤200 字才可保存；导入只填内容，不落盘。</summary>
public sealed partial class PersonaEditorDialog : ContentDialog
{
    private readonly Brush? _countBrush;

    public PersonaEditorDialog(string title, string? name, string? content)
    {
        InitializeComponent();

        Title = title;
        _countBrush = ContentCountText.Foreground;
        NameBox.Text = name ?? string.Empty;
        ContentBox.Text = content ?? string.Empty;

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

    /// <summary>导入：选 .txt → 全文填入内容框（不截断、不拒绝），是否落盘由「保存」决定。</summary>
    private async void OnImportClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();

        // 点「导入」不关闭窗口
        args.Cancel = true;

        try
        {
            var main = App.Main;
            if (main is null)
            {
                ShowImportError("主窗口不可用，无法打开文件选择器");
                return;
            }

            // unpackaged 应用用 Windows App SDK 自带的选择器；旧的 Windows.Storage.Pickers 会 E_FAIL
            var picker = new FileOpenPicker(main.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".txt");

            var result = await picker.PickSingleFileAsync();
            if (result is null || string.IsNullOrWhiteSpace(result.Path))
            {
                return;
            }

            var text = File.ReadAllText(result.Path, Encoding.UTF8);
            ContentBox.Text = text ?? string.Empty;

            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = LimitName(Path.GetFileNameWithoutExtension(result.Path));
            }
            ImportErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowImportError("导入失败：" + ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void UpdateCounters()
    {
        var nameText = NameBox.Text ?? string.Empty;
        var limited = LimitName(nameText);
        if (!string.Equals(nameText, limited, StringComparison.Ordinal))
        {
            NameBox.Text = limited;
            nameText = limited;
        }

        NameCountText.Text = nameText.Length.ToString() + "/" + PersonaStore.MaxNameLength;

        var contentLength = (ContentBox.Text ?? string.Empty).Length;
        ContentCountText.Text = contentLength.ToString() + "/" + PersonaStore.MaxContentLength;

        var over = contentLength > PersonaStore.MaxContentLength;
        ContentCountText.Foreground = over ? DangerBrush() : _countBrush;

        // 超限或文件名为空时保存不可点
        IsPrimaryButtonEnabled = !over && nameText.Trim().Length > 0;
    }

    private static string LimitName(string text)
    {
        return text.Length <= PersonaStore.MaxNameLength
            ? text
            : text.Substring(0, PersonaStore.MaxNameLength);
    }

    private void ShowImportError(string message)
    {
        ImportErrorText.Text = message;
        ImportErrorText.Foreground = DangerBrush();
        ImportErrorText.Visibility = Visibility.Visible;
    }

    private static Brush DangerBrush()
    {
        if (Application.Current.Resources.TryGetValue("DangerBrush", out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 107, 107));
    }
}




