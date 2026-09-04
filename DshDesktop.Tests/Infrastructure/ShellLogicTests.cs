using DshDesktop.Infrastructure;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace DshDesktop.Tests.Infrastructure;

public class ShellLogicTests
{
    // ---- ResolveTarget ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveTarget_Empty_ReturnsDefault(string? env)
    {
        var (url, port) = ShellLogic.ResolveTarget(env);
        Assert.Equal("http://127.0.0.1:3080", url);
        Assert.Equal(3080, port);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("file:///c:/x")]
    public void ResolveTarget_Invalid_ReturnsDefault(string env)
    {
        var (url, port) = ShellLogic.ResolveTarget(env);
        Assert.Equal("http://127.0.0.1:3080", url);
        Assert.Equal(3080, port);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9000", "http://127.0.0.1:9000", 9000)]
    [InlineData("http://localhost:8080/path/", "http://localhost:8080/path", 8080)]
    [InlineData("https://example.com:8443", "https://example.com:8443", 8443)]
    public void ResolveTarget_Valid_ReturnsParsed(string env, string expectedUrl, int expectedPort)
    {
        var (url, port) = ShellLogic.ResolveTarget(env);
        Assert.Equal(expectedUrl, url);
        Assert.Equal(expectedPort, port);
    }

    // ---- ClassifyPopup ----

    [Theory]
    [InlineData("http://127.0.0.1:3080/foo", ShellLogic.PopupTarget.Internal)]
    [InlineData("http://localhost:3080/bar", ShellLogic.PopupTarget.Internal)]
    [InlineData("https://example.com", ShellLogic.PopupTarget.External)]
    [InlineData("http://example.com:8080", ShellLogic.PopupTarget.External)]
    public void ClassifyPopup_HttpHosts(string uri, ShellLogic.PopupTarget expected)
    {
        Assert.Equal(expected, ShellLogic.ClassifyPopup(uri));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("blob:https://127.0.0.1/uuid")]
    [InlineData("data:text/html,hi")]
    [InlineData("about:blank")]
    [InlineData("not a uri")]
    public void ClassifyPopup_NonHttp_ReturnsDefault(string? uri)
    {
        Assert.Equal(ShellLogic.PopupTarget.Default, ShellLogic.ClassifyPopup(uri));
    }

    // ---- IsAutoGrantedPermission ----

    [Theory]
    [InlineData(CoreWebView2PermissionKind.Notifications)]
    [InlineData(CoreWebView2PermissionKind.ClipboardRead)]
    [InlineData(CoreWebView2PermissionKind.Autoplay)]
    [InlineData(CoreWebView2PermissionKind.MultipleAutomaticDownloads)]
    [InlineData(CoreWebView2PermissionKind.PersistentStorage)]
    public void IsAutoGrantedPermission_GrantsExpectedKinds(CoreWebView2PermissionKind kind)
    {
        Assert.True(ShellLogic.IsAutoGrantedPermission(kind));
    }

    [Theory]
    [InlineData(CoreWebView2PermissionKind.Microphone)]
    [InlineData(CoreWebView2PermissionKind.Camera)]
    [InlineData(CoreWebView2PermissionKind.Geolocation)]
    public void IsAutoGrantedPermission_DeniesPrivacyKinds(CoreWebView2PermissionKind kind)
    {
        Assert.False(ShellLogic.IsAutoGrantedPermission(kind));
    }

    // ---- SanitizeFileName ----

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("CON", "_CON")]           // 保留设备名
    [InlineData("com1.txt", "_com1.txt")]
    [InlineData("   ", "dsh-")]           // 空结果回退时间戳前缀
    public void SanitizeFileName_CleansNames(string input, string expectedPrefix)
    {
        var result = ShellLogic.SanitizeFileName(input);
        Assert.StartsWith(expectedPrefix, result);
    }

    [Fact]
    public void SanitizeFileName_KeepsUnicode()
    {
        var result = ShellLogic.SanitizeFileName("报告-最终版.docx");
        Assert.Equal("报告-最终版.docx", result);
    }

    // ---- SuggestDownloadName ----

    [Fact]
    public void SuggestDownloadName_UsesContentDisposition()
    {
        var name = ShellLogic.SuggestDownloadName(
            "attachment; filename=\"data.csv\"", "http://127.0.0.1:3080/api/export", "text/csv");
        Assert.Equal("data.csv", name);
    }

    [Fact]
    public void SuggestDownloadName_FallsBackToUriSegment()
    {
        var name = ShellLogic.SuggestDownloadName(null, "http://127.0.0.1:3080/files/report.pdf", null);
        Assert.Equal("report.pdf", name);
    }

    [Fact]
    public void SuggestDownloadName_BlobGetsMimeExtension()
    {
        var name = ShellLogic.SuggestDownloadName(null, "blob:https://127.0.0.1/uuid", "image/png");
        Assert.EndsWith(".png", name);
        Assert.StartsWith("dsh-", name);
    }

    // ---- ResolveNpmRegistry ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveNpmRegistry_Empty_ReturnsNull(string? env)
    {
        Assert.Null(ShellLogic.ResolveNpmRegistry(env));
    }

    [Fact]
    public void ResolveNpmRegistry_ReturnsTrimmedValue()
    {
        Assert.Equal("https://registry.npmmirror.com",
            ShellLogic.ResolveNpmRegistry("  https://registry.npmmirror.com  "));
    }

    // ---- BuildDshWebArgs ----

    [Fact]
    public void BuildDshWebArgs_UsesResolvedPort()
    {
        var args = ShellLogic.BuildDshWebArgs(9000);
        Assert.Equal("web", args[0]);
        Assert.Equal("127.0.0.1", args[2]);
        Assert.Equal("--port", args[3]);
        Assert.Equal("9000", args[4]);
        Assert.Contains("--no-open", args);
    }

    // ---- ParseDshWebUrl ----

    [Theory]
    [InlineData("dsh web: http://127.0.0.1:3080/?token=abc123", "http://127.0.0.1:3080/?token=abc123")]
    [InlineData("dsh web: http://127.0.0.1:3080", "http://127.0.0.1:3080/")]
    [InlineData("  dsh web:  http://127.0.0.1:9000/?token=xyz  ", "http://127.0.0.1:9000/?token=xyz")]
    [InlineData("dsh web: http://localhost:3080/?token=t", "http://localhost:3080/?token=t")]
    public void ParseDshWebUrl_ExtractsUrl(string line, string expected)
    {
        Assert.Equal(expected, ShellLogic.ParseDshWebUrl(line));
    }

    [Fact]
    public void ParseDshWebUrl_StripsAnsi()
    {
        var line = "\u001b[32mdsh web:\u001b[0m http://127.0.0.1:3080/?token=abc";
        Assert.Equal("http://127.0.0.1:3080/?token=abc", ShellLogic.ParseDshWebUrl(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("listening on 3080")]
    [InlineData("dsh web starting")]
    [InlineData("web: http://127.0.0.1:3080")]
    public void ParseDshWebUrl_NonMatch_ReturnsNull(string? line)
    {
        Assert.Null(ShellLogic.ParseDshWebUrl(line));
    }

    // ---- FormatRuntimeSummary ----

    [Fact]
    public void FormatRuntimeSummary_AllInstalled_ListsVersions()
    {
        var probe = new ShellLogic.RuntimeProbe(true, "v22.14.0", true, true);
        Assert.Equal("Node.js 已安装 (v22.14.0) · npm 已安装 · dsh 已安装",
            ShellLogic.FormatRuntimeSummary(probe));
    }

    [Fact]
    public void FormatRuntimeSummary_DshMissing_NotesNpxFallback()
    {
        var probe = new ShellLogic.RuntimeProbe(true, "v20.11.1", true, false);
        Assert.Equal("Node.js 已安装 (v20.11.1) · npm 已安装 · dsh 未安装(将自动用 npx 启动)",
            ShellLogic.FormatRuntimeSummary(probe));
    }

    [Fact]
    public void FormatRuntimeSummary_NodeMissing_AppendsActionableHint()
    {
        var probe = new ShellLogic.RuntimeProbe(false, null, false, false);
        Assert.Equal("Node.js 未安装 · npm 未安装 · dsh 未安装(将自动用 npx 启动) —— 无法启动 dsh 服务,请先安装 Node.js",
            ShellLogic.FormatRuntimeSummary(probe));
    }
}
