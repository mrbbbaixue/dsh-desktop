using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DshDesktop;

/// <summary>
/// dsh 服务进程管理器:用独立进程拉起 dsh,并跟踪其生命周期。
/// - 优先 PATH 中的 `dsh`,回退 `npx -y @deepseek-ai/dsh`(可用 DSH_NPM_REGISTRY 指定 npm 镜像)
/// - 全程静默(无控制台窗口、无 vbs 等脚本文件),输出重定向到日志
/// - 意外退出自动重启(节流 + 失败上限);停止/退出只杀自己拉起的进程
/// - 设置 DSH_WEB_URL 时视为外部托管服务,不拉起也不停止
/// </summary>
public sealed class DshProcessManager : IDisposable
{
    public enum ServiceState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Failed,
    }

    private readonly bool _externalManaged;
    private readonly object _gate = new();
    private Process? _process;
    private IntPtr _job;
    private DateTime _lastUnexpectedExit = DateTime.MinValue;
    private int _consecutiveFailures;
    private bool _disposed;

    public string Url { get; }
    public int Port { get; }
    public ServiceState State { get; private set; } = ServiceState.Stopped;

    /// <summary>状态变化事件(任意线程触发,订阅方需自行切到 UI 线程)。</summary>
    public event Action<ServiceState>? StateChanged;

    public DshProcessManager(string url, int port, bool externalManaged)
    {
        Url = url;
        Port = port;
        _externalManaged = externalManaged;
    }

    /// <summary>探测 127.0.0.1 端口是否已有服务在监听。</summary>
    public static bool PortOpen(int port)
    {
        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 确保服务运行:端口已开 → 直接就绪;未开 → 拉起并等待就绪(最长 90s)。
    /// 并发调用幂等(Starting/Running 时直接返回)。
    /// </summary>
    public async Task EnsureRunningAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_externalManaged)
            {
                SetState(PortOpen(Port) ? ServiceState.Running : ServiceState.Failed);
                return;
            }
            if (State is ServiceState.Starting or ServiceState.Running) return;
            SetState(ServiceState.Starting);
        }
        await StartCoreAsync();
    }

    private async Task StartCoreAsync()
    {
        try
        {
            if (PortOpen(Port))
            {
                _consecutiveFailures = 0;
                SetState(ServiceState.Running);
                return;
            }

            KillOwnProcess(); // 清理可能残留的旧进程
            var plan = BuildLaunchPlan();
            Log.Info($"拉起 dsh: {plan.Command} {plan.Arguments}");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{plan.Command} {plan.Arguments}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var (k, v) in plan.Environment)
                psi.Environment[k] = v;

            var p = Process.Start(psi);
            if (p is null)
                throw new InvalidOperationException("Process.Start 返回 null");

            AssignToJob(p); // 壳退出(含强杀)时由系统连带终止 dsh,防残留
            lock (_gate) _process = p;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Info($"[dsh] {e.Data}"); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log.Error($"[dsh] {e.Data}"); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.Exited += OnProcessExited;

            var ready = await WaitReadyAsync(TimeSpan.FromSeconds(90));
            if (ready)
            {
                _consecutiveFailures = 0;
                SetState(ServiceState.Running);
            }
            else
            {
                _consecutiveFailures++;
                SetState(ServiceState.Failed);
                Log.Error("dsh 90 秒内未就绪。若为 npx 首次下载过慢,可设置 DSH_NPM_REGISTRY 指定 npm 镜像;详情见 %USERPROFILE%\\.dsh-desktop.log");
            }
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            SetState(ServiceState.Failed);
            Log.Error($"启动 dsh 失败: {ex}");
        }
    }

    /// <summary>进程意外退出(非主动停止)→ 自动重启,10s 节流,连续失败 5 次后停止。</summary>
    private void OnProcessExited(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (State is ServiceState.Stopping or ServiceState.Stopped || _disposed) return;
        }

        var code = -1;
        try { if (sender is Process p) code = p.ExitCode; } catch { }
        Log.Error($"dsh 进程意外退出 (exit={code}),自动重启");

        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_consecutiveFailures >= 5 || (now - _lastUnexpectedExit).TotalSeconds < 10)
            {
                Log.Error("超出自动重启节流/上限,停止自动重启;请从托盘菜单手动重启");
                SetState(ServiceState.Failed);
                return;
            }
            _lastUnexpectedExit = now;
        }
        _ = Task.Run(EnsureRunningAsync);
    }

    /// <summary>停止自己拉起的 dsh 进程(外部托管进程不碰)。</summary>
    public async Task StopAsync()
    {
        if (_externalManaged) return;
        lock (_gate)
        {
            if (State is ServiceState.Stopped or ServiceState.Stopping || _disposed) return;
            SetState(ServiceState.Stopping);
        }
        Log.Info("停止 dsh 服务");
        KillOwnProcess();
        // 端口可能被残留的旧 dsh 进程占用(非本壳拉起的,例如升级 Node.js 前的
        // 32 位旧服务仍占着 3080)。这类进程 spawn 新 node.exe 会报 ENOENT(位数不匹配),
        // 一并结束,保证"停止/重启"真正生效。
        if (FindPortPid(Port) is int owner)
        {
            Log.Info($"端口 {Port} 仍被 PID {owner} 占用,结束该残留进程");
            KillPidTree(owner);
        }
        await WaitPortClosedAsync(TimeSpan.FromSeconds(10));
        lock (_gate)
        {
            if (State is ServiceState.Stopping) SetState(ServiceState.Stopped);
        }
    }

    /// <summary>停止 → 重新拉起 → 等待就绪(托盘菜单"重启服务")。</summary>
    public async Task RestartAsync()
    {
        Log.Info("重启 dsh 服务");
        await StopAsync();
        await EnsureRunningAsync();
    }

    /// <summary>轮询等待端口就绪;启动的进程已退出时提前返回 false。</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            Process? p;
            lock (_gate) p = _process;
            if (p is not null && p.HasExited) return false;
            if (PortOpen(Port)) return true;
            await Task.Delay(500);
        }
        return PortOpen(Port);
    }

    private async Task WaitPortClosedAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!PortOpen(Port)) return;
            await Task.Delay(500);
        }
    }

    private void KillOwnProcess()
    {
        Process? p;
        lock (_gate) { p = _process; _process = null; }
        if (p is null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true); // 连 npx/node 子进程树一起杀
            p.WaitForExit(5000);
        }
        catch { /* 进程已退出等情况忽略 */ }
        try { p.Dispose(); } catch { }
    }

    /// <summary>启动命令解析:优先 PATH 中的 dsh;否则 npx 回退(可注入 npm 镜像源)。</summary>
    private (string Command, string Arguments, IReadOnlyDictionary<string, string> Environment) BuildLaunchPlan()
    {
        var args = $"web --host 127.0.0.1 --port {Port}";
        if (CommandExistsOnPath("dsh"))
            return ("dsh", args, new Dictionary<string, string>());

        var env = new Dictionary<string, string>();
        var registry = ShellLogic.ResolveNpmRegistry(Environment.GetEnvironmentVariable("DSH_NPM_REGISTRY"));
        if (registry is not null)
        {
            env["npm_config_registry"] = registry;
            Log.Info($"npx 回退使用 npm 镜像: {registry}");
        }
        return ("npx -y @deepseek-ai/dsh", args, env);
    }

    private static bool CommandExistsOnPath(string command)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("where.exe", command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    private void SetState(ServiceState state)
    {
        if (State == state) return;
        State = state;
        Log.Info($"服务状态: {state}");
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        KillOwnProcess();
        CloseJob();
    }

    // ---- 端口占用进程清理:结束残留的旧 dsh 进程(停止/重启服务时) ----

    /// <summary>用 netstat 查找监听指定端口的进程 PID。</summary>
    private static int? FindPortPid(int port)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netstat.exe", "-ano")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var marker = $":{port}";
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // 行格式: TCP  127.0.0.1:3080  0.0.0.0:0  LISTENING  <pid>
                if (parts.Length >= 5
                    && parts[0].StartsWith("TCP", StringComparison.OrdinalIgnoreCase)
                    && parts[^2].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(parts[^1], out var pid) && pid > 0)
                    return pid;
            }
        }
        catch { /* 解析失败时降级 */ }
        return null;
    }

    /// <summary>结束指定 PID 及其子进程树(taskkill /T /F)。</summary>
    private static void KillPidTree(int pid)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(5000);
        }
        catch { /* 进程已退出等情况忽略 */ }
    }

    // ---- Job Object:壳进程退出(含任务管理器强杀)时,系统自动终止 job 内的 dsh 进程 ----

    private const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectBasicLimitInformation = 9;

    /// <summary>把 dsh 进程放入"关闭即杀"的 Job Object;失败时降级(仍可用 Kill(entireProcessTree))。</summary>
    private void AssignToJob(Process p)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                if (_job == IntPtr.Zero) return;
                var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                };
                SetInformationJobObject(_job, JobObjectBasicLimitInformation, ref info,
                    (uint)Marshal.SizeOf<JOBOBJECT_BASIC_LIMIT_INFORMATION>());
            }
            // 进程刚启动可能有竞态,重试几次
            for (var i = 0; i < 5 && !AssignProcessToJobObject(_job, p.Handle); i++)
                Thread.Sleep(50);
        }
        catch
        {
            // 分配失败降级:正常路径的 Kill(true) 与 Process.Exited 仍然生效
        }
    }

    private void CloseJob()
    {
        if (_job == IntPtr.Zero) return;
        try { CloseHandle(_job); } catch { }
        _job = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public IntPtr MinimumWorkingSetSize;
        public IntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass,
        ref JOBOBJECT_BASIC_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
