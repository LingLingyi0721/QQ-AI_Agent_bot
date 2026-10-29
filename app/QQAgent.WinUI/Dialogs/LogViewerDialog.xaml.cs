using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace QQAgent.WinUI.Dialogs;

/// <summary>日志查看弹窗：在本程序内显示日志文件内容，按行着色，不写任何内容到文件。</summary>
public sealed partial class LogViewerDialog : ContentDialog
{
    /// <summary>不超过该大小（1 MB）时全读。</summary>
    private const long FullReadLimitBytes = 1024L * 1024L;

    /// <summary>超过 1 MB 时只显示末尾这么多行。</summary>
    private const int TailLineCount = 3000;

    /// <summary>行首时间戳 [HH:mm:ss]（可带一个空格）。</summary>
    private static readonly Regex TimestampRegex = new Regex("^\\[(\\d{2}:\\d{2}:\\d{2})\\]\\s?", RegexOptions.Compiled);

    /// <summary>报错关键词。</summary>
    private static readonly Regex ErrorRegex = new Regex("Error|Exception|Traceback|错误|失败", RegexOptions.Compiled);

    private static readonly SolidColorBrush TimeBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));

    private static readonly SolidColorBrush NormalBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F));

    private static readonly SolidColorBrush ErrorBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));

    private static readonly SolidColorBrush SeparatorBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x0F, 0x6C, 0xBD));

    /// <summary>「复制全部」用的全文（= 已加载的行，不含截断提示）。</summary>
    private readonly StringBuilder _copyText = new();

    public LogViewerDialog(string path)
    {
        InitializeComponent();

        Title = Path.GetFileName(path) ?? "日志";
        Load(path);
    }

    /// <summary>按大小策略读取并渲染；文件为空显示（空文件），不写任何内容到文件。</summary>
    private void Load(string path)
    {
        var lines = new List<string>();
        var truncated = false;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                AddParagraph("（文件不存在）", TimeBrush);
                return;
            }

            if (info.Length <= FullReadLimitBytes)
            {
                lines.AddRange(File.ReadLines(path));
            }
            else
            {
                lines.AddRange(TailLines(path, TailLineCount));
                truncated = true;
            }
        }
        catch (Exception ex)
        {
            AddParagraph("（读取失败：" + ex.Message + "）", ErrorBrush);
            return;
        }

        if (truncated)
        {
            AddParagraph("（仅显示末尾 " + TailLineCount + " 行）", TimeBrush);
        }

        if (lines.Count == 0)
        {
            AddParagraph("（空文件）", TimeBrush);
            return;
        }

        foreach (var line in lines)
        {
            AddLine(line);
        }
    }

    /// <summary>流式读整个文件，只保留末尾 count 行。</summary>
    private static List<string> TailLines(string path, int count)
    {
        var tail = new Queue<string>(count);
        foreach (var line in File.ReadLines(path))
        {
            if (tail.Count >= count)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
        }

        return new List<string>(tail);
    }
    /// <summary>追加一段纯色文本（提示行 / 空文件提示）。</summary>
    private void AddParagraph(string text, SolidColorBrush brush)
    {
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = text, Foreground = brush });
        ContentBlock.Blocks.Add(paragraph);
    }

    /// <summary>追加一行：===== 分隔行整行蓝，时间戳灰，含报错关键词的部分红，其余常规色。</summary>
    private void AddLine(string line)
    {
        _copyText.AppendLine(line);

        var paragraph = new Paragraph();

        if (line.StartsWith("=====", StringComparison.Ordinal))
        {
            paragraph.Inlines.Add(new Run { Text = line, Foreground = SeparatorBrush });
            ContentBlock.Blocks.Add(paragraph);
            return;
        }

        var rest = line;
        var match = TimestampRegex.Match(line);
        if (match.Success)
        {
            paragraph.Inlines.Add(new Run { Text = match.Groups[0].Value.TrimEnd(), Foreground = TimeBrush });
            rest = line.Substring(match.Length);
        }

        paragraph.Inlines.Add(new Run { Text = rest, Foreground = ErrorRegex.IsMatch(rest) ? ErrorBrush : NormalBrush });
        ContentBlock.Blocks.Add(paragraph);
    }

    /// <summary>复制全部：把已加载的全文放进剪贴板，不关闭弹窗。</summary>
    private void OnCopyAllClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 只复制内容，不关闭窗口
        args.Cancel = true;

        try
        {
            var package = new DataPackage();
            package.SetText(_copyText.ToString());
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception)
        {
            // 剪贴板失败不影响查看
        }
    }
}