using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QQAgent.WinUI.Pages;
using QQAgent.WinUI.Services;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace QQAgent.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly UISettings _uiSettings = new();

    private readonly Dictionary<string, Type> _pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["model"] = typeof(ModelPage),
        ["runtime"] = typeof(RuntimePage),
        ["logs"] = typeof(LogsPage),
    };

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _oldWndProc = IntPtr.Zero;
    private WndProcDelegate? _wndProc;
    private bool _initialized;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        Title = "QQ-AI_Agent";

        // 自定义标题栏：系统按钮保留在右上角，最小化/最大化/关闭仍由 AppWindow 绘制。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);

        SetupNavigation();
        RootGrid.SizeChanged += OnRootSizeChanged;
        Activated += OnWindowActivated;
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            _hwnd = WindowNative.GetWindowHandle(this);
            AppWindow.Resize(new SizeInt32(1120, 760));
            InstallMinSize(_hwnd);
            UpdateTitleBarInset();
            AppWindow.Closing += OnAppWindowClosing;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }

        SetupBackdrop();
        ApplyTheme(App.Settings.ThemeMode);
        ApplySystemAccent();
    }

    /// <summary>
    /// 背景材质分级：Win11 22H2+ 用 Mica；Win10/旧版回退 Acrylic；
    /// 两层都不可用时退回纯色。任何一步失败都静默降级，绝不带崩窗口。
    /// </summary>
    private void SetupBackdrop()
    {
        try
        {
            var build = Environment.OSVersion.Version.Build;
            var canCustomizeCaption = AppWindowTitleBar.IsCustomizationSupported();

            if (canCustomizeCaption && build >= 22621 && MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                return;
            }

            if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }

        SystemBackdrop = null;
        try
        {
            if (Application.Current.Resources["PageFallbackBackgroundStyle"] is Style fallback)
            {
                RootGrid.Style = fallback;
            }
        }
        catch (Exception)
        {
            // 兜底样式也拿不到时保持透明，至少能正常使用
        }
    }

    /// <summary>标题栏右端给系统按钮让位（RightInset 随 DPI/缩放变化）。</summary>
    private void UpdateTitleBarInset()
    {
        try
        {
            var inset = AppWindow.TitleBar.RightInset;
            TitleBarRightSpacer.Width = inset > 0 ? inset : 138;
        }
        catch (Exception)
        {
            TitleBarRightSpacer.Width = 138;
        }
    }

    /// <summary>应用主题模式（页面「外观」卡片与启动流程共用）。</summary>
    public void ApplyTheme(ThemeMode mode)
    {
        RootGrid.RequestedTheme = mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        SyncTitleBarTheme();
        if (DispatcherQueue is not null)
        {
            DispatcherQueue.TryEnqueue(SyncTitleBarTheme);
        }
    }

    /// <summary>标题栏系统按钮跟随深浅色（深色下前景为白）。Win10 上赋值无效果，异常吞掉。</summary>
    private void SyncTitleBarTheme()
    {
        try
        {
            var bar = AppWindow.TitleBar;
            var dark = RootGrid.ActualTheme == ElementTheme.Dark;

            var foreground = dark ? Colors.White : Colors.Black;
            bar.ButtonForegroundColor = foreground;
            bar.ButtonHoverForegroundColor = foreground;
            bar.ButtonPressedForegroundColor = foreground;
            bar.ButtonInactiveForegroundColor = dark
                ? Windows.UI.Color.FromArgb(255, 170, 170, 170)
                : Windows.UI.Color.FromArgb(255, 90, 90, 90);

            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonHoverBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(36, 255, 255, 255)
                : Windows.UI.Color.FromArgb(24, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(64, 255, 255, 255)
                : Windows.UI.Color.FromArgb(40, 0, 0, 0);
        }
        catch (Exception)
        {
            // 不支持自定义标题栏时忽略
        }
    }

    /// <summary>把系统强调色写回应用资源，让原生按钮/选中态/Slider 跟随系统主题色。</summary>
    private void ApplySystemAccent()
    {
        try
        {
            var accent = _uiSettings.GetColorValue(UIColorType.Accent);
            var resources = Application.Current.Resources;

            resources["SystemAccentColor"] = accent;
            resources["SystemAccentColorLight2"] = Blend(accent, Colors.White, 0.42);
            resources["SystemAccentColorDark1"] = Blend(accent, Colors.Black, 0.28);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }
    }

    private static Windows.UI.Color Blend(Windows.UI.Color from, Windows.UI.Color to, double amount)
    {
        var t = Math.Clamp(amount, 0d, 1d);

        byte Mix(byte a, byte b)
        {
            return (byte)Math.Round(a + ((b - a) * t));
        }

        return Windows.UI.Color.FromArgb(from.A, Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        if (DispatcherQueue is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ApplySystemAccent();
            SyncTitleBarTheme();
        });
    }

    private void SetupNavigation()
    {
        NavView.SelectionChanged += OnNavSelectionChanged;
        NavigateTo(ModelNavItem, typeof(ModelPage));
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item
            && item.Tag is string tag
            && _pages.TryGetValue(tag, out var pageType)
            && ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void NavigateTo(NavigationViewItem item, Type pageType)
    {
        NavView.SelectedItem = item;
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    /// <summary>宽度 &lt; 1000 自动折叠为图标栏。</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 1000;
        var target = compact ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Left;

        if (NavView.PaneDisplayMode != target)
        {
            NavView.PaneDisplayMode = target;
            if (!compact)
            {
                NavView.IsPaneOpen = true;
            }
        }
    }

    // ---- 关闭窗口：Bot 仍在运行时先确认 ----

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !BotRunner.Current.IsRunning)
        {
            return;
        }

        args.Cancel = true;
        _ = ConfirmStopAndCloseAsync();
    }

    private async Task ConfirmStopAndCloseAsync()
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Bot 仍在运行",
                Content = "Bot 仍在运行，是否一并停止？",
                PrimaryButtonText = "停止并关闭",
                CloseButtonText = "取消关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            await Task.Run(BotRunner.Current.Stop);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
            return;
        }

        _closeConfirmed = true;
        Close();
    }
    // ---- 最小窗口尺寸：子类化窗口过程，拦截 WM_GETMINMAXINFO ----

    private const int GwlWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrNative(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProcNative(IntPtr prevWndProc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcNative(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private void InstallMinSize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _wndProc = MinSizeWndProc;
        _oldWndProc = SetWindowLongPtrNative(hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));
    }

    private IntPtr MinSizeWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmGetMinMaxInfo)
        {
            try
            {
                var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                var dpi = GetDpiForWindow(hWnd);
                var scale = dpi > 0 ? dpi / 96.0 : 1.0;

                info.MinTrackSize.X = (int)Math.Round(900 * scale);
                info.MinTrackSize.Y = (int)Math.Round(620 * scale);
                Marshal.StructureToPtr(info, lParam, false);
            }
            catch (Exception)
            {
                // 结构读取失败就退回系统默认处理
            }

            return IntPtr.Zero;
        }

        return _oldWndProc != IntPtr.Zero
            ? CallWindowProcNative(_oldWndProc, hWnd, msg, wParam, lParam)
            : DefWindowProcNative(hWnd, msg, wParam, lParam);
    }
}
