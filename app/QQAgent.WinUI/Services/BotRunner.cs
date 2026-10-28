using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace QQAgent.WinUI.Services;

/// <summary>
/// Bot 进程的唯一管理者：启动 / 停止 / 采集 stdout+stderr / 保留最近日志。
/// 事件一律在触发方线程直接抛出，由订阅方自行切回 UI 线程。
/// </summary>
public sealed class BotRunner
{
    /// <summary>日志保留上限（超出丢弃最旧）。</summary>
    public const int MaxRecentLines = 3000;

    private static readonly Lazy<BotRunner> LazyInstance = new(() => new BotRunner());

    private readonly object _gate = new();
    private readonly Queue<(string Text, bool IsErr)> _recent = new();

    private Process? _process;
    private bool _running;
    private int? _pid;
    private int? _lastExitCode;
    private bool _hasExited;

    private BotRunner()
    {
    }

    public static BotRunner Current => LazyInstance.Value;
    /// <summary>每采集到一行就抛一次（文本、是否 stderr）。</summary>
    public event Action<string, bool>? LogReceived;

    /// <summary>运行状态变化（是否运行、PID）。</summary>
    public event Action<bool, int?>? StateChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public int? Pid
    {
        get
        {
            lock (_gate)
            {
                return _pid;
            }
        }
    }

    /// <summary>最近一次退出码（从未启动过为 null）。</summary>
    public int? LastExitCode
    {
        get
        {
            lock (_gate)
            {
                return _lastExitCode;
            }
        }
    }

    /// <summary>是否启动过并已退出（用于区分「未运行」与「已退出」）。</summary>
    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                return _hasExited;
            }
        }
    }

    /// <summary>最近日志快照（最多 MaxRecentLines 行，最旧在前）。</summary>
    public IReadOnlyList<(string Text, bool IsErr)> Recent
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }
    }
    /// <summary>启动 python -m src.main。返回 (是否成功, 说明)。</summary>
    public (bool Ok, string Message) Start()
    {
        var root = App.BotRoot ?? EnvConfig.FindBotRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            return (false, "未找到 bot 项目根目录");
        }

        lock (_gate)
        {
            if (_running)
            {
                return (false, "已在运行");
            }
        }

        if (!IsPythonAvailable())
        {
            return (false, "未找到 python");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = "-m src.main",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Publish(e.Data, false);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Publish(e.Data, true);
            }
        };
        process.Exited += OnProcessExited;
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return (false, "python 进程启动失败");
            }
        }
        catch (Exception ex)
        {
            process.Dispose();
            return (false, "启动失败：" + ex.Message);
        }
        var pid = 0;
        try
        {
            pid = process.Id;
        }
        catch (Exception)
        {
            pid = 0;
        }

        lock (_gate)
        {
            _process = process;
            _pid = pid;
            _running = true;
            _hasExited = false;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        StateChanged?.Invoke(true, pid == 0 ? null : pid);
        return (true, "已启动 python -m src.main（PID " + pid + "）");
    }
    /// <summary>结束整棵进程树。返回是否成功结束。</summary>
    public bool Stop()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
        }

        if (process is null)
        {
            return false;
        }

        try
        {
            if (process.HasExited)
            {
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            return process.WaitForExit(3000);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>清空日志缓存（不影响进程）。</summary>
    public void ClearRecent()
    {
        lock (_gate)
        {
            _recent.Clear();
        }
    }
    private void OnProcessExited(object? sender, EventArgs e)
    {
        var code = -1;
        if (sender is Process p)
        {
            try
            {
                code = p.ExitCode;
            }
            catch (Exception)
            {
                code = -1;
            }

            // 保证异步读流的尾部行先落日志，再报退出。
            try
            {
                p.WaitForExit(500);
            }
            catch (Exception)
            {
                // 读取尾部输出失败不影响状态更新
            }

            p.Dispose();
        }

        lock (_gate)
        {
            _process = null;
            _running = false;
            _pid = null;
            _lastExitCode = code;
            _hasExited = true;
        }

        Publish("[系统] bot 进程已退出，退出码 " + code, false);
        StateChanged?.Invoke(false, null);
    }
    private void Publish(string text, bool isErr)
    {
        lock (_gate)
        {
            _recent.Enqueue((text, isErr));
            while (_recent.Count > MaxRecentLines)
            {
                _recent.Dequeue();
            }
        }

        LogReceived?.Invoke(text, isErr);
    }
    /// <summary>python --version 预检，3 秒超时。</summary>
    private static bool IsPythonAvailable()
    {
        Process? probe = null;
        try
        {
            probe = Process.Start(new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            });

            if (probe is null)
            {
                return false;
            }

            if (!probe.WaitForExit(3000))
            {
                try
                {
                    probe.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 探针杀不掉也无所谓
                }

                return false;
            }

            return probe.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            probe?.Dispose();
        }
    }
}
