using System;

namespace QQAgent.WinUI.Models;

/// <summary>报错页的单条条目（ListView 绑定用）。</summary>
public sealed class ErrorItem
{
    public DateTime Time { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>展示用时间戳；文件里的纯文本行没有时间戳，留空。</summary>
    public string TimeText { get; set; } = string.Empty;
}