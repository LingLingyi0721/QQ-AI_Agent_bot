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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QQAgent.WinUI.Pages;
using QQAgent.WinUI.Services;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace QQAgent.WinUI;

public sealed partial class MainWindow : Window
{
    /// <summary>悬停多久后自动展开侧栏（毫秒）。</summary>

    private const int PaneHoverDelayMs = 350;

    private readonly UISettings _uiSettings = new();

    private readonly DispatcherTimer _paneHoverTimer = new();

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

    /// <summary>侧栏已「点开即常驻」，移出不再自动收起。</summary>

    private bool _panePinned;

    /// <summary>指针当前是否停在收起状态的图标栏范围内。</summary>

    private bool _pointerInPaneStrip;

    public MainWindow()
    {
        InitializeComponent();
        Title = "QQ-AI_Agent";

        // 自定义标题栏：系统按钮保留在右上角，最小化/最大化/关闭仍由 AppWindow 绘制。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);

        SetupNavigation();
        SetupPaneHover();
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
        _paneHoverTimer.Stop();
        _paneHoverTimer.Tick -= OnPaneHoverTimerTick;

        NavView.PointerEntered -= OnNavPointerEntered;
        NavView.PointerMoved -= OnNavPointerMoved;
        NavView.PointerExited -= OnNavPointerExited;
        NavView.PointerPressed -= OnNavPointerPressed;
        NavView.ItemInvoked -= OnNavItemInvoked;
        NavView.PaneClosing -= OnPaneClosing;
        NavView.PaneClosed -= OnPaneClosed;

        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        Activated -= OnWindowActivated;
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

    // ---- 导航 ----

    private void SetupNavigation()
    {
        NavView.ItemInvoked += OnNavItemInvoked;
        NavView.PaneClosing += OnPaneClosing;
        NavView.PaneClosed += OnPaneClosed;

        ApplySettingsItemText();

        NavView.SelectedItem = ModelNavItem;
        if (ContentFrame.CurrentSourcePageType != typeof(ModelPage))
        {
            ContentFrame.Navigate(typeof(ModelPage));
        }
    }

    /// <summary>内置「设置」项的文案改成中文；拿不到就保持系统默认。</summary>

    private void ApplySettingsItemText()
    {
        try
        {
            if (NavView.SettingsItem is NavigationViewItem settingsItem)
            {
                settingsItem.Content = "设置";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        // 点任意导航项（或汉堡）后展开并常驻，之后移出不再收起
        _panePinned = true;

        if (args.IsSettingsInvoked)
        {
            NavigateTo(typeof(SettingsPage));
            SelectSettingsItem();
            return;
        }

        if (args.InvokedItemContainer is NavigationViewItem item
            && item.Tag is string tag
            && _pages.TryGetValue(tag, out var pageType))
        {
            NavigateTo(pageType);
        }
    }

    private void NavigateTo(Type pageType)
    {
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }

        // LeftCompact 下导航会顺手收起侧栏，这里在布局之后重新撑开
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_panePinned && !NavView.IsPaneOpen)
            {
                NavView.IsPaneOpen = true;
            }
        });
    }

    private void SelectSettingsItem()
    {
        try
        {
            NavView.SelectedItem = NavView.SettingsItem;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }
    }

    // ---- 侧边栏悬停展开 ----

    private void SetupPaneHover()
    {
        _paneHoverTimer.Interval = TimeSpan.FromMilliseconds(PaneHoverDelayMs);
        _paneHoverTimer.Tick += OnPaneHoverTimerTick;

        NavView.PointerEntered += OnNavPointerEntered;
        NavView.PointerMoved += OnNavPointerMoved;
        NavView.PointerExited += OnNavPointerExited;
        NavView.PointerPressed += OnNavPointerPressed;
    }

    /// <summary>指针是否落在「当前状态的侧栏」宽度范围内（收起 = 图标栏宽度）。</summary>

    private bool IsPointerInPaneStrip(Windows.Foundation.Point position)
    {
        if (position.X < 0 || position.Y < 0)
        {
            return false;
        }

        var stripWidth = NavView.IsPaneOpen ? NavView.OpenPaneLength : NavView.CompactPaneLength;
        return position.X <= stripWidth;
    }

    private void OnNavPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (NavView.IsPaneOpen)
        {
            return;
        }

        if (!IsPointerInPaneStrip(e.GetCurrentPoint(NavView).Position))
        {
            return;
        }

        _pointerInPaneStrip = true;
        if (!_paneHoverTimer.IsEnabled)
        {
            _paneHoverTimer.Start();
        }
    }

    /// <summary>指针划过时判定进/出侧栏：进入启动定时器，离开按常驻状态收起。</summary>

    private void OnNavPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var inside = IsPointerInPaneStrip(e.GetCurrentPoint(NavView).Position);

        if (!inside)
        {
            _pointerInPaneStrip = false;
            _paneHoverTimer.Stop();
            CollapsePaneIfNotPinned();
            return;
        }

        if (_pointerInPaneStrip)
        {
            return;
        }

        _pointerInPaneStrip = true;
        if (!NavView.IsPaneOpen && !_paneHoverTimer.IsEnabled)
        {
            _paneHoverTimer.Start();
        }
    }

    private void OnNavPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInPaneStrip = false;
        _paneHoverTimer.Stop();
        CollapsePaneIfNotPinned();
    }

    private void OnPaneHoverTimerTick(object? sender, object e)
    {
        _paneHoverTimer.Stop();

        if (!_pointerInPaneStrip || NavView.IsPaneOpen)
        {
            return;
        }

        try
        {
            NavView.IsPaneOpen = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }
    }

    /// <summary>移出收起：常驻（点过导航项/汉堡）后不再自动收起。</summary>

    private void CollapsePaneIfNotPinned()
    {
        if (_panePinned || !NavView.IsPaneOpen)
        {
            return;
        }

        try
        {
            NavView.IsPaneOpen = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }
    }

    /// <summary>点汉堡按钮：按下瞬间记录开合意图，展开即视为常驻。</summary>

    private void OnNavPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPaneToggleSource(e.OriginalSource))
        {
            return;
        }

        // 按下时侧栏已展开，说明这次点击是收起，常驻状态同步解除
        _panePinned = !NavView.IsPaneOpen;
        if (_panePinned)
        {
            try
            {
                NavView.IsPaneOpen = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }
    }

    /// <summary>命中的元素是否属于汉堡按钮（NavigationView 模板里的 ToggleButton）。</summary>

    private bool IsPaneToggleSource(object? source)
    {
        var node = source as DependencyObject;
        for (var depth = 0; node is not null && depth < 16; depth++)
        {
            if (node is ToggleButton)
            {
                return true;
            }

            if (ReferenceEquals(node, NavView))
            {
                return false;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>常驻状态下禁止 NavigationView 自己把侧栏收掉（点击导航项后不收回）。</summary>

    private void OnPaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        if (_panePinned)
        {
            args.Cancel = true;
        }
    }

    private void OnPaneClosed(NavigationView sender, object args)
    {
        _paneHoverTimer.Stop();
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
