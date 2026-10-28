using QQAgent.WinUI.Services;

namespace QQAgent.WinUI.Models;

/// <summary>人设下拉项：显示文本 + 目标条目（默认模式 Target 为 null，只用于显示文件名）。</summary>
public sealed record PersonaOption(string DisplayText, PersonaItem? Target);
