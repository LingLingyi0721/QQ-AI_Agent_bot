using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace QQAgent.Gui.Services;

/// <summary>.env 里的四项模型配置。</summary>
public sealed class BotSettings
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string ReasoningEffort { get; set; } = "";
}

/// <summary>定位 bot 根目录，读写 .env 的四个键，粗读 config.yaml 的 ws_url。</summary>
public static class BotConfig
{
    public const string KeyBaseUrl = "LLM_BASE_URL";
    public const string KeyApiKey = "LLM_API_KEY";
    public const string KeyModel = "LLM_MODEL";
    public const string KeyEffort = "LLM_REASONING_EFFORT";

    private static readonly string[] PersonaOrder =
    {
        "deepseek", "chatglm", "claude", "gemini", "tongyi", "qwen", "glm", "gpt", "o1", "o3",
    };

    /// <summary>从 exe 目录上溯找 src\main.py；找不到再从当前目录上溯 5 层。</summary>
    public static string? TryFindBotRoot()
    {
        var fromExe = Probe(AppContext.BaseDirectory, 8);
        if (fromExe is not null)
        {
            return fromExe;
        }

        return Probe(Environment.CurrentDirectory, 5);
    }

    public static string BotRoot()
    {
        var root = TryFindBotRoot();
        if (root is null)
        {
            throw new InvalidOperationException("未能定位 bot 根目录（应包含 src\\main.py），请在仓库内运行本程序");
        }

        return root;
    }

    private static string? Probe(string start, int maxUp)
    {
        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(start);
        }
        catch
        {
            return null;
        }

        for (var i = 0; i <= maxUp; i++)
        {
            if (dir is null)
            {
                return null;
            }

            if (File.Exists(Path.Combine(dir.FullName, "src", "main.py")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static BotSettings Load(string root)
    {
        var settings = new BotSettings();
        var envPath = Path.Combine(root, ".env");
        if (!File.Exists(envPath))
        {
            return settings;
        }

        foreach (var raw in File.ReadAllLines(envPath, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
            {
                continue;
            }

            if (line.StartsWith("export "))
            {
                line = line.Substring(7).Trim();
            }

            var idx = line.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = line.Substring(0, idx).Trim();
            var value = Unquote(line.Substring(idx + 1).Trim());
            if (key == KeyBaseUrl) { settings.BaseUrl = value; }
            else if (key == KeyApiKey) { settings.ApiKey = value; }
            else if (key == KeyModel) { settings.Model = value; }
            else if (key == KeyEffort) { settings.ReasoningEffort = value; }
        }

        return settings;
    }

    /// <summary>只更新四个键，保留其它行与顺序；写临时文件再替换，UTF-8 无 BOM。</summary>
    public static void Save(string root, BotSettings settings)
    {
        var envPath = Path.Combine(root, ".env");
        var lines = new List<string>();
        if (File.Exists(envPath))
        {
            lines.AddRange(File.ReadAllLines(envPath, Encoding.UTF8));
        }

        SetLine(lines, KeyBaseUrl, settings.BaseUrl);
        SetLine(lines, KeyApiKey, settings.ApiKey);
        SetLine(lines, KeyModel, settings.Model);
        SetLine(lines, KeyEffort, settings.ReasoningEffort);

        var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        WriteAtomic(envPath, text);
    }

    private static void SetLine(List<string> lines, string key, string value)
    {
        var text = key + "=" + QuoteIfNeeded(value);
        for (var i = 0; i <= lines.Count - 1; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#"))
            {
                continue;
            }

            if (line.StartsWith("export "))
            {
                line = line.Substring(7).Trim();
            }

            var idx = line.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            if (line.Substring(0, idx).Trim() == key)
            {
                lines[i] = text;
                return;
            }
        }

        lines.Add(text);
    }

    private static string QuoteIfNeeded(string? value)
    {
        var v = value ?? "";
        if (v.Length == 0)
        {
            return "";
        }

        if (v.IndexOf(' ') != -1 || v.IndexOf('#') != -1 || v.IndexOf('"') != -1)
        {
            return "\"" + v.Replace("\"", "\\\"") + "\"";
        }

        return v;
    }

    private static string Unquote(string value)
    {
        if (value.Length != 0 && value[0] == '"' && value[value.Length - 1] == '"')
        {
            return value.Substring(1, value.Length - 2).Replace("\\\"", "\"");
        }

        return value;
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(tmp, content, utf8);
        File.Move(tmp, path, true);
    }
    /// <summary>正则粗读 config.yaml 的 onebot.ws_url，不引 YAML 库。</summary>
    public static string ReadWsUrl(string root)
    {
        var path = Path.Combine(root, "config", "config.yaml");
        if (!File.Exists(path))
        {
            return "(未找到 config/config.yaml)";
        }

        try
        {
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var match = Regex.Match(line, @"^\s*ws_url:\s*(\S+)");
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim().Trim('"');
                }
            }
        }
        catch
        {
            // 读不到就当没配置
        }

        return "(未配置 ws_url)";
    }

    /// <summary>从 ws_url 里取端口用于展示。</summary>
    public static string ReadWsPort(string root)
    {
        var url = ReadWsUrl(root);
        var idx = url.LastIndexOf(':');
        if (idx != -1 && idx != url.Length - 1)
        {
            return url.Substring(idx + 1);
        }

        return "?";
    }

    /// <summary>按模型名关键字推断人设文件，规则与 Python 侧 persona_map 一致。</summary>
    public static string InferPersona(string? modelName)
    {
        var model = (modelName ?? "").ToLowerInvariant();
        foreach (var key in PersonaOrder)
        {
            if (model.IndexOf(key, StringComparison.Ordinal) != -1)
            {
                return PersonaFile(key);
            }
        }

        return "default.txt";
    }

    public static string PersonaDisplayPath(string? modelName)
    {
        return "config/personas/" + InferPersona(modelName);
    }

    private static string PersonaFile(string key)
    {
        switch (key)
        {
            case "deepseek": return "deepseek.txt";
            case "claude": return "claude.txt";
            case "gpt": return "gpt.txt";
            case "o1": return "gpt.txt";
            case "o3": return "gpt.txt";
            case "gemini": return "gemini.txt";
            case "qwen": return "qwen.txt";
            case "tongyi": return "qwen.txt";
            case "glm": return "glm.txt";
            case "chatglm": return "glm.txt";
            default: return "default.txt";
        }
    }
}