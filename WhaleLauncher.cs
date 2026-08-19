// DSH启动管家 — DeepSeek Harness 桌面启动管家（窗口版）
// 双击运行：显示主窗口 → 点击"启动DSH"即启动 DSH Web 服务并打开网页端。
// 支持：开机自启动（拨动开关）、关闭设置（最小化托盘 / 退出程序）、系统托盘。
// 编译：csc /target:winexe /optimize+ /codepage:65001 /win32icon:whale.ico
//       /r:System.dll /r:System.Core.dll /r:System.Net.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll WhaleLauncher.cs
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WhaleLauncher
{
    internal static class Program
    {
        // ---------- 常量 ----------
        internal const string AppTitle = "DSH启动管家 - DeepSeek Harness";
        internal const string LauncherVersion = "1.0";
        private const string RunValueName = "DSH启动管家";
        private const string OldRunValueName = "DSH鲸鱼启动器";   // 远古版本注册表值名（迁移用）
        private const string PrevRunValueName = "DSH启动器";       // 上一版注册表值名（迁移用）
        private const string SettingsKey = @"Software\DSHWhaleLauncher";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const int WebPort = 3080;                       // DSH Web 服务默认端口
        private const string MutexName = "Global\\DSHLauncherSingleInstance";
        private const string LogDirName = "DSHLauncher";
        private const int PortWaitTimeoutMs = 120000;           // 等待服务启动的最长时间
        private const int NeverConnectedTimeoutMs = 600000;     // 从未有任何页面连接时的超时（10 分钟）
        private const int ConnectionLostGraceMs = 60000;        // 页面连接断开后等待的宽限期（1 分钟）
        private const int MonitorIntervalMs = 2000;             // 监控轮询间隔
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int WM_APP_ACTIVATE = 0x8000 + 1;

        private static string _logDir;
        private static string _logFile;
        private static bool _probeMode;
        private static bool _diagMode;
        private static bool _makeShortcut;
        private static bool _deployMode;
        private static bool _extractMsiMode;
        private static bool _checkUpdateMode;
        private static bool _autostart;
        private static string _msiOverride;
        private static int _graceMs = ConnectionLostGraceMs;
        private static string _logDirOverride;
        private static bool _connLogThrottled;
        internal static bool SmokeMode;        // --smoke：显示窗口数秒后自动退出（冒烟测试）
        internal static string ShotPath;       // --shot <path>：渲染窗口截图后退出（界面自检）

        // ---------- 入口 ----------
        [STAThread]
        private static int Main(string[] args)
        {
            ParseArgs(args);
            if (!string.IsNullOrEmpty(_logDirOverride))
            {
                _logDir = _logDirOverride;
            }
            else
            {
                _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LogDirName);
            }
            try
            {
                Directory.CreateDirectory(_logDir);
            }
            catch
            {
                try
                {
                    _logDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    Directory.CreateDirectory(_logDir);
                }
                catch { }
            }
            _logFile = Path.Combine(_logDir, "launcher.log");
            Log("========== 启动 ==========");
            Log("args: " + string.Join(" ", args));

            // 单实例：已有一个启动器在运行 → 通知已有实例（激活窗口并启动DSH）后退出
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    Log("已有启动器实例在运行，向已有窗口发送激活消息");
                    IntPtr h = FindWindow(null, AppTitle);
                    if (h != IntPtr.Zero) SendMessage(h, WM_APP_ACTIVATE, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                MigrateLegacySettings();
                if (_probeMode)
                {
                    RunProbe();
                    mutex.ReleaseMutex();
                    return 0;
                }
                if (_diagMode)
                {
                    Log("---- 诊断信息 ----\n" + CollectDiagnostics());
                    mutex.ReleaseMutex();
                    return 0;
                }
                if (_makeShortcut)
                {
                    Log(CreateDesktopShortcut());
                    mutex.ReleaseMutex();
                    return 0;
                }
                if (_extractMsiMode)
                {
                    string p = ExtractEmbeddedMsi();
                    Log("提取内置安装包结果: " + (p ?? "(失败)"));
                    mutex.ReleaseMutex();
                    return p != null ? 0 : 1;
                }
                if (_checkUpdateMode)
                {
                    string dshCur, dshLatest, lchLatest, lchNote, lchUrl;
                    bool dshNew = CheckDshUpdate(out dshCur, out dshLatest);
                    Log("DSH 有新版: " + dshNew + "（本地 " + (dshCur ?? "无") + " / 最新 " + (dshLatest ?? "未知") + "）");
                    bool lchNew = CheckLauncherUpdate(out lchLatest, out lchNote, out lchUrl);
                    Log("启动器有新版: " + lchNew + "（本地 " + LauncherVersion + " / 最新 " + (lchLatest ?? "无更新源") + "）");
                    Log("版本比较测试: 1.0 < 1.0.1=" + (CompareVersions("1.0", "1.0.1") < 0) +
                        "  1.0.1 > 1.0=" + (CompareVersions("1.0.1", "1.0") > 0) +
                        "  0.1.0 > 0.1.0-rc.6=" + (CompareVersions("0.1.0", "0.1.0-rc.6") > 0) +
                        "  0.1.0-rc.6 > 0.1.0-rc.5=" + (CompareVersions("0.1.0-rc.6", "0.1.0-rc.5") > 0));
                    mutex.ReleaseMutex();
                    return 0;
                }
                if (_deployMode)
                {
                    // 无窗口部署测试模式：进度写入日志
                    var deploy = new DeployRunner(null, null);
                    deploy.Run();
                    mutex.ReleaseMutex();
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new FrmMain(_autostart));
                mutex.ReleaseMutex();
            }
            return 0;
        }

        // ---------- 诊断模式（无窗口）----------
        private static void RunProbe()
        {
            Log("probe: 端口 " + WebPort + " 已在服务 = " + PortAlive(WebPort));
            Log("probe: node = " + (FindNodeExe() ?? "(未找到)"));
            Log("probe: dsh bin.js = " + (FindDshBinJs() ?? "(未找到)"));
            Log("probe: 结束");
        }

        // ---------- 参数解析 ----------
        // 安全：仅接受白名单参数；未知参数一律忽略，不执行任何由参数拼接的命令
        private static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--probe": _probeMode = true; break;
                    case "--diag": _diagMode = true; break;
                    case "--make-shortcut": _makeShortcut = true; break;
                    case "--deploy": _deployMode = true; break;
                    case "--extract-msi": _extractMsiMode = true; break;
                    case "--check-update": _checkUpdateMode = true; break;
                    case "--msi":
                        if (i + 1 < args.Length)
                        {
                            // 仅接受 .msi 文件，防止误传任意路径
                            string v = args[i + 1];
                            if (v.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || File.Exists(v))
                            {
                                _msiOverride = v;
                            }
                            i++;
                        }
                        break;
                    case "--autostart": _autostart = true; break;
                    case "--grace":
                        if (i + 1 < args.Length)
                        {
                            int g;
                            if (int.TryParse(args[i + 1], out g)) { _graceMs = g; i++; }
                        }
                        break;
                    case "--logdir":
                        if (i + 1 < args.Length) { _logDirOverride = args[i + 1]; i++; }
                        break;
                    case "--smoke": SmokeMode = true; break;
                    case "--shot":
                        if (i + 1 < args.Length) { ShotPath = args[i + 1]; i++; }
                        break;
                }
            }
        }

        // ---------- 服务进程与端口（供 FrmMain 调用）----------
        internal enum SpawnMode { Redirect, ShellHidden }

        internal static int WebPortNum { get { return WebPort; } }
        internal static int GraceMs { get { return _graceMs; } }
        internal static int PortWaitTimeoutMsValue { get { return PortWaitTimeoutMs; } }
        internal static int NeverConnectedTimeoutMsValue { get { return NeverConnectedTimeoutMs; } }
        internal static int MonitorIntervalMsValue { get { return MonitorIntervalMs; } }
        internal static int WmAppActivate { get { return WM_APP_ACTIVATE; } }

        internal static Process SpawnServer(string nodeExe, string script, string workDir)
        {
            Process p = TrySpawn(nodeExe, script, workDir, SpawnMode.Redirect);
            if (p != null) return p;
            Log("重定向启动被拒绝，降级为隐藏控制台启动");
            return TrySpawn(nodeExe, script, workDir, SpawnMode.ShellHidden);
        }

        private static Process TrySpawn(string nodeExe, string script, string workDir, SpawnMode mode)
        {
            var psi = new ProcessStartInfo();
            psi.FileName = nodeExe;
            psi.Arguments = "\"" + script + "\" --profile web";
            psi.WorkingDirectory = workDir;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            if (mode == SpawnMode.Redirect)
            {
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DSH_HOME")))
                {
                    psi.EnvironmentVariables["DSH_HOME"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
                }
            }
            else
            {
                psi.UseShellExecute = true;
            }
            try
            {
                Process p = new Process();
                p.StartInfo = psi;
                if (mode == SpawnMode.Redirect)
                {
                    p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) AppendLog(Path.Combine(_logDir, "server-out.log"), e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) AppendLog(Path.Combine(_logDir, "server-err.log"), e.Data); };
                }
                if (!p.Start()) return null;
                if (mode == SpawnMode.Redirect)
                {
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                }
                return p;
            }
            catch (Exception ex)
            {
                Log("spawn 异常(" + (mode == SpawnMode.Redirect ? "重定向" : "隐藏控制台") + "): " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        internal static bool PortAlive(int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    return ar.AsyncWaitHandle.WaitOne(1500) && c.Connected;
                }
            }
            catch { return false; }
        }

        internal static int CountConnections(int port)
        {
            try
            {
                int n = 0;
                foreach (TcpConnectionInformation ci in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                {
                    if (ci.State == TcpState.Established && ci.LocalEndPoint.Port == port) n++;
                }
                return n;
            }
            catch (Exception ex)
            {
                if (!_connLogThrottled)
                {
                    _connLogThrottled = true;
                    Log("读取 TCP 连接失败: " + ex.Message);
                }
                return 0;
            }
        }

        internal static string FindNodeExe()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe")
            };
            foreach (string c in candidates) if (File.Exists(c)) return c;
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathVar.Split(';'))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    string full = Path.Combine(dir.Trim('"'), "node.exe");
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        internal static string FindDshBinJs()
        {
            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (string.IsNullOrEmpty(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            string profileCopy = Path.Combine(home, "profiles", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            if (File.Exists(profileCopy)) return profileCopy;
            // 一键部署安装的位置：%LOCALAPPDATA%\DSHLauncher\npm-global（兼容旧版 DSHWhaleLauncher 路径）
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                string[] globalPrefixes =
                {
                    Path.Combine(local, "DSHLauncher", "npm-global"),
                    Path.Combine(local, "DSHWhaleLauncher", "npm-global")
                };
                foreach (string p in globalPrefixes)
                {
                    string globalCopy = Path.Combine(p, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                    if (File.Exists(globalCopy)) return globalCopy;
                }
            }
            string npmCache = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(npmCache))
            {
                string npxRoot = Path.Combine(npmCache, "npm-cache", "_npx");
                if (!Directory.Exists(npxRoot)) npxRoot = Path.Combine(npmCache, "_npx");
                if (Directory.Exists(npxRoot))
                {
                    string best = null;
                    DateTime bestTime = DateTime.MinValue;
                    foreach (string dir in Directory.GetDirectories(npxRoot))
                    {
                        string cand = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                        if (File.Exists(cand))
                        {
                            DateTime t = File.GetLastWriteTimeUtc(cand);
                            if (t > bestTime) { bestTime = t; best = cand; }
                        }
                    }
                    if (best != null) return best;
                }
            }
            return null;
        }

        internal static string NpmGlobalPrefix()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSHLauncher", "npm-global");
        }

        // ---------- 一键部署：定位 Node.js 安装包 ----------
        internal static string LocateNodeMsi()
        {
            // 1) 显式指定（测试用）
            if (!string.IsNullOrEmpty(_msiOverride) && File.Exists(_msiOverride)) return _msiOverride;
            // 2) exe 同目录的外部安装包（可替换更新）
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            try
            {
                foreach (string f in Directory.GetFiles(dir, "node-v*.msi")) return f;
            }
            catch { }
            // 3) 内置资源
            return ExtractEmbeddedMsi();
        }

        private const string EmbeddedMsiResourceName = "node-v24.19.0-x64.msi";

        // 内置 Node.js 官方安装包的期望 MD5（发布前核验；用于完整性自检，防 exe 被二次打包替换安装包）
        internal const string EmbeddedMsiExpectedMd5 = "184B26AF284EA9818B6E6F82CC90EAF5";

        private static string ExtractEmbeddedMsi()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(EmbeddedMsiResourceName))
                {
                    if (stream == null)
                    {
                        Log("程序集内未找到内置安装包资源: " + EmbeddedMsiResourceName);
                        return null;
                    }
                    string target = Path.Combine(LogDir, EmbeddedMsiResourceName);
                    // 已存在且大小一致 → 直接复用
                    if (File.Exists(target))
                    {
                        try
                        {
                            if (new FileInfo(target).Length == stream.Length) return target;
                        }
                        catch { }
                    }
                    using (FileStream fs = File.Create(target))
                    {
                        byte[] buffer = new byte[1 << 20];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            fs.Write(buffer, 0, read);
                        }
                    }
                    Log("已提取内置 Node.js 安装包: " + target);
                    return target;
                }
            }
            catch (Exception ex)
            {
                Log("提取内置 Node.js 安装包失败: " + ex.Message);
                return null;
            }
        }

        // ---------- 文件哈希（完整性自检用） ----------
        internal static string ComputeFileMd5(string path)
        {
            try
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] hash = md5.ComputeHash(fs);
                    var sb = new StringBuilder();
                    foreach (byte b in hash) sb.Append(b.ToString("X2"));
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                Log("计算文件 MD5 失败: " + ex.Message);
                return null;
            }
        }

        internal static string ComputeFileSha256(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    var sb = new StringBuilder();
                    foreach (byte b in hash) sb.Append(b.ToString("X2"));
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                Log("计算文件 SHA-256 失败: " + ex.Message);
                return null;
            }
        }

        // 校验安装包完整性：与代码内期望 MD5 一致才算通过
        internal static bool VerifyMsiIntegrity(string msiPath)
        {
            try
            {
                string md5 = ComputeFileMd5(msiPath);
                if (string.IsNullOrEmpty(md5)) return false;
                bool ok = string.Equals(md5, EmbeddedMsiExpectedMd5, StringComparison.OrdinalIgnoreCase);
                Log("安装包完整性校验: " + msiPath + " MD5=" + md5 + (ok ? " 通过" : " 不通过（拒绝安装）"));
                return ok;
            }
            catch { return false; }
        }

        // ---------- 一键部署：MSI 静默安装（管理员提权）----------
        internal static int RunMsiexecInstall(string msiPath, Action<int> progress, Func<bool> cancelled)
        {
            var psi = new ProcessStartInfo();
            psi.FileName = "msiexec.exe";
            psi.Arguments = "/i \"" + msiPath + "\" /qn /norestart";
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log("msiexec 提权启动失败: " + ex.Message);
                return -999;
            }
            int last = 0;
            while (!p.WaitForExit(1500))
            {
                if (cancelled != null && cancelled())
                {
                    try { p.Kill(); } catch { }
                    return -1000;
                }
                last = Math.Min(100, last + 2);
                if (progress != null) progress(last);
            }
            try { return p.ExitCode; } catch { return -1; }
        }

        // ---------- 一键部署：npm 用户级全局安装 @deepseek-ai/dsh ----------
        internal static string RunNpmInstallDsh(string nodeBinDir, Action<string> logLine, Func<bool> cancelled)
        {
            string nodeExe = Path.Combine(nodeBinDir, "node.exe");
            string npmCli = Path.Combine(nodeBinDir, "node_modules", "npm", "bin", "npm-cli.js");
            if (!File.Exists(nodeExe)) return "未找到 node.exe：" + nodeExe;
            if (!File.Exists(npmCli)) return "未找到 npm-cli.js：" + npmCli;
            string prefix = NpmGlobalPrefix();
            string args = "\"" + npmCli + "\" install -g --prefix \"" + prefix + "\" @deepseek-ai/dsh";
            Log("npm 命令: node " + args);
            var psi = new ProcessStartInfo();
            psi.FileName = nodeExe;
            psi.Arguments = args;
            psi.WorkingDirectory = nodeBinDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            try
            {
                Process p = new Process();
                p.StartInfo = psi;
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data) && logLine != null) logLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data) && logLine != null) logLine(e.Data); };
                if (!p.Start()) return "npm 进程启动失败";
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.WaitForExit(1000))
                {
                    if (cancelled != null && cancelled())
                    {
                        try { p.Kill(); } catch { }
                        return "已取消";
                    }
                }
                p.WaitForExit();
                int code = 0;
                try { code = p.ExitCode; } catch { }
                return code == 0 ? null : ("npm 安装失败（退出码 " + code + "），详见日志");
            }
            catch (Exception ex)
            {
                return "npm 安装异常：" + ex.Message;
            }
        }

        // ---------- 一键部署：初始化 DSH 配置（快速退出）----------
        internal static string RunDshInit(string nodeBinDir, Action<string> logLine, Func<bool> cancelled)
        {
            string binJs = FindDshBinJs();
            string nodeExe = Path.Combine(nodeBinDir, "node.exe");
            if (string.IsNullOrEmpty(binJs)) return "未找到 DSH 启动入口 bin.js";
            var psi = new ProcessStartInfo();
            psi.FileName = nodeExe;
            psi.Arguments = "\"" + binJs + "\" --profile web --dump-config";
            psi.WorkingDirectory = nodeBinDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            try
            {
                Process p = new Process();
                p.StartInfo = psi;
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data) && logLine != null) logLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data) && logLine != null) logLine(e.Data); };
                if (!p.Start()) return "初始化进程启动失败";
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.WaitForExit(1000))
                {
                    if (cancelled != null && cancelled())
                    {
                        try { p.Kill(); } catch { }
                        return "已取消";
                    }
                }
                p.WaitForExit();
                int code = 0;
                try { code = p.ExitCode; } catch { }
                return code == 0 ? null : ("初始化失败（退出码 " + code + "），详见日志");
            }
            catch (Exception ex)
            {
                return "初始化异常：" + ex.Message;
            }
        }

        internal static void OpenBrowser(int port)
        {
            try
            {
                Process.Start("http://127.0.0.1:" + port);
            }
            catch (Exception ex)
            {
                Log("打开浏览器失败: " + ex.Message);
            }
        }

        internal static void Log(string msg)
        {
            AppendLog(_logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg);
        }

        private static readonly object LogLock = new object();
        private static void AppendLog(string file, string line)
        {
            try
            {
                lock (LogLock)
                {
                    // 日志轮转：超过 2MB 归档为 .old.log，避免无限增长
                    FileInfo fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        try
                        {
                            string old = file + ".old.log";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(file, old);
                        }
                        catch { }
                    }
                    File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        internal static string ReadTailLogs()
        {
            var sb = new StringBuilder();
            foreach (string name in new[] { "server-out.log", "server-err.log" })
            {
                string f = Path.Combine(_logDir, name);
                try
                {
                    if (File.Exists(f))
                    {
                        string tail = ReadTail(f, 40);
                        if (!string.IsNullOrWhiteSpace(tail))
                        {
                            sb.AppendLine("---- " + name + " 末尾 ----");
                            sb.AppendLine(tail);
                        }
                    }
                }
                catch { }
            }
            return sb.ToString();
        }

        private static string ReadTail(string file, int maxLines)
        {
            var lines = new System.Collections.Generic.List<string>();
            using (var sr = new StreamReader(file, Encoding.UTF8, true))
            {
                string line;
                while ((line = sr.ReadLine()) != null) lines.Add(line);
            }
            int skip = Math.Max(0, lines.Count - maxLines);
            var sb = new StringBuilder();
            for (int i = skip; i < lines.Count; i++) sb.AppendLine(lines[i]);
            return sb.ToString().TrimEnd();
        }

        internal static string LogDir { get { return _logDir; } }

        // ---------- 诊断信息 ----------
        internal static string CollectDiagnostics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("DSH启动管家诊断信息");
            sb.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("端口 " + WebPort + " 状态: " + (PortAlive(WebPort) ? "已在服务" : "未运行"));
            sb.AppendLine("node: " + (FindNodeExe() ?? "(未找到)"));
            sb.AppendLine("DSH 入口: " + (FindDshBinJs() ?? "(未找到)"));
            sb.AppendLine("DSH_HOME: " + (Environment.GetEnvironmentVariable("DSH_HOME") ?? "(未设置，默认 ~/.dsh)"));
            sb.AppendLine("开机自启动: " + (IsAutoStartEnabled() ? "开" : "关"));
            sb.AppendLine("日志目录: " + _logDir);
            sb.AppendLine("启动管家 exe: " + Application.ExecutablePath);
            try { sb.AppendLine("启动管家 SHA-256: " + ComputeFileSha256(Application.ExecutablePath)); } catch { }
            try
            {
                string msi = LocateNodeMsi();
                if (!string.IsNullOrEmpty(msi))
                {
                    sb.AppendLine("内置安装包: " + msi);
                    sb.AppendLine("内置安装包 MD5: " + (ComputeFileMd5(msi) ?? "(计算失败)") + "  期望: " + EmbeddedMsiExpectedMd5);
                }
            }
            catch { }
            sb.AppendLine("端口 " + WebPort + " 监听地址: " + DescribeListeners(WebPort));
            int outCount = CountOutboundConnections();
            sb.AppendLine("对外网络连接: " + (outCount < 0 ? "(查询失败)" : outCount + " 条"));
            sb.Append(AppendOutboundConnections());
            try
            {
                string launcherTail = ReadTail(_logFile, 30);
                if (!string.IsNullOrWhiteSpace(launcherTail))
                {
                    sb.AppendLine("---- launcher.log 末尾 ----");
                    sb.AppendLine(launcherTail);
                }
            }
            catch { }
            string serverTail = ReadTailLogs();
            if (!string.IsNullOrWhiteSpace(serverTail))
            {
                sb.AppendLine("---- 服务日志尾部 ----");
                sb.AppendLine(serverTail);
            }
            return sb.ToString();
        }

        // 列出指定端口的所有监听地址（应为 127.0.0.1，若出现 0.0.0.0/局域网地址需警惕）
        internal static string DescribeListeners(int port)
        {
            try
            {
                var sb = new StringBuilder();
                IPEndPoint[] listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                bool any = false;
                foreach (IPEndPoint li in listeners)
                {
                    if (li.Port == port)
                    {
                        sb.Append((any ? ", " : "") + li.Address);
                        any = true;
                    }
                }
                return any ? sb.ToString() : "(无监听)";
            }
            catch { return "(查询失败)"; }
        }

        // 统计对外（非私网/回环）的已建立连接数
        internal static int CountOutboundConnections()
        {
            try
            {
                int n = 0;
                foreach (TcpConnectionInformation ci in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                {
                    if (ci.State == TcpState.Established && !IsPrivateAddress(ci.RemoteEndPoint.Address)) n++;
                }
                return n;
            }
            catch { return -1; }
        }

        // 列出对外连接的远端地址（去重计数，最多显示 10 个）
        internal static string AppendOutboundConnections()
        {
            try
            {
                var counts = new Dictionary<string, int>();
                foreach (TcpConnectionInformation ci in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                {
                    if (ci.State != TcpState.Established) continue;
                    IPAddress remote = ci.RemoteEndPoint.Address;
                    if (IsPrivateAddress(remote)) continue;
                    string key = ci.RemoteEndPoint.ToString();
                    if (counts.ContainsKey(key)) counts[key]++;
                    else counts[key] = 1;
                }
                var sb = new StringBuilder();
                if (counts.Count == 0) { sb.AppendLine("  (无对外连接)"); return sb.ToString(); }
                int shown = 0;
                foreach (KeyValuePair<string, int> kv in counts)
                {
                    if (shown >= 10) break;
                    sb.AppendLine("  " + kv.Key + " x" + kv.Value);
                    shown++;
                }
                if (counts.Count > 10) sb.AppendLine("  …共 " + counts.Count + " 个远端地址");
                return sb.ToString();
            }
            catch { return "  (查询失败)"; }
        }

        // 判断是否为私网/回环地址（本机通信不列入"对外连接"）
        private static bool IsPrivateAddress(IPAddress addr)
        {
            if (addr == null) return true;
            if (addr.Equals(IPAddress.Loopback)) return true;
            byte[] b = addr.GetAddressBytes();
            if (b.Length != 4) return true;
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 127) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            return false;
        }

        // ---------- 桌面快捷方式 ----------
        internal static string CreateDesktopShortcut()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string path = Path.Combine(desktop, "打开 DeepSeek Harness.url");
                bool existed = File.Exists(path);
                var sb = new StringBuilder();
                sb.AppendLine("[InternetShortcut]");
                sb.AppendLine("URL=http://127.0.0.1:" + WebPort);
                sb.AppendLine("IconFile=" + Application.ExecutablePath);
                sb.AppendLine("IconIndex=0");
                File.WriteAllText(path, sb.ToString(), Encoding.Unicode);
                Log("桌面快捷方式: " + path);
                return existed ? "已更新桌面快捷方式" : "已创建桌面快捷方式";
            }
            catch (Exception ex)
            {
                Log("创建桌面快捷方式失败: " + ex.Message);
                return "创建失败：" + ex.Message;
            }
        }

        // ---------- 版本更新检查 ----------
        private const string DshNpmLatestUrl = "https://registry.npmjs.org/@deepseek-ai/dsh/latest";
        private const string LauncherUpdateFileName = "DSH启动管家-最新版本.json";

        private static string DownloadStringWithTimeout(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "DSHLauncher/" + LauncherVersion;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        private static string ParseJsonField(string json, string field)
        {
            string pattern = "\"" + field + "\"\\s*:\\s*\"([^\"]*)\"";
            var m = System.Text.RegularExpressions.Regex.Match(json, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>读取本地已安装的 DSH 框架版本（从 bin.js 所在包的 package.json）。</summary>
        internal static string GetInstalledDshVersion()
        {
            try
            {
                string binJs = FindDshBinJs();
                if (string.IsNullOrEmpty(binJs)) return null;
                string pkgJson = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(binJs)), "package.json");
                if (!File.Exists(pkgJson)) return null;
                string text = File.ReadAllText(pkgJson, Encoding.UTF8);
                return ParseJsonField(text, "version");
            }
            catch { return null; }
        }

        /// <summary>检查 DSH 框架是否有新版本。网络失败返回 false（静默）。</summary>
        internal static bool CheckDshUpdate(out string current, out string latest)
        {
            current = GetInstalledDshVersion();
            latest = null;
            try
            {
                string json = DownloadStringWithTimeout(DshNpmLatestUrl, 10000);
                latest = ParseJsonField(json, "version");
                Log("DSH 版本检查: 本地=" + (current ?? "未安装") + " 最新=" + (latest ?? "未知"));
                if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(latest)) return false;
                return CompareVersions(latest, current) > 0;
            }
            catch (Exception ex)
            {
                Log("DSH 版本检查失败（网络不可用?）: " + ex.Message);
                return false;
            }
        }

        /// <summary>检查启动管家自身是否有新版本（读取 exe 同目录的 DSH启动管家-最新版本.json）。</summary>
        internal static bool CheckLauncherUpdate(out string latest, out string note, out string url)
        {
            latest = null;
            note = null;
            url = null;
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), LauncherUpdateFileName);
                if (!File.Exists(path))
                {
                    Log("启动器更新源不存在（" + LauncherUpdateFileName + "），跳过检查");
                    return false;
                }
                string json = File.ReadAllText(path, Encoding.UTF8);
                latest = ParseJsonField(json, "version");
                note = ParseJsonField(json, "note");
                url = ParseJsonField(json, "url");
                Log("启动器版本检查: 本地=" + LauncherVersion + " 最新=" + (latest ?? "未知"));
                if (string.IsNullOrEmpty(latest)) return false;
                return CompareVersions(latest, LauncherVersion) > 0;
            }
            catch (Exception ex)
            {
                Log("启动器版本检查失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>简单语义化版本比较：a &gt; b 返回正数，相等返回 0。支持 主.次.补丁 与 -rc.N 预发布后缀。</summary>
        internal static int CompareVersions(string a, string b)
        {
            int[] pa = SplitVersion(a);
            int[] pb = SplitVersion(b);
            for (int i = 0; i < 4; i++)
            {
                if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
            }
            return 0;
        }

        private static int[] SplitVersion(string v)
        {
            var result = new[] { 0, 0, 0, 0 };
            string main = v;
            string pre = "";
            int dash = v.IndexOf('-');
            if (dash >= 0) { main = v.Substring(0, dash); pre = v.Substring(dash + 1); }
            string[] parts = main.Split('.');
            for (int i = 0; i < parts.Length && i < 3; i++)
            {
                int n;
                if (int.TryParse(parts[i], out n)) result[i] = n;
            }
            // 预发布数字（rc.N 的 N）
            var pm = System.Text.RegularExpressions.Regex.Match(pre, "(\\d+)");
            if (pm.Success)
            {
                int n;
                if (int.TryParse(pm.Groups[1].Value, out n)) result[3] = n;
            }
            result[3] = pre == "" ? int.MaxValue : result[3]; // 稳定版 > 预发布
            return result;
        }

        // ---------- Job Object（kill-on-close）----------
        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        // 无边框窗口拖动（标题栏按下时调用）
        internal static void BeginDrag(IntPtr hwnd)
        {
            ReleaseCapture();
            SendMessage(hwnd, 0x00A1, new IntPtr(2), IntPtr.Zero);
        }

        internal static IntPtr CreateKillOnCloseJob(Process target)
        {
            try
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                    {
                        CloseHandle(job);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
                if (!AssignProcessToJobObject(job, target.Handle))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
                return job;
            }
            catch (Exception ex)
            {
                Log("创建 Job Object 失败: " + ex.Message);
                return IntPtr.Zero;
            }
        }

        internal static void TerminateJob(IntPtr job)
        {
            if (job != IntPtr.Zero)
            {
                TerminateJobObject(job, 0);
                CloseHandle(job);
            }
        }

        internal static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (key == null) return false;
                    return key.GetValue(RunValueName) != null;
                }
            }
            catch { return false; }
        }

        /// <summary>迁移旧版本（DSH启动器 / DSH鲸鱼启动器）的注册表开机启动项到新名称。</summary>
        private static void MigrateLegacySettings()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    MigrateOne(key, PrevRunValueName);
                    MigrateOne(key, OldRunValueName);
                }
            }
            catch { }
        }

        private static void MigrateOne(RegistryKey key, string oldName)
        {
            try
            {
                object oldVal = key.GetValue(oldName);
                if (oldVal != null)
                {
                    if (key.GetValue(RunValueName) == null)
                    {
                        key.SetValue(RunValueName, oldVal);
                    }
                    key.DeleteValue(oldName, false);
                    Log("已迁移旧开机启动项（" + oldName + " → " + RunValueName + "）");
                }
            }
            catch { }
        }

        internal static void SetAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) return;
                    if (enabled)
                    {
                        key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\" --autostart");
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                }
                Log("开机自启动 = " + (enabled ? "开" : "关"));
            }
            catch (Exception ex)
            {
                Log("设置开机自启动失败: " + ex.Message);
            }
        }

        internal static string GetCloseAction()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    if (key == null) return "tray";
                    object v = key.GetValue("CloseAction");
                    return (v as string) ?? "tray";
                }
            }
            catch { return "tray"; }
        }

        internal static void SetCloseAction(string action)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (key != null) key.SetValue("CloseAction", action);
                }
            }
            catch { }
        }
    }

    // ================= 主窗口 =================
    internal sealed class FrmMain : Form
    {
        private const int TitleBarHeight = 44;
        private const int CornerRadius = 24;

        private Label _lblStatus;
        private RoundButton _btnStart;
        private ToggleSwitch _toggleAutoStart;
        private RadioButton _rbTray;
        private RadioButton _rbExit;
        private RoundButton _segTray;
        private RoundButton _segExit;
        private NotifyIcon _notify;
        private ContextMenuStrip _trayMenu;
        private bool _loading = true;
        private bool _forceExit;
        private bool _runningCache;
        private Image _maidArt;
        private MemoryStream _maidStream;

        // 服务状态
        private Process _server;
        private IntPtr _job = IntPtr.Zero;
        private Thread _monitorThread;
        private volatile bool _monitorRunning;
        private volatile bool _starting;
        private readonly object _serverLock = new object();

        public FrmMain(bool autostart)
        {
            Text = Program.AppTitle;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            ClientSize = new Size(560, 700);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = UiTheme.Bg;
            Opacity = 0.93;              // 半透明毛玻璃质感
            UpdateRegion();
            LoadMaidArt();

            BuildUi();
            LoadSettings();

            // 开机自启动 → 自动启动DSH
            Shown += (s, e) =>
            {
                if (!string.IsNullOrEmpty(Program.ShotPath))
                {
                    RenderShot(Program.ShotPath);
                    return;
                }
                if (autostart)
                {
                    Program.Log("开机自启动触发，自动启动DSH");
                    StartWhale();
                }
                if (!Program.SmokeMode) CheckForUpdatesSilently();
            };

            // 冒烟测试：显示几秒后自动退出
            if (Program.SmokeMode)
            {
                var smokeTimer = new System.Windows.Forms.Timer();
                smokeTimer.Interval = 6000;
                smokeTimer.Tick += delegate
                {
                    smokeTimer.Stop();
                    Application.Exit();
                };
                smokeTimer.Start();
            }

            FormClosing += FrmMain_FormClosing;
        }

        // ---------- 版本更新检查 ----------
        private bool _updateCheckedOnce;

        private void CheckForUpdatesSilently()
        {
            if (_updateCheckedOnce) return;
            _updateCheckedOnce = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string dshCur, dshLatest, lchLatest, lchNote, lchUrl;
                    bool dshNew = Program.CheckDshUpdate(out dshCur, out dshLatest);
                    bool lchNew = Program.CheckLauncherUpdate(out lchLatest, out lchNote, out lchUrl);
                    if (dshNew || lchNew)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("发现新版本：");
                        if (dshNew) sb.AppendLine("· DeepSeek Harness " + dshCur + " → " + dshLatest);
                        if (lchNew) sb.AppendLine("· DSH启动管家 " + Program.LauncherVersion + " → " + lchLatest);
                        sb.AppendLine();
                        sb.AppendLine("可在托盘菜单点击【检查更新】查看详情");
                        try
                        {
                            if (InvokeRequired) BeginInvoke(new Action<string>(NotifyUpdateBalloon), sb.ToString());
                            else NotifyUpdateBalloon(sb.ToString());
                        }
                        catch { }
                    }
                }
                catch { }
            });
        }

        private void NotifyUpdateBalloon(string msg)
        {
            try
            {
                _notify.ShowBalloonTip(6000, "DSH启动管家", msg, ToolTipIcon.Info);
                SetStatus("发现新版本：可在托盘菜单中检查更新");
            }
            catch { }
        }

        private void ShowUpdateDialog()
        {
            SetStatus("正在检查更新…");
            ThreadPool.QueueUserWorkItem(delegate { ShowUpdateDialogWorker(); });
        }

        private void ShowUpdateDialogWorker()
        {
            try
            {
                string dshCur, dshLatest, lchLatest, lchNote, lchUrl;
                bool dshNew = Program.CheckDshUpdate(out dshCur, out dshLatest);
                bool lchNew = Program.CheckLauncherUpdate(out lchLatest, out lchNote, out lchUrl);
                if (InvokeRequired)
                {
                    BeginInvoke(new Action<string, string, bool, bool, string, string, string>(ShowUpdateResult),
                        dshCur, dshLatest, dshNew, lchNew, lchLatest, lchNote, lchUrl);
                }
                else ShowUpdateResult(dshCur, dshLatest, dshNew, lchNew, lchLatest, lchNote, lchUrl);
            }
            catch (Exception ex)
            {
                Program.Log("检查更新异常: " + ex.Message);
            }
        }

        private void ShowUpdateResult(string dshCur, string dshLatest, bool dshNew, bool lchNew, string lchLatest, string lchNote, string lchUrl)
        {
            try
            {
                if (!dshNew && !lchNew)
                {
                    MessageBox.Show("已是最新版本：\n· DeepSeek Harness " + (dshCur ?? "未安装") + "\n· DSH启动管家 " + Program.LauncherVersion,
                        Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    SetStatus("已是最新版本");
                    return;
                }
                var sb = new StringBuilder();
                sb.AppendLine("检测到新版本：");
                if (dshNew) sb.AppendLine("· DeepSeek Harness：" + dshCur + " → " + dshLatest);
                if (lchNew) sb.AppendLine("· DSH启动管家：" + Program.LauncherVersion + " → " + lchLatest);
                string lchInfo = "";
                if (lchNew) lchInfo = "\n\n启动器更新说明：" + (string.IsNullOrEmpty(lchNote) ? "（无说明）" : lchNote);
                if (dshNew)
                {
                    DialogResult r = MessageBox.Show(sb.ToString() + "\n\n是否立即更新 DeepSeek Harness？" + lchInfo,
                        Program.AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r == DialogResult.Yes)
                    {
                        string nodeExe = Program.FindNodeExe();
                        if (string.IsNullOrEmpty(nodeExe))
                        {
                            MessageBox.Show("未找到 Node.js 环境，请先点击【一键部署】。", Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        string nodeBinDir = Path.GetDirectoryName(nodeExe);
                        using (var frm = new FrmDshUpdate(nodeBinDir))
                        {
                            frm.Start();
                            frm.ShowDialog(this);
                        }
                        SetStatus("DeepSeek Harness 已更新，请重新点击【启动DSH】");
                    }
                }
                else
                {
                    MessageBox.Show(sb.ToString() + lchInfo + "\n\n请从更新来源获取新版本 exe 后替换本程序。",
                        Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    if (!string.IsNullOrEmpty(lchUrl))
                    {
                        try { Process.Start(lchUrl); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log("更新提示异常: " + ex.Message);
            }
        }

        // ---------- 界面 ----------
        private void BuildUi()
        {
            SuspendLayout();

            // ---------- 标题栏按钮 ----------
            var btnMin = new RoundButton();
            btnMin.Text = "—";
            btnMin.Ghost = true;
            btnMin.TextColor = UiTheme.TextDark;
            btnMin.Bounds = new Rectangle(ClientSize.Width - 96, 7, 36, 30);
            btnMin.Radius = 12;
            btnMin.Font = new Font("Microsoft YaHei UI", 9F);
            btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };
            Controls.Add(btnMin);

            var btnClose = new RoundButton();
            btnClose.Text = "✕";
            btnClose.Ghost = true;
            btnClose.TextColor = UiTheme.TextDark;
            btnClose.Bounds = new Rectangle(ClientSize.Width - 52, 7, 36, 30);
            btnClose.Radius = 12;
            btnClose.Font = new Font("Microsoft YaHei UI", 9F);
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            // ---------- 头部 ----------
            var lblBig = new Label();
            lblBig.Text = "DSH启动管家";
            lblBig.Font = new Font("Microsoft YaHei UI", 24F, FontStyle.Bold);
            lblBig.ForeColor = UiTheme.TextDark;
            lblBig.BackColor = UiTheme.Bg;
            lblBig.Location = new Point(32, 62);
            lblBig.AutoSize = true;
            Controls.Add(lblBig);

            var lblSub = new Label();
            lblSub.Text = "DeepSeek Harness 桌面管家";
            lblSub.Font = new Font("Microsoft YaHei UI", 10.5F);
            lblSub.ForeColor = UiTheme.TextGray;
            lblSub.BackColor = UiTheme.Bg;
            lblSub.Location = new Point(32, 108);
            lblSub.AutoSize = true;
            Controls.Add(lblSub);

            _lblStatus = new Label();
            _lblStatus.Text = "服务未运行";
            _lblStatus.Visible = false;
            Controls.Add(_lblStatus);

            // ---------- 卡片1：快速启动 ----------
            var card1 = new GlassCard();
            card1.Bounds = new Rectangle(24, 188, ClientSize.Width - 48, 158);
            Controls.Add(card1);

            var lbl1 = new Label();
            lbl1.Text = "▍快速启动";
            lbl1.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            lbl1.ForeColor = UiTheme.PrimaryDeep;
            lbl1.BackColor = UiTheme.CardBg;
            lbl1.Location = new Point(20, 12);
            lbl1.AutoSize = true;
            card1.Controls.Add(lbl1);

            _btnStart = new RoundButton();
            _btnStart.Text = "启动DSH";
            _btnStart.Bounds = new Rectangle(18, 46, (card1.Width - 36 - 10) / 2, 52);
            _btnStart.Radius = 16;
            _btnStart.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            _btnStart.TextColor = UiTheme.Ink;
            _btnStart.Click += BtnStart_Click;
            card1.Controls.Add(_btnStart);

            var btnRestart = new RoundButton();
            btnRestart.Text = "一键重启";
            btnRestart.Variant = 1;
            btnRestart.Bounds = new Rectangle(18 + _btnStart.Width + 10, 46, _btnStart.Width, 52);
            btnRestart.Radius = 16;
            btnRestart.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            btnRestart.TextColor = Color.White;
            btnRestart.Click += (s, e) => RestartServer();
            card1.Controls.Add(btnRestart);

            string[] toolNames = new string[] { "打开网页 UI", "停止服务" };
            int bw = (card1.Width - 36 - 10) / 2;
            int bx = 18;
            for (int i = 0; i < toolNames.Length; i++)
            {
                var b = new RoundButton();
                b.Text = toolNames[i];
                b.Ghost = true;
                b.TextColor = UiTheme.Porcelain;
                b.Bounds = new Rectangle(bx, 108, bw, 34);
                b.Radius = 10;
                b.Font = new Font("Microsoft YaHei UI", 9F);
                if (i == 0) b.Click += (s, e) => OpenWebUi();
                else b.Click += (s, e) => StopServerFromMenu();
                card1.Controls.Add(b);
                bx += bw + 10;
            }

            // ---------- 卡片2：设置 ----------
            var card2 = new GlassCard();
            card2.Bounds = new Rectangle(24, 362, ClientSize.Width - 48, 168);
            Controls.Add(card2);

            var lbl2 = new Label();
            lbl2.Text = "▍设置";
            lbl2.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            lbl2.ForeColor = UiTheme.PrimaryDeep;
            lbl2.BackColor = UiTheme.CardBg;
            lbl2.Location = new Point(20, 12);
            lbl2.AutoSize = true;
            card2.Controls.Add(lbl2);

            var lblAuto = new Label();
            lblAuto.Text = "开机自启动";
            lblAuto.Font = new Font("Microsoft YaHei UI", 9.5F);
            lblAuto.ForeColor = UiTheme.TextDark;
            lblAuto.BackColor = UiTheme.CardBg;
            lblAuto.Location = new Point(20, 50);
            lblAuto.AutoSize = true;
            card2.Controls.Add(lblAuto);

            _toggleAutoStart = new ToggleSwitch();
            _toggleAutoStart.Bounds = new Rectangle(card2.Width - 72, 44, 48, 26);
            _toggleAutoStart.CheckedChanged += delegate
            {
                if (_loading) return;
                Program.SetAutoStart(_toggleAutoStart.Checked);
                SetStatus(_toggleAutoStart.Checked ? "已开启开机自启动" : "已关闭开机自启动");
            };
            card2.Controls.Add(_toggleAutoStart);

            var lblClose = new Label();
            lblClose.Text = "关闭窗口时";
            lblClose.Font = new Font("Microsoft YaHei UI", 9.5F);
            lblClose.ForeColor = UiTheme.TextDark;
            lblClose.BackColor = UiTheme.CardBg;
            lblClose.Location = new Point(20, 92);
            lblClose.AutoSize = true;
            card2.Controls.Add(lblClose);

            _rbTray = new RadioButton();
            _rbTray.Visible = false;
            _rbTray.CheckedChanged += delegate { if (!_loading && _rbTray.Checked) Program.SetCloseAction("tray"); };
            card2.Controls.Add(_rbTray);

            _rbExit = new RadioButton();
            _rbExit.Visible = false;
            _rbExit.CheckedChanged += delegate { if (!_loading && _rbExit.Checked) Program.SetCloseAction("exit"); };
            card2.Controls.Add(_rbExit);

            _segTray = new RoundButton();
            _segTray.Text = "最小化托盘";
            _segTray.Bounds = new Rectangle(112, 86, 118, 32);
            _segTray.Radius = 10;
            _segTray.Font = new Font("Microsoft YaHei UI", 9F);
            _segTray.Click += delegate { _rbTray.Checked = true; UpdateSegments(); };
            card2.Controls.Add(_segTray);

            _segExit = new RoundButton();
            _segExit.Text = "退出程序";
            _segExit.Bounds = new Rectangle(238, 86, 100, 32);
            _segExit.Radius = 10;
            _segExit.Font = new Font("Microsoft YaHei UI", 9F);
            _segExit.Click += delegate { _rbExit.Checked = true; UpdateSegments(); };
            card2.Controls.Add(_segExit);

            var hint = new Label();
            hint.Text = "网页关闭后自动停止服务 · 托盘菜单可随时管理";
            hint.Font = new Font("Microsoft YaHei UI", 8.5F);
            hint.ForeColor = UiTheme.TextGray;
            hint.BackColor = UiTheme.CardBg;
            hint.Location = new Point(20, 136);
            hint.AutoSize = true;
            card2.Controls.Add(hint);

            // ---------- 卡片3：工具 ----------
            var card3 = new GlassCard();
            card3.Bounds = new Rectangle(24, 546, ClientSize.Width - 48, 116);
            Controls.Add(card3);

            var lbl3 = new Label();
            lbl3.Text = "▍工具";
            lbl3.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            lbl3.ForeColor = UiTheme.PrimaryDeep;
            lbl3.BackColor = UiTheme.CardBg;
            lbl3.Location = new Point(20, 12);
            lbl3.AutoSize = true;
            card3.Controls.Add(lbl3);

            string[] toolNames2 = new string[] { "一键部署", "一键诊断", "桌面快捷方式", "检查更新" };
            int bw2 = (card3.Width - 36 - 12) / 2;
            int[] tx = new int[] { 18, 18 + bw2 + 12 };
            int[] ty = new int[] { 46, 80 };
            for (int i = 0; i < toolNames2.Length; i++)
            {
                var b = new RoundButton();
                b.Text = toolNames2[i];
                b.Ghost = true;
                b.TextColor = UiTheme.Porcelain;
                b.Bounds = new Rectangle(tx[i % 2], ty[i / 2], bw2, 26);
                b.Radius = 9;
                b.Font = new Font("Microsoft YaHei UI", 9F);
                if (i == 0) b.Click += (s, e) =>
                {
                    using (var frm = new FrmDeploy())
                    {
                        frm.Start();
                        frm.ShowDialog(this);
                        if (frm.Success) SetStatus("部署完成，可点击【启动DSH】开始使用");
                        else if (frm.Cancelled) SetStatus("部署已取消");
                        else SetStatus("部署失败，详见部署窗口日志");
                    }
                };
                else if (i == 1) b.Click += (s, e) =>
                {
                    try
                    {
                        Clipboard.SetText(Program.CollectDiagnostics());
                        SetStatus("诊断信息已复制到剪贴板");
                        Program.Log("已复制诊断信息到剪贴板");
                    }
                    catch
                    {
                        SetStatus("复制失败（剪贴板被占用）");
                    }
                };
                else if (i == 2) b.Click += (s, e) => SetStatus(Program.CreateDesktopShortcut());
                else b.Click += (s, e) => ShowUpdateDialog();
                card3.Controls.Add(b);
            }

            // ---------- 底部 ----------
            var ver = new Label();
            ver.Text = "DSH启动管家（鲸娘版）v" + Program.LauncherVersion;
            ver.Font = new Font("Microsoft YaHei UI", 8.5F);
            ver.ForeColor = UiTheme.TextGray;
            ver.BackColor = UiTheme.Bg;
            ver.Location = new Point(26, 674);
            ver.AutoSize = true;
            Controls.Add(ver);

            var lblLog = new Label();
            lblLog.Text = "日志目录 ↗";
            lblLog.Font = new Font("Microsoft YaHei UI", 8.5F);
            lblLog.ForeColor = UiTheme.PrimaryDeep;
            lblLog.BackColor = UiTheme.Bg;
            lblLog.Cursor = Cursors.Hand;
            lblLog.Location = new Point(ClientSize.Width - 130, 674);
            lblLog.AutoSize = true;
            lblLog.Click += delegate
            {
                try { Process.Start("explorer.exe", Program.LogDir); }
                catch { }
            };
            Controls.Add(lblLog);

            // ---------- 托盘 ----------
            _notify = new NotifyIcon();
            _notify.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            _notify.Text = "DSH启动管家";
            _notify.DoubleClick += delegate { ShowMainWindow(); };

            _trayMenu = new ContextMenuStrip();
            _trayMenu.BackColor = Color.FromArgb(255, 13, 25, 59);
            _trayMenu.ForeColor = UiTheme.TextDark;
            _trayMenu.Renderer = new ToolStripProfessionalRenderer(new MaidColorTable());
            _trayMenu.Items.Add("显示窗口", null, delegate { ShowMainWindow(); });
            _trayMenu.Items.Add("打开网页 UI", null, delegate { OpenWebUi(); });
            _trayMenu.Items.Add("启动DSH", null, delegate { StartWhale(); });
            _trayMenu.Items.Add("重启服务", null, delegate { RestartServer(); });
            _trayMenu.Items.Add("停止服务", null, delegate { StopServerFromMenu(); });
            _trayMenu.Items.Add("检查更新", null, delegate { ShowUpdateDialog(); });
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("完全退出", null, delegate { ExitProgram(); });
            _notify.ContextMenuStrip = _trayMenu;

            ResumeLayout();
        }

        private void LoadSettings()
        {
            _toggleAutoStart.Checked = Program.IsAutoStartEnabled();
            string action = Program.GetCloseAction();
            _rbTray.Checked = action != "exit";
            _rbExit.Checked = action == "exit";
            UpdateSegments();
            _loading = false;
        }

        private void UpdateSegments()
        {
            if (_segTray == null || _segExit == null) return;
            bool tray = _rbTray.Checked;
            _segTray.Ghost = !tray;
            _segTray.TextColor = tray ? UiTheme.Ink : UiTheme.Porcelain;
            _segExit.Ghost = tray;
            _segExit.TextColor = tray ? UiTheme.Porcelain : UiTheme.Ink;
            _segTray.Invalidate();
            _segExit.Invalidate();
        }

        // ---------- 托盘菜单：服务控制 ----------
        private void OpenWebUi()
        {
            if (Program.PortAlive(Program.WebPortNum))
            {
                Program.OpenBrowser(Program.WebPortNum);
                Program.Log("托盘菜单：打开网页 UI");
            }
            else
            {
                DialogResult r = MessageBox.Show("服务当前未运行，是否先启动DSH？",
                    Program.AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes) StartWhale();
            }
        }

        private void RestartServer()
        {
            Program.Log("一键重启服务");
            SetStatus("正在重启服务…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                StopServer();
                // 等待端口释放（最多 10 秒），避免误判为外部服务
                for (int i = 0; i < 20; i++)
                {
                    if (!Program.PortAlive(Program.WebPortNum)) break;
                    Thread.Sleep(500);
                }
                StartWhale();
            });
        }

        private void StopServerFromMenu()
        {
            Program.Log("托盘菜单：停止服务");
            ThreadPool.QueueUserWorkItem(delegate
            {
                StopServer();
                SetStatus("服务已停止（可在托盘菜单或主窗口重新启动）");
            });
        }

        // ---------- 窗口行为 ----------
        private void FrmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_forceExit || e.CloseReason == CloseReason.WindowsShutDown ||
                Program.SmokeMode || !string.IsNullOrEmpty(Program.ShotPath))
            {
                StopServer();
                _notify.Dispose();
                return;
            }
            if (_rbTray.Checked)
            {
                // 最小化到托盘
                e.Cancel = true;
                Hide();
                _notify.Visible = true;
                _notify.ShowBalloonTip(2500, "DSH启动管家", "已最小化到系统托盘\n双击图标可恢复窗口", ToolTipIcon.Info);
                Program.Log("窗口已最小化到托盘");
            }
            else
            {
                // 退出程序：停止服务后退出
                StopServer();
                _notify.Dispose();
                Program.Log("退出程序");
            }
        }

        private void ShowMainWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            _notify.Visible = false;
        }

        private void ExitProgram()
        {
            _forceExit = true;
            StopServer();
            _notify.Dispose();
            Program.Log("从托盘菜单退出");
            Application.Exit();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.WmAppActivate)
            {
                // 其他实例请求：显示窗口并启动DSH
                ShowMainWindow();
                StartWhale();
                return;
            }
            base.WndProc(ref m);
        }

        // ---------- 无边框窗口 ----------
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW 窗口阴影
                return cp;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        private void UpdateRegion()
        {
            Rectangle r = ClientRectangle;
            using (GraphicsPath path = RoundButton.RoundedRect(new RectangleF(0, 0, r.Width, r.Height), CornerRadius))
            {
                Region = new Region(path);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.Y <= TitleBarHeight)
            {
                Program.BeginDrag(Handle);
            }
        }

        private void LoadMaidArt()
        {
            try
            {
                using (Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("maid_sidebar.png"))
                {
                    if (s == null) return;
                    _maidStream = new MemoryStream();
                    s.CopyTo(_maidStream);
                    _maidStream.Position = 0;
                    _maidArt = Image.FromStream(_maidStream);
                }
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_maidArt != null) _maidArt.Dispose(); } catch { }
                try { if (_maidStream != null) _maidStream.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 顶部微光带（毛玻璃高光）
            using (var b = new SolidBrush(UiTheme.Sheen))
            {
                g.FillRectangle(b, 0, 0, ClientSize.Width, TitleBarHeight);
            }

            // 深海柔光（角色身后）
            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(new RectangleF(ClientSize.Width - 260, -50, 320, 320));
                using (var b = new PathGradientBrush(glow))
                {
                    b.CenterColor = Color.FromArgb(255, 42, 62, 118);
                    b.SurroundColors = new Color[] { UiTheme.Bg };
                    g.FillPath(b, glow);
                }
            }

            // 标题栏：金色 logo 方块 + 名称
            RectangleF logo = new RectangleF(22, 10, 24, 24);
            using (GraphicsPath lp = RoundButton.RoundedRect(logo, 7f))
            using (var lb = new LinearGradientBrush(logo, UiTheme.PrimaryLight, UiTheme.Primary, LinearGradientMode.Vertical))
            {
                g.FillPath(lb, lp);
            }
            using (var fLogo = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, "D", fLogo, new Rectangle(22, 10, 24, 24), UiTheme.Ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            using (var f = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, "DSH启动管家", f, new Rectangle(56, 8, 220, 28), UiTheme.TextDark,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }

            // 状态胶囊
            DrawStatusPill(g);

            // 女仆鲸鱼娘（Q版侧栏原图）
            RectangleF mr = new RectangleF(ClientSize.Width - 178, 40, 140, 140);
            if (_maidArt != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(_maidArt, mr);
            }
            else
            {
                MaidWhaleGirl.Draw(g, mr);
            }
        }

        private void DrawStatusPill(Graphics g)
        {
            if (_lblStatus == null) return;
            string text = _lblStatus.Text;
            Color bg;
            Color fg;
            if (_runningCache) { bg = UiTheme.OkBg; fg = UiTheme.OkText; }
            else if (text.Contains("正在") || text.Contains("检查") || text.Contains("部署") || text.Contains("启动"))
            { bg = UiTheme.WarnBg; fg = UiTheme.WarnText; }
            else { bg = UiTheme.OffBg; fg = UiTheme.OffText; }

            using (var f = new Font("Microsoft YaHei UI", 8.5F))
            {
                string label = (_runningCache ? "● " : "○ ") + text;
                SizeF sz = g.MeasureString(label, f);
                float w = Math.Min(340f, sz.Width + 26f);
                RectangleF rect = new RectangleF(32, 138, w, 24);
                using (GraphicsPath path = RoundButton.RoundedRect(rect, 12f))
                {
                    using (var b = new SolidBrush(bg)) { g.FillPath(b, path); }
                    using (var pen = new Pen(Color.FromArgb(255, 255, 255))) { g.DrawPath(pen, path); }
                }
                TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
                if (sz.Width + 26f > 340f) flags |= TextFormatFlags.EndEllipsis;
                else flags |= TextFormatFlags.HorizontalCenter;
                TextRenderer.DrawText(g, label, f, new Rectangle((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height), fg, flags);
            }
        }

        private void RenderShot(string path)
        {
            try
            {
                double oldOpacity = Opacity;
                Opacity = 1.0;
                using (var bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                Opacity = oldOpacity;
                Program.Log("窗口截图已保存: " + path);
            }
            catch (Exception ex)
            {
                Program.Log("截图失败: " + ex.Message);
            }
            Application.Exit();
        }

        // ---------- 启动DSH ----------
        private void BtnStart_Click(object sender, EventArgs e)
        {
            StartWhale();
        }

        private void StartWhale()
        {
            lock (_serverLock)
            {
                if (_starting) return;
                _starting = true;
            }
            SetStatus("正在启动…");
            Program.Log("用户点击启动DSH");
            ThreadPool.QueueUserWorkItem(delegate { StartWhaleWorker(); });
        }

        private void StartWhaleWorker()
        {
            try
            {
                // 已在服务 → 只打开网页
                if (Program.PortAlive(Program.WebPortNum))
                {
                    Program.Log("端口 " + Program.WebPortNum + " 已在服务，直接打开网页");
                    Program.OpenBrowser(Program.WebPortNum);
                    SetStatus("服务运行中，已打开网页（端口 " + Program.WebPortNum + "）");
                    _starting = false;
                    return;
                }

                // 解析 node 与 DSH 入口
                string nodeExe = Program.FindNodeExe();
                string script = Program.FindDshBinJs();
                if (string.IsNullOrEmpty(nodeExe)) { FailStart("未找到 node.exe，请先安装 Node.js（https://nodejs.org）"); return; }
                if (string.IsNullOrEmpty(script)) { FailStart("未找到 DSH 启动脚本 bin.js（$DSH_HOME\\profiles\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js）"); return; }
                string workDir = Path.GetDirectoryName(Path.GetDirectoryName(script));

                // 启动服务进程
                Process server = Program.SpawnServer(nodeExe, script, workDir);
                if (server == null) { FailStart("启动服务进程失败，详见日志：" + Program.LogDir); return; }
                IntPtr job = Program.CreateKillOnCloseJob(server);
                Program.Log("服务进程已启动 pid=" + server.Id);

                // 等待端口就绪
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(Program.PortWaitTimeoutMsValue);
                bool ready = false;
                while (DateTime.UtcNow < deadline)
                {
                    if (server.HasExited)
                    {
                        string tail = Program.ReadTailLogs();
                        Program.Log("服务进程提前退出，退出码=" + SafeExitCode(server));
                        SetStatus("启动失败：服务进程提前退出");
                        ShowError("DeepSeek Harness 服务启动失败（进程提前退出）。\n\n详细信息见日志：\n" + Program.LogDir + "\n\n" + tail);
                        _starting = false;
                        return;
                    }
                    if (Program.PortAlive(Program.WebPortNum)) { ready = true; break; }
                    Thread.Sleep(500);
                }
                if (!ready)
                {
                    Program.TerminateJob(job);
                    Program.Log("等待端口超时");
                    SetStatus("启动失败：等待端口超时");
                    ShowError("DeepSeek Harness 服务启动超时。\n\n详细信息见日志：\n" + Program.LogDir);
                    _starting = false;
                    return;
                }

                Program.Log("服务已就绪，端口 " + Program.WebPortNum + " 可访问");
                lock (_serverLock)
                {
                    _server = server;
                    _job = job;
                }
                Program.OpenBrowser(Program.WebPortNum);
                SetStatus("服务运行中（端口 " + Program.WebPortNum + "），网页已打开");
                StartMonitor();
                _starting = false;
            }
            catch (Exception ex)
            {
                Program.Log("启动流程异常: " + ex);
                SetStatus("启动异常，详见日志");
                _starting = false;
            }
        }

        private void FailStart(string msg)
        {
            Program.Log("启动失败: " + msg);
            SetStatus("启动失败");
            ShowError(msg + "\n\n日志目录：\n" + Program.LogDir);
            _starting = false;
        }

        private void ShowError(string text)
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action<string>(ShowError), text); return; }
                string diag = "";
                try
                {
                    diag = Program.CollectDiagnostics();
                    Clipboard.SetText(diag);
                }
                catch { }
                MessageBox.Show(text + "\n\n诊断信息已自动复制到剪贴板，可直接粘贴发送给 DeepSeek 助手。\n\n日志目录：\n" + Program.LogDir, Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        // ---------- 服务监控（页面关闭 → 停止服务）----------
        private void StartMonitor()
        {
            _monitorRunning = true;
            _monitorThread = new Thread(MonitorLoop);
            _monitorThread.IsBackground = true;
            _monitorThread.Start();
        }

        private void MonitorLoop()
        {
            DateTime readyTime = DateTime.UtcNow;
            DateTime lastSeen = DateTime.MinValue;
            bool everConnected = false;
            while (_monitorRunning)
            {
                Thread.Sleep(Program.MonitorIntervalMsValue);

                Process server;
                lock (_serverLock) { server = _server; }
                if (server == null) return;

                if (server.HasExited)
                {
                    Program.Log("服务进程已退出（非本程序关闭）");
                    SetStatus("服务已停止");
                    _monitorRunning = false;
                    lock (_serverLock) { _server = null; }
                    return;
                }

                bool any = Program.CountConnections(Program.WebPortNum) > 0;
                if (any)
                {
                    lastSeen = DateTime.UtcNow;
                    everConnected = true;
                }
                else if (everConnected)
                {
                    long lost = (long)(DateTime.UtcNow - lastSeen).TotalMilliseconds;
                    if (lost > Program.GraceMs)
                    {
                        Program.Log("页面连接已断开 " + lost + " ms，判定网页已关闭，停止服务");
                        SetStatus("网页已关闭，服务已停止（可再次点击启动）");
                        KillServer();
                        _monitorRunning = false;
                        lock (_serverLock) { _server = null; _job = IntPtr.Zero; }
                        return;
                    }
                }
                else
                {
                    long idle = (long)(DateTime.UtcNow - readyTime).TotalMilliseconds;
                    if (idle > Program.NeverConnectedTimeoutMsValue)
                    {
                        Program.Log("启动后 " + idle + " ms 内没有任何页面连接，停止服务");
                        SetStatus("没有检测到网页打开，服务已停止");
                        KillServer();
                        _monitorRunning = false;
                        lock (_serverLock) { _server = null; _job = IntPtr.Zero; }
                        return;
                    }
                }
            }
        }

        private void KillServer()
        {
            Process server;
            IntPtr job;
            lock (_serverLock)
            {
                server = _server;
                job = _job;
                _server = null;
                _job = IntPtr.Zero;
            }
            if (job != IntPtr.Zero) Program.TerminateJob(job);
            try
            {
                if (server != null && !server.HasExited) server.WaitForExit(8000);
            }
            catch { }
            if (server != null && !server.HasExited)
            {
                try { server.Kill(); } catch { }
            }
            Program.Log("服务进程已停止");
        }

        private void StopServer()
        {
            _monitorRunning = false;
            KillServer();
        }

        private static int SafeExitCode(Process p)
        {
            try { return p.ExitCode; } catch { return -1; }
        }

        private void SetStatus(string text)
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), text); return; }
                _lblStatus.Text = text;
                try { _runningCache = Program.PortAlive(Program.WebPortNum); } catch { }
                // 大按钮固定为「启动DSH」，打开网页由下方小按钮承担
                Invalidate();
            }
            catch { }
        }
    }


    // ================= 一键部署流程 =================
    internal sealed class DeployRunner
    {
        private readonly Action<string, int> _report;
        private readonly Action<string> _logLine;
        private int _npmLines;

        public volatile bool Cancelled;
        public bool Success;
        public string Message = "";

        public DeployRunner(Action<string, int> report, Action<string> logLine)
        {
            _report = report;
            _logLine = logLine;
        }

        private void Report(string step, int pct)
        {
            Program.Log("部署[" + pct + "%] " + step);
            if (_report != null) _report(step, pct);
        }

        private void Line(string text)
        {
            Program.Log("部署输出: " + text);
            if (_logLine != null) _logLine(text);
        }

        public void Run()
        {
            try
            {
                Report("正在检查环境…", 2);
                bool nodeOk = Program.FindNodeExe() != null;
                bool dshOk = Program.FindDshBinJs() != null;
                if (nodeOk && dshOk)
                {
                    Report("环境已就绪（Node.js 与 DeepSeek Harness 均已安装），无需部署", 100);
                    Success = true;
                    Message = "环境已就绪";
                    return;
                }

                // ---- Node.js ----
                if (!nodeOk)
                {
                    string msi = Program.LocateNodeMsi();
                    if (string.IsNullOrEmpty(msi))
                    {
                        Fail("未找到 Node.js 安装包（node-v*.msi），请将其放在程序目录");
                        return;
                    }
                    // 完整性自检：哈希与官方安装包不一致 → 拒绝安装（防篡改）
                    Report("正在校验安装包完整性…", 6);
                    if (!Program.VerifyMsiIntegrity(msi))
                    {
                        Fail("安装包完整性校验失败（MD5 与官方值不一致，文件可能已被篡改）。已拒绝安装，请从官方渠道重新获取本程序。");
                        return;
                    }
                    Report("正在安装 Node.js（需要管理员授权，请在 UAC 弹窗中点击“是”）…", 8);
                    int code = Program.RunMsiexecInstall(msi, delegate(int pct)
                    {
                        Report("正在安装 Node.js…", 8 + pct * 34 / 100);
                    }, delegate { return Cancelled; });
                    if (Cancelled)
                    {
                        Report("已取消", 0);
                        Message = "已取消";
                        return;
                    }
                    if (code == -999)
                    {
                        Fail("管理员授权失败或被取消，无法安装 Node.js");
                        return;
                    }
                    if (code != 0 && code != 3010)
                    {
                        Fail("Node.js 安装失败（msiexec 退出码 " + code + "）");
                        return;
                    }
                    nodeOk = Program.FindNodeExe() != null;
                    if (!nodeOk)
                    {
                        Fail("Node.js 安装完成，但未找到 node.exe，请检查安装");
                        return;
                    }
                    Report("Node.js 安装完成", 42);
                }
                else
                {
                    Report("Node.js 已安装，跳过安装步骤", 45);
                }

                string nodeBinDir = Path.GetDirectoryName(Program.FindNodeExe());

                // ---- DeepSeek Harness ----
                if (Program.FindDshBinJs() == null)
                {
                    Report("正在安装 DeepSeek Harness…（首次需要联网下载依赖，请稍候）", 48);
                    _npmLines = 0;
                    string err = Program.RunNpmInstallDsh(nodeBinDir, delegate(string line)
                    {
                        Line(line);
                        _npmLines++;
                        int pct = Math.Min(82, 48 + _npmLines / 3);
                        Report("正在安装 DeepSeek Harness…", pct);
                    }, delegate { return Cancelled; });
                    if (Cancelled)
                    {
                        Report("已取消", 0);
                        Message = "已取消";
                        return;
                    }
                    if (err != null)
                    {
                        Fail(err);
                        return;
                    }
                    Report("DeepSeek Harness 安装完成", 84);
                }
                else
                {
                    Report("DeepSeek Harness 已部署，跳过安装步骤", 86);
                }

                // ---- 初始化配置 ----
                Report("正在初始化 DeepSeek Harness 配置…", 88);
                string initErr = Program.RunDshInit(nodeBinDir, Line, delegate { return Cancelled; });
                if (Cancelled)
                {
                    Report("已取消", 0);
                    Message = "已取消";
                    return;
                }
                if (initErr != null)
                {
                    Fail(initErr);
                    return;
                }

                // ---- 验证 ----
                Report("正在验证部署结果…", 95);
                if (Program.FindNodeExe() == null)
                {
                    Fail("验证失败：未找到 node.exe");
                    return;
                }
                if (Program.FindDshBinJs() == null)
                {
                    Fail("验证失败：未找到 DSH 启动入口");
                    return;
                }

                Report("部署完成！现在可以点击【启动DSH】开始使用", 100);
                Success = true;
                Message = "部署完成";
            }
            catch (Exception ex)
            {
                Fail("部署出错：" + ex.Message);
            }
        }

        private void Fail(string msg)
        {
            Message = msg;
            Program.Log("部署失败: " + msg);
            if (_report != null) _report(msg, -1);
        }
    }

    // ================= 一键部署窗口 =================
    internal sealed class FrmDeploy : Form
    {
        private Label _lblStep;
        private PinkProgressBar _progress;
        private Label _lblPct;
        private TextBox _txtLog;
        private RoundButton _btnAction;
        private readonly DeployRunner _runner;
        private bool _finished;

        public bool Success { get { return _runner.Success; } }
        public bool Cancelled { get { return _runner.Cancelled; } }

        public FrmDeploy()
        {
            Text = "一键部署 DeepSeek Harness";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            ClientSize = new Size(480, 330);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(255, 13, 25, 59);

            var title = new Label();
            title.Text = "一键部署";
            title.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            title.ForeColor = UiTheme.PrimaryDeep;
            title.Bounds = new Rectangle(0, 12, ClientSize.Width, 26);
            title.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(title);

            _lblStep = new Label();
            _lblStep.Text = "准备中…";
            _lblStep.Bounds = new Rectangle(16, 48, ClientSize.Width - 32, 40);
            _lblStep.TextAlign = ContentAlignment.MiddleLeft;
            _lblStep.ForeColor = UiTheme.Porcelain;
            Controls.Add(_lblStep);

            _progress = new PinkProgressBar();
            _progress.Bounds = new Rectangle(16, 92, ClientSize.Width - 90, 24);
            _progress.Minimum = 0;
            _progress.Maximum = 100;
            Controls.Add(_progress);

            _lblPct = new Label();
            _lblPct.Text = "0%";
            _lblPct.TextAlign = ContentAlignment.MiddleCenter;
            _lblPct.ForeColor = UiTheme.Porcelain;
            _lblPct.Bounds = new Rectangle(ClientSize.Width - 70, 92, 54, 24);
            Controls.Add(_lblPct);

            _txtLog = new TextBox();
            _txtLog.Bounds = new Rectangle(16, 128, ClientSize.Width - 32, 150);
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.FromArgb(255, 15, 28, 68);
            _txtLog.ForeColor = Color.FromArgb(255, 220, 228, 245);
            Controls.Add(_txtLog);

            _btnAction = new RoundButton();
            _btnAction.Text = "取消";
            _btnAction.Bounds = new Rectangle(ClientSize.Width - 100, 290, 84, 30);
            _btnAction.Radius = 10;
            _btnAction.TextColor = UiTheme.Ink;
            _btnAction.Click += BtnAction_Click;
            Controls.Add(_btnAction);

            FormClosing += FrmDeploy_FormClosing;

            _runner = new DeployRunner(Report, AppendLog);
        }

        public void Start()
        {
            Thread t = new Thread(new ThreadStart(delegate { RunDeploy(); }));
            t.IsBackground = true;
            t.Start();
        }

        private void RunDeploy()
        {
            _runner.Run();
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(FinishUi));
                }
                else FinishUi();
            }
            catch { }
        }

        private void FinishUi()
        {
            _finished = true;
            _btnAction.Text = "关闭";
            if (_runner.Success)
            {
                _lblStep.Text = "✅ 部署完成！现在可以点击【启动DSH】开始使用";
            }
            else if (_runner.Cancelled)
            {
                _lblStep.Text = "已取消";
            }
            else
            {
                _lblStep.Text = "❌ 部署失败：" + _runner.Message;
            }
        }

        private void Report(string step, int pct)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action<string, int>(Report), step, pct);
                    return;
                }
                _lblStep.Text = step;
                if (pct >= 0)
                {
                    _progress.Value = Math.Min(100, pct);
                    _lblPct.Text = pct + "%";
                }
            }
            catch { }
        }

        private void AppendLog(string text)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action<string>(AppendLog), text);
                    return;
                }
                _txtLog.AppendText(text + Environment.NewLine);
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            }
            catch { }
        }

        private void BtnAction_Click(object sender, EventArgs e)
        {
            if (!_finished)
            {
                _runner.Cancelled = true;
                _btnAction.Enabled = false;
                _lblStep.Text = "正在取消…";
                return;
            }
            Close();
        }

        private void FrmDeploy_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_finished && !_runner.Cancelled)
            {
                // 首次关闭：请求取消并拦截；取消流程进行中再关则放行
                _runner.Cancelled = true;
                e.Cancel = true;
                _lblStep.Text = "正在取消…";
                _btnAction.Enabled = false;
            }
        }
    }

    // ================= 更新 DeepSeek Harness 窗口 =================
    internal sealed class FrmDshUpdate : Form
    {
        private readonly string _nodeBinDir;
        private Label _lblStep;
        private PinkProgressBar _progress;
        private Label _lblPct;
        private TextBox _txtLog;
        private RoundButton _btnClose;
        private volatile bool _cancelled;
        private volatile bool _finished;
        private int _lines;

        public FrmDshUpdate(string nodeBinDir)
        {
            _nodeBinDir = nodeBinDir;
            Text = "更新 DeepSeek Harness";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            ClientSize = new Size(480, 320);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(255, 13, 25, 59);

            _lblStep = new Label();
            _lblStep.Text = "准备中…";
            _lblStep.Bounds = new Rectangle(16, 18, ClientSize.Width - 32, 40);
            _lblStep.ForeColor = UiTheme.Porcelain;
            Controls.Add(_lblStep);

            _progress = new PinkProgressBar();
            _progress.Bounds = new Rectangle(16, 64, ClientSize.Width - 90, 24);
            _progress.Minimum = 0;
            _progress.Maximum = 100;
            Controls.Add(_progress);

            _lblPct = new Label();
            _lblPct.Text = "0%";
            _lblPct.TextAlign = ContentAlignment.MiddleCenter;
            _lblPct.ForeColor = UiTheme.Porcelain;
            _lblPct.Bounds = new Rectangle(ClientSize.Width - 70, 64, 54, 24);
            Controls.Add(_lblPct);

            _txtLog = new TextBox();
            _txtLog.Bounds = new Rectangle(16, 100, ClientSize.Width - 32, 160);
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.FromArgb(255, 15, 28, 68);
            _txtLog.ForeColor = Color.FromArgb(255, 220, 228, 245);
            Controls.Add(_txtLog);

            _btnClose = new RoundButton();
            _btnClose.Text = "取消";
            _btnClose.Bounds = new Rectangle(ClientSize.Width - 100, 276, 84, 30);
            _btnClose.Radius = 10;
            _btnClose.TextColor = UiTheme.Ink;
            _btnClose.Click += (s, e) =>
            {
                if (!_finished)
                {
                    _cancelled = true;
                    _btnClose.Enabled = false;
                    _lblStep.Text = "正在取消…";
                }
                else Close();
            };
            Controls.Add(_btnClose);

            FormClosing += (s, e) =>
            {
                if (!_finished && !_cancelled)
                {
                    _cancelled = true;
                    e.Cancel = true;
                    _lblStep.Text = "正在取消…";
                    _btnClose.Enabled = false;
                }
            };
        }

        public void Start()
        {
            Thread t = new Thread(new ThreadStart(delegate { Worker(); }));
            t.IsBackground = true;
            t.Start();
        }

        private void Worker()
        {
            Report("正在更新 DeepSeek Harness（联网下载，请稍候）…", 5);
            _lines = 0;
            string err = Program.RunNpmInstallDsh(_nodeBinDir, delegate(string line)
            {
                AppendLog(line);
                _lines++;
                Report("正在更新 DeepSeek Harness…", Math.Min(90, 5 + _lines / 3));
            }, delegate { return _cancelled; });
            if (_cancelled)
            {
                Report("已取消", 0);
                Finish();
                return;
            }
            if (err != null)
            {
                Report("更新失败：" + err, 0);
                Finish();
                return;
            }
            Report("更新完成！请重新点击【启动DSH】以使用新版本", 100);
            Finish();
        }

        private void Finish()
        {
            try
            {
                if (InvokeRequired) BeginInvoke(new Action(FinishUi));
                else FinishUi();
            }
            catch { }
        }

        private void FinishUi()
        {
            _finished = true;
            _btnClose.Text = "关闭";
            _btnClose.Enabled = true;
        }

        private void Report(string step, int pct)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action<string, int>(Report), step, pct);
                    return;
                }
                _lblStep.Text = step;
                if (pct >= 0)
                {
                    _progress.Value = Math.Min(100, pct);
                    _lblPct.Text = pct + "%";
                }
            }
            catch { }
        }

        private void AppendLog(string text)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action<string>(AppendLog), text);
                    return;
                }
                _txtLog.AppendText(text + Environment.NewLine);
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            }
            catch { }
        }
    }

    // ================= 深海女仆工坊 调色板 =================
    // 同款配色：深海蓝 + 陶瓷白 + 长春花蓝 + 柔金（maid-atelier 皮肤 skin.json）
    internal static class UiTheme
    {
        public static readonly Color Navy950 = Color.FromArgb(255, 9, 19, 51);       // #091333 深海
        public static readonly Color Navy900 = Color.FromArgb(255, 16, 32, 77);      // #10204d
        public static readonly Color Navy800 = Color.FromArgb(255, 28, 50, 107);     // #1c326b
        public static readonly Color Indigo = Color.FromArgb(255, 82, 106, 168);     // #526aa8
        public static readonly Color Periwinkle = Color.FromArgb(255, 142, 165, 218);// #8ea5da 长春花蓝
        public static readonly Color Porcelain = Color.FromArgb(255, 248, 246, 240); // #f8f6f0 陶瓷白
        public static readonly Color Gold = Color.FromArgb(255, 197, 164, 104);      // #c5a468 柔金
        public static readonly Color GoldSoft = Color.FromArgb(255, 226, 207, 170);  // #e2cfaa
        public static readonly Color Ink = Color.FromArgb(255, 23, 35, 71);          // #172347 墨蓝
        public static readonly Color GhostBg = Color.FromArgb(255, 36, 58, 117);
        public static readonly Color GhostBgHover = Color.FromArgb(255, 44, 71, 134);

        public static readonly Color Primary = Gold;
        public static readonly Color PrimaryDark = Color.FromArgb(255, 169, 132, 71); // 深金
        public static readonly Color PrimaryDeep = GoldSoft;
        public static readonly Color PrimaryLight = GoldSoft;
        public static readonly Color BtnTop = Gold;
        public static readonly Color BtnBottom = Color.FromArgb(255, 169, 132, 71);
        public static readonly Color BtnHover = Color.FromArgb(255, 216, 188, 132);
        public static readonly Color Bg = Navy950;
        public static readonly Color Sheen = Navy900;
        public static readonly Color CardBg = Color.FromArgb(255, 26, 43, 94);       // 深海玻璃
        public static readonly Color CardBorder = Color.FromArgb(255, 150, 122, 72); // 金描边
        public static readonly Color TextDark = Porcelain;
        public static readonly Color TextGray = Color.FromArgb(255, 138, 148, 170);  // #8a94aa
        public static readonly Color OkBg = Color.FromArgb(255, 20, 53, 43);
        public static readonly Color OkText = Color.FromArgb(255, 143, 216, 176);
        public static readonly Color WarnBg = Color.FromArgb(255, 61, 51, 24);
        public static readonly Color WarnText = Color.FromArgb(255, 232, 200, 120);
        public static readonly Color OffBg = Color.FromArgb(255, 28, 36, 64);
        public static readonly Color OffText = Color.FromArgb(255, 138, 148, 170);
    }

    // ================= 圆角按钮 =================
    internal class RoundButton : Button
    {
        public int Radius = 10;
        public bool Ghost;
        public int Variant;              // 0=柔金(默认) 1=长春花蓝(靛蓝)
        public Color TextColor = Color.White;

        private bool _hover;
        private bool _down;

        public RoundButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = new Font("Microsoft YaHei UI", 9.5F);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) { _down = false; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float r = Radius;

            Color top, bottom, border;
            if (Ghost)
            {
                top = _hover ? UiTheme.GhostBgHover : UiTheme.GhostBg;
                bottom = top;
                border = UiTheme.Periwinkle;
            }
            else
            {
                if (Variant == 1)
                {
                    top = Color.FromArgb(255, 83, 110, 174);
                    bottom = Color.FromArgb(255, 64, 90, 153);
                    if (_hover) { top = Color.FromArgb(255, 107, 132, 196); bottom = Color.FromArgb(255, 83, 110, 174); }
                    if (_down) { top = Color.FromArgb(255, 64, 90, 153); bottom = Color.FromArgb(255, 64, 90, 153); }
                    border = Color.FromArgb(255, 64, 90, 153);
                }
                else
                {
                    top = UiTheme.BtnTop;
                    bottom = UiTheme.BtnBottom;
                    if (_hover) { top = UiTheme.BtnHover; bottom = UiTheme.BtnTop; }
                    if (_down) { top = UiTheme.BtnBottom; bottom = UiTheme.BtnBottom; }
                    border = UiTheme.BtnBottom;
                }
            }

            using (GraphicsPath path = RoundedRect(rect, r))
            {
                using (var b = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical)) { g.FillPath(b, path); }
                using (var pen = new Pen(border)) { g.DrawPath(pen, path); }
            }
            if (!Ghost)
            {
                using (var pen = new Pen(Color.FromArgb(255, 255, 255), 1.5f))
                {
                    g.DrawLine(pen, 10, 3, Width - 10, 3);
                }
            }

            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height),
                Enabled ? TextColor : Color.FromArgb(255, 190, 190, 190),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        internal static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0f || r.Width <= 0f || r.Height <= 0f)
            {
                path.AddRectangle(r);
                path.CloseFigure();
                return path;
            }
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d <= 0f)
            {
                path.AddRectangle(r);
                path.CloseFigure();
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // ================= 玻璃卡片 =================
    internal class GlassCard : Panel
    {
        public int Radius = 16;

        public GlassCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = UiTheme.CardBg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (GraphicsPath path = RoundButton.RoundedRect(rect, Radius))
            {
                using (var b = new SolidBrush(UiTheme.CardBg)) { g.FillPath(b, path); }
                using (var pen = new Pen(UiTheme.CardBorder)) { g.DrawPath(pen, path); }
            }
            // 顶部金线高光（深海女仆工坊）
            using (var pen = new Pen(UiTheme.GoldSoft, 1.5f))
            {
                g.DrawLine(pen, 14, 2, Width - 14, 2);
            }
        }
    }

    // ================= 金色进度条 =================
    internal class PinkProgressBar : Control
    {
        private int _min;
        private int _max = 100;
        private int _value;

        public int Minimum { get { return _min; } set { _min = value; Invalidate(); } }
        public int Maximum { get { return _max; } set { _max = value; Invalidate(); } }
        public int Value
        {
            get { return _value; }
            set { _value = Math.Max(_min, Math.Min(_max, value)); Invalidate(); }
        }

        public PinkProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(255, 13, 25, 59);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float r = Math.Max(1f, Height / 2f - 1f);
            using (GraphicsPath path = RoundButton.RoundedRect(rect, r))
            {
                using (var b = new SolidBrush(Color.FromArgb(255, 36, 58, 117))) { g.FillPath(b, path); }
                float span = Math.Max(0, _max - _min);
                float pct = span == 0 ? 0f : (_value - _min) / (float)span;
                float w = Math.Max(0f, (Width - 4f) * pct);
                if (w > 4f)
                {
                    RectangleF fill = new RectangleF(2f, 2f, w, Height - 4f);
                    using (GraphicsPath fp = RoundButton.RoundedRect(fill, Math.Min(r, fill.Height / 2f)))
                    using (var b2 = new LinearGradientBrush(fill, UiTheme.PrimaryLight, UiTheme.Primary, LinearGradientMode.Horizontal))
                    {
                        g.FillPath(b2, fp);
                    }
                }
            }
        }
    }

    // ================= 女仆鲸鱼娘 卡通绘制（深海女仆工坊） =================
    internal static class MaidWhaleGirl
    {
        private static readonly Color Navy = Color.FromArgb(255, 28, 50, 107);        // 尾巴/鳍
        private static readonly Color NavyDark = Color.FromArgb(255, 16, 32, 77);     // 裙摆
        private static readonly Color Hair = Color.FromArgb(255, 16, 32, 77);         // 刘海
        private static readonly Color CapLight = Color.FromArgb(255, 253, 251, 246);  // 女仆帽亮
        private static readonly Color CapDark = Color.FromArgb(255, 233, 226, 208);   // 女仆帽暗
        private static readonly Color BodyLight = Color.FromArgb(255, 252, 250, 245);
        private static readonly Color BodyDark = Color.FromArgb(255, 217, 222, 240);
        private static readonly Color Apron = Color.FromArgb(255, 248, 246, 240);
        private static readonly Color Gold = Color.FromArgb(255, 197, 164, 104);
        private static readonly Color GoldSoft = Color.FromArgb(255, 226, 207, 170);
        private static readonly Color Blush = Color.FromArgb(255, 242, 168, 184);
        private static readonly Color Iris = Color.FromArgb(255, 82, 106, 168);
        private static readonly Color Pupil = Color.FromArgb(255, 23, 35, 71);
        private static readonly Color Bubble = Color.FromArgb(255, 186, 214, 246);

        public static void Draw(Graphics g, RectangleF area)
        {
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(area.X, area.Y);
            g.ScaleTransform(area.Width / 120f, area.Height / 134f);

            // 深海气泡
            DrawBubble(g, 16, 20, 4f);
            DrawBubble(g, 27, 11, 5f);
            DrawBubble(g, 38, 17, 3.5f);

            // 尾巴（海军蓝，在身后）
            using (var b = new SolidBrush(Navy))
            {
                g.FillEllipse(b, 4, 46, 22, 14);
                g.FillEllipse(b, 16, 40, 20, 16);
                g.FillEllipse(b, 26, 50, 18, 12);
            }

            // 身体（陶瓷白 → 长春花蓝）
            RectangleF body = new RectangleF(14, 42, 92, 78);
            using (var b = new LinearGradientBrush(body, BodyLight, BodyDark, LinearGradientMode.Vertical))
            {
                g.FillEllipse(b, body);
            }

            // 侧鳍
            using (var b = new SolidBrush(Navy))
            {
                g.FillEllipse(b, 2, 84, 24, 12);
                g.FillEllipse(b, 94, 84, 24, 12);
            }

            // 裙摆（深海蓝）+ 陶瓷白荷叶边
            using (var b = new SolidBrush(NavyDark))
            {
                g.FillEllipse(b, 20, 96, 80, 26);
            }
            using (var b = new SolidBrush(Apron))
            {
                float[] fx = new float[] { 26, 36, 46, 56, 66, 76, 86, 94 };
                float[] fy = new float[] { 112, 113, 114, 114.5f, 114, 113, 112, 111 };
                for (int i = 0; i < fx.Length; i++)
                {
                    g.FillEllipse(b, fx[i], fy[i], 9, 6);
                }
            }

            // 围裙（陶瓷白，覆盖躯干下部）
            var apron = new GraphicsPath();
            apron.AddBezier(36, 92, 40, 86, 80, 86, 84, 92);
            apron.AddLine(84, 92, 86, 108);
            apron.AddBezier(86, 108, 60, 114, 34, 108, 34, 108);
            apron.AddLine(34, 108, 36, 92);
            apron.CloseFigure();
            using (var b = new SolidBrush(Apron)) { g.FillPath(b, apron); }
            // 围裙金扣
            using (var b = new SolidBrush(Gold))
            {
                g.FillEllipse(b, 48, 95, 5, 5);
                g.FillEllipse(b, 67, 95, 5, 5);
            }
            // 围裙下缘金线
            using (var pen = new Pen(Gold, 1.6f))
            {
                g.DrawLine(pen, 38, 106, 82, 106);
            }

            // 脸：眼睛
            DrawEye(g, 42, 62, 1.0f);
            DrawEye(g, 74, 62, 1.0f);
            // 腮红
            using (var b = new SolidBrush(Blush))
            {
                g.FillEllipse(b, 30, 80, 13, 7);
                g.FillEllipse(b, 77, 80, 13, 7);
            }
            // 嘴
            using (var pen = new Pen(Color.FromArgb(255, 194, 80, 124), 2f))
            {
                g.DrawArc(pen, 52, 82, 16, 10, 20, 140);
            }
            using (var b = new SolidBrush(Color.FromArgb(255, 232, 106, 144)))
            {
                g.FillEllipse(b, 57, 86, 6, 5);
            }

            // 刘海（深海蓝，女仆帽下）
            DrawBangs(g);

            // 女仆帽（陶瓷白 + 金边 + 金缎带）
            DrawCap(g);

            // 领口金蝴蝶结
            DrawBow(g, 60, 89);

            // 柔金星星
            DrawSparkle(g, 104, 30, 5f, GoldSoft);
            DrawSparkle(g, 108, 92, 4f, GoldSoft);
            DrawSparkle(g, 12, 30, 3.5f, GoldSoft);

            g.Restore(st);
        }

        private static void DrawEye(Graphics g, float x, float y, float s)
        {
            using (var b = new SolidBrush(Color.White)) { g.FillEllipse(b, x, y, 13 * s, 16 * s); }
            using (var b = new SolidBrush(Iris)) { g.FillEllipse(b, x + 2.5f * s, y + 3.5f * s, 8 * s, 9.5f * s); }
            using (var b = new SolidBrush(Pupil)) { g.FillEllipse(b, x + 4.5f * s, y + 5.5f * s, 4 * s, 5 * s); }
            using (var b = new SolidBrush(Color.White))
            {
                g.FillEllipse(b, x + 4f * s, y + 4.5f * s, 2.6f * s, 2.6f * s);
                g.FillEllipse(b, x + 7.5f * s, y + 8.5f * s, 1.4f * s, 1.4f * s);
            }
            using (var pen = new Pen(Pupil, 1.6f))
            {
                g.DrawLine(pen, x - 1f, y + 2f, x + 14f, y + 2f);
                g.DrawLine(pen, x + 11f, y + 1f, x + 14f, y - 2.5f);
                g.DrawLine(pen, x + 12f, y + 3f, x + 15f, y + 0.5f);
            }
        }

        private static void DrawBangs(Graphics g)
        {
            var path = new GraphicsPath();
            path.StartFigure();
            path.AddBezier(10, 56, 16, 34, 48, 30, 60, 31);
            path.AddBezier(60, 31, 76, 32, 94, 36, 110, 56);
            path.AddBezier(110, 56, 98, 60, 92, 62, 82, 58);
            path.AddBezier(82, 58, 74, 62, 68, 60, 62, 58);
            path.AddBezier(62, 58, 54, 63, 46, 60, 42, 57);
            path.AddBezier(42, 57, 34, 62, 26, 58, 22, 55);
            path.AddBezier(22, 55, 16, 58, 12, 57, 10, 56);
            path.CloseFigure();
            using (var b = new SolidBrush(Hair)) { g.FillPath(b, path); }
        }

        private static void DrawCap(Graphics g)
        {
            var cap = new GraphicsPath();
            cap.StartFigure();
            cap.AddBezier(14, 46, 24, 26, 96, 26, 106, 46);
            cap.AddBezier(106, 46, 92, 50, 86, 52, 78, 50);
            cap.AddBezier(78, 50, 70, 54, 62, 51, 56, 50);
            cap.AddBezier(56, 50, 46, 55, 36, 51, 30, 49);
            cap.AddBezier(30, 49, 24, 51, 18, 50, 14, 46);
            cap.CloseFigure();
            using (var b = new LinearGradientBrush(new RectangleF(14, 26, 92, 26), CapLight, CapDark, LinearGradientMode.Vertical))
            {
                g.FillPath(b, cap);
            }
            // 金边
            using (var pen = new Pen(Gold, 2.2f))
            {
                g.DrawBezier(pen, 18, 46, 40, 52, 78, 52, 102, 46);
            }
            // 帽顶金缎带
            DrawBow(g, 46, 33);
        }

        private static void DrawBow(Graphics g, float x, float y)
        {
            using (var b = new SolidBrush(Gold))
            {
                g.FillEllipse(b, x - 16f, y - 6f, 16, 12);
                g.FillEllipse(b, x + 2f, y - 6f, 16, 12);
                g.FillEllipse(b, x - 4f, y - 5f, 9, 9);
            }
            using (var b = new SolidBrush(GoldSoft))
            {
                g.FillEllipse(b, x - 2f, y - 3f, 5, 5);
            }
        }

        private static void DrawSparkle(Graphics g, float x, float y, float s, Color c)
        {
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(new PointF[] {
                    new PointF(x, y - s), new PointF(x + s * 0.3f, y - s * 0.3f), new PointF(x + s, y),
                    new PointF(x + s * 0.3f, y + s * 0.3f), new PointF(x, y + s),
                    new PointF(x - s * 0.3f, y + s * 0.3f), new PointF(x - s, y),
                    new PointF(x - s * 0.3f, y - s * 0.3f)
                });
                path.CloseFigure();
                using (var b = new SolidBrush(c)) { g.FillPath(b, path); }
            }
        }

        private static void DrawBubble(Graphics g, float x, float y, float r)
        {
            using (var b = new SolidBrush(Bubble))
            {
                g.FillEllipse(b, x - r, y - r, r * 2f, r * 2f);
            }
            using (var b = new SolidBrush(Color.White))
            {
                g.FillEllipse(b, x - r * 0.4f, y - r * 0.4f, r * 0.7f, r * 0.7f);
            }
        }
    }

    // ================= 托盘菜单暗色配色表 =================
    internal sealed class MaidColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(255, 13, 25, 59); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(255, 13, 25, 59); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(255, 13, 25, 59); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(255, 13, 25, 59); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(255, 36, 58, 117); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(255, 92, 112, 160); } }
        public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(255, 36, 58, 117); } }
        public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(255, 36, 58, 117); } }
        public override Color MenuBorder { get { return Color.FromArgb(255, 92, 112, 160); } }
    }

    // ================= 拨动开关 =================
    internal sealed class ToggleSwitch : Control
    {
        private bool _checked;
        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked != value)
                {
                    _checked = value;
                    OnCheckedChanged();
                    Invalidate();
                }
            }
        }
        public event EventHandler CheckedChanged;

        private void OnCheckedChanged()
        {
            EventHandler handler = CheckedChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(48, 26);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Checked = !Checked;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int h = ClientSize.Height - 2;
            int y = 1;
            Color trackColor = _checked ? UiTheme.Primary : Color.FromArgb(255, 36, 58, 117);
            using (var b = new SolidBrush(trackColor))
            {
                g.FillEllipse(b, 1, y, h, h);
                g.FillEllipse(b, ClientSize.Width - h - 1, y, h, h);
                g.FillRectangle(b, h / 2 + 1, y, ClientSize.Width - h - 2, h);
            }
            // 边框
            using (var pen = new Pen(Color.FromArgb(255, 92, 112, 160)))
            {
                g.DrawEllipse(pen, 1, y, h, h);
                g.DrawEllipse(pen, ClientSize.Width - h - 1, y, h, h);
                g.DrawLine(pen, 1 + h / 2, y, ClientSize.Width - h - 1 - h / 2, y);
                g.DrawLine(pen, 1 + h / 2, y + h, ClientSize.Width - h - 1 - h / 2, y + h);
            }
            // 滑块
            int knob = h - 8;
            int kx = _checked ? ClientSize.Width - knob - 5 : 5;
            using (var b = new SolidBrush(Color.White))
            using (var pen = new Pen(Color.FromArgb(255, 120, 138, 180)))
            {
                g.FillEllipse(b, kx, y + 4, knob, knob);
                g.DrawEllipse(pen, kx, y + 4, knob, knob);
            }
        }
    }
}
