using DshDesktop.Infrastructure;
using DshDesktop.Services;
using Xunit;

namespace DshDesktop.Tests.Services;

public class ToolchainInstallerTests
{
    [Fact]
    public void BuildWingetInstallArgs_PinsIdAndSuppressesAllPrompts()
    {
        var args = ToolchainInstaller.BuildWingetInstallArgs(InstallSource.NodeWingetId);
        Assert.Equal(
            "install --id OpenJS.NodeJS.LTS --exact --silent --accept-source-agreements --accept-package-agreements",
            args);
    }

    [Fact]
    public void BuildMsiexecArgs_QuotesPathAndStaysSilent()
    {
        Assert.Equal("/i \"C:\\Temp\\a b\\node-v24.21.0-x64.msi\" /qn /norestart",
            ToolchainInstaller.BuildMsiexecArgs(@"C:\Temp\a b\node-v24.21.0-x64.msi"));
    }

    [Fact]
    public void BuildNpmInstallArgs_AddsRegistryOnlyForMirror()
    {
        Assert.Equal("install -g @deepseek-ai/dsh", ToolchainInstaller.BuildNpmInstallArgs(null));
        Assert.Equal("install -g @deepseek-ai/dsh --registry=https://registry.npmmirror.com",
            ToolchainInstaller.BuildNpmInstallArgs(InstallSource.NpmRegistry(InstallSourceKind.Mirror)));
    }

    [Fact]
    public void CmdLine_SwitchesConsoleToUtf8BeforeRunning()
    {
        Assert.Equal("/d /c chcp 65001>nul & winget --version", ToolchainInstaller.CmdLine("winget --version"));
    }
}
