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

/// <summary>
/// 界面偏好设置，落盘到 %LOCALAPPDATA%\QQAgent\settings.json。
/// 文件不存在或内容损坏时一律返回默认值，不向上抛异常。
/// </summary>
public sealed class AppSettings
{
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

            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
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
