# 本地发布:
#   ./Scripts/build.ps1
#   ./Scripts/build.ps1 -IconSet deepseek     # 第二套图标(程序 + 任务栏),托盘图标不变
# CI 发布(GitHub Actions):
#   ./Scripts/build.ps1 [-Tag v1.2.0] [-IconSet deepseek]
param(
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "build",
    # CI 打 tag 时可显式传入(如 v1.1.0);空则 git describe
    [string]$Tag = "",
    # 图标集:default = DeepSeek 鲸鱼;deepseek = 拟人形象(程序图标 + 窗口/任务栏图标)
    [ValidateSet("default", "deepseek")]
    [string]$IconSet = "default"
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

Write-Host "发布 DshDesktop $version ($Runtime, net48 Costura 单文件, 图标集 $IconSet)..." -ForegroundColor Cyan

$publishDir = Join-Path $root "$OutputDir/publish"
# 同一次构建可能连编两套图标:清掉上一套的产物,避免残留
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
$publishArgs = @(
    (Join-Path $root "DshDesktop"),
    "-c", "Release",
    "-r", $Runtime,
    "-o", $publishDir,
    # 写进 exe 的文件属性:FileVersion / ProductVersion
    "-p:Version=$version",
    "-p:IconSet=$IconSet"
)

dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

if ($IconSet -eq "default") { $suffix = "" } else { $suffix = "-$IconSet" }
$zipName = "dsh-desktop$suffix-$version-$Runtime.zip"
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
