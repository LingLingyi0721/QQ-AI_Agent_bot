using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace QQAgent.WinUI.Services;

/// <summary>提示等级：只决定图标，配色交给 Fluent 默认。</summary>
public enum TipSeverity
{
    Info,
    Success,
    Warning,
    Error,
}


/// <summary>
/// 页面级浮动提示：一个可复用的 TeachingTip，显示后 2.5 秒自动收起，
/// 重复显示只刷新内容并重置计时，不占页面布局。
/// </summary>
public sealed class TipHelper
{
    private const int AutoCloseMs = 2500;

    private readonly TeachingTip _tip;

    /// <summary>显示序号：只有最后一次显示到期的那个才会关闭浮层。</summary>
    private int _version;

    public TipHelper(TeachingTip tip)
    {
        _tip = tip;
    }


    /// <summary>浮层当前是否展开（自检用）。</summary>
    public bool IsOpen => _tip.IsOpen;

    public void Show(FrameworkElement? target, string title, string message, TipSeverity severity)
    {
        // 页面还没进视觉树时没有 XamlRoot，打开会抛，直接跳过。
        if (_tip.XamlRoot is null)
        {
            return;
        }


        if (target is not null)
        {
            _tip.Target = target;
        }

        _tip.Title = title;
        _tip.Subtitle = message;
        _tip.IconSource = new SymbolIconSource { Symbol = IconOf(severity) };
        _tip.IsOpen = true;


        // 重复提示时重置计时，避免被上一条的计时提前关掉。
        var version = ++_version;
        _ = CloseLaterAsync(version);
    }


    public void Hide()
    {
        _version++;
        _tip.IsOpen = false;
    }

    private async Task CloseLaterAsync(int version)
    {
        await Task.Delay(AutoCloseMs);
        if (version == _version && _tip.IsOpen)
        {
            _tip.IsOpen = false;
        }
    }


    private static Symbol IconOf(TipSeverity severity)
    {
        return severity switch
        {
            TipSeverity.Success => Symbol.Accept,
            TipSeverity.Warning => Symbol.Important,
            TipSeverity.Error => Symbol.Cancel,
            _ => Symbol.Message,
        };
    }
}

