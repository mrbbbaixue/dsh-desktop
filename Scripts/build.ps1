# 本地发布:
#   ./Scripts/build.ps1
# CI 发布(GitHub Actions):
#   ./Scripts/build.ps1 [-Tag v1.2.0]
param(
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "build",
    # CI 打 tag 时可显式传入(如 v1.1.0);空则 git describe
    [string]$Tag = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not [string]::IsNullOrWhiteSpace($Tag)) {
    $version = $Tag.TrimStart('v')
} else {
    # 版本:优先取最近 git tag(vX.Y.Z),否则 0.1.0
    # (PS 5.1 在 EAP=Stop 下会把原生命令 stderr 抛为错误,此处临时降级)
    # 注意:不能接 Select-Object 管道——它会提前关闭 git 的 stdout 导致 $LASTEXITCODE
    # 变 $null(而 $null -ne 0 为真),误回退 0.1.0。
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $version = & git -C $root describe --tags --abbrev=0 2>$null
    $ErrorActionPreference = $prevEap
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) { $version = "0.1.0" }
    $version = $version.TrimStart('v')
}

Write-Host "发布 DshDesktop $version ($Runtime, net48)..." -ForegroundColor Cyan

$publishDir = Join-Path $root "$OutputDir/publish"
$publishArgs = @(
    (Join-Path $root "DshDesktop"),
    "-c", "Release",
    "-r", $Runtime,
    "-o", $publishDir
)

dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

$zipName = "dsh-desktop-$version-$Runtime.zip"
$zipPath = Join-Path $root "$OutputDir/$zipName"
Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath -Force

# SHA256:按 build 里现有 zip 重写
$outDir = Join-Path $root $OutputDir
$lines = Get-ChildItem $outDir -File -Filter "dsh-desktop-*.zip" | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    "$hash  $($_.Name)"
}
Set-Content -Path (Join-Path $outDir "SHA256SUMS.txt") -Value $lines -Encoding ascii

Write-Host "完成: $OutputDir/$zipName" -ForegroundColor Green
