using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace QQAgent.WinUI.Services;

public sealed class EnvValues
{
    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string ReasoningEffort { get; init; } = string.Empty;

    public string PersonaMode { get; init; } = "auto";

    public string PersonaFile { get; init; } = string.Empty;
}

/// <summary>
/// 定位 Bot 根目录并读写 .env（只碰 LLM_BASE_URL / LLM_API_KEY / LLM_MODEL / LLM_REASONING_EFFORT / LLM_PERSONA_MODE / LLM_PERSONA_FILE 六个键）。
/// </summary>
public static class EnvConfig
{
    public const string BaseUrlKey = "LLM_BASE_URL";
    public const string ApiKeyKey = "LLM_API_KEY";
    public const string ModelKey = "LLM_MODEL";
    public const string ReasoningKey = "LLM_REASONING_EFFORT";
    public const string PersonaModeKey = "LLM_PERSONA_MODE";
    public const string PersonaFileKey = "LLM_PERSONA_FILE";

    /// <summary>
    /// 从程序目录向上最多 6 层查找含 src\main.py 的目录；找不到再从当前工作目录同样上溯。
    /// </summary>
    public static string? FindBotRoot()
    {
        var starts = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory };
        foreach (var start in starts)
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            DirectoryInfo? dir;
            try
            {
                dir = new DirectoryInfo(Path.GetFullPath(start));
            }
            catch (Exception)
            {
                continue;
            }

            // i=0 为起点自身，最多再向上 6 层（共 7 个候选）
            for (var i = 0; i <= 6 && dir is not null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "src", "main.py")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }

    public static EnvValues Load(string botRoot)
    {
        var result = new EnvValues();
        try
        {
            var path = Path.Combine(botRoot, ".env");
            if (!File.Exists(path))
            {
                return result;
            }

            var values = ParseLines(File.ReadAllLines(path, Encoding.UTF8));
            return new EnvValues
            {
                BaseUrl = values.TryGetValue(BaseUrlKey, out var u) ? u : string.Empty,
                ApiKey = values.TryGetValue(ApiKeyKey, out var k) ? k : string.Empty,
                Model = values.TryGetValue(ModelKey, out var m) ? m : string.Empty,
                ReasoningEffort = values.TryGetValue(ReasoningKey, out var r) ? r : string.Empty,
                PersonaMode = values.TryGetValue(PersonaModeKey, out var pm) && pm.Trim().Length > 0 ? pm.Trim() : "auto",
                PersonaFile = values.TryGetValue(PersonaFileKey, out var pf) ? pf : string.Empty,
            };
        }
        catch (Exception)
        {
            return result;
        }
    }

    /// <summary>
    /// 只更新 6 个 LLM 键：已有行原地替换，缺的追加到末尾，其余行（注释/顺序）原样保留。
    /// 写临时文件后原子覆盖，UTF-8 无 BOM。返回写入的文件路径。
    /// </summary>
    public static string Save(string botRoot, string baseUrl, string apiKey, string model, string reasoningEffort)
        => Save(botRoot, baseUrl, apiKey, model, reasoningEffort, "auto", string.Empty);

    /// <summary>同上，并额外写入 LLM_PERSONA_MODE / LLM_PERSONA_FILE（共 6 个键）。</summary>
    public static string Save(
        string botRoot,
        string baseUrl,
        string apiKey,
        string model,
        string reasoningEffort,
        string personaMode,
        string personaFile)
    {
        var path = Path.Combine(botRoot, ".env");
        var newLine = DetectNewLine(path);
        var lines = File.Exists(path)
            ? File.ReadAllLines(path, Encoding.UTF8).ToList()
            : new List<string>();

        UpdateOrAppend(lines, BaseUrlKey, baseUrl);
        UpdateOrAppend(lines, ApiKeyKey, apiKey);
        UpdateOrAppend(lines, ModelKey, model);
        UpdateOrAppend(lines, ReasoningKey, reasoningEffort);

        var mode = string.IsNullOrWhiteSpace(personaMode) ? "auto" : personaMode.Trim().ToLowerInvariant();
        UpdateOrAppend(lines, PersonaModeKey, mode);
        UpdateOrAppend(lines, PersonaFileKey, mode == "custom" ? (personaFile ?? string.Empty).Trim() : string.Empty);

        var text = string.Join(newLine, lines) + newLine;
        WriteAtomic(path, text);
        return path;
    }

    /// <summary>从 config\config.yaml 里取 onebot 的 ws_url（用于展示，找不到返回 null）。</summary>
    public static string? ReadOneBotUrl(string botRoot)
    {
        try
        {
            var path = Path.Combine(botRoot, "config", "config.yaml");
            if (!File.Exists(path))
            {
                return null;
            }

            var match = Regex.Match(File.ReadAllText(path, Encoding.UTF8), @"ws_url:\s*(\S+)");
            return match.Success ? match.Groups[1].Value.Trim('"', '\'') : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseLines(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            // 允许 export KEY=VALUE 形式
            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line.Substring(7).TrimStart();
            }

            var index = line.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = line.Substring(0, index).Trim();
            var value = line.Substring(index + 1).Trim();
            map[key] = Unquote(value);
        }

        return map;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            if ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            {
                return value.Substring(1, value.Length - 2);
            }
        }

        return value;
    }

    private static void UpdateOrAppend(List<string> lines, string key, string value)
    {
        var pattern = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=", RegexOptions.IgnoreCase);
        var clean = (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        for (var i = 0; i < lines.Count; i++)
        {
            if (pattern.IsMatch(lines[i]))
            {
                lines[i] = key + "=" + clean;
                return;
            }
        }

        if (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.Insert(lines.Count - 1, key + "=" + clean);
        }
        else
        {
            lines.Add(key + "=" + clean);
        }
    }

    private static string DetectNewLine(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                if (text.Contains("\r\n", StringComparison.Ordinal))
                {
                    return "\r\n";
                }
            }
        }
        catch (Exception)
        {
            // 读不到就按平台默认
        }

        return Environment.NewLine;
    }

    private static void WriteAtomic(string path, string text)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));

        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, null);
                }
                catch (Exception)
                {
                    File.Move(tmp, path, true);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception)
                {
                    // 清理残留失败无所谓
                }
            }
        }
    }
}
