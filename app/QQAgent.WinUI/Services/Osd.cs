using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace QQAgent.WinUI.Services;

/// <summary>浮层等级：只有 Warning / Error 会显示，Info / Success 一律静默。</summary>
public enum OsdSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// 系统 OSD 式浮层：屏幕下方短暂浮现，淡入 → 停留 → 淡出后自动收起。
/// 无按钮、不可点击、不占布局；只有需要特殊提示的 Warning / Error 才出现。
/// </summary>
public static class Osd
{
    private const int FadeInMs = 150;
    private const int HoldMs = 1800;
    private const int FadeOutMs = 250;

    // Warning 用「感叹号圆圈」（Important），Error 用「叉圆圈」（Cancel）。
    private const string WarningGlyph = "\uE7BA";
    private const string ErrorGlyph = "\uE711";

    /// <summary>每个叠加层一份计时状态。</summary>
    private sealed class Entry
    {
        public int Version;
        public DispatcherTimer? Timer;
        public Storyboard? Fade;
    }

    private static readonly Dictionary<FrameworkElement, Entry> Entries = new();

    /// <summary>浮层当前是否可见（自检用）。</summary>
    public static bool IsVisible(FrameworkElement osd)
    {
        return osd.Visibility == Visibility.Visible;
    }
    /// <summary>
    /// 显示一条浮层。Info / Success 直接返回，不打扰用户；
    /// 重复调用会重置计时，只有最后一次到期的那次负责收起。
    /// </summary>
    public static void Show(FrameworkElement osd, FontIcon icon, TextBlock text, string msg, OsdSeverity sev)
    {
        if (sev != OsdSeverity.Warning && sev != OsdSeverity.Error)
        {
            return;
        }

        var queue = osd.DispatcherQueue;
        if (queue is null)
        {
            return;
        }

        // 后台线程调用时切回 UI 线程，保证动画与计时都在 UI 线程上跑。
        if (!queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => Show(osd, icon, text, msg, sev));
            return;
        }

        Play(osd, icon, text, msg, sev);
    }

    private static void Play(FrameworkElement osd, FontIcon icon, TextBlock text, string msg, OsdSeverity sev)
    {
        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(osd, out entry!))
            {
                entry = new Entry();
                Entries[osd] = entry;
            }
        }

        var version = ++entry.Version;

        icon.Glyph = sev == OsdSeverity.Error ? ErrorGlyph : WarningGlyph;
        text.Text = msg;
        AutomationProperties.SetName(text, msg);
        AutomationProperties.SetName(osd, msg);

        osd.Visibility = Visibility.Visible;
        osd.Opacity = 1;

        StartFade(osd, entry, 0, 1, FadeInMs, null, version);

        if (entry.Timer is null)
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(FadeInMs + HoldMs),
            };
            timer.Tick += (_, _) => BeginFadeOut(osd, entry);
            entry.Timer = timer;
        }
        entry.Timer.Stop();
        entry.Timer.Start();
    }

    private static void BeginFadeOut(FrameworkElement osd, Entry entry)
    {
        var version = entry.Version;
        StartFade(osd, entry, 1, 0, FadeOutMs, () => Hide(osd, entry, version), version);
    }

    private static void StartFade(
        FrameworkElement osd,
        Entry entry,
        double from,
        double to,
        int durationMs,
        Action? completed,
        int version)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
        };
        Storyboard.SetTarget(animation, osd);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var story = new Storyboard();
        story.Children.Add(animation);
        story.Completed += (_, _) =>
        {
            // 期间又有新提示时，旧动画的收尾不生效。
            if (entry.Version != version)
            {
                return;
            }

            completed?.Invoke();
        };

        entry.Fade?.Stop();
        entry.Fade = story;
        story.Begin();
    }

    private static void Hide(FrameworkElement osd, Entry entry, int version)
    {
        if (entry.Version != version)
        {
            return;
        }

        entry.Fade?.Stop();
        entry.Fade = null;
        entry.Timer?.Stop();
        osd.Opacity = 0;
        osd.Visibility = Visibility.Collapsed;
    }
}
