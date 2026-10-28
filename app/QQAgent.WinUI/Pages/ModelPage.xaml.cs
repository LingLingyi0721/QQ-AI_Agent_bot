using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QQAgent.WinUI.Dialogs;
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

    /// <summary>主按钮当前处于「立即重启」态。</summary>
    private bool _restartMode;

    /// <summary>重启进行中，防连点。</summary>
    private bool _restarting;

    private IReadOnlyList<PersonaItem> _personas = Array.Empty<PersonaItem>();

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

            ReasonLevel.SelectedIndex = EffortToIndex(env.ReasoningEffort);
            ThemeChoice.SelectedIndex = _settings.ThemeMode switch
            {
                ThemeMode.Light => 1,
                ThemeMode.Dark => 2,
                _ => 0,
            };

            _personas = PersonaStore.ListAll(_botRoot ?? string.Empty);
            PersonaCombo.ItemsSource = _personas;

            var customMode = string.Equals(env.PersonaMode, "custom", StringComparison.OrdinalIgnoreCase);
            PersonaMode.SelectedIndex = customMode ? 1 : 0;
            ApplyPersonaMode();

            if (customMode && SelectPersonaByPath(env.PersonaFile))
            {
                PersonaAutoBox.Text = PersonaCombo.SelectedItem is PersonaItem picked
                    ? Path.GetFileName(picked.RelativePath)
                    : string.Empty;
            }
            else
            {
                PersonaAutoBox.Text = Presets.InferPersona(env.Model);
            }

            SaveButton.IsEnabled = _botRoot is not null;
            ResetPrimaryState();
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
            return;
        }

        ResetPrimaryState();

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

        _settings.Provider = preset.Id;
        _settings.Save();
    }


    private void OnBaseUrlChanged(object sender, TextChangedEventArgs e)
    {
        ResetPrimaryState();
    }

    private void OnModelTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            ResetPrimaryState();
        }

        if (PersonaMode.SelectedIndex == 1)
        {
            return;
        }

        PersonaAutoBox.Text = Presets.InferPersona(ModelBox.Text);
    }

    private void OnApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            ResetPrimaryState();
        }

        if (!string.Equals(ApiKeyPlainBox.Text, ApiKeyBox.Password, StringComparison.Ordinal))
        {
            ApiKeyPlainBox.Text = ApiKeyBox.Password;
        }
    }

    private void OnApiKeyPlainTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            ResetPrimaryState();
        }

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

    private void OnReasonLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            ResetPrimaryState();
        }
    }

    private void OnThemeChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ResetPrimaryState();

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

    // ---------- 人设 ----------

    private void OnPersonaModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ResetPrimaryState();
        ApplyPersonaMode();
    }

    /// <summary>两个模式的控件互斥显示；默认模式只读框显示自动推断的文件名。</summary>
    private void ApplyPersonaMode()
    {
        var custom = PersonaMode.SelectedIndex == 1;
        PersonaCustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

        if (!custom)
        {
            PersonaAutoBox.Text = Presets.InferPersona(ModelBox.Text);
            return;
        }

        if (PersonaCombo.SelectedIndex < 0 && _personas.Count > 0)
        {
            PersonaCombo.SelectedIndex = 0;
        }

        if (PersonaCombo.SelectedItem is PersonaItem item)
        {
            PersonaAutoBox.Text = Path.GetFileName(item.RelativePath);
        }
        else
        {
            PersonaAutoBox.Text = string.Empty;
        }
    }

    private void OnPersonaSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ResetPrimaryState();

        PersonaAutoBox.Text = PersonaCombo.SelectedItem is PersonaItem item
            ? Path.GetFileName(item.RelativePath)
            : string.Empty;
    }

    private async void OnNewPersonaClicked(object sender, RoutedEventArgs e)
    {
        ResetPrimaryState();
        await EditPersonaAsync("新建人设", null, string.Empty, false);
    }

    private async void OnEditPersonaClicked(object sender, RoutedEventArgs e)
    {
        ResetPrimaryState();

        if (_botRoot is null)
        {
            ShowResult(InfoBarSeverity.Error, "未找到 Bot 根目录，无法编辑人设");
            return;
        }

        if (PersonaCombo.SelectedItem is not PersonaItem item)
        {
            ShowResult(InfoBarSeverity.Informational, "请先选择要编辑的人设");
            return;
        }

        var content = PersonaStore.Load(_botRoot, item.RelativePath);
        await EditPersonaAsync("编辑人设", item.Name, content, item.IsCustom);
    }

    private async void OnImportPersonaClicked(object sender, RoutedEventArgs e)
    {
        ResetPrimaryState();

        if (_botRoot is null)
        {
            ShowResult(InfoBarSeverity.Error, "未找到 Bot 根目录，无法导入人设");
            return;
        }

        if (App.Main is null)
        {
            ShowResult(InfoBarSeverity.Error, "主窗口不可用，无法打开文件选择器");
            return;
        }

        string? sourcePath;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".txt");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Main);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            sourcePath = file.Path;
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "打开文件选择器失败：" + ex.Message);
            return;
        }

        var result = PersonaStore.Import(sourcePath);
        if (!result.Ok)
        {
            await ShowInfoAsync("导入失败", result.Message);
            return;
        }

        var name = Path.GetFileNameWithoutExtension(sourcePath);
        await EditPersonaAsync("导入人设", name, result.Content, false);
    }

    /// <summary>弹窗编辑并保存；overwrite 仅对已有的自定义人设生效。</summary>
    private async Task EditPersonaAsync(string title, string? name, string content, bool overwrite)
    {
        if (_botRoot is null)
        {
            ShowResult(InfoBarSeverity.Error, "未找到 Bot 根目录，无法保存人设");
            return;
        }

        var dialog = new PersonaEditorDialog(title, name, content)
        {
            XamlRoot = XamlRoot,
        };

        var picked = await dialog.ShowAsync();
        if (picked != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var saved = PersonaStore.Save(_botRoot, dialog.PersonaName, dialog.PersonaContent, overwrite);
            _personas = PersonaStore.ListAll(_botRoot);
            PersonaCombo.ItemsSource = _personas;

            PersonaMode.SelectedIndex = 1;
            ApplyPersonaMode();
            SelectPersonaByPath(saved.RelativePath);
            PersonaAutoBox.Text = Path.GetFileName(saved.RelativePath);
            ShowResult(InfoBarSeverity.Success, "人设已保存：" + saved.DisplayName);
        }
        catch (Exception ex)
        {
            await ShowInfoAsync("保存人设失败", ex.Message);
        }
    }

    private bool SelectPersonaByPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var target = relativePath.Trim().Replace('\\', '/');
        foreach (var item in _personas)
        {
            if (string.Equals(item.RelativePath, target, StringComparison.OrdinalIgnoreCase))
            {
                PersonaCombo.SelectedItem = item;
                return true;
            }
        }

        // 只对不上路径时，退回按文件名匹配
        var fileName = Path.GetFileName(target);
        foreach (var item in _personas)
        {
            if (string.Equals(Path.GetFileName(item.RelativePath), fileName, StringComparison.OrdinalIgnoreCase))
            {
                PersonaCombo.SelectedItem = item;
                return true;
            }
        }

        return false;
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }

    // ---------- 保存 / 重启 ----------

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
            var custom = PersonaMode.SelectedIndex == 1;
            if (custom && PersonaCombo.SelectedItem is not PersonaItem)
            {
                ShowResult(InfoBarSeverity.Warning, "自定义模式还没选人设，先选一个或新建");
                return;
            }

            var personaFile = custom && PersonaCombo.SelectedItem is PersonaItem item ? item.RelativePath : string.Empty;
            var effort = IndexToEffort(ReasonLevel.SelectedIndex);
            EnvConfig.Save(
                root,
                BaseUrlBox.Text.Trim(),
                ApiKeyBox.Password,
                ModelBox.Text.Trim(),
                effort,
                custom ? "custom" : "auto",
                personaFile);

            _botRoot = root;
            if (ProviderCombo.SelectedItem is Preset preset)
            {
                _settings.Provider = preset.Id;
            }

            _settings.Save();
            SaveButton.IsEnabled = true;
            ShowResult(InfoBarSeverity.Success, "已保存");
            EnterRestartState();
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "写入失败：" + ex.Message);
        }
    }

    /// <summary>保存成功后主按钮变成「立即重启」。</summary>
    private void EnterRestartState()
    {
        if (_restartMode || _restarting)
        {
            return;
        }

        _restartMode = true;
        SaveButton.Content = "立即重启";
        SaveButton.Click -= OnSaveClicked;
        SaveButton.Click += OnRestartClicked;
    }

    /// <summary>任何控件改动后回到「保存配置」态。</summary>
    private void ResetPrimaryState()
    {
        if (!_restartMode || _restarting)
        {
            return;
        }

        _restartMode = false;
        SaveButton.Content = "保存配置";
        SaveButton.Click -= OnRestartClicked;
        SaveButton.Click += OnSaveClicked;
    }

    private async void OnRestartClicked(object sender, RoutedEventArgs e)
    {
        if (_restarting)
        {
            return;
        }

        _restarting = true;
        SaveButton.IsEnabled = false;
        ResetPrimaryState();

        try
        {
            var runner = BotRunner.Current;
            if (runner.IsRunning)
            {
                await Task.Run(runner.Stop);
                for (var i = 0; i < 20 && runner.IsRunning; i++)
                {
                    await Task.Delay(50);
                }
            }

            var result = await Task.Run(runner.Start);
            ShowResult(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error, result.Message);
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "重启失败：" + ex.Message);
        }
        finally
        {
            // 禁用 1 秒防连点
            await Task.Delay(1000);
            _restarting = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void OnReloadClicked(object sender, RoutedEventArgs e)
    {
        _loadedOnce = true;
        ReloadFromDisk();
        ShowResult(InfoBarSeverity.Informational, "已重新载入 .env 与 settings.json");
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