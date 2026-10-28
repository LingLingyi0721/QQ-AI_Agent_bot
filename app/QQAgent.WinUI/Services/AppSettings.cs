using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QQAgent.WinUI.Services;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum AccentMode
{
    System,
    Custom,
}

/// <summary>
/// 界面偏好设置，落盘到 %LOCALAPPDATA%\QQAgent\settings.json。
/// 文件不存在或内容损坏时一律返回默认值，不向上抛异常；
/// 旧版本文件缺少新字段时由属性初始值自动补齐。
/// </summary>
public sealed class AppSettings
{
    public const string DefaultAccentColor = "#4CC2FF";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQAgent");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    [JsonConverter(typeof(JsonStringEnumConverter<ThemeMode>))]
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    [JsonConverter(typeof(JsonStringEnumConverter<AccentMode>))]
    public AccentMode AccentMode { get; set; } = AccentMode.System;

    /// <summary>自定义主题色，格式 #RRGGBB。</summary>
    public string AccentColor { get; set; } = DefaultAccentColor;

    public string Provider { get; set; } = "deepseek";

    public bool ReducedMotion { get; set; }

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                return new AppSettings();
            }

            if (string.IsNullOrWhiteSpace(settings.AccentColor))
            {
                settings.AccentColor = DefaultAccentColor;
            }

            return settings;
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var dir = SettingsDirectory;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 偏好设置写失败不影响主流程，静默忽略。
        }
    }
}