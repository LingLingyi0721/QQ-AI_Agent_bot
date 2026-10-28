using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace QQAgent.WinUI.Services;

/// <summary>人设条目：显示名 + 相对 Bot 根目录的路径。</summary>
public sealed class PersonaItem
{
    public PersonaItem(string name, string relativePath, bool isCustom, string displayName)
    {
        Name = name;
        RelativePath = relativePath;
        IsCustom = isCustom;
        DisplayName = displayName;
    }

    /// <summary>去扩展名的文件名。</summary>
    public string Name { get; }

    /// <summary>相对 Bot 根目录的路径，如 config/personas/deepseek.txt。</summary>
    public string RelativePath { get; }

    /// <summary>是否位于 personas\custom 下。</summary>
    public bool IsCustom { get; }

    /// <summary>下拉显示名（与预设重名时带「（自定义）」后缀）。</summary>
    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}

/// <summary>
/// 人设仓库：列出 / 读取 / 新建 / 导入。自定义人设一律写入 config\personas\custom\。
/// </summary>
public static class PersonaStore
{
    public const int MaxNameLength = 20;
    public const int MaxContentLength = 200;

    private static readonly char[] UnsafeChars = BuildUnsafeChars();

    public static string PersonaRoot(string botRoot) => Path.Combine(botRoot, "config", "personas");

    public static string CustomRoot(string botRoot) => Path.Combine(PersonaRoot(botRoot), "custom");

    /// <summary>列出 personas\*.txt 与 personas\custom\*.txt（主目录在前）。</summary>
    public static IReadOnlyList<PersonaItem> ListAll(string botRoot)
    {
        var list = new List<PersonaItem>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var root = PersonaRoot(botRoot);
        foreach (var file in EnumerateTxt(root))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length == 0 || !names.Add(name))
            {
                continue;
            }

            list.Add(new PersonaItem(name, "config/personas/" + name + ".txt", false, name));
        }

        foreach (var file in EnumerateTxt(CustomRoot(botRoot)))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length == 0)
            {
                continue;
            }

            var display = names.Contains(name) ? name + "（自定义）" : name;
            names.Add(name);
            list.Add(new PersonaItem(name, "config/personas/custom/" + name + ".txt", true, display));
        }

        return list;
    }

    /// <summary>按名字或相对路径读取人设内容，找不到返回空字符串。</summary>
    public static string Load(string botRoot, string? nameOrPath)
    {
        var path = ResolveExisting(botRoot, nameOrPath);
        if (path is null)
        {
            return string.Empty;
        }

        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 写入自定义人设。文件名去扩展名、非法字符替换为 _、截断到 20 字；
    /// overwrite=false 时若重名自动追加 -2、-3。返回最终条目。
    /// </summary>
    public static PersonaItem Save(string botRoot, string? name, string content, bool overwrite)
    {
        // TextBox 的换行是 CR，统一成 LF 落盘，保证与内置人设文件的行尾一致
        var text = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length > MaxContentLength)
        {
            throw new ArgumentException("内容超过 " + MaxContentLength + " 字");
        }

        var clean = SanitizeName(name);
        var dir = CustomRoot(botRoot);
        Directory.CreateDirectory(dir);

        var finalName = clean;
        if (!overwrite)
        {
            var index = 2;
            while (File.Exists(Path.Combine(dir, finalName + ".txt")))
            {
                var suffix = "-" + index.ToString();
                var keep = Math.Max(1, MaxNameLength - suffix.Length);
                finalName = (clean.Length > keep ? clean.Substring(0, keep) : clean) + suffix;
                index++;
            }
        }

        var target = Path.Combine(dir, finalName + ".txt");
        File.WriteAllText(target, text, new UTF8Encoding(false));

        var list = ListAll(botRoot);
        foreach (var item in list)
        {
            if (string.Equals(item.Name, finalName, StringComparison.OrdinalIgnoreCase) && item.IsCustom)
            {
                return item;
            }
        }

        return new PersonaItem(finalName, "config/personas/custom/" + finalName + ".txt", true, finalName);
    }

    /// <summary>读取外部 txt，内容超过 200 字直接拒绝。</summary>
    public static (bool Ok, string Content, string Message) Import(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return (false, string.Empty, "文件不存在");
            }

            if (!string.Equals(Path.GetExtension(sourcePath), ".txt", StringComparison.OrdinalIgnoreCase))
            {
                return (false, string.Empty, "仅支持 .txt 文件");
            }

            var text = File.ReadAllText(sourcePath, Encoding.UTF8);
            if (text.Length > MaxContentLength)
            {
                return (false, string.Empty, "内容超过 200 字，未导入");
            }

            return (true, text, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, "读取失败：" + ex.Message);
        }
    }

    /// <summary>按名字去掉不安全字符与扩展名，并截断到 20 字。</summary>
    public static string SanitizeName(string? name)
    {
        var raw = (name ?? string.Empty).Trim();
        raw = Path.GetFileName(raw);
        if (raw.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw.Substring(0, raw.Length - 4);
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            builder.Append(UnsafeChars.Contains(ch) ? '_' : ch);
        }

        var clean = builder.ToString().Trim().TrimEnd('.');
        if (clean.Length > MaxNameLength)
        {
            clean = clean.Substring(0, MaxNameLength);
        }

        return clean.Length == 0 ? "persona-" + DateTime.Now.ToString("yyyyMMddHHmmss") : clean;
    }

    private static string? ResolveExisting(string botRoot, string? nameOrPath)
    {
        var value = (nameOrPath ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (value.Contains('/') || value.Contains('\\'))
        {
            var candidate = Path.IsPathRooted(value) ? value : Path.Combine(botRoot, value.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(candidate) ? candidate : null;
        }

        foreach (var item in ListAll(botRoot))
        {
            if (string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(botRoot, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        var bare = Path.Combine(PersonaRoot(botRoot), value);
        if (File.Exists(bare))
        {
            return bare;
        }

        if (!bare.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            bare += ".txt";
            if (File.Exists(bare))
            {
                return bare;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateTxt(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.GetFiles(dir, "*.txt").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static char[] BuildUnsafeChars()
    {
        var chars = new List<char>(Path.GetInvalidFileNameChars());
        for (var c = 0; c < 32; c++)
        {
            chars.Add((char)c);
        }

        return chars.Distinct().ToArray();
    }
}
