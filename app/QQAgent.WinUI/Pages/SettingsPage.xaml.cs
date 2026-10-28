using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QQAgent.WinUI.Services;
using Windows.UI;

namespace QQAgent.WinUI.Pages;

/// <summary>设置页：目前只有「外观」卡片（主题 + 主题色）。</summary>
public sealed partial class SettingsPage : Page
{
    private bool _loading;
    private Color _pendingAccent = ThemeService.DefaultAccent;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            var settings = App.Settings;

            ThemeChoice.SelectedIndex = settings.ThemeMode switch
            {
                ThemeMode.Light => 1,
                ThemeMode.Dark => 2,
                _ => 0,
            };

            AccentChoice.SelectedIndex = settings.AccentMode == AccentMode.Custom ? 1 : 0;

            _pendingAccent = EffectiveAccent(settings);
            AccentPicker.Color = _pendingAccent;
            ApplyAccentVisual(_pendingAccent);
            UpdateAccentPanel();
        }
        catch (Exception)
        {
            // 读取失败就保持默认外观，不打断页面
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>当前生效的主题色：自定义取 settings 里的色值，系统模式取系统强调色。</summary>
    private static Color EffectiveAccent(AppSettings settings)
    {
        return settings.AccentMode == AccentMode.Custom
            ? ThemeService.ParseColor(settings.AccentColor) ?? ThemeService.DefaultAccent
            : ThemeService.SystemAccent();
    }

    /// <summary>系统模式隐藏色块与色值，自定义模式显示。</summary>
    private void UpdateAccentPanel()
    {
        var custom = AccentChoice.SelectedIndex == 1;
        AccentPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyAccentVisual(Color color)
    {
        try
        {
            AccentRect.Fill = new SolidColorBrush(color);
            AccentText.Text = ThemeService.ToHex(color);
        }
        catch (Exception)
        {
            // 色块画不出来不影响设置本身
        }
    }

    private void OnThemeChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var mode = ThemeChoice.SelectedIndex switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

        App.Settings.ThemeMode = mode;
        App.Settings.Save();
        ThemeService.ApplyTheme(App.Main, mode);
    }

    private void OnAccentChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Settings.AccentMode = AccentChoice.SelectedIndex == 1 ? AccentMode.Custom : AccentMode.System;
        App.Settings.Save();

        _pendingAccent = EffectiveAccent(App.Settings);
        AccentPicker.Color = _pendingAccent;
        ApplyAccentVisual(_pendingAccent);
        UpdateAccentPanel();

        ThemeService.ApplyAccent(App.Settings);
    }

    /// <summary>取色过程中实时更新色块与色值，真正的保存放到 Flyout.Closed。</summary>
    private void OnAccentColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        _pendingAccent = args.NewColor;
        ApplyAccentVisual(args.NewColor);
    }

    private void OnPickColorClicked(object sender, RoutedEventArgs e)
    {
        SyncPickerWithSwatch();
    }

    private void OnAccentSwatchTapped(object sender, TappedRoutedEventArgs e)
    {
        SyncPickerWithSwatch();
        PickColorButton.Flyout.ShowAt(AccentRect);
    }

    private void SyncPickerWithSwatch()
    {
        try
        {
            AccentPicker.Color = _pendingAccent;
        }
        catch (Exception)
        {
            // 取色器初始化失败时不阻塞弹出
        }
    }

    private void OnColorFlyoutClosed(object sender, object e)
    {
        if (_loading || App.Settings.AccentMode != AccentMode.Custom)
        {
            return;
        }

        App.Settings.AccentColor = ThemeService.ToHex(_pendingAccent);
        App.Settings.Save();
        ThemeService.ApplyAccent(App.Settings);
    }
}