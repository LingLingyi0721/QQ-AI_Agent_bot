using System;

namespace QQAgent.WinUI.Models;

/// <summary>日志文件列表页的单条条目：一个磁盘上的日志文件。</summary>
public sealed class LogFileItem
{
    /// <summary>文件名（如 errors.log / run.log / run.log.1）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>完整路径（打开弹窗用）。</summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>显示用大小（如 26 B / 12.3 KB / 1.2 MB）。</summary>
    public string SizeText { get; set; } = string.Empty;

    /// <summary>显示用修改时间（yyyy-MM-dd HH:mm:ss）。</summary>
    public string ModifiedText { get; set; } = string.Empty;

    /// <summary>排序用的修改时间。</summary>
    public DateTime Modified { get; set; }
}