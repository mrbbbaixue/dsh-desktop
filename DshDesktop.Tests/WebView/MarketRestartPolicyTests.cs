using DshDesktop.WebView;
using Xunit;

namespace DshDesktop.Tests.WebView;

/// <summary>
/// 市场一键重启的接管规则:只认 POST 到市场那两个重启路径、且来自我们托管的 origin;
/// 忙碌时照市场那样 409 挡下,而不是把 dsh 杀在半写入的安装过程中。
/// </summary>
public class MarketRestartPolicyTests
{
    private const string Origin = "http://127.0.0.1:3080";

    /// <summary>真实 status 载荷(取自 dsh-market 1.45.1 运行实例,已裁剪)。</summary>
    private const string IdleStatusPayload = """
        {"active":false,"target":"","seconds":0,"lastLine":"","phase":null,"done":0,"total":null,
         "currentPackage":null,"ndjson":false,"error":null,"cancelling":false,"busy":false,"pnpm":true,
         "boot":"67320-1789119334757","version":"1.45.1","channel":"stable","region":"china",
         "installed":{"dshmarket":"1.45.1","dsh-context":"0.49.4"}}
        """;

    private static string Url(string path) => Origin + path;

    [Theory]
    [InlineData("POST", "/dsh-market/restart")]
    [InlineData("POST", "/dsh-market/api/v1/restart")]
    [InlineData("POST", "/dsh-market/restart?retry=1")]
    public void IsRestartRequest_TakesOverMarketRestartPost(string method, string path) =>
        Assert.True(MarketRestartPolicy.IsRestartRequest(method, Url(path), Origin));

    [Theory]
    [InlineData("GET", "/dsh-market/restart")]      // 非 POST 交给 dsh 自己回 405,不算一次重启
    [InlineData("POST", "/dsh-market/status")]      // 探状态用的只读接口,不能被拦
    [InlineData("POST", "/dsh-market/restart/")]    // 尾斜杠服务端也认不出
    [InlineData("POST", "/dsh-market/restarts")]
    [InlineData("POST", "/dsh-market/api/v1/rollback")]
    public void IsRestartRequest_LeavesEveryOtherRequestAlone(string method, string path) =>
        Assert.False(MarketRestartPolicy.IsRestartRequest(method, Url(path), Origin));

    [Fact]
    public void IsRestartRequest_RejectsAnotherOrigin() =>
        Assert.False(MarketRestartPolicy.IsRestartRequest(
            "POST", Url(MarketRestartPolicy.LegacyPath), "http://127.0.0.1:3081"));

    [Fact]
    public void IsRestartRequest_RejectsMissingOrRelativeParts()
    {
        Assert.False(MarketRestartPolicy.IsRestartRequest("POST", "/dsh-market/restart", Origin));
        Assert.False(MarketRestartPolicy.IsRestartRequest("POST", null, Origin));
        Assert.False(MarketRestartPolicy.IsRestartRequest(null, Url(MarketRestartPolicy.LegacyPath), Origin));
    }

    [Fact]
    public void IsApiV1_OnlyForTheApiPath()
    {
        Assert.True(MarketRestartPolicy.IsApiV1(Url(MarketRestartPolicy.ApiV1Path)));
        Assert.False(MarketRestartPolicy.IsApiV1(Url(MarketRestartPolicy.LegacyPath)));
        Assert.False(MarketRestartPolicy.IsApiV1(null));
    }

    [Fact]
    public void StatusBusy_DetectsEitherFlag()
    {
        Assert.True(MarketRestartPolicy.StatusBusy("""{"busy":true}"""));
        Assert.True(MarketRestartPolicy.StatusBusy("""{"active":true,"busy":false}"""));
        Assert.True(MarketRestartPolicy.StatusBusy("""{"busy" : true}"""));
    }

    [Fact]
    public void StatusBusy_IdlePayloadIsNotBusy() =>
        Assert.False(MarketRestartPolicy.StatusBusy(IdleStatusPayload));

    [Fact]
    public void StatusBusy_ToleratesGarbage()
    {
        Assert.False(MarketRestartPolicy.StatusBusy(null));
        Assert.False(MarketRestartPolicy.StatusBusy(""));
        Assert.False(MarketRestartPolicy.StatusBusy("<html>401</html>"));
        // 值里出现 busy 字样不该被当成操作锁
        Assert.False(MarketRestartPolicy.StatusBusy("""{"error":"another install is already busy"}"""));
    }

    [Fact]
    public void AcceptedBody_LegacyShapeCarriesOk()
    {
        var body = MarketRestartPolicy.AcceptedBody(Url(MarketRestartPolicy.LegacyPath));
        Assert.Contains("\"ok\":true", body);
        Assert.Contains("\"managedBy\":\"desktop-shell\"", body);
        Assert.DoesNotContain("schema", body);
    }

    [Fact]
    public void AcceptedBody_V1ShapeKeepsSchemaAndNestedResult()
    {
        var body = MarketRestartPolicy.AcceptedBody(Url(MarketRestartPolicy.ApiV1Path));
        Assert.Contains("\"schema\":\"dsh-market/update-api/v1\"", body);
        Assert.Contains("\"ok\":true", body);          // 顶层:legacy 客户端读这个
        Assert.Contains("\"result\":{\"ok\":true", body); // v1 客户端读这个
    }

    [Fact]
    public void ErrorBody_KeepsMarketWordingAndEscapesQuotes()
    {
        var legacy = Url(MarketRestartPolicy.LegacyPath);
        // $$ 原始字符串:单花括号是字面量,{{ }} 才是插值
        Assert.Equal($$"""{"error":"{{MarketRestartPolicy.BusyMessage}}"}""",
            MarketRestartPolicy.ErrorBody(MarketRestartPolicy.BusyMessage, legacy));
        Assert.Equal("""{"error":"say \"hi\""}""",
            MarketRestartPolicy.ErrorBody("say \"hi\"", legacy));
    }
}
