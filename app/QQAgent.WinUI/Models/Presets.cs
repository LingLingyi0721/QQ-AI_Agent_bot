using System;
using System.Collections.Generic;

namespace QQAgent.WinUI.Models;

/// <summary>服务商预设：id / 显示名 / 默认 Base URL / 默认模型 / 人设文件 / 备注。</summary>
public sealed record Preset(string Id, string DisplayName, string BaseUrl, string DefaultModel, string Persona, string Note = "")
{
    public bool IsCustom => string.Equals(Id, Presets.CustomId, StringComparison.OrdinalIgnoreCase);
}

public static class Presets
{
    public const string CustomId = "custom";
    public const string DefaultPersona = "default.txt";

    private static readonly Dictionary<string, string> PersonaMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek"] = "deepseek.txt",
        ["claude"] = "claude.txt",
        ["gpt"] = "gpt.txt",
        ["o1"] = "gpt.txt",
        ["o3"] = "gpt.txt",
        ["gemini"] = "gemini.txt",
        ["qwen"] = "qwen.txt",
        ["tongyi"] = "qwen.txt",
        ["glm"] = "glm.txt",
        ["chatglm"] = "glm.txt",
    };

    public static readonly IReadOnlyList<Preset> All = new List<Preset>
    {
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", "deepseek.txt"),
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o", "gpt.txt"),
        new("claude", "Claude", "https://api.anthropic.com/v1", "claude-sonnet-4-5", "claude.txt", "需中转或兼容网关"),
        new("gemini", "Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-2.5-pro", "gemini.txt"),
        new("qwen", "通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", "qwen.txt"),
        new("glm", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-plus", "glm.txt"),
        new("custom", "自定义", string.Empty, string.Empty, DefaultPersona),
    };

    public static Preset Get(string? id)
    {
        foreach (var preset in All)
        {
            if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return All[0];
    }

    /// <summary>按 Base URL 反查预设，找不到返回自定义。</summary>
    public static Preset MatchByUrl(string? baseUrl)
    {
        var url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            return Get(CustomId);
        }

        foreach (var preset in All)
        {
            if (preset.IsCustom)
            {
                continue;
            }

            if (string.Equals(preset.BaseUrl.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return Get(CustomId);
    }

    /// <summary>按模型名小写包含匹配人设文件，匹配不到用 default.txt。</summary>
    public static string InferPersona(string? model)
    {
        var name = (model ?? string.Empty).Trim().ToLowerInvariant();
        if (name.Length == 0)
        {
            return DefaultPersona;
        }

        foreach (var pair in PersonaMap)
        {
            if (name.Contains(pair.Key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return DefaultPersona;
    }
}
