using System;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace QQAgent.WinUI.Services;

/// <summary>
/// 主题与主题色的统一应用入口：深浅色写到主窗口根 Grid，
/// 主题色写到 Application.Resources 的 SystemAccentColor 系列资源。
/// 所有操作都吞异常，最坏情况是颜色没变，窗口不崩。
/// </summary>
public static class ThemeService
{
    public static readonly Color DefaultAccent = Color.FromArgb(255, 0x4C, 0xC2, 0xFF);

    private static readonly UISettings UiSettings = new();

    /// <summary>同时应用主题模式与主题色（主窗口启动时调用）。</summary>
    public static void Apply(MainWindow? win, AppSettings settings)
    {
        ApplyTheme(win, settings.ThemeMode);
        ApplyAccent(settings);
    }

    /// <summary>主题模式：改根 Grid 的 RequestedTheme，并重刷标题栏系统按钮颜色。</summary>
    public static void ApplyTheme(MainWindow? win, ThemeMode mode)
    {
        if (win is null)
        {
            return;
        }

        try
        {
            var theme = mode switch
            {
                ThemeMode.Light => ElementTheme.Light,
                ThemeMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

            win.ThemeRoot.RequestedTheme = theme;
            win.RefreshTitleBarColors();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }
    }

    /// <summary>主题色：自定义色或系统强调色，连同 Light1/2/3、Dark1/2/3 一起写入资源。</summary>
    public static void ApplyAccent(AppSettings settings)
    {
        try
        {
            var accent = settings.AccentMode == AccentMode.Custom
                ? ParseColor(settings.AccentColor) ?? DefaultAccent
                : SystemAccent();

            var resources = Application.Current.Resources;
            resources["SystemAccentColor"] = accent;
            resources["SystemAccentColorLight1"] = Shade(accent, 0.30);
            resources["SystemAccentColorLight2"] = Shade(accent, 0.50);
            resources["SystemAccentColorLight3"] = Shade(accent, 0.70);
            resources["SystemAccentColorDark1"] = Shade(accent, -0.30);
            resources["SystemAccentColorDark2"] = Shade(accent, -0.50);
            resources["SystemAccentColorDark3"] = Shade(accent, -0.70);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }
    }

    /// <summary>系统强调色（取不到时退回默认色）。</summary>
    public static Color SystemAccent()
    {
        try
        {
            return UiSettings.GetColorValue(UIColorType.Accent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
            return DefaultAccent;
        }
    }

    /// <summary>
    /// 亮度插值：ratio &gt; 0 向白插值，ratio &lt; 0 向黑插值，绝对值即插值比例。
    /// </summary>
    public static Color Shade(Color color, double ratio)
    {
        var target = ratio >= 0 ? Colors.White : Colors.Black;
        var t = Math.Clamp(Math.Abs(ratio), 0d, 1d);

        static byte Mix(byte from, byte to, double amount)
        {
            return (byte)Math.Round(from + ((to - from) * amount));
        }

        return Color.FromArgb(
            color.A,
            Mix(color.R, target.R, t),
            Mix(color.G, target.G, t),
            Mix(color.B, target.B, t));
    }

    /// <summary>解析 #RRGGBB 或 #AARRGGBB，失败返回 null。</summary>
    public static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        var text = hex.Trim().TrimStart('#');
        if (text.Length == 8
            && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        if (text.Length == 6
            && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        return null;
    }

    /// <summary>格式化为 #RRGGBB。</summary>
    public static string ToHex(Color color)
    {
        return "#" + color.R.ToString("X2", CultureInfo.InvariantCulture)
                   + color.G.ToString("X2", CultureInfo.InvariantCulture)
                   + color.B.ToString("X2", CultureInfo.InvariantCulture);
    }
}