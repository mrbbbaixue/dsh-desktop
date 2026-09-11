using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DshDesktop.Services;

/// <summary>
/// dsh 服务进程管理器:用本进程的子进程拉起 dsh,并独自跟踪其生命周期。
/// - 优先 PATH 中的 `dsh`,回退 `npx -y @deepseek-ai/dsh`(可用 DSH_NPM_REGISTRY 指定 npm 镜像)
/// - 以隐藏控制台窗口拉起(无 vbs 等脚本文件);托盘可显示/隐藏该终端查看实时输出
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
    /// <summary>重启串行闸:托盘与页面(市场一键重启)可能同时点,不允许两次重启交叉。</summary>
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private Process? _process;
    private IntPtr _job;
    private DateTime _lastUnexpectedExit = DateTime.MinValue;
    private int _consecutiveFailures;
    private bool _disposed;
    private Func<int, LaunchPlan>? _buildLaunchPlan;
    private string? _sessionUrl;
    private IntPtr _consoleHwnd;
    private bool _consoleWantedVisible;
    private string _lastConsoleSnapshot = "";

    public string Url { get; }
    public int Port { get; }
    public ServiceState State { get; private set; } = ServiceState.Stopped;

    /// <summary>外部托管(设置了 DSH_WEB_URL):壳不拉起、不停止,也不接管重启。</summary>
    public bool ExternalManaged => _externalManaged;

    /// <summary>
    /// 托盘「显示/隐藏 dsh 终端」是否可点:有自己拉起的子进程(句柄可能还在绑定中)。
    /// 外部托管服务没有后台控制台,返回 false。
    /// </summary>
    public bool CanToggleConsole
    {
        get
        {
            lock (_gate)
            {
                if (_externalManaged) return false;
                if (HiddenConsole.IsAlive(_consoleHwnd)) return true;
                return _process is not null && !_process.HasExited;
            }
        }
    }

    /// <summary>后台控制台当前是否可见。用户点 X 以外的途径改变可见性时,以实际窗口为准。</summary>
    public bool IsConsoleVisible
    {
        get
        {
            lock (_gate)
                return HiddenConsole.IsAlive(_consoleHwnd) && HiddenConsole.IsVisible(_consoleHwnd);
        }
    }

    /// <summary>
    /// 窗口应打开的地址:优先用子进程打印的 `dsh web:` URL(含 launch-token),
    /// 尚未捕获时回退到构造时的裸地址。
    /// </summary>
    public string NavigateUrl
    {
        get { lock (_gate) return _sessionUrl ?? Url; }
    }

    /// <summary>
    /// 显示或隐藏后台控制台。默认隐藏;进程尚未绑上 HWND 时记下意图,绑定后补上。
    /// 无子进程时返回 false。
    /// </summary>
    public bool SetConsoleVisible(bool visible)
    {
        int pid;
        lock (_gate)
        {
            if (_process is null || _process.HasExited)
                return false;
            _consoleWantedVisible = visible;
            pid = _process.Id;
            if (HiddenConsole.IsAlive(_consoleHwnd))
            {
                if (visible) HiddenConsole.Show(_consoleHwnd);
                else HiddenConsole.Hide(_consoleHwnd);
                return true;
            }
        }
        // 绑定尚未完成或失败时再找一次,找到则立刻按意图显示/隐藏
        var hwnd = HiddenConsole.FindWindow(pid);
        if (hwnd == IntPtr.Zero)
            return true;
        lock (_gate)
        {
            if (_process is null || _process.Id != pid)
                return false;
            _consoleHwnd = hwnd;
            visible = _consoleWantedVisible;
        }
        HiddenConsole.DisableCloseButton(hwnd);
        if (visible) HiddenConsole.Show(hwnd);
        else HiddenConsole.Hide(hwnd);
        return true;
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
            Log.Info($"拉起 dsh: {plan.Command} {string.Join(" ", plan.Args)}");

            // 隐藏控制台(CREATE_NEW_CONSOLE + SW_HIDE):输出进真实终端,不再重定向管道。
            // launch-token 从屏幕缓冲抓;用户经托盘「显示 dsh 终端」看实时输出。
            var p = HiddenConsole.Start("cmd.exe", BuildConsoleArguments(plan), plan.Environment);
            AssignToJob(p);
            lock (_gate)
            {
                _process = p;
                _consoleHwnd = IntPtr.Zero;
                _consoleWantedVisible = false;
                _lastConsoleSnapshot = "";
            }
            Log.Info($"dsh 子进程已启动 PID={p.Id}");
            p.EnableRaisingEvents = true;
            p.Exited += OnProcessExited;
            _ = BindConsoleWindowAsync(p);

            var ready = await WaitOwnReadyAsync(TimeSpan.FromSeconds(90));
            if (ready)
            {
                _consecutiveFailures = 0;
                SetState(ServiceState.Running);
            }
            else if (ProcessAlive())
            {
                // 子进程还活着(常见于 npx 首次下载):保持 Starting,后台继续等,窗口的 WaitReady 仍能成功
                Log.Error("dsh 90 秒内未就绪。若为 npx 首次下载过慢,可设置 DSH_NPM_REGISTRY 指定 npm 镜像;详情见 %USERPROFILE%\\.dsh\\desktop.log");
                Log.Info("dsh 子进程仍在运行,转入后台等待就绪…");
                _ = WaitForReadyInBackgroundAsync();
            }
            else
            {
                _consecutiveFailures++;
                SetState(ServiceState.Failed);
                Log.Error("dsh 90 秒内未就绪且子进程已退出。详情见 %USERPROFILE%\\.dsh\\desktop.log");
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
            _consoleHwnd = IntPtr.Zero;
            _consoleWantedVisible = false;
            _lastConsoleSnapshot = "";
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

    /// <summary>
    /// 停止 → 重新拉起 → 等待就绪(托盘菜单"重启服务"、市场一键重启)。
    /// 并发调用只执行一次:后来的直接返回,不制造两次重启交叉。
    /// </summary>
    public async Task RestartAsync()
    {
        if (!await _restartGate.WaitAsync(TimeSpan.Zero))
        {
            Log.Info("已有重启在进行中,忽略重复的重启请求");
            return;
        }
        try
        {
            Log.Info("重启 dsh 服务");
            await StopAsync();
            await EnsureRunningAsync();
        }
        finally
        {
            _restartGate.Release();
        }
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
    /// 等自己的子进程把端口打开,并尽量等到控制台打出 `dsh web:` URL
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
            if (session is null)
                TryCaptureUrlFromConsole();
            lock (_gate) session = _sessionUrl;
            if (session is not null && PortOpen(Port)) return true;
            await Task.Delay(500);
        }
        return ProcessAlive() && PortOpen(Port);
    }

    /// <summary>等到控制台 HWND 后按用户意图显示或保持隐藏,并去掉关闭按钮。</summary>
    private async Task BindConsoleWindowAsync(Process p)
    {
        try
        {
            var hwnd = await HiddenConsole.WaitForWindowAsync(p.Id, TimeSpan.FromSeconds(5));
            lock (_gate)
            {
                if (!ReferenceEquals(_process, p)) return;
                if (hwnd == IntPtr.Zero)
                {
                    Log.Error("未能绑定 dsh 控制台窗口,托盘「显示 dsh 终端」可能不可用");
                    return;
                }
                _consoleHwnd = hwnd;
                HiddenConsole.DisableCloseButton(hwnd);
                if (_consoleWantedVisible) HiddenConsole.Show(hwnd);
                else HiddenConsole.Hide(hwnd);
            }
            Log.Info($"dsh 控制台已绑定 HWND=0x{hwnd.ToInt64():X}");
        }
        catch (Exception ex)
        {
            Log.Error($"绑定 dsh 控制台失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从隐藏控制台屏幕缓冲抓新输出:记日志,并解析 `dsh web:` launch-token。
    /// 与管道重定向互斥(真实终端要自己画输出),只在等待就绪期间轮询。
    /// </summary>
    private void TryCaptureUrlFromConsole()
    {
        int pid;
        lock (_gate) pid = _process?.Id ?? 0;
        if (pid == 0) return;
        string? snapshot;
        try { snapshot = HiddenConsole.ReadSnapshot(pid); }
        catch { return; }
        if (string.IsNullOrEmpty(snapshot) || snapshot == _lastConsoleSnapshot)
            return;

        var common = 0;
        var max = Math.Min(_lastConsoleSnapshot.Length, snapshot!.Length);
        while (common < max && _lastConsoleSnapshot[common] == snapshot[common])
            common++;
        var delta = snapshot.Substring(common);
        _lastConsoleSnapshot = snapshot;
        foreach (var raw in delta.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            OnDshLine(line);
        }
        // 控制台按缓冲宽度折行时,token URL 可能被拆成两行;整段再扫一次
        bool haveUrl;
        lock (_gate) haveUrl = _sessionUrl is not null;
        if (haveUrl) return;
        var url = ShellLogic.ParseDshWebUrl(snapshot.Replace("\n", ""));
        if (url is null) return;
        lock (_gate)
        {
            if (_sessionUrl is not null) return;
            _sessionUrl = url;
        }
        Log.Info($"捕获 dsh web URL: {url}");
    }

    private void OnDshLine(string line)
    {
        Log.Info($"[dsh] {line}");

        var url = ShellLogic.ParseDshWebUrl(line);
        if (url is null) return;
        lock (_gate)
        {
            if (_sessionUrl == url) return;
            _sessionUrl = url;
        }
        Log.Info($"捕获 dsh web URL: {url}");
    }

    /// <summary>
    /// cmd.exe 参数:隐藏控制台内先切 UTF-8 代码页,再跑 dsh/npx。
    /// /c 吃掉开关后整行,`&` 作为 cmd 命令分隔,无需再包一层引号。
    /// </summary>
    internal static string BuildConsoleArguments(LaunchPlan plan)
    {
        var cmdArgs = new List<string> { plan.Command };
        cmdArgs.AddRange(plan.Args);
        return "/d /c chcp 65001>nul & " + QuoteWin32Args(cmdArgs);
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
    /// 只结束本管理器拉起的进程:先杀记录的 cmd 子进程,再 TerminateJobObject
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
            _consoleHwnd = IntPtr.Zero;
            _consoleWantedVisible = false;
            _lastConsoleSnapshot = "";
            job = _job;
        }
        if (p is not null)
        {
            try
            {
                if (!p.HasExited)
                {
                    Log.Info($"结束自己的 dsh 子进程树 PID={p.Id}");
                    // .NET Framework 没有 Kill(entireProcessTree);taskkill /T 杀进程树,失败再 Kill 自身
                    try
                    {
                        using var killer = Process.Start(new ProcessStartInfo(
                            "taskkill.exe", $"/PID {p.Id} /T /F")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        });
                        killer?.WaitForExit(5000);
                    }
                    catch { /* ignore */ }
                    try { if (!p.HasExited) p.Kill(); } catch { }
                }
                p.WaitForExit(5000);
            }
            catch { /* 进程已退出等情况忽略 */ }
            try { p.Dispose(); } catch { }
        }
        if (job != IntPtr.Zero)
            TerminateJobObject(job, 1);
    }

    /// <summary>按 Win32 argv 规则拼接参数(.NET Framework 没有 ProcessStartInfo.ArgumentList)。</summary>
    private static string QuoteWin32Args(IEnumerable<string> args) =>
        string.Join(" ", args.Select(QuoteWin32Arg));

    private static string QuoteWin32Arg(string arg)
    {
        if (arg.Length != 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
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
