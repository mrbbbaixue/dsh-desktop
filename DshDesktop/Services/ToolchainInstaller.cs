using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace DshDesktop.Services;

/// <summary>安装阶段。</summary>
internal enum InstallStage
{
    Preparing,
    InstallingNode,
    InstallingDsh,
    Verifying,
    Done,
    Failed,
    Cancelled,
}

/// <summary>安装进度:Percent 只有下载阶段有真实值(0-100),其余阶段为 null(界面显示不定态)。</summary>
internal sealed record InstallProgress(InstallStage Stage, string Message, double? Percent);

/// <summary>
/// 工具链安装编排:Node.js(npm/npx 随它一起)与 dsh,一律装到系统。
/// - 官方源:winget 安装 OpenJS.NodeJS.LTS(winget 从 nodejs.org 拉 MSI)
/// - 镜像源:从 npmmirror 下载 MSI 后 msiexec 静默安装(下载有真实百分比)
/// - dsh:npm install -g(镜像源加 --registry),装到 %APPDATA%\npm
/// 系统级安装必然触发一次 UAC;成功与否以"重新探测环境"为准,不依赖退出码语义。
/// </summary>
internal sealed class ToolchainInstaller
{
    private readonly Action<string> _log;
    private InstallSourceKind _source;
    private string? _nodeVersion;

    public ToolchainInstaller(InstallSourceKind source, Action<string> log)
    {
        _source = source;
        _log = log;
    }

    /// <summary>
    /// 安装缺失组件;失败与取消都通过进度回调上报(Stage = Failed),不再向外抛。
    /// 安装体整体跑在线程池上:里面有环境探测、taskkill 等待、提权安装进程 WaitForExit 等同步等待,
    /// 一律不能占调用方的 UI 线程(进度经 Progress&lt;T&gt; 自动切回 UI 线程);
    /// 在线程池上执行也保证调用方无论从哪个线程调用都不会被阻塞。
    /// </summary>
    public Task InstallMissingAsync(EnvReport report, IProgress<InstallProgress> progress, CancellationToken ct) =>
        Task.Run(() => RunGuardedAsync(report, progress, ct));

    private async Task RunGuardedAsync(EnvReport report, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        try
        {
            await RunCoreAsync(report, progress, ct);
            progress.Report(new InstallProgress(InstallStage.Done, "安装完成", 100));
        }
        catch (OperationCanceledException)
        {
            _log("安装已取消");
            progress.Report(new InstallProgress(InstallStage.Cancelled, "已取消安装", null));
        }
        catch (Exception ex)
        {
            Log.Error($"安装失败: {ex.Message}");
            progress.Report(new InstallProgress(InstallStage.Failed, ex.Message, null));
        }
    }

    private async Task RunCoreAsync(EnvReport report, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        progress.Report(new InstallProgress(InstallStage.Preparing, "正在准备…", null));
        await PrepareAsync(ct);

        if (report.NodeReady)
        {
            _log("Node.js 与 npm 已就绪,跳过安装");
        }
        else
        {
            _log(_source == InstallSourceKind.Official
                ? $"将执行: winget {BuildWingetInstallArgs(InstallSource.NodeWingetId)}"
                : $"将安装镜像版本: {InstallSource.NodeMsiUrl(InstallSourceKind.Mirror, _nodeVersion!)}");
            _log("系统会弹出 UAC 提权窗口,请点击「是」");
            progress.Report(new InstallProgress(InstallStage.InstallingNode,
                _source == InstallSourceKind.Official ? "winget 正在安装 Node.js(npm/npx 随附)…" : "正在下载 Node.js…",
                _source == InstallSourceKind.Official ? null : 0));
            await InstallNodeAsync(progress, ct);
            var installed = ShellLogic.RunForOutput("node", "--version");
            if (string.IsNullOrWhiteSpace(installed))
                throw new InvalidOperationException("Node.js 安装未生效:可能 UAC 被取消,或需要换一个安装源重试");
            _log($"Node.js 已安装: {installed!.Trim()}");
        }

        if (report.DshReady)
        {
            _log("dsh 已就绪,跳过安装");
        }
        else
        {
            var args = BuildNpmInstallArgs(InstallSource.NpmRegistry(_source));
            _log($"将执行: npm {args}");
            progress.Report(new InstallProgress(InstallStage.InstallingDsh, "正在安装 dsh(约 1-3 分钟)…", null));
            await InstallDshAsync(args, ct);
            if (string.IsNullOrWhiteSpace(ShellLogic.WherePath("dsh")))
                throw new InvalidOperationException("dsh 安装未生效:可切换安装源后重试");
            _log("dsh 已安装");
        }

        progress.Report(new InstallProgress(InstallStage.Verifying, "正在校验…", null));
        var after = EnvProbe.Run();
        if (!after.NodeReady || !after.DshReady)
            throw new InvalidOperationException(after.NodeReady ? "校验未通过:dsh 仍不可用" : "校验未通过:Node.js 仍不可用");
    }

    /// <summary>准备:官方源确认 winget 可用;镜像源取版本清单(拿不到就回退官方源)。</summary>
    private async Task PrepareAsync(CancellationToken ct)
    {
        if (_source == InstallSourceKind.Official)
        {
            var version = await Task.Run(() => ShellLogic.RunForOutput("winget.exe", "--version"), ct);
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException("未找到 winget,请改用「国内镜像」安装源重试");
            _log($"winget {version!.Trim()} 可用");
            return;
        }

        _log($"读取镜像版本清单: {InstallSource.NodeIndexUrl(InstallSourceKind.Mirror)}");
        var json = await Task.Run(() => DownloadString(InstallSource.NodeIndexUrl(InstallSourceKind.Mirror)), ct);
        _nodeVersion = InstallSource.SelectLatestLts(json);
        if (_nodeVersion is null)
        {
            _log("镜像源未取到 Node 版本清单,回退官方源(winget)");
            _source = InstallSourceKind.Official;
            await PrepareAsync(ct);
            return;
        }
        _log($"镜像最新 LTS: {_nodeVersion}(winget 清单版本可能比它旧一个补丁版本)");
    }

    private async Task InstallNodeAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        if (_source == InstallSourceKind.Official)
        {
            var code = await RunAsync("cmd.exe", CmdLine($"winget {BuildWingetInstallArgs(InstallSource.NodeWingetId)}"),
                null, ct);
            _log($"winget 退出码: {code}");
            if (!string.IsNullOrWhiteSpace(ShellLogic.RunForOutput("node", "--version"))) return;
            throw new InvalidOperationException(DescribeWingetFailure(code));
        }

        var msi = Path.Combine(Path.GetTempPath(), "DshDesktop", $"node-{_nodeVersion}-x64.msi");
        Directory.CreateDirectory(Path.GetDirectoryName(msi)!);
        try
        {
            await DownloadFileAsync(InstallSource.NodeMsiUrl(InstallSourceKind.Mirror, _nodeVersion!), msi, progress, ct);
            _log($"下载完成: {new FileInfo(msi).Length / (1024 * 1024)} MB,开始静默安装");
            // msiexec 装机器级必须提权;壳自身保持 asInvoker,只有这一步提权
            await RunElevatedAsync("msiexec.exe", BuildMsiexecArgs(msi));
        }
        finally
        {
            try { if (File.Exists(msi)) File.Delete(msi); } catch { /* 临时文件删不掉不影响结果 */ }
        }
    }

    private async Task InstallDshAsync(string npmArgs, CancellationToken ct)
    {
        var code = await RunAsync("cmd.exe", CmdLine($"npm.cmd {npmArgs}"), null, ct);
        _log($"npm 退出码: {code}");
    }

    // ---- 纯参数拼装(单元测试覆盖) ----

    internal static string BuildWingetInstallArgs(string packageId) =>
        $"install --id {packageId} --exact --silent --accept-source-agreements --accept-package-agreements";

    internal static string BuildMsiexecArgs(string msiPath) => $"/i \"{msiPath}\" /qn /norestart";

    internal static string BuildNpmInstallArgs(string? registry) =>
        registry is null
            ? "install -g @deepseek-ai/dsh"
            : $"install -g @deepseek-ai/dsh --registry={registry}";

    /// <summary>cmd.exe 的 /c 行:先切 UTF-8 代码页,避免中文输出乱码。</summary>
    internal static string CmdLine(string commandLine) => $"/d /c chcp 65001>nul & {commandLine}";

    // ---- 执行 ----

    /// <summary>跑外部命令并把输出逐行喂给日志;取消时结束进程树。</summary>
    private async Task<int> RunAsync(string fileName, string arguments, IDictionary<string, string>? extraEnv,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            EnvironmentVariables = { ["PATH"] = SystemPath.Current() },
        };
        if (extraEnv is not null)
            foreach (var pair in extraEnv)
                psi.EnvironmentVariables[pair.Key] = pair.Value;

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => LogLine(e.Data);
        p.ErrorDataReceived += (_, e) => LogLine(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        await Task.Run(() =>
        {
            using var registration = ct.Register(() => KillTree(p));
            p.WaitForExit();
        }).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }

    private void LogLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line)) _log(line!.TrimEnd());
    }

    private static void KillTree(Process p)
    {
        try
        {
            if (p.HasExited) return;
            // .NET Framework 没有 Kill(entireProcessTree);cmd → npm → node 要整树结束
            using var killer = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {p.Id} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            killer?.WaitForExit(5000);
            if (!p.HasExited) p.Kill();
        }
        catch { /* 进程已退出等情况忽略 */ }
    }

    /// <summary>提权启动(msiexec / UAC)并等它装完。用户拒绝提权抛 1223,转成可读文案。</summary>
    private static async Task RunElevatedAsync(string fileName, string arguments)
    {
        await Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                p?.WaitForExit();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new InvalidOperationException("安装被取消:未同意 UAC 提权");
            }
        });
    }

    /// <summary>下载文件;国内镜像直连(不走系统代理),每 1% 上报一次进度。</summary>
    private async Task DownloadFileAsync(string url, string target, IProgress<InstallProgress> progress,
        CancellationToken ct)
    {
        using var client = CreateClient();
        var lastPercent = -1.0;
        client.DownloadProgressChanged += (_, e) =>
        {
            if (e.TotalBytesToReceive <= 0) return;
            var percent = e.BytesReceived * 100.0 / e.TotalBytesToReceive;
            if (percent - lastPercent < 1) return;
            lastPercent = percent;
            progress.Report(new InstallProgress(InstallStage.InstallingNode,
                $"下载 Node.js {percent:F0}% · {Mb(e.BytesReceived)} / {Mb(e.TotalBytesToReceive)} MB", percent));
        };
        using var registration = ct.Register(() => client.CancelAsync());
        try
        {
            await client.DownloadFileTaskAsync(url, target);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"下载失败: {ex.Message};可切换到「官方源」重试");
        }
        // 33 MB 的 MSI;几百 KB 只可能是错误页
        if (new FileInfo(target).Length < 1024 * 1024)
            throw new InvalidOperationException("下载的安装包不完整,请切换安装源后重试");
    }

    private string? DownloadString(string url)
    {
        try
        {
            using var client = CreateClient();
            return client.DownloadString(url);
        }
        catch (Exception ex)
        {
            _log($"取版本清单失败: {ex.Message}");
            return null;
        }
    }

    private WebClient CreateClient()
    {
        var client = new WebClient();
        // npmmirror 是境内站点,直连;官方源沿用系统代理(公司代理/加速器场景)
        if (_source == InstallSourceKind.Mirror) client.Proxy = null;
        return client;
    }

    private string DescribeWingetFailure(int code)
    {
        var description = ShellLogic.RunForOutput("winget.exe", $"error {code}");
        if (!string.IsNullOrWhiteSpace(description))
            _log($"winget error: {description!.Trim()}");
        return $"Node.js 安装失败(winget 退出码 {code});可改用「国内镜像」重试";
    }

    private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1");
}
