using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QQAgent.WinUI.Models;
using QQAgent.WinUI.Services;
using Windows.UI.ViewManagement;

namespace QQAgent.WinUI.Pages;

public sealed partial class ModelPage : Page
{
    private readonly UISettings _uiSettings = new();

    private AppSettings _settings = new();
    private string? _botRoot;
    private bool _loading;
    private bool _loadedOnce;
    private string? _suppressUrl;
    private string? _suppressModel;

    public ModelPage()
    {
        InitializeComponent();
        ProviderCombo.ItemsSource = Presets.All;

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        _uiSettings.ColorValuesChanged += OnSystemColorChanged;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;
        ReloadFromDisk();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _uiSettings.ColorValuesChanged -= OnSystemColorChanged;
    }

    /// <summary>从 settings.json + .env 回填全部控件。</summary>
    private void ReloadFromDisk()
    {
        _loading = true;
        try
        {
            _botRoot = EnvConfig.FindBotRoot();
            _settings = AppSettings.Load();

            var env = _botRoot is null ? new EnvValues() : EnvConfig.Load(_botRoot);

            _suppressUrl = env.BaseUrl ?? string.Empty;
            _suppressModel = env.Model ?? string.Empty;
            BaseUrlBox.Text = env.BaseUrl;
            ModelBox.Text = env.Model;
            ApiKeyBox.Password = env.ApiKey;
            ApiKeyPlainBox.Text = env.ApiKey;
            _settings.Provider = ResolveProviderId(env.BaseUrl);

            var preset = Presets.Get(_settings.Provider);
            ProviderCombo.SelectedItem = preset;
            UpdateProviderInfo(preset);

            ReasonLevel.SelectedIndex = EffortToIndex(env.ReasoningEffort);
            ThemeChoice.SelectedIndex = _settings.ThemeMode switch
            {
                ThemeMode.Light => 1,
                ThemeMode.Dark => 2,
                _ => 0,
            };

            PersonaBox.Text = Presets.InferPersona(env.Model);
            SaveButton.IsEnabled = _botRoot is not null;
            UpdateBotRootHint();
            UpdateAccentSwatch();
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "读取配置失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>先按 Base URL 认预设，认不出再回落到 settings.json 里记的 id。</summary>
    private static string ResolveProviderId(string? baseUrl)
    {
        var matched = Presets.MatchByUrl(baseUrl);
        if (!matched.IsCustom)
        {
            return matched.Id;
        }

        var saved = Presets.Get(App.Settings.Provider);
        return saved.Id;
    }

    private void OnProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderCombo.SelectedItem is not Preset preset)
        {
            return;
        }

        // 回填 SelectedItem 时 ComboBox 会异步补发一次 SelectionChanged，按回填值识别并吞掉，
        // 避免把用户自定义的 Base URL / 模型名冲掉。
        if (_suppressUrl is not null
            && string.Equals(BaseUrlBox.Text ?? string.Empty, _suppressUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ModelBox.Text ?? string.Empty, _suppressModel ?? string.Empty, StringComparison.Ordinal))
        {
            _suppressUrl = null;
            _suppressModel = null;
            UpdateProviderInfo(preset);
            return;
        }

        // 自定义清空，其它预设覆盖 URL 与模型名
        if (preset.IsCustom)
        {
            BaseUrlBox.Text = string.Empty;
            ModelBox.Text = string.Empty;
        }
        else
        {
            BaseUrlBox.Text = preset.BaseUrl;
            ModelBox.Text = preset.DefaultModel;
        }

        UpdateProviderInfo(preset);
        _settings.Provider = preset.Id;
        _settings.Save();
    }

    private void UpdateProviderInfo(Preset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Note))
        {
            ProviderInfoBar.Message = string.Empty;
            ProviderInfoBar.IsOpen = false;
            return;
        }

        ProviderInfoBar.Message = preset.Note;
        ProviderInfoBar.IsOpen = true;
    }

    private void OnModelTextChanged(object sender, TextChangedEventArgs e)
    {
        PersonaBox.Text = Presets.InferPersona(ModelBox.Text);
    }

    private void OnApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(ApiKeyPlainBox.Text, ApiKeyBox.Password, StringComparison.Ordinal))
        {
            ApiKeyPlainBox.Text = ApiKeyBox.Password;
        }
    }

    private void OnApiKeyPlainTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.Equals(ApiKeyBox.Password, ApiKeyPlainBox.Text, StringComparison.Ordinal))
        {
            ApiKeyBox.Password = ApiKeyPlainBox.Text;
        }
    }

    private void OnRevealKeyChecked(object sender, RoutedEventArgs e)
    {
        ApiKeyPlainBox.Text = ApiKeyBox.Password;
        ApiKeyPlainBox.Visibility = Visibility.Visible;
        ApiKeyBox.Visibility = Visibility.Collapsed;
        ApiKeyPlainBox.Focus(FocusState.Programmatic);
    }

    private void OnRevealKeyUnchecked(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Password = ApiKeyPlainBox.Text;
        ApiKeyPlainBox.Visibility = Visibility.Collapsed;
        ApiKeyBox.Visibility = Visibility.Visible;
        ApiKeyBox.Focus(FocusState.Programmatic);
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

        _settings.ThemeMode = mode;
        _settings.Save();
        App.ApplyTheme(mode);
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var root = _botRoot ?? EnvConfig.FindBotRoot();
        if (root is null)
        {
            ShowResult(InfoBarSeverity.Error, "未找到 Bot 根目录（含 src\\main.py），无法写入 .env");
            return;
        }

        try
        {
            var effort = IndexToEffort(ReasonLevel.SelectedIndex);
            var path = EnvConfig.Save(root, BaseUrlBox.Text.Trim(), ApiKeyBox.Password, ModelBox.Text.Trim(), effort);

            _botRoot = root;
            if (ProviderCombo.SelectedItem is Preset preset)
            {
                _settings.Provider = preset.Id;
            }

            _settings.Save();
            SaveButton.IsEnabled = true;
            UpdateBotRootHint();
            ShowResult(InfoBarSeverity.Success, "已写入 " + path + "，重启 Bot 后生效");
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "写入失败：" + ex.Message);
        }
    }

    private void OnReloadClicked(object sender, RoutedEventArgs e)
    {
        _loadedOnce = true;
        ReloadFromDisk();
        ShowResult(InfoBarSeverity.Informational, "已重新载入 .env 与 settings.json");
    }

    private void UpdateBotRootHint()
    {
        if (_botRoot is null)
        {
            BotRootText.Text = "未定位到 Bot 根目录，保存已禁用。";
            SaveButton.IsEnabled = false;
            ShowResult(InfoBarSeverity.Warning, "未找到含 src\\main.py 的目录，无法写入 .env");
            return;
        }

        var wsUrl = EnvConfig.ReadOneBotUrl(_botRoot);
        var suffix = wsUrl is null ? string.Empty : "　｜　OneBot: " + wsUrl;
        BotRootText.Text = "Bot 根目录：" + _botRoot + suffix;
    }

    private void ShowResult(InfoBarSeverity severity, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }

    private void UpdateAccentSwatch()
    {
        try
        {
            var color = _uiSettings.GetColorValue(UIColorType.Accent);
            AccentRect.Fill = new SolidColorBrush(color);
            AccentText.Text = "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }
        catch (Exception)
        {
            AccentText.Text = string.Empty;
        }
    }

    private void OnSystemColorChanged(UISettings sender, object args)
    {
        if (DispatcherQueue is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(UpdateAccentSwatch);
    }

    private static int EffortToIndex(string? effort)
    {
        return (effort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "low" => 1,
            "medium" => 2,
            "high" => 3,
            _ => 0,
        };
    }

    private static string IndexToEffort(int index)
    {
        return index switch
        {
            1 => "low",
            2 => "medium",
            3 => "high",
            _ => string.Empty,
        };
    }
}


