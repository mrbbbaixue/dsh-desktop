using System.Text.RegularExpressions;

namespace DshDesktop.Infrastructure;

/// <summary>安装源:官方(winget 从 nodejs.org 拉 MSI)或国内镜像(npmmirror 的 MSI 与 npm registry)。</summary>
internal enum InstallSourceKind
{
    Official,
    Mirror,
}

/// <summary>
/// 安装源相关的纯策略:URL 拼接与版本清单解析。独立成类以便单元测试覆盖,不联网。
/// npmmirror 的版本清单与官方 dist/index.json 是同一份文件、格式一致(实测),
/// 所以解析逻辑一套即可。
/// </summary>
internal static class InstallSource
{
    /// <summary>npmmirror 的 npm registry(仅镜像源使用)。</summary>
    public const string NpmMirrorRegistry = "https://registry.npmmirror.com";

    /// <summary>winget 包 ID:Node.js LTS(npm/npx 随它一起装)。</summary>
    public const string NodeWingetId = "OpenJS.NodeJS.LTS";

    /// <summary>版本清单地址:官方源与镜像源的返回格式一致。</summary>
    public static string NodeIndexUrl(InstallSourceKind kind) => kind == InstallSourceKind.Mirror
        ? $"{NpmMirrorRegistry}/-/binary/node/index.json"
        : "https://nodejs.org/dist/index.json";

    /// <summary>Windows x64 的 MSI 地址(两个源文件名同名,镜像会 302 到 cdn.npmmirror.com)。</summary>
    public static string NodeMsiUrl(InstallSourceKind kind, string version) => kind == InstallSourceKind.Mirror
        ? $"{NpmMirrorRegistry}/-/binary/node/{version}/node-{version}-x64.msi"
        : $"https://nodejs.org/dist/{version}/node-{version}-x64.msi";

    /// <summary>npm 镜像源:官方源返回 null(沿用本机 npm 配置),镜像源返回 npmmirror。</summary>
    public static string? NpmRegistry(InstallSourceKind kind) =>
        kind == InstallSourceKind.Mirror ? NpmMirrorRegistry : null;

    // 清单是"按版本倒序"的对象数组,每项恰好一个 version 与一个 lts;按出现次序配对即可
    private static readonly Regex VersionField = new(@"""version"":\s*""(v[\d.]+)""", RegexOptions.Compiled);
    private static readonly Regex LtsField = new(@"""lts"":\s*(false|""[^""]*"")", RegexOptions.Compiled);

    /// <summary>
    /// 取清单里第一个非 false 的 lts 版本(即最新 LTS);解析不出来返回 null。
    /// </summary>
    public static string? SelectLatestLts(string? indexJson)
    {
        if (string.IsNullOrWhiteSpace(indexJson)) return null;
        var versions = VersionField.Matches(indexJson!);
        var lts = LtsField.Matches(indexJson!);
        for (var i = 0; i < versions.Count && i < lts.Count; i++)
        {
            if (!string.Equals(lts[i].Groups[1].Value, "false", StringComparison.Ordinal))
                return versions[i].Groups[1].Value;
        }
        return null;
    }
}
