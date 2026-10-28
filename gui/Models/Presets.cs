namespace QQAgent.Gui.Models;

/// <summary>服务商预设：名称 / 默认 Base URL / 默认模型 / 推断出的人设文件。</summary>
public sealed record ProviderPreset(string Name, string BaseUrl, string Model, string Persona);

internal static class Presets
{
    public const string CustomName = "自定义";

    public static readonly IReadOnlyList<ProviderPreset> All = new List<ProviderPreset>
    {
        new("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", "deepseek.txt"),
        new("OpenAI", "https://api.openai.com/v1", "gpt-4o", "gpt.txt"),
        new("Claude", "https://api.anthropic.com/v1", "claude-sonnet-4-5", "claude.txt"),
        new("Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-2.5-pro", "gemini.txt"),
        new("Qwen(通义)", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", "qwen.txt"),
        new("GLM(智谱)", "https://open.bigmodel.cn/api/paas/v4", "glm-4-plus", "glm.txt"),
        new(CustomName, "", "", "default.txt"),
    };

    /// <summary>按 Base URL + 模型名反查预设，找不到返回 null。</summary>
    public static ProviderPreset? Match(string baseUrl, string model)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        var name = (model ?? "").Trim();
        foreach (var preset in All)
        {
            if (preset.Name == CustomName)
            {
                continue;
            }

            if (string.Equals(preset.BaseUrl.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase)
                && string.Equals(preset.Model, name, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return null;
    }
}