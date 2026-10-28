using System.Diagnostics;
using System.Text;
using System.Windows.Threading;

namespace QQAgent.Gui.Services;

/// <summary>管理 python -m src.main 子进程，输出按 UTF-8 解码并切回 UI 线程。</summary>
public sealed class BotProcess
{
    private readonly Dispatcher _dispatcher;
    private Process? _process;

    public BotProcess(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>收到一行输出：(行内容, 是否 stderr)。</summary>
    public event Action<string, bool>? LogReceived;

    /// <summary>进程退出：(退出码)。</summary>
    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public int? ProcessId
    {
        get
        {
            try
            {
                var proc = _process;
                if (proc is null || proc.HasExited)
                {
                    return null;
                }

                return proc.Id;
            }
            catch
            {
                return null;
            }
        }
    }

    public void Start(string botRoot)
    {
        if (IsRunning)
        {
            return;
        }

        var psi = new ProcessStartInfo();
        psi.FileName = "python";
        psi.Arguments = "-m src.main";
        psi.WorkingDirectory = botRoot;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        var proc = new Process();
        psi.UseShellExecute = false;
        proc.StartInfo = psi;
        proc.EnableRaisingEvents = true;
        proc.OutputDataReceived += OnOutput;
        proc.ErrorDataReceived += OnError;
        proc.Exited += OnExited;

        if (proc.Start() == false)
        {
            throw new InvalidOperationException("python 进程启动失败");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _process = proc;
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        Emit(e.Data, false);
    }

    private void OnError(object sender, DataReceivedEventArgs e)
    {
        Emit(e.Data, true);
    }

    private void Emit(string? line, bool isError)
    {
        if (line is null)
        {
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _dispatcher.Invoke(new Action(() => LogReceived?.Invoke(line, isError)));
        }
        catch
        {
            // 关闭过程中丢弃即可
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var proc = _process;
        var code = -1;
        try
        {
            if (proc is not null)
            {
                code = proc.ExitCode;
            }
        }
        catch
        {
            code = -1;
        }

        _process = null;
        if (_dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            _dispatcher.Invoke(new Action(() => Exited?.Invoke(code)));
        }
        catch
        {
            // 窗口已关，忽略
        }
    }

    /// <summary>结束整个进程树，最多等 3 秒。</summary>
    public void Stop()
    {
        var proc = _process;
        if (proc is null)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(true);
            }
        }
        catch
        {
            // 已经退出
        }

        try
        {
            proc.WaitForExit(3000);
        }
        catch
        {
            // 超时不再等
        }

        _process = null;
    }
}