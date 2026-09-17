using DshDesktop.Services;
using Xunit;

namespace DshDesktop.Tests.Services;

public class EnvReportTests
{
    private static EnvItem Item(ToolKind kind, bool ok) =>
        new(kind, kind.ToString(), ok, ok ? "已安装" : "未安装", null);

    private static EnvReport Report(bool node, bool npm, bool dsh) =>
        new([Item(ToolKind.Node, node), Item(ToolKind.Npm, npm), Item(ToolKind.Dsh, dsh)]);

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void NeedsInstall_WhenAnyRequiredPartMissing_IsTrue(bool node, bool npm, bool dsh)
    {
        Assert.True(Report(node, npm, dsh).NeedsInstall);
    }

    [Fact]
    public void NeedsInstall_WhenNodeAndDshReady_IsFalse()
    {
        var report = Report(node: true, npm: true, dsh: true);
        Assert.False(report.NeedsInstall);
        Assert.True(report.NodeReady);
        Assert.True(report.DshReady);
        Assert.Equal("环境完整", report.Summary);
    }

    [Fact]
    public void NodeReady_RequiresBothNodeAndNpm()
    {
        Assert.False(Report(node: true, npm: false, dsh: true).NodeReady);
        Assert.True(Report(node: true, npm: true, dsh: false).NodeReady);
    }

    [Fact]
    public void Summary_WhenInstallNeeded_SaysMissing()
    {
        Assert.Equal("缺少必需组件", Report(node: false, npm: false, dsh: false).Summary);
    }

    [Theory]
    [InlineData(true, "v24.21.0", "已安装 v24.21.0")]
    [InlineData(true, "  v24.21.0  ", "已安装 v24.21.0")]
    [InlineData(true, null, "已安装")]
    [InlineData(true, "   ", "已安装")]
    [InlineData(false, "v24.21.0", "未安装")]
    [InlineData(false, null, "未安装")]
    public void Describe_FormatsVersionOrFallsBack(bool found, string? version, string expected)
    {
        Assert.Equal(expected, EnvProbe.Describe(found, version));
    }
}
