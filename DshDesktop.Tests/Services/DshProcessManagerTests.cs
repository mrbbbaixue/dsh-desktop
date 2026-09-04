using DshDesktop.Services;
using Xunit;

namespace DshDesktop.Tests.Services;

public class DshProcessManagerTests
{
    [Fact]
    public void BuildConsoleArguments_RunsChcpThenCommand()
    {
        var plan = new DshProcessManager.LaunchPlan(
            "dsh", ["web", "--host", "127.0.0.1", "--port", "3080", "--no-open"],
            new Dictionary<string, string>());
        var args = DshProcessManager.BuildConsoleArguments(plan);
        Assert.StartsWith("/d /c chcp 65001>nul & ", args);
        Assert.Contains("dsh web --host 127.0.0.1 --port 3080 --no-open", args);
    }

    [Fact]
    public void BuildConsoleArguments_QuotesArgsWithSpaces()
    {
        var plan = new DshProcessManager.LaunchPlan(
            "dsh", ["web", "--note", "hello world"],
            new Dictionary<string, string>());
        var args = DshProcessManager.BuildConsoleArguments(plan);
        Assert.Contains("dsh web --note \"hello world\"", args);
    }

    [Fact]
    public void HiddenConsole_Start_ProcessRunsToExit()
    {
        // GetProcessById 在 net48 上没有 Start 得到的进程句柄,读 ExitCode 会抛;
        // 壳只用 HasExited / EnableRaisingEvents / Kill,这三项必须可用。
        var p = HiddenConsole.Start(
            "cmd.exe", "/d /c ping -n 2 127.0.0.1 >nul",
            new Dictionary<string, string>());
        var exited = new ManualResetEventSlim(false);
        try
        {
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => exited.Set();
            Assert.False(p.HasExited);
            Assert.True(p.WaitForExit(15000));
            Assert.True(p.HasExited);
            Assert.True(exited.Wait(2000));
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(); } catch { /* ignore */ }
            p.Dispose();
            exited.Dispose();
        }
    }
}
