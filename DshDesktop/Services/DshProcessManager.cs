using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DshDesktop.Services;

/// <summary>
/// dsh 服务进程管理器:用本进程的子进程拉起 dsh,并独自跟踪其生命周期。
/// - 优先 PATH 中的 `dsh`,回退 `npx -y @deepseek-ai/dsh`(可用 DSH_NPM_REGISTRY 指定 npm 镜像)
/// - 全程静默(无控制台窗口、无 vbs 等脚本文件),输出重定向到日志
/// - 只杀自己拉起的子进程树,不接管、不清理端口上已有的其它进程
/// - 意外退出自动重启(节流 + 失败上限);壳退出(含强杀)时由 Job Object 连带终止子进程
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

    /// <summary>启动命令:可执行文件 + 参数列表(经 cmd.exe 执行)+ 需要注入的环境变量。</summary>
    internal sealed record LaunchPlan(string Command, string[] Args, IReadOnlyDictionary<string, string> Environment);

    private readonly bool _externalManaged;
    private readonly object _gate = new();
    private Process? _process;
    private IntPtr _job;
    private DateTime _lastUnexpectedExit = DateTime.MinValue;
    private int _consecutiveFailures;
    private bool _disposed;
    private Func<int, LaunchPlan>? _buildLaunchPlan;
    private string? _sessionUrl;

    public string Url { get; }
    public int Port { get; }
    public ServiceState State { get; private set; } = ServiceState.Stopped;

    /// <summary>
    /// 窗口应打开的地址:优先用子进程打印的 `dsh web:` URL(含 launch-token),
    /// 尚未捕获时回退到构造时的裸地址。
    /// </summary>
    public string NavigateUrl
    {
        get { lock (_gate) return _sessionUrl ?? Url; }
    }

    /// <summary>状态变化事件(任意线程触发,订阅方需自行切到 UI 线程)。</summary>
    public event Action<ServiceState>? StateChanged;

    public DshProcessManager(string url, int port, bool externalManaged)
    {
        Url = url;
        Port = port;
        _externalManaged = externalManaged;
        _buildLaunchPlan = port => BuildLaunchPlanCore(port);
    }

    /// <summary>测试专用:注入启动命令构造器,便于断言端口/参数传递(默认用 BuildLaunchPlanCore)。</summary>
    internal DshProcessManager(
        string url, int port, bool externalManaged, Func<int, LaunchPlan> buildLaunchPlan)
        : this(url, port, externalManaged)
    {
        _buildLaunchPlan = buildLaunchPlan;
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
    /// 确保服务运行:拉起自己的子进程并等待就绪(最长 90s)。
    /// 不接管端口上已有的其它进程。并发调用幂等(Starting/Running 时直接返回)。
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
            // 端口已被占用 → 无法把我们的子进程绑上去,也不接管他人进程。
            if (PortOpen(Port))
            {
                Log.Error($"端口 {Port} 已被占用,不会接管已有进程。请先结束占用该端口的程序,或设置 DSH_WEB_URL 指向外部服务。");
                SetState(ServiceState.Failed);
                return;
            }

            KillOwnProcess();
            var plan = _buildLaunchPlan!(Port);
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // ArgumentList 由系统完成引号/转义; /d 跳过 AutoRun,/c 后跟可执行文件与参数
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(plan.Command);
            foreach (var arg in plan.Args)
                psi.ArgumentList.Add(arg);
            foreach (var (k, v) in plan.Environment)
                psi.Environment[k] = v;
            Log.Info($"拉起 dsh: {plan.Command} {string.Join(' ', plan.Args)}");

            var p = Process.Start(psi);
            if (p is null)
                throw new InvalidOperationException("Process.Start 返回 null");

            AssignToJob(p);
            lock (_gate) _process = p;
            Log.Info($"dsh 子进程已启动 PID={p.Id}");
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) OnDshLine(e.Data, error: false); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnDshLine(e.Data, error: true); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.Exited += OnProcessExited;

            var ready = await WaitOwnReadyAsync(TimeSpan.FromSeconds(90));
            if (ready)
            {
                _consecutiveFailures = 0;
                SetState(ServiceState.Running);
            }
            else if (ProcessAlive())
            {
                // 子进程还活着(常见于 npx 首次下载):保持 Starting,后台继续等,窗口的 WaitReady 仍能成功
                Log.Error("dsh 90 秒内未就绪。若为 npx 首次下载过慢,可设置 DSH_NPM_REGISTRY 指定 npm 镜像;详情见 %USERPROFILE%\\.dsh-desktop.log");
                Log.Info("dsh 子进程仍在运行,转入后台等待就绪…");
                _ = WaitForReadyInBackgroundAsync();
            }
            else
            {
                _consecutiveFailures++;
                SetState(ServiceState.Failed);
                Log.Error("dsh 90 秒内未就绪且子进程已退出。详情见 %USERPROFILE%\\.dsh-desktop.log");
            }
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            SetState(ServiceState.Failed);
            Log.Error($"启动 dsh 失败: {ex}");
        }
    }

    /// <summary>当前子进程意外退出(非主动停止)→ 自动重启,10s 节流,连续失败 5 次后停止。</summary>
    private void OnProcessExited(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || State is ServiceState.Stopping or ServiceState.Stopped) return;
            // 只响应当前子进程;KillOwnProcess 之后的过期 Exited 一律忽略
            if (!ReferenceEquals(_process, sender)) return;
            _process = null;
            _sessionUrl = null;
        }

        var code = -1;
        try { if (sender is Process p) code = p.ExitCode; } catch { }
        Log.Error($"dsh 子进程意外退出 (exit={code}),自动重启");

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

    /// <summary>90 秒未就绪但子进程仍存活时(如 npx 首次下载慢):后台继续等端口,
    /// 一旦就绪自动转 Running,窗口经 StateChanged 事件随即自动加载。</summary>
    private async Task WaitForReadyInBackgroundAsync()
    {
        var ok = await WaitOwnReadyAsync(TimeSpan.FromMinutes(10));
        lock (_gate)
        {
            if (_disposed || State is ServiceState.Stopping or ServiceState.Stopped) return;
        }
        if (!ok)
        {
            _consecutiveFailures++;
            SetState(ServiceState.Failed);
            Log.Error("后台等待就绪超时,dsh 服务未能启动");
            return;
        }
        _consecutiveFailures = 0;
        Log.Info("后台等待就绪成功,dsh 服务已运行");
        SetState(ServiceState.Running);
    }

    private bool ProcessAlive()
    {
        lock (_gate)
        {
            var p = _process;
            return p is not null && !p.HasExited;
        }
    }

    /// <summary>停止自己拉起的 dsh 子进程。不碰端口上其它进程。</summary>
    public async Task StopAsync()
    {
        if (_externalManaged) return;
        lock (_gate)
        {
            if (State is ServiceState.Stopped or ServiceState.Stopping || _disposed) return;
            SetState(ServiceState.Stopping);
        }
        Log.Info("停止 dsh 服务(只结束自己的子进程)");
        KillOwnProcess();
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

    /// <summary>
    /// 等待本管理器把服务带到 Running。不把他人占用的端口当成就绪。
    /// Failed 立即返回 false;Stopped/Starting 继续等(启动可能尚未开始)。
    /// </summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout)
    {
        if (_externalManaged)
            return await WaitPortAsync(timeout, wantOpen: true);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var s = State;
            if (s == ServiceState.Running) return true;
            if (s == ServiceState.Failed) return false;
            await Task.Delay(200);
        }
        return State == ServiceState.Running;
    }

    /// <summary>
    /// 等自己的子进程把端口打开,并尽量等到 stdout 打出 `dsh web:` URL
    /// (0.1.2+ 带一次性 token,必须用这条 URL 打开页面)。
    /// 超时仍无 URL 但端口已开 → 按旧版 dsh 处理,回退裸地址。
    /// </summary>
    private async Task<bool> WaitOwnReadyAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            Process? p;
            string? session;
            lock (_gate) { p = _process; session = _sessionUrl; }
            if (p is null || p.HasExited) return false;
            if (session is not null && PortOpen(Port)) return true;
            await Task.Delay(500);
        }
        return ProcessAlive() && PortOpen(Port);
    }

    private void OnDshLine(string line, bool error)
    {
        if (error) Log.Error($"[dsh] {line}");
        else Log.Info($"[dsh] {line}");

        var url = ShellLogic.ParseDshWebUrl(line);
        if (url is null) return;
        lock (_gate)
        {
            if (_sessionUrl == url) return;
            _sessionUrl = url;
        }
        Log.Info($"捕获 dsh web URL: {url}");
    }

    private async Task WaitPortClosedAsync(TimeSpan timeout) =>
        await WaitPortAsync(timeout, wantOpen: false);

    private async Task<bool> WaitPortAsync(TimeSpan timeout, bool wantOpen)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (PortOpen(Port) == wantOpen) return true;
            await Task.Delay(500);
        }
        return PortOpen(Port) == wantOpen;
    }

    /// <summary>
    /// 只结束本管理器拉起的进程:先杀记录的子进程树,再 TerminateJobObject
    /// 清掉仍留在 job 里的后代(cmd 提前退出时 node 不会变成"别人的进程")。
    /// </summary>
    private void KillOwnProcess()
    {
        Process? p;
        IntPtr job;
        lock (_gate)
        {
            p = _process;
            _process = null;
            _sessionUrl = null;
            job = _job;
        }
        if (p is not null)
        {
            try
            {
                if (!p.HasExited)
                {
                    Log.Info($"结束自己的 dsh 子进程树 PID={p.Id}");
                    p.Kill(entireProcessTree: true);
                }
                p.WaitForExit(5000);
            }
            catch { /* 进程已退出等情况忽略 */ }
            try { p.Dispose(); } catch { }
        }
        if (job != IntPtr.Zero)
            TerminateJobObject(job, 1);
    }

    /// <summary>
    /// 启动命令解析:优先 PATH 中的 dsh;否则 npx 回退(可注入 npm 镜像源)。
    /// 端口显式取自调用方解析的 Port(与导航地址同源),不会出现"服务起了但窗口访问的
    /// 是另一个端口"的错位。Node.js 与 dsh 都不可用 → 立即抛错,避免静默失败后干等 90 秒。
    /// </summary>
    private LaunchPlan BuildLaunchPlanCore(int port)
    {
        // 参数与窗口导航地址同源(ShellLogic.BuildDshWebArgs),端口不会错位
        var args = ShellLogic.BuildDshWebArgs(port);
        var probe = ShellLogic.ProbeRuntime();

        if (probe.DshFound)
        {
            if (!probe.NodeFound)
                Log.Error("PATH 中存在 dsh,但未检测到 Node.js —— dsh 可能无法运行,请确认 Node.js 已安装");
            return new LaunchPlan("dsh", args, new Dictionary<string, string>());
        }

        if (!probe.NodeFound)
            throw new InvalidOperationException(
                "未检测到 Node.js(PATH 中也没有 dsh),无法在后台启动 dsh 服务;请先安装 Node.js 后重试");

        // npx 回退:registry 通过环境变量注入(npx 只读 npm_config_registry,不能当命令参数追加)
        var env = new Dictionary<string, string>();
        var registry = ShellLogic.ResolveNpmRegistry(Environment.GetEnvironmentVariable("DSH_NPM_REGISTRY"));
        if (registry is not null)
        {
            env["npm_config_registry"] = registry;
            Log.Info($"npx 回退使用 npm 镜像: {registry}");
        }

        // cmd.exe 会直接执行第一个词,npx 是 PATH 里的可执行文件,后续参数原样传递
        var npxArgs = new[] { "-y", "@deepseek-ai/dsh" }.Concat(args).ToArray();
        return new LaunchPlan("npx", npxArgs, env);
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

    // ---- Job Object:壳进程退出(含任务管理器强杀)时,系统自动终止 job 内的子进程 ----
    // KILL_ON_JOB_CLOSE 必须走 JobObjectExtendedLimitInformation,否则设置不会生效。

    private const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    private void AssignToJob(Process p)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                if (_job == IntPtr.Zero)
                {
                    Log.Error($"CreateJobObject 失败: {Marshal.GetLastWin32Error()}");
                    return;
                }
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    },
                };
                if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info,
                        (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    Log.Error($"SetInformationJobObject 失败: {Marshal.GetLastWin32Error()}");
                    CloseHandle(_job);
                    _job = IntPtr.Zero;
                    return;
                }
            }
            for (var i = 0; i < 5 && !AssignProcessToJobObject(_job, p.Handle); i++)
                Thread.Sleep(50);
        }
        catch (Exception ex)
        {
            Log.Error($"加入 Job Object 失败(停止时仍会杀自己的子进程树): {ex.Message}");
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
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

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
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
