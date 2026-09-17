using DshDesktop.Infrastructure;
using Xunit;

namespace DshDesktop.Tests.Infrastructure;

public class InstallSourceTests
{
    [Fact]
    public void NodeIndexUrl_Official_IsNodejsDist()
    {
        Assert.Equal("https://nodejs.org/dist/index.json", InstallSource.NodeIndexUrl(InstallSourceKind.Official));
    }

    [Fact]
    public void NodeIndexUrl_Mirror_IsNpmmirrorBinary()
    {
        Assert.Equal("https://registry.npmmirror.com/-/binary/node/index.json",
            InstallSource.NodeIndexUrl(InstallSourceKind.Mirror));
    }

    [Fact]
    public void NodeMsiUrl_Official_UsesVersionedX64MsiName()
    {
        Assert.Equal("https://nodejs.org/dist/v24.21.0/node-v24.21.0-x64.msi",
            InstallSource.NodeMsiUrl(InstallSourceKind.Official, "v24.21.0"));
    }

    [Fact]
    public void NodeMsiUrl_Mirror_UsesVersionedX64MsiName()
    {
        Assert.Equal("https://registry.npmmirror.com/-/binary/node/v24.21.0/node-v24.21.0-x64.msi",
            InstallSource.NodeMsiUrl(InstallSourceKind.Mirror, "v24.21.0"));
    }

    [Fact]
    public void NpmRegistry_OnlyMirrorOverridesNpmConfig()
    {
        Assert.Null(InstallSource.NpmRegistry(InstallSourceKind.Official));
        Assert.Equal("https://registry.npmmirror.com", InstallSource.NpmRegistry(InstallSourceKind.Mirror));
    }

    // 清单按版本倒序;第一条 lts 非 false 的就是最新 LTS
    private const string Index = """
        [
          {"version":"v26.9.0","date":"2026-09-16","files":["win-x64-zip"],"npm":"11.19.1","lts":false},
          {"version":"v25.1.0","files":["win-x64-msi"],"lts":false},
          {"version":"v24.21.0","files":["win-x64-msi"],"lts":"Krypton"},
          {"version":"v22.20.0","files":["win-x64-msi"],"lts":"Jod"}
        ]
        """;

    [Fact]
    public void SelectLatestLts_SkipsNonLtsEntries()
    {
        Assert.Equal("v24.21.0", InstallSource.SelectLatestLts(Index));
    }

    [Fact]
    public void SelectLatestLts_AllNonLts_ReturnsNull()
    {
        Assert.Null(InstallSource.SelectLatestLts("""[{"version":"v26.9.0","lts":false}]"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""[{"name":"node"}]""")]
    public void SelectLatestLts_Unparsable_ReturnsNull(string? json)
    {
        Assert.Null(InstallSource.SelectLatestLts(json));
    }
}
