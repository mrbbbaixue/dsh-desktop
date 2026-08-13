param(
    # 全自包含发布:内置 .NET 运行时,单个 exe 无需安装 .NET Desktop Runtime(体积 ~100MB+)
    [switch]$SelfContained,
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# 版本:优先取最近 git tag(vX.Y.Z),否则 0.1.0
# (PS 5.1 在 EAP=Stop 下会把原生命令 stderr 抛为错误,此处临时降级)
$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$version = & git -C $root describe --tags --abbrev=0 2>$null | Select-Object -First 1
$ErrorActionPreference = $prevEap
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) { $version = "0.1.0" }
$version = $version.TrimStart('v')

$suffix = if ($SelfContained) { "self-contained" } else { "framework-dependent" }
Write-Host "发布 DshDesktop $version ($Runtime, $suffix)..." -ForegroundColor Cyan

$publishDir = Join-Path $root "$OutputDir/publish"
$publishArgs = @(
    (Join-Path $root "src/DshDesktop"),
    "-c", "Release",
    "-r", $Runtime,
    "-o", $publishDir
)
if ($SelfContained) { $publishArgs += "--self-contained" }
else { $publishArgs += "--self-contained", "false" }

dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

# 打 zip(单文件发布产物只有 DshDesktop.exe;含自解压依赖时仍然只有 exe)
$zipName = "dsh-desktop-$version-$Runtime-$suffix.zip"
$zipPath = Join-Path $root "$OutputDir/$zipName"
Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath -Force

# SHA256 校验和
$lines = Get-ChildItem -Recurse $publishDir -File | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    "$hash  $($_.Name)"
}
Set-Content -Path (Join-Path $root "$OutputDir/SHA256SUMS.txt") -Value $lines -Encoding ascii

Write-Host "完成: $OutputDir/$zipName" -ForegroundColor Green
