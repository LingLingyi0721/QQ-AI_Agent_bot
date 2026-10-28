// QQAgent 自举启动器
// 目标框架：.NET Framework 4.7.2（Win10 1809+ 自带），C# 5 语法。
// 职责：检测 .NET 桌面运行时 10.x 与 Windows App Runtime 1.8，缺失则静默安装，然后拉起主程序。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QQAgent.Launcher
{
    internal delegate void ProgressWindowUpdate(int percent, string detail);

    internal sealed class RuntimeStatus
    {
        public bool AppFound;
        public string AppPath;
        public bool DotnetPresent;
        public string DotnetBest;
        public string DotnetVersions;
        public bool WinAppRuntimePresent;
        public string WinAppRuntimeEvidence;

        public RuntimeStatus()
        {
            AppPath = "";
            DotnetBest = "";
            DotnetVersions = "";
            WinAppRuntimeEvidence = "";
        }

        public bool Ready
        {
            get { return AppFound && DotnetPresent && WinAppRuntimePresent; }
        }
    }

    internal static class Program
    {
        // 直链：已用 HEAD 实际验证。
        private const string DotnetPrimaryUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
        private const string DotnetFallbackUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.10/windowsdesktop-runtime-10.0.10-win-x64.exe";
        private const string WinAppRuntimePrimaryUrl = "https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe";
        private const string WinAppRuntimeFallbackUrl = "https://aka.ms/windowsappsdk/1.8/1.8.260921001/windowsappruntimeinstall-x64.exe";

        private const string UserAgent = "QQAgent-Launcher/1.0";
        private const string DotnetDownloadPage = "https://dotnet.microsoft.com/download/dotnet/10.0";
        private const string WinAppRuntimeDownloadPage = "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads";
        private const string CheckFileName = "qqagent-launcher-check.txt";
        private const string TempDirName = "qqagent-launcher";

        [STAThread]
        internal static int Main(string[] args)
        {
            bool checkOnly = false;
            bool noInstall = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] == null ? "" : args[i].Trim();
                if (string.Compare(a, "--check", true) == 0) checkOnly = true;
                else if (string.Compare(a, "--no-install", true) == 0) noInstall = true;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            RuntimeStatus status;
            try
            {
                status = Detect();
            }
            catch (Exception ex)
            {
                ShowError("启动器内部错误", "检测运行环境时出错：\n\n" + ex.Message, null);
                return 2;
            }

            if (checkOnly)
            {
                string report = BuildCheckReport(status);
                string path = Path.Combine(Path.GetTempPath(), CheckFileName);
                try
                {
                    File.WriteAllText(path, report, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    ShowError("自检报告写入失败", "写入 " + path + " 失败：\n\n" + ex.Message, null);
                    return 3;
                }
                return status.Ready ? 0 : 1;
            }

            if (!status.AppFound)
            {
                ShowError("找不到主程序",
                    "启动器同目录下没有 app\\QQAgent.WinUI.exe。\n\n"
                    + "请确认 QQAgent.exe 与 app 文件夹在同一个目录（从 dist 压缩包整体解压后再双击）。",
                    null);
                return 4;
            }
            bool needDotnet = !status.DotnetPresent;
            bool needWinAppRuntime = !status.WinAppRuntimePresent;

            if (needDotnet || needWinAppRuntime)
            {
                if (noInstall)
                {
                    string msg = "检测到缺少运行环境（已按 --no-install 跳过自动安装）：\n";
                    if (needDotnet) msg += "\n- .NET 桌面运行时 10.x";
                    if (needWinAppRuntime) msg += "\n- Windows App Runtime 1.8";
                    msg += "\n\n手动安装地址：\n" + DotnetDownloadPage + "\n" + WinAppRuntimeDownloadPage;
                    List<KeyValuePair<string, string>> links = new List<KeyValuePair<string, string>>();
                    if (needDotnet) links.Add(new KeyValuePair<string, string>(".NET 下载页", DotnetDownloadPage));
                    if (needWinAppRuntime) links.Add(new KeyValuePair<string, string>("WinAppRuntime 下载页", WinAppRuntimeDownloadPage));
                    ShowError("缺少运行环境", msg, links);
                    return 1;
                }

                string firstText = needDotnet ? "正在安装 .NET 桌面运行时…" : "正在安装 Windows App Runtime 1.8…";
                string failStage = "";
                string failDetail = "";
                string failPage = "";

                using (ProgressWindow win = new ProgressWindow("QQAgent 启动器", firstText))
                {
                    win.Show();
                    Application.DoEvents();
                    try
                    {
                        if (needDotnet)
                        {
                            string detail;
                            string error;
                            if (!InstallPackage(win, ".NET 桌面运行时 10", DotnetPrimaryUrl, DotnetFallbackUrl,
                                    "windowsdesktop-runtime-10-win-x64.exe", "/install /quiet /norestart", out detail, out error))
                            {
                                failStage = "安装 .NET 桌面运行时失败";
                                failPage = DotnetDownloadPage;
                                failDetail = "失败环节：" + error + "\n\n" + detail;
                            }
                        }
                        if (failStage.Length == 0 && needWinAppRuntime)
                        {
                            string detail;
                            string error;
                            if (!InstallPackage(win, "Windows App Runtime 1.8", WinAppRuntimePrimaryUrl, WinAppRuntimeFallbackUrl,
                                    "WindowsAppRuntimeInstall-x64.exe", "--quiet", out detail, out error))
                            {
                                failStage = "安装 Windows App Runtime 1.8 失败";
                                failPage = WinAppRuntimeDownloadPage;
                                failDetail = "失败环节：" + error + "\n\n" + detail;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failStage = "安装运行时过程中出现异常";
                        failPage = needDotnet ? DotnetDownloadPage : WinAppRuntimeDownloadPage;
                        failDetail = ex.ToString();
                    }
                    finally
                    {
                        try { win.Close(); } catch (Exception) { }
                    }
                }

                if (failStage.Length > 0)
                {
                    List<KeyValuePair<string, string>> links = new List<KeyValuePair<string, string>>();
                    links.Add(new KeyValuePair<string, string>("打开下载页面", failPage));
                    ShowError(failStage, failDetail + "\n\n请手动下载安装后重新双击 QQAgent.exe。", links);
                    return 5;
                }

                RuntimeStatus after;
                try
                {
                    after = Detect();
                }
                catch (Exception ex)
                {
                    ShowError("安装后复检失败", ex.Message, null);
                    return 6;
                }

                if (!after.DotnetPresent || !after.WinAppRuntimePresent)
                {
                    string msg = "安装流程已结束，但仍检测到缺失：\n";
                    if (!after.DotnetPresent) msg += "\n- .NET 桌面运行时 10.x";
                    if (!after.WinAppRuntimePresent) msg += "\n- Windows App Runtime 1.8";
                    msg += "\n\n若安装程序提示需要重启，请重启后再双击 QQAgent.exe。";
                    List<KeyValuePair<string, string>> links = new List<KeyValuePair<string, string>>();
                    if (!after.DotnetPresent) links.Add(new KeyValuePair<string, string>(".NET 下载页", DotnetDownloadPage));
                    if (!after.WinAppRuntimePresent) links.Add(new KeyValuePair<string, string>("WinAppRuntime 下载页", WinAppRuntimeDownloadPage));
                    ShowError("运行环境未就绪", msg, links);
                    return 6;
                }
            }

            string launchError;
            if (!LaunchApp(status.AppPath, out launchError))
            {
                ShowError("启动主程序失败", "无法启动：\n" + status.AppPath + "\n\n" + launchError, null);
                return 7;
            }
            return 0;
        }
        // ---------------- 检测 ----------------

        private static RuntimeStatus Detect()
        {
            RuntimeStatus st = new RuntimeStatus();
            st.AppPath = FindAppPath();
            st.AppFound = st.AppPath.Length > 0;

            List<string> versions = new List<string>();
            string[] roots = DotnetRoots();
            for (int i = 0; i < roots.Length; i++)
            {
                string shared = Path.Combine(roots[i], Path.Combine("shared", "Microsoft.WindowsDesktop.App"));
                try
                {
                    if (!Directory.Exists(shared)) continue;
                    string[] dirs = Directory.GetDirectories(shared);
                    for (int j = 0; j < dirs.Length; j++)
                    {
                        string name = Path.GetFileName(dirs[j]);
                        if (name != null && name.Length > 0 && versions.IndexOf(name) < 0) versions.Add(name);
                    }
                }
                catch (Exception) { }
            }

            string best = "";
            for (int i = 0; i < versions.Count; i++)
            {
                if (!versions[i].StartsWith("10.", StringComparison.Ordinal)) continue;
                if (best.Length == 0 || CompareVersion(versions[i], best) > 0) best = versions[i];
            }
            st.DotnetPresent = best.Length > 0;
            st.DotnetBest = best;
            st.DotnetVersions = versions.Count > 0 ? string.Join(", ", versions.ToArray()) : "";

            string evidence;
            st.WinAppRuntimePresent = CheckWinAppRuntime(out evidence);
            st.WinAppRuntimeEvidence = evidence;
            return st;
        }

        private static int CompareVersion(string a, string b)
        {
            try
            {
                Version va = new Version(a);
                Version vb = new Version(b);
                return va.CompareTo(vb);
            }
            catch (Exception)
            {
                return string.Compare(a, b, StringComparison.Ordinal);
            }
        }

        private static string[] DotnetRoots()
        {
            List<string> list = new List<string>();
            string envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(envRoot) && list.IndexOf(envRoot) < 0) list.Add(envRoot);
            string pf = @"C:\Program Files\dotnet";
            if (list.IndexOf(pf) < 0) list.Add(pf);
            string w6432 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!string.IsNullOrEmpty(w6432))
            {
                string p = Path.Combine(w6432, "dotnet");
                if (list.IndexOf(p) < 0) list.Add(p);
            }
            return list.ToArray();
        }

        private static string[] ProgramFilesRoots()
        {
            List<string> list = new List<string>();
            string w6432 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!string.IsNullOrEmpty(w6432)) list.Add(w6432);
            list.Add(@"C:\Program Files");
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(pf86)) list.Add(pf86);
            return list.ToArray();
        }
        // Windows App Runtime 1.8 探测：注册表 + WindowsApps 目录 + Appx 包仓库 + bootstrap 加载，任一命中即视为存在。
        private static bool CheckWinAppRuntime(out string evidence)
        {
            evidence = "";

            // 1) 注册表
            string[] regRoots = new string[]
            {
                @"SOFTWARE\Microsoft\WindowsAppRuntime",
                @"SOFTWARE\WOW6432Node\Microsoft\WindowsAppRuntime"
            };
            for (int i = 0; i < regRoots.Length; i++)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(regRoots[i]))
                    {
                        if (key == null) continue;
                        string[] subs = key.GetSubKeyNames();
                        for (int j = 0; j < subs.Length; j++)
                        {
                            if (subs[j].StartsWith("1.8", StringComparison.OrdinalIgnoreCase))
                            {
                                evidence = "HKLM\\" + regRoots[i] + "\\" + subs[j];
                                return true;
                            }
                        }
                    }
                }
                catch (Exception) { }
            }

            // 2) %ProgramFiles%\WindowsApps 下的 1.8 framework 包目录
            string[] pfRoots = ProgramFilesRoots();
            for (int i = 0; i < pfRoots.Length; i++)
            {
                try
                {
                    string windowsApps = Path.Combine(pfRoots[i], "WindowsApps");
                    if (!Directory.Exists(windowsApps)) continue;
                    string[] dirs = Directory.GetDirectories(windowsApps, "Microsoft.WindowsAppRuntime.1.8*");
                    if (dirs.Length > 0)
                    {
                        evidence = "WindowsApps\\" + Path.GetFileName(dirs[0]);
                        return true;
                    }
                }
                catch (Exception) { }
            }

            // 3) Appx 包仓库注册表（机器级 + 用户级）
            string repo = @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
            RegistryKey[] hives = new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser };
            for (int h = 0; h < hives.Length; h++)
            {
                try
                {
                    using (RegistryKey key = hives[h].OpenSubKey(repo))
                    {
                        if (key == null) continue;
                        string[] subs = key.GetSubKeyNames();
                        for (int j = 0; j < subs.Length; j++)
                        {
                            if (subs[j].IndexOf("Microsoft.WindowsAppRuntime.1.8", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                evidence = "AppxPackages\\" + subs[j];
                                return true;
                            }
                        }
                    }
                }
                catch (Exception) { }
            }

            // 4) 尝试加载 bootstrap dll（与上面取或）
            string bootstrap = FindBootstrapDll();
            if (bootstrap.Length > 0)
            {
                IntPtr h = IntPtr.Zero;
                try
                {
                    h = LoadLibraryW(bootstrap);
                    if (h != IntPtr.Zero)
                    {
                        evidence = "bootstrap\\" + Path.GetFileName(bootstrap);
                        return true;
                    }
                }
                catch (Exception) { }
                finally
                {
                    if (h != IntPtr.Zero)
                    {
                        try { FreeLibrary(h); } catch (Exception) { }
                    }
                }
            }

            evidence = "未命中（注册表 / WindowsApps / Appx 包仓库 / bootstrap 均未发现 1.8）";
            return false;
        }

        private static string FindBootstrapDll()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            List<string> candidates = new List<string>();
            candidates.Add(Path.Combine(baseDir, "Microsoft.WindowsAppRuntime.Bootstrap.dll"));
            candidates.Add(Path.Combine(Path.Combine(baseDir, "app"), "Microsoft.WindowsAppRuntime.Bootstrap.dll"));
            for (int i = 0; i < candidates.Count; i++)
            {
                try { if (File.Exists(candidates[i])) return candidates[i]; }
                catch (Exception) { }
            }
            return "";
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadLibraryW")]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", SetLastError = true, EntryPoint = "FreeLibrary")]
        private static extern bool FreeLibrary(IntPtr handle);

        private static string FindAppPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            List<string> candidates = new List<string>();
            candidates.Add(Path.Combine(Path.Combine(baseDir, "app"), "QQAgent.WinUI.exe"));
            candidates.Add(Path.Combine(baseDir, "QQAgent.WinUI.exe"));

            try
            {
                string trimmed = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                DirectoryInfo parent = Directory.GetParent(trimmed);
                if (parent != null)
                {
                    candidates.Add(Path.Combine(parent.FullName, "QQAgent.WinUI.exe"));
                    candidates.Add(Path.Combine(Path.Combine(parent.FullName, "app"), "QQAgent.WinUI.exe"));
                }
            }
            catch (Exception) { }

            for (int i = 0; i < candidates.Count; i++)
            {
                try { if (File.Exists(candidates[i])) return candidates[i]; }
                catch (Exception) { }
            }
            return "";
        }

        private static string BuildCheckReport(RuntimeStatus st)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("QQAgent 启动器自检报告");
            sb.AppendLine("========================");
            sb.AppendLine("生成时间 : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("启动器   : " + Application.ExecutablePath);
            sb.AppendLine("");
            sb.AppendLine("dotnet 桌面运行时 10.x : " + (st.DotnetPresent ? "存在（" + st.DotnetBest + "）" : "缺失"));
            sb.AppendLine("  已装版本 : " + (st.DotnetVersions.Length > 0 ? st.DotnetVersions : "（无）"));
            sb.AppendLine("winappruntime 1.8      : " + (st.WinAppRuntimePresent ? "存在" : "缺失"));
            sb.AppendLine("  判定依据 : " + st.WinAppRuntimeEvidence);
            sb.AppendLine("主程序                 : " + (st.AppFound ? st.AppPath : "未找到"));
            sb.AppendLine("");
            sb.AppendLine("结论 : " + (st.Ready ? "READY（可直接启动）" : "NOT READY（存在缺失项）"));
            return sb.ToString();
        }
        // ---------------- 安装 ----------------

        private static bool InstallPackage(ProgressWindow win, string stageText, string primaryUrl, string fallbackUrl,
            string fileName, string installerArgs, out string detail, out string failedStage)
        {
            detail = "";

            string dir = Path.Combine(Path.GetTempPath(), TempDirName);
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                failedStage = "创建临时目录";
                detail = ex.Message;
                return false;
            }

            string dest = Path.Combine(dir, fileName);
            try { if (File.Exists(dest)) File.Delete(dest); }
            catch (Exception) { }

            failedStage = "下载安装包";
            win.SetText("正在下载 " + stageText + " 安装包…");
            win.SetProgress(0, "正在连接下载服务器…");

            string downloadError;
            if (!DownloadWithFallback(win, primaryUrl, fallbackUrl, dest, out downloadError))
            {
                detail = downloadError;
                return false;
            }

            long length = 0;
            try { length = new FileInfo(dest).Length; }
            catch (Exception) { }
            if (length < 1024 * 1024)
            {
                detail = "下载的安装包大小异常（" + length.ToString() + " 字节）";
                return false;
            }

            failedStage = "运行安装程序";
            win.SetText("正在安装 " + stageText + "…");
            win.SetProgress(-1, "安装程序运行中，请稍候（可能需要 1-3 分钟）…");

            int exitCode;
            string runError;
            if (!RunInstaller(dest, installerArgs, out exitCode, out runError))
            {
                detail = runError;
                return false;
            }

            if (exitCode != 0 && exitCode != 3010)
            {
                detail = "安装程序退出码 " + exitCode.ToString() + (exitCode == 1602 ? "（用户取消）" : "");
                return false;
            }

            try { File.Delete(dest); }
            catch (Exception) { }

            failedStage = "";
            if (exitCode == 3010) detail = "安装成功（3010：需重启后生效）";
            else detail = "安装成功（退出码 0）";
            return true;
        }

        private static bool DownloadWithFallback(ProgressWindow win, string primaryUrl, string fallbackUrl, string dest, out string error)
        {
            error = "";
            string firstError;
            if (DownloadOnce(win, primaryUrl, dest, out firstError)) return true;

            string secondError = "";
            if (!string.IsNullOrEmpty(fallbackUrl) && string.Compare(primaryUrl, fallbackUrl, true) != 0)
            {
                win.SetProgress(0, "主地址不可用，正在切换备用地址…");
                if (DownloadOnce(win, fallbackUrl, dest, out secondError)) return true;
            }

            error = "主地址：" + firstError;
            if (secondError.Length > 0) error += "\n备用地址：" + secondError;
            return false;
        }

        private static bool DownloadOnce(ProgressWindow win, string url, string dest, out string error)
        {
            error = "";
            try
            {
                Uri uri = new Uri(url);
                using (WebClient client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = UserAgent;

                    string capturedUrl = url;
                    client.DownloadProgressChanged += delegate(object sender, DownloadProgressChangedEventArgs e)
                    {
                        string text = string.Format("{0}  {1:0.0} MB / {2:0.0} MB",
                            ShortUrl(capturedUrl),
                            ((double)e.BytesReceived) / 1048576.0,
                            ((double)e.TotalBytesToReceive) / 1048576.0);
                        try { PostProgress(win, e.ProgressPercentage, text); }
                        catch (Exception) { }
                    };

                    Task task = client.DownloadFileTaskAsync(uri, dest);
                    while (!task.IsCompleted)
                    {
                        Application.DoEvents();
                        Thread.Sleep(50);
                    }

                    if (task.IsCanceled)
                    {
                        error = "下载被取消";
                        return false;
                    }
                    if (task.Exception != null)
                    {
                        error = DescribeException(task.Exception.GetBaseException());
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = DescribeException(ex);
                return false;
            }
        }

        private static string ShortUrl(string url)
        {
            try
            {
                Uri u = new Uri(url);
                return u.Host;
            }
            catch (Exception)
            {
                return url;
            }
        }

        private static string DescribeException(Exception ex)
        {
            if (ex == null) return "未知错误";
            WebException we = ex as WebException;
            if (we != null && we.Response != null)
            {
                try
                {
                    HttpWebResponse resp = (HttpWebResponse)we.Response;
                    return we.Message + "（HTTP " + ((int)resp.StatusCode).ToString() + "）";
                }
                catch (Exception) { }
            }
            return ex.Message;
        }

        private static void PostProgress(ProgressWindow win, int percent, string detail)
        {
            if (win == null || win.IsDisposed) return;
            if (win.InvokeRequired)
            {
                try { win.BeginInvoke(new ProgressWindowUpdate(win.SetProgress), new object[] { percent, detail }); }
                catch (Exception) { }
                return;
            }
            win.SetProgress(percent, detail);
        }
        private static bool RunInstaller(string installerPath, string arguments, out int exitCode, out string error)
        {
            exitCode = -1;
            error = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = installerPath;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(installerPath);
                using (Process p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        error = "无法创建安装进程";
                        return false;
                    }
                    while (!p.HasExited)
                    {
                        Application.DoEvents();
                        Thread.Sleep(120);
                    }
                    p.WaitForExit();
                    exitCode = p.ExitCode;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool LaunchApp(string appPath, out string error)
        {
            error = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = appPath;
                psi.WorkingDirectory = Path.GetDirectoryName(appPath);
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void ShowError(string caption, string message, List<KeyValuePair<string, string>> links)
        {
            try
            {
                using (ErrorDialog dialog = new ErrorDialog(caption, message, links))
                {
                    dialog.ShowDialog();
                }
            }
            catch (Exception)
            {
                try { MessageBox.Show(message, caption, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch (Exception) { }
            }
        }
    }
    internal sealed class ProgressWindow : Form
    {
        private readonly Label _text;
        private readonly Label _detail;
        private readonly ProgressBar _bar;

        public ProgressWindow(string caption, string text)
        {
            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(480, 152);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.White;

            _text = new Label();
            _text.AutoSize = false;
            _text.Text = text;
            _text.Bounds = new Rectangle(18, 20, 444, 26);
            _text.Font = new Font("Microsoft YaHei UI", 10F);
            Controls.Add(_text);

            _bar = new ProgressBar();
            _bar.Bounds = new Rectangle(18, 58, 444, 22);
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            Controls.Add(_bar);

            _detail = new Label();
            _detail.AutoSize = false;
            _detail.Text = "";
            _detail.Bounds = new Rectangle(18, 88, 444, 46);
            _detail.ForeColor = Color.DimGray;
            Controls.Add(_detail);
        }

        public void SetText(string text)
        {
            if (IsDisposed) return;
            _text.Text = text;
        }

        public void SetProgress(int percent, string detail)
        {
            if (IsDisposed) return;

            if (percent < 0)
            {
                if (_bar.Style != ProgressBarStyle.Marquee)
                {
                    _bar.Style = ProgressBarStyle.Marquee;
                    _bar.MarqueeAnimationSpeed = 30;
                }
            }
            else
            {
                if (_bar.Style != ProgressBarStyle.Continuous) _bar.Style = ProgressBarStyle.Continuous;
                int value = percent;
                if (value < 0) value = 0;
                if (value > 100) value = 100;
                _bar.Value = value;
            }

            if (detail != null) _detail.Text = detail;
        }
    }
    internal sealed class ErrorDialog : Form
    {
        public ErrorDialog(string caption, string message, List<KeyValuePair<string, string>> links)
        {
            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(540, 240);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.White;

            Label label = new Label();
            label.AutoSize = false;
            label.Text = message;
            label.Bounds = new Rectangle(18, 18, 504, 148);
            label.TextAlign = ContentAlignment.TopLeft;
            Controls.Add(label);

            Button close = new Button();
            close.Text = "关闭";
            close.Bounds = new Rectangle(432, 184, 90, 30);
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;

            if (links != null && links.Count > 0)
            {
                int right = 432 - 8;
                for (int i = links.Count - 1; i >= 0; i--)
                {
                    KeyValuePair<string, string> link = links[i];
                    string url = link.Value;
                    Button open = new Button();
                    open.Text = link.Key;
                    open.Bounds = new Rectangle(right - 120, 184, 120, 30);
                    open.Click += delegate(object sender, EventArgs e)
                    {
                        try { Process.Start(url); }
                        catch (Exception) { }
                    };
                    Controls.Add(open);
                    right = right - 120 - 8;
                }
            }
        }
    }
}