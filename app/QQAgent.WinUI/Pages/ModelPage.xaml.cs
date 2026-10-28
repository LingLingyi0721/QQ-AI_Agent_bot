using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using QQAgent.WinUI.Dialogs;
using QQAgent.WinUI.Models;
using QQAgent.WinUI.Services;

namespace QQAgent.WinUI.Pages;

public sealed partial class ModelPage : Page
{
    private AppSettings _settings = new();
    private string? _botRoot;
    private bool _loading;
    private bool _loadedOnce;

    /// <summary>刷新人设下拉内容时抑制 SelectionChanged。</summary>
    private bool _refreshingPersona;

    /// <summary>回填期间写入的值，仅用于识别 ComboBox 异步补发的那一次 SelectionChanged。</summary>
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
    }

    private bool IsCustomMode => PersonaMode.SelectedIndex == 1;

    private PersonaItem? SelectedPersona => (PersonaCombo.SelectedItem as PersonaOption)?.Target;

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;
        ReloadFromDisk();
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

            _personas = PersonaStore.ListAll(_botRoot ?? string.Empty);

            var customMode = string.Equals(env.PersonaMode, "custom", StringComparison.OrdinalIgnoreCase);
            PersonaMode.SelectedIndex = customMode ? 1 : 0;
            ApplyPersonaMode();

            if (customMode)
            {
                SelectPersonaByPath(env.PersonaFile);
                UpdatePersonaAutomationName();
            }

            SaveButton.IsEnabled = _botRoot is not null;
            ResetPrimaryState();
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "读取配置失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
        }

        // 回填标志只用于吞掉一次异步补发；延迟清空，避免把用户第一次切换服务商也当成回填吞掉。
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _suppressUrl = null;
            _suppressModel = null;
        });
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

        _suppressUrl = null;
        _suppressModel = null;
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

        // 不依赖 TextChanged 的时序：切换服务商后显式刷新一次人设显示
        UpdatePersonaFromModel();

        _settings.Provider = preset.Id;
        _settings.Save();
    }

    private void OnBaseUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            ResetPrimaryState();
        }
    }

    private void OnModelTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ResetPrimaryState();
        UpdatePersonaFromModel();
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

    /// <summary>默认：下拉禁用，只显示当前模型对应的人设文件名；自定义：下拉可选、编辑可点。</summary>
    private void ApplyPersonaMode()
    {
        var custom = IsCustomMode;
        PersonaCombo.IsEnabled = custom;
        EditPersonaButton.IsEnabled = custom;

        if (custom)
        {
            ShowPersonaList();
        }
        else
        {
            ShowAutoPersona();
        }
    }

    /// <summary>默认模式：下拉只有一项只读内容，文本是纯文件名（如 deepseek.txt）。</summary>
    private void ShowAutoPersona()
    {
        var file = Presets.InferPersona(ModelBox.Text);

        _refreshingPersona = true;
        try
        {
            PersonaCombo.ItemsSource = new List<PersonaOption> { new(file, null) };
            PersonaCombo.SelectedIndex = 0;
            AutomationProperties.SetName(PersonaCombo, file);
        }
        finally
        {
            _refreshingPersona = false;
        }
    }

    /// <summary>自定义模式：列出内置与 custom 下全部 .txt（显示名去扩展名）。</summary>
    private void ShowPersonaList()
    {
        var keep = SelectedPersona?.RelativePath;
        var options = new List<PersonaOption>(_personas.Count);
        foreach (var item in _personas)
        {
            options.Add(new PersonaOption(item.DisplayName, item));
        }

        _refreshingPersona = true;
        try
        {
            PersonaCombo.ItemsSource = options;
            if (keep is null || !SelectPersonaByPath(keep))
            {
                PersonaCombo.SelectedIndex = options.Count > 0 ? 0 : -1;
            }
        }
        finally
        {
            _refreshingPersona = false;
        }

        UpdatePersonaAutomationName();
    }

    private void OnPersonaSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _refreshingPersona)
        {
            return;
        }

        ResetPrimaryState();
        UpdatePersonaAutomationName();
    }

    private void UpdatePersonaAutomationName()
    {
        var text = PersonaCombo.SelectedItem is PersonaOption option
            ? option.DisplayText
            : string.Empty;
        AutomationProperties.SetName(PersonaCombo, text);
    }

    /// <summary>默认模式下按模型名刷新人设文件名；自定义模式不覆盖用户已选。</summary>
    private void UpdatePersonaFromModel()
    {
        if (IsCustomMode)
        {
            return;
        }

        ShowAutoPersona();
    }

    private async void OnEditPersonaClicked(object sender, RoutedEventArgs e)
    {
        ResetPrimaryState();

        if (_botRoot is null)
        {
            ShowResult(InfoBarSeverity.Error, "未找到 Bot 根目录，无法编辑人设");
            return;
        }

        // 默认模式（下拉禁用）没有可选条目，编辑的是当前模型对应的内置人设
        var item = SelectedPersona;
        if (!IsCustomMode || item is null)
        {
            var file = Presets.InferPersona(ModelBox.Text);
            await EditPersonaAsync(
                Path.GetFileNameWithoutExtension(file),
                PersonaStore.Load(_botRoot, "config/personas/" + file),
                false);
            return;
        }

        // 内置预设（personas 根下）预填后另存 custom 副本；custom 下的直接覆盖
        await EditPersonaAsync(item.Name, PersonaStore.Load(_botRoot, item.RelativePath), item.IsCustom);
    }

    /// <summary>打开编辑窗；Primary 表示保存。overwrite 仅对已有 custom 人设生效。</summary>
    private async Task EditPersonaAsync(string name, string content, bool overwrite)
    {
        if (_botRoot is null)
        {
            ShowResult(InfoBarSeverity.Error, "未找到 Bot 根目录，无法保存人设");
            return;
        }

        var dialog = new PersonaEditorDialog("编辑人设", name, content)
        {
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var saved = PersonaStore.Save(_botRoot, dialog.PersonaName, dialog.PersonaContent, overwrite);
            _personas = PersonaStore.ListAll(_botRoot);

            // 保存后切到自定义模式并选中落地文件（内置人设落地的是 custom 副本）
            var wasLoading = _loading;
            _loading = true;
            try
            {
                PersonaMode.SelectedIndex = 1;
                ApplyPersonaMode();
                SelectPersonaByPath(saved.RelativePath);
            }
            finally
            {
                _loading = wasLoading;
            }

            UpdatePersonaAutomationName();
            ShowResult(InfoBarSeverity.Success, "人设已保存：" + saved.DisplayName);
        }
        catch (Exception ex)
        {
            await ShowInfoAsync("保存人设失败", ex.Message);
        }
    }

    private bool SelectPersonaByPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || PersonaCombo.ItemsSource is not IEnumerable<PersonaOption> options)
        {
            return false;
        }

        var target = relativePath.Trim().Replace('\\', '/');
        foreach (var option in options)
        {
            if (option.Target is not null
                && string.Equals(option.Target.RelativePath, target, StringComparison.OrdinalIgnoreCase))
            {
                PersonaCombo.SelectedItem = option;
                return true;
            }
        }

        // 只对不上路径时，退回按文件名匹配
        var fileName = Path.GetFileName(target);
        foreach (var option in options)
        {
            if (option.Target is not null
                && string.Equals(Path.GetFileName(option.Target.RelativePath), fileName, StringComparison.OrdinalIgnoreCase))
            {
                PersonaCombo.SelectedItem = option;
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
            var custom = IsCustomMode;
            var picked = SelectedPersona;
            if (custom && picked is null)
            {
                ShowResult(InfoBarSeverity.Warning, "自定义模式还没选人设，先选一个或点编辑新建");
                return;
            }

            var personaFile = custom && picked is not null ? picked.RelativePath : string.Empty;
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

