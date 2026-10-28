﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using QQAgent.WinUI.Pages;
using QQAgent.WinUI.Services;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace QQAgent.WinUI;

public sealed partial class MainWindow : Window
{
    /// <summary>侧栏收起态宽度。</summary>
    private const double SideBarCompactWidth = 48;

    /// <summary>侧栏展开态宽度。</summary>
    private const double SideBarExpandedWidth = 200;

    /// <summary>侧栏宽度动画时长（ms）。</summary>
    private const int SideBarAnimateDurationMs = 180;

    private static readonly SolidColorBrush HoverBrush = new(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

    private static readonly SolidColorBrush SelectedBrush = new(Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    private readonly UISettings _uiSettings = new();

    private readonly Dictionary<string, Type> _pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["model"] = typeof(ModelPage),
        ["runtime"] = typeof(RuntimePage),
        ["logs"] = typeof(LogsPage),
    };

    private readonly List<Button> _navButtons = new();

    private readonly List<TextBlock> _navTexts = new();

    private IntPtr _hwnd = IntPtr.Zero;

    private IntPtr _oldWndProc = IntPtr.Zero;

    private WndProcDelegate? _wndProc;

    private bool _initialized;

    private bool _closeConfirmed;

    /// <summary>侧栏是否展开（唯一状态源，不再依赖 NavigationView pane）。</summary>
    private bool _isSideBarOpen;

    /// <summary>当前选中的导航按钮。</summary>
    private Button? _selectedNav;

    /// <summary>侧栏宽度动画帧定时器（GridLength 无法用 Storyboard 动画，逐帧驱动列宽）。</summary>
    private readonly DispatcherTimer _sideBarTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };

    /// <summary>动画进行中标记，用于连点时停旧动画并从当前值续接。</summary>
    private bool _sideBarAnimating;

    private System.Diagnostics.Stopwatch? _sideBarWatch;

    private double _sideBarFromWidth;

    private double _sideBarToWidth;

    private bool _sideBarTargetOpen;

    public MainWindow()
    {
        InitializeComponent();
        Title = "QQ-AI_Agent";

        // 自定义标题栏：系统按钮保留在右上角，最小化/最大化/关闭仍由 AppWindow 绘制。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);

        SetupNavigation();
        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
    }

    /// <summary>主题应用的目标根元素（ThemeService 用）。</summary>

    public FrameworkElement ThemeRoot => RootGrid;

    /// <summary>重刷标题栏系统按钮前景/背景色（ThemeService 用）。</summary>

    public void RefreshTitleBarColors()
    {
        SyncTitleBarTheme();
    }

    /// <summary>当前侧栏是否展开（自检/自动化用）。</summary>
    public bool IsPaneOpenNow => _isSideBarOpen;

    /// <summary>当前侧栏实际宽度（自检/自动化用，动画中间态采样）。</summary>
    public double SideBarWidthNow => SideBar.ActualWidth;

    /// <summary>侧栏动画是否正在播放（自检/自动化用）。</summary>
    public bool IsSideBarAnimatingNow => _sideBarAnimating;

    /// <summary>自检用：主动展开侧栏（走与点汉堡相同的动画路径）。</summary>
    public void OpenPaneForTest()
    {
        if (_isSideBarOpen)
        {
            return;
        }

        _isSideBarOpen = true;
        AnimateSideBar();
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
        ThemeService.Apply(this, App.Settings);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        StopSideBarAnimation();

        foreach (var button in _navButtons)
        {
            button.Click -= NavButton_Click;
            button.PointerEntered -= NavButton_PointerEntered;
            button.PointerExited -= NavButton_PointerExited;
        }

        _navButtons.Clear();
        _navTexts.Clear();
        _selectedNav = null;

        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        Activated -= OnWindowActivated;
    }

    /// <summary>

    /// 背景材质分级：Win11 22H2+ 用 Mica；Win10/旧版回退 Acrylic；

    /// 两层都不可用时退回纯色。任何一步失败都静默降级，绝不带崩窗口。

    /// </summary>

    private void SetupBackdrop()
    {
        // 根 Grid 保持透明：Mica / Acrylic 生效时直接透出系统材质，不铺任何实色底板。
        RootGrid.Background = new SolidColorBrush(Colors.Transparent);

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

        // Mica / Acrylic 都不可用时才铺纯色兜底，保证可读性。
        SystemBackdrop = null;
        try
        {
            if (Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] is Brush fallback)
            {
                RootGrid.Background = fallback;
            }
        }
        catch (Exception)
        {
            // 兜底色也拿不到时保持透明，至少能正常使用
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

    /// <summary>应用主题模式（模型页「外观」卡片与设置页共用）。</summary>

    public void ApplyTheme(ThemeMode mode)
    {
        ThemeService.ApplyTheme(this, mode);
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

    /// <summary>系统强调色变化：仅「跟随系统」模式重刷，自定义色不受影响。</summary>

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        if (DispatcherQueue is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            SyncTitleBarTheme();
            if (App.Settings.AccentMode == AccentMode.System)
            {
                ThemeService.ApplyAccent(App.Settings);
            }
        });
    }

    // ---- 导航（自绘侧栏，不使用 NavigationView pane）----

    private void SetupNavigation()
    {
        _navButtons.Clear();
        _navTexts.Clear();

        foreach (var button in new[] { ModelNav, RuntimeNav, LogsNav, SettingsNav })
        {
            _navButtons.Add(button);
            button.Click += NavButton_Click;
            button.PointerEntered += NavButton_PointerEntered;
            button.PointerExited += NavButton_PointerExited;
        }

        foreach (var text in new[] { ModelText, RuntimeText, LogsText, SettingsText })
        {
            _navTexts.Add(text);
            text.Opacity = 0;
            text.Visibility = Visibility.Collapsed;
        }

        SideBarColumn.Width = new GridLength(SideBarCompactWidth);

        SetSelected(ModelNav);
        if (ContentFrame.CurrentSourcePageType != typeof(ModelPage))
        {
            ContentFrame.Navigate(typeof(ModelPage));
        }
    }

    /// <summary>汉堡：只翻转展开状态并播放宽度/文字动画，不碰布局基准。</summary>
    private void Hamburger_Click(object sender, RoutedEventArgs e)
    {
        _isSideBarOpen = !_isSideBarOpen;
        AnimateSideBar();
    }

    /// <summary>
    /// 侧栏展开/收起过渡：逐帧驱动 SideBarColumn.Width（48 到 200，180ms 三次缓出），
    /// 文字同步淡入（120ms，延迟 40ms）/淡出（80ms，结束后收起可见性）。
    /// 侧栏始终位于 Column 0，X 基准恒为 0，反复开合不会累积偏移。
    /// 说明：GridLength 不是 double，Storyboard 无法直接动画列宽，故用帧定时器逐帧赋值。
    /// </summary>
    private void AnimateSideBar()
    {
        var open = _isSideBarOpen;
        StopSideBarAnimation();
        _sideBarTargetOpen = open;
        var current = SideBarColumn.ActualWidth;
        if (current <= 0)
        {
            current = open ? SideBarCompactWidth : SideBarExpandedWidth;
        }
        _sideBarFromWidth = current;
        _sideBarToWidth = open ? SideBarExpandedWidth : SideBarCompactWidth;
        if (open)
        {
            foreach (var text in _navTexts)
            {
                text.Visibility = Visibility.Visible;
            }
        }
        _sideBarTimer.Tick -= OnSideBarTick;
        _sideBarTimer.Tick += OnSideBarTick;
        _sideBarWatch = Stopwatch.StartNew();
        _sideBarAnimating = true;
        _sideBarTimer.Start();
    }

    /// <summary>每帧推进宽度与文字透明度；三次缓出（CubicEaseOut）。</summary>
    private void OnSideBarTick(object? sender, object e)
    {
        if (_sideBarWatch is null)
        {
            StopSideBarAnimation();
            return;
        }

        var elapsed = _sideBarWatch.Elapsed.TotalMilliseconds;
        var t = Math.Clamp(elapsed / SideBarAnimateDurationMs, 0d, 1d);
        var eased = 1 - Math.Pow(1 - t, 3);
        SideBarColumn.Width = new GridLength(_sideBarFromWidth + ((_sideBarToWidth - _sideBarFromWidth) * eased));
        if (_sideBarTargetOpen)
        {
            var fade = Math.Clamp((elapsed - 40) / 120d, 0d, 1d);
            var opacity = 1 - Math.Pow(1 - fade, 3);
            foreach (var text in _navTexts)
            {
                text.Opacity = opacity;
            }
        }
        else
        {
            var fade = Math.Clamp(elapsed / 80d, 0d, 1d);
            foreach (var text in _navTexts)
            {
                text.Opacity = 1 - fade;
            }
        }

        if (t >= 1)
        {
            FinishSideBarAnimation();
        }
    }

    /// <summary>动画收尾：落在目标宽度，收起时隐藏文字。</summary>
    private void FinishSideBarAnimation()
    {
        _sideBarTimer.Stop();
        _sideBarWatch = null;
        _sideBarAnimating = false;
        SideBarColumn.Width = new GridLength(_sideBarToWidth);
        foreach (var text in _navTexts)
        {
            text.Opacity = _sideBarTargetOpen ? 1 : 0;
            text.Visibility = _sideBarTargetOpen ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>停掉正在跑的侧栏动画，保留当前宽度/文字透明度作为下一次的起点（防连点跳变）。</summary>
    private void StopSideBarAnimation()
    {
        _sideBarTimer.Stop();
        _sideBarWatch = null;
        _sideBarAnimating = false;
    }
    /// <summary>导航项悬停：选中项保持选中底色，其余显示悬停底色。</summary>
    private void NavButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button && !ReferenceEquals(button, _selectedNav))
        {
            button.Background = HoverBrush;
        }
    }

    /// <summary>导航项离开：非选中项恢复透明底色。</summary>
    private void NavButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button && !ReferenceEquals(button, _selectedNav))
        {
            button.Background = TransparentBrush;
        }
    }

    /// <summary>点导航项：切页 + 更新选中态，不改变侧栏展开状态。</summary>
    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag)
        {
            return;
        }

        SetSelected(button);

        if (string.Equals(tag, "settings", StringComparison.OrdinalIgnoreCase))
        {
            NavigateTo(typeof(SettingsPage));
            return;
        }

        if (_pages.TryGetValue(tag, out var pageType))
        {
            NavigateTo(pageType);
        }
    }

    /// <summary>更新选中态：恢复上一个按钮的底色/竖条，设置当前按钮。</summary>
    private void SetSelected(Button? button)
    {
        if (ReferenceEquals(_selectedNav, button))
        {
            return;
        }

        if (_selectedNav is not null)
        {
            _selectedNav.Background = TransparentBrush;
            var oldIndicator = FindIndicator(_selectedNav);
            if (oldIndicator is not null)
            {
                oldIndicator.Visibility = Visibility.Collapsed;
            }
        }

        _selectedNav = button;

        if (button is null)
        {
            return;
        }

        button.Background = SelectedBrush;
        var indicator = FindIndicator(button);
        if (indicator is not null)
        {
            indicator.Visibility = Visibility.Visible;
        }
    }

    /// <summary>取导航按钮内容里左侧 3px 的强调色竖条。</summary>
    private static Border? FindIndicator(Button button)
    {
        return button.Content is Grid grid
            ? grid.Children.OfType<Border>().FirstOrDefault(b => (b.Tag as string) == "indicator")
            : null;
    }

    private void NavigateTo(Type pageType)
    {
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
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
