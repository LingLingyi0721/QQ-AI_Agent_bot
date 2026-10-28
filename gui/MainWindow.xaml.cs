using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using QQAgent.Gui.Interop;
using QQAgent.Gui.Models;
using QQAgent.Gui.Services;

namespace QQAgent.Gui;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 4000;

    private readonly BotProcess _bot;
    private string _botRoot = "";
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();

        _bot = new BotProcess(Dispatcher);
        _bot.LogReceived += OnBotLog;
        _bot.Exited += OnBotExited;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;

        ProviderBox.ItemsSource = Presets.All.Select(p => p.Name).ToList();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        // Mica 成功或失败都无碍：窗口根 Grid 已是纯色兜底背景
        Win32.TryApply(handle);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        UpdateRunState();
        AppendLog("控制台已就绪，bot 目录：" + (_botRoot.Length != 0 ? _botRoot : "（未定位）"), false);
    }

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var root = BotConfig.TryFindBotRoot();
            if (root is null)
            {
                SaveStatus.Text = "未找到 bot 根目录（需包含 src\\main.py）";
                SaveStatus.Foreground = (Brush)FindResource("Danger");
                return;
            }

            _botRoot = root;
            var settings = BotConfig.Load(root);
            BaseUrlBox.Text = settings.BaseUrl;
            ModelBox.Text = settings.Model;
            ApiKeyBox.Password = settings.ApiKey;
            ApiKeyPlainBox.Text = settings.ApiKey;
            SelectEffort(settings.ReasoningEffort);
            SelectProvider(settings.BaseUrl, settings.Model);
            UpdatePersona(settings.Model);
            WsText.Text = BotConfig.ReadWsUrl(root);
            PortText.Text = BotConfig.ReadWsPort(root);
            SaveStatus.Text = "";
            SaveStatus.Foreground = (Brush)FindResource("TextSecondary");
        }
        finally
        {
            _loading = false;
        }
    }

    private void SelectProvider(string baseUrl, string model)
    {
        var preset = Presets.Match(baseUrl, model);
        var name = preset is null ? Presets.CustomName : preset.Name;
        foreach (var item in ProviderBox.ItemsSource)
        {
            if (string.Equals(item as string, name, StringComparison.Ordinal))
            {
                ProviderBox.SelectedItem = item;
                return;
            }
        }
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderBox.SelectedItem is null)
        {
            return;
        }

        var name = ProviderBox.SelectedItem as string;
        var preset = Presets.All.FirstOrDefault(p => p.Name == name);
        if (preset is null)
        {
            return;
        }

        BaseUrlBox.Text = preset.BaseUrl;
        ModelBox.Text = preset.Model;
        ProviderHint.Text = preset.Name == Presets.CustomName
            ? "自定义：Base URL 与模型名已清空，请手动填写"
            : "已套用 " + preset.Name + " 的默认地址与模型名";
    }

    private void SelectEffort(string effort)
    {
        var value = (effort ?? "").Trim().ToLowerInvariant();
        if (value == "low")
        {
            EffortLow.IsChecked = true;
        }
        else if (value == "medium")
        {
            EffortMedium.IsChecked = true;
        }
        else if (value == "high")
        {
            EffortHigh.IsChecked = true;
        }
        else
        {
            EffortOff.IsChecked = true;
        }
    }

    private string CurrentEffort()
    {
        if (EffortLow.IsChecked == true)
        {
            return "low";
        }

        if (EffortMedium.IsChecked == true)
        {
            return "medium";
        }

        if (EffortHigh.IsChecked == true)
        {
            return "high";
        }

        return "";
    }

    private void ModelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePersona(ModelBox.Text);
    }

    private void UpdatePersona(string? model)
    {
        if (PersonaBox is null)
        {
            return;
        }

        PersonaBox.Text = BotConfig.PersonaDisplayPath(model);
    }

    private void ShowKeyToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ApiKeyPlainBox is null || ShowKeyToggle is null)
        {
            return;
        }

        if (ShowKeyToggle.IsChecked == true)
        {
            ApiKeyPlainBox.Text = ApiKeyBox.Password;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ApiKeyPlainBox.Visibility = Visibility.Visible;
            return;
        }

        ApiKeyBox.Password = ApiKeyPlainBox.Text;
        ApiKeyPlainBox.Visibility = Visibility.Collapsed;
        ApiKeyBox.Visibility = Visibility.Visible;
    }

    private string CurrentKey()
    {
        return ShowKeyToggle.IsChecked == true ? ApiKeyPlainBox.Text : ApiKeyBox.Password;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageModel is null)
        {
            return;
        }

        var tag = (sender as FrameworkElement)?.Tag as string;
        PageModel.Visibility = tag == "model" ? Visibility.Visible : Visibility.Collapsed;
        PageRuntime.Visibility = tag == "runtime" ? Visibility.Visible : Visibility.Collapsed;
        PageLogs.Visibility = tag == "logs" ? Visibility.Visible : Visibility.Collapsed;
        ContentScroll.ScrollToTop();
    }
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_botRoot.Length == 0)
        {
            SaveStatus.Text = "未找到 bot 根目录，无法保存";
            SaveStatus.Foreground = (Brush)FindResource("Danger");
            return;
        }

        try
        {
            var settings = new BotSettings();
            settings.BaseUrl = BaseUrlBox.Text.Trim();
            settings.ApiKey = CurrentKey().Trim();
            settings.Model = ModelBox.Text.Trim();
            settings.ReasoningEffort = CurrentEffort();
            BotConfig.Save(_botRoot, settings);
            SaveStatus.Text = "已保存到 .env（" + DateTime.Now.ToString("HH:mm:ss") + "）";
            SaveStatus.Foreground = (Brush)FindResource("Success");
            AppendLog("配置已写入 .env：" + settings.Model + " / " + settings.BaseUrl, false);
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "保存失败：" + ex.Message;
            SaveStatus.Foreground = (Brush)FindResource("Danger");
            App.WriteErrorLog(ex);
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        SaveStatus.Text = "已重新载入 .env";
        SaveStatus.Foreground = (Brush)FindResource("TextSecondary");
        AppendLog("配置已重新载入", false);
    }

    private void StartBot_Click(object sender, RoutedEventArgs e)
    {
        if (_bot.IsRunning)
        {
            return;
        }

        if (_botRoot.Length == 0)
        {
            MessageBox.Show(this, "未找到 bot 根目录（需包含 src\\main.py）。", "无法启动",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = BotConfig.Load(_botRoot);
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            MessageBox.Show(this, "请先在「模型配置」里填写 LLM_API_KEY 并点「保存配置」。", "缺少 LLM_API_KEY",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _bot.Start(_botRoot);
            UpdateRunState();
            AppendLog("已启动 bot：python -m src.main", false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动失败：" + ex.Message, "启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            App.WriteErrorLog(ex);
        }
    }

    private void StopBot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _bot.Stop();
        }
        catch (Exception ex)
        {
            App.WriteErrorLog(ex);
        }

        UpdateRunState();
        AppendLog("已发送停止请求（结束整个进程树）", false);
    }

    private void UpdateRunState()
    {
        var running = _bot.IsRunning;
        StatusDot.Fill = (Brush)FindResource(running ? "Success" : "TextSecondary");
        StatusText.Text = running ? "运行中" : "未运行";
        var pid = _bot.ProcessId;
        PidText.Text = pid is null ? "" : "PID " + pid;
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
    }

    private void OnBotLog(string line, bool isError)
    {
        AppendLog(line, isError);
    }

    private void OnBotExited(int code)
    {
        UpdateRunState();
        AppendLog("bot 进程已退出，退出码 " + code, true);
    }

    private void AppendLog(string line, bool isError)
    {
        if (LogBox is null)
        {
            return;
        }

        if (MaxLogLines <= LogBox.LineCount)
        {
            LogBox.Clear();
        }

        var prefix = isError ? "[err] " : "";
        LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + prefix + line + Environment.NewLine);
        if (AutoScrollCheck.IsChecked == true)
        {
            LogBox.ScrollToEnd();
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog();
        dialog.Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
        dialog.FileName = "qqagent-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log";
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, LogBox.Text, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "另存为",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 鼠标已松开，忽略
            }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaxButton.Content = "\uE922";
            return;
        }

        WindowState = WindowState.Maximized;
        MaxButton.Content = "\uE923";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_bot.IsRunning)
        {
            _bot.Stop();
        }
    }
}