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

    /// <summary>最近一次点导航项的时间（UTC），用于区分「点导航项导致的收起」与「用户主动收起」。</summary>

    private DateTime _lastItemInvokedUtc = DateTime.MinValue;

    /// <summary>汉堡按钮：用户主动点击时重置上面的时间戳，保证仍能正常收起。</summary>

    private Button? _paneToggleButton;

    /// <summary>点导航项后这段时间内忽略 Pane 收起（对齐任务管理器：切页不收起侧栏）。</summary>

    private const int NavItemCollapseGuardMs = 600;

    /// <summary>展开动画时长（ms）。</summary>
    private const int PaneOpenDurationMs = 180;

    /// <summary>收起动画时长（ms）。</summary>
    private const int PaneCollapseDurationMs = 140;

    /// <summary>收起动画的右移终点（逻辑 px）。</summary>
    private const double PaneCollapseExitOffset = 150;

    /// <summary>防止设置 PaneDisplayMode 时重入 IsPaneOpen 回调。</summary>

    private bool _syncingPaneDisplayMode;

    /// <summary>侧栏过渡动画（每次播放重建，连点时先停旧的）。</summary>
    private Storyboard? _paneStoryboard;

    /// <summary>动画进行中标记；Completed 后复位，配合版本号防止旧回调改错模式。</summary>
    private bool _paneAnimating;

    /// <summary>当前动画的目标方向（true = 展开），用于忽略同向重复触发。</summary>
    private bool _paneAnimTargetOpen;

    /// <summary>动画版本号：连点时递增，旧 Storyboard 的 Completed 直接作废。</summary>
    private int _paneAnimVersion;

    /// <summary>IsPaneOpen 属性变化的回调令牌，窗口关闭时注销。</summary>

    private long _paneOpenCallbackToken;

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
    public bool IsPaneOpenNow => NavView.IsPaneOpen;

    /// <summary>当前侧栏水平位移（自检/自动化用，动画中间态采样）。</summary>
    public double PaneTranslateX => PaneTransform.TranslateX;

    /// <summary>当前侧栏整体透明度（自检/自动化用）。</summary>
    public double PaneOpacity => NavView.Opacity;

    /// <summary>自检用：主动展开侧栏（走与点汉堡相同的 IsPaneOpen 回调）。</summary>
    public void OpenPaneForTest()
    {
        NavView.IsPaneOpen = true;
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
        StopPaneAnimation();

        NavView.ItemInvoked -= OnNavItemInvoked;
        NavView.Loaded -= OnNavViewLoaded;
        NavView.PaneClosing -= OnNavPaneClosing;

        if (_paneOpenCallbackToken != 0)
        {
            NavView.UnregisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, _paneOpenCallbackToken);
            _paneOpenCallbackToken = 0;
        }

        if (_paneToggleButton is not null)
        {
            _paneToggleButton.Click -= OnPaneToggleButtonClicked;
            _paneToggleButton.PointerPressed -= OnPaneToggleButtonPointerPressed;
            _paneToggleButton = null;
        }

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

    // ---- 导航 ----

    private void SetupNavigation()
    {
        NavView.ItemInvoked += OnNavItemInvoked;
        NavView.PaneClosing += OnNavPaneClosing;
        NavView.Loaded += OnNavViewLoaded;
        _paneOpenCallbackToken = NavView.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, OnIsPaneOpenPropertyChanged);

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

    /// <summary>
    /// 展开态切到 Left（Left 模式下点导航项不会自动收起，对齐任务管理器），
    /// 收起态切回 LeftCompact（保留 60px 图标栏）。仅改显示模式，不直接改 IsPaneOpen。
    /// 模式切换本身是瞬间的布局变化，视觉过渡由 PlayPaneAnimation 的合成动画补上。
    /// </summary>
    private void OnIsPaneOpenPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_syncingPaneDisplayMode)
        {
            return;
        }

        if (NavView.IsPaneOpen)
        {
            // 动画已经在往展开方向跑就忽略重复请求，避免自己和自己抢
            if (_paneAnimating && _paneAnimTargetOpen)
            {
                return;
            }

            SwitchPaneDisplayMode(NavigationViewPaneDisplayMode.Left);
            PlayPaneAnimation(open: true);
            return;
        }

        if (NavView.PaneDisplayMode == NavigationViewPaneDisplayMode.Left)
        {
            // 收起动画期间保持 Left 模式，等动画结束后再切回紧凑模式
            if (_paneAnimating && !_paneAnimTargetOpen)
            {
                return;
            }

            PlayPaneAnimation(open: false);
            return;
        }

        StopPaneAnimation();
        ResetPaneVisual();
    }

    /// <summary>切换侧栏显示模式；切换期间屏蔽 IsPaneOpen 回调，避免重入。</summary>
    private void SwitchPaneDisplayMode(NavigationViewPaneDisplayMode mode)
    {
        if (NavView.PaneDisplayMode == mode)
        {
            return;
        }

        _syncingPaneDisplayMode = true;
        try
        {
            NavView.PaneDisplayMode = mode;
            NavView.UpdateLayout();
        }
        finally
        {
            _syncingPaneDisplayMode = false;
        }
    }

    /// <summary>
    /// 展开/收起过渡动画：只动合成属性（RenderTransform.TranslateX + Opacity），不触发布局动画。
    /// 展开 180ms EaseOut：-162 → 0，Opacity 0.35 → 1；
    /// 收起 140ms EaseIn：0 → +150，Opacity 1 → 0，完成后才切回 LeftCompact 并复位。
    /// </summary>
    private void PlayPaneAnimation(bool open)
    {
        _paneAnimVersion++;
        var version = _paneAnimVersion;
        StopPaneAnimation();

        _paneAnimating = true;
        _paneAnimTargetOpen = open;

        try
        {
            var durationMs = open ? PaneOpenDurationMs : PaneCollapseDurationMs;
            var fromX = open ? -(NavView.OpenPaneLength - NavView.CompactPaneLength) : 0d;
            var toX = open ? 0d : PaneCollapseExitOffset;
            var fromOpacity = open ? 0.35 : 1d;
            var toOpacity = open ? 1d : 0d;

            // 先落到起始帧，避免 Begin 之前闪一帧终态
            PaneTransform.TranslateY = 0;
            PaneTransform.TranslateX = fromX;
            NavView.Opacity = fromOpacity;

            var storyboard = new Storyboard();
            var easing = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };

            var move = new DoubleAnimation
            {
                From = fromX,
                To = toX,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EasingFunction = easing,
            };
            Storyboard.SetTarget(move, NavView);
            Storyboard.SetTargetProperty(move, "(UIElement.RenderTransform).(CompositeTransform.TranslateX)");
            storyboard.Children.Add(move);

            var fade = new DoubleAnimation
            {
                From = fromOpacity,
                To = toOpacity,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EasingFunction = easing,
            };
            Storyboard.SetTarget(fade, NavView);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);

            storyboard.Completed += (_, _) =>
            {
                if (version != _paneAnimVersion)
                {
                    return;
                }

                _paneAnimating = false;

                if (!open)
                {
                    // 已经淡到全透明，此时切模式用户看不到宽度跳变
                    SwitchPaneDisplayMode(NavigationViewPaneDisplayMode.LeftCompact);
                }

                ResetPaneVisual();
            };

            _paneStoryboard = storyboard;
            storyboard.Begin();
        }
        catch (Exception ex)
        {
            // 动画失败不影响功能：直接落到目标态
            Debug.WriteLine(ex.ToString());
            _paneAnimating = false;
            if (!open)
            {
                SwitchPaneDisplayMode(NavigationViewPaneDisplayMode.LeftCompact);
            }

            ResetPaneVisual();
        }
    }

    /// <summary>停掉正在跑的侧栏动画（连点时先停旧的再起新的）。</summary>
    private void StopPaneAnimation()
    {
        if (_paneStoryboard is null)
        {
            return;
        }

        try
        {
            _paneStoryboard.Stop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }

        _paneStoryboard = null;
    }

    /// <summary>把动画留下的偏移与透明度复位，避免影响后续布局/截图。</summary>
    private void ResetPaneVisual()
    {
        PaneTransform.TranslateX = 0;
        PaneTransform.TranslateY = 0;
        NavView.Opacity = 1;
    }

    /// <summary>
    /// 兜底：Pane 真要收起时，若刚点过导航项（600ms 内）就取消。
    /// 注意：本平台版本对 Cancel 不生效（实测），真正起作用的是上面的显示模式切换。
    /// </summary>
    private void OnNavPaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        if (args.Cancel)
        {
            return;
        }

        if ((DateTime.UtcNow - _lastItemInvokedUtc).TotalMilliseconds < NavItemCollapseGuardMs)
        {
            args.Cancel = true;
        }
    }

    private void OnNavViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_paneToggleButton is not null)
        {
            return;
        }

        _paneToggleButton = FindPaneToggleButton(NavView);
        if (_paneToggleButton is null)
        {
            return;
        }

        _paneToggleButton.Click += OnPaneToggleButtonClicked;
        _paneToggleButton.PointerPressed += OnPaneToggleButtonPointerPressed;
    }

    /// <summary>用户主动点汉堡：清掉时间戳，让默认开合逻辑照常执行。不取消默认行为。</summary>
    private void OnPaneToggleButtonClicked(object sender, RoutedEventArgs e)
    {
        _lastItemInvokedUtc = DateTime.MinValue;
    }

    /// <summary>按下早于 Click，先清一次，避免「刚点完导航项立刻点汉堡」被误判。</summary>
    private void OnPaneToggleButtonPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _lastItemInvokedUtc = DateTime.MinValue;
    }

    private static Button? FindPaneToggleButton(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button && string.Equals(button.Name, "TogglePaneButton", StringComparison.Ordinal))
            {
                return button;
            }

            var found = FindPaneToggleButton(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        // 记录时间点：紧随其后的 PaneClosing 属于「点导航项导致的收起」，应当被取消。
        _lastItemInvokedUtc = DateTime.UtcNow;

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
