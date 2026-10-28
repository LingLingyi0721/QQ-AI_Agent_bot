using System;

namespace QQAgent.WinUI.Models;

/// <summary>报错页的单条条目（ListView 绑定用）。</summary>
public sealed class ErrorItem
{
    public DateTime Time { get; set; }

    public string Message { get; set; } = string.Empty;

    public string TimeText => Time.ToString("HH:mm:ss");
}