# make-release.ps1 — 发布构建：编译 → 复制产物 → 计算哈希 → 生成 SHA256SUMS → 输出签名指引
# 用法:  powershell -ExecutionPolicy Bypass -File make-release.ps1 -Tag v1.0
param(
    [string]$Tag = "v1.0"
)
$ErrorActionPreference = 'Stop'
$src  = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = 'E:\项目\DSH启动器\DSH启动器.exe'
$msi  = Join-Path $src 'node-v24.19.0-x64.msi'
$rel  = Join-Path $src ("release-" + $Tag)
New-Item -ItemType Directory -Force -Path $rel | Out-Null

Write-Host "== 1/4 构建 =="
& (Join-Path $src 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw '构建失败，终止发布' }

Write-Host "== 2/4 复制发布产物 =="
$exeName = 'DSH启动管家.exe'
$exeOut = Join-Path $rel $exeName
Copy-Item $out $exeOut -Force
foreach ($doc in @('使用说明.txt', '简介.md')) {
    $d = Join-Path 'E:\项目\DSH启动器' $doc
    if (Test-Path $d) { Copy-Item $d (Join-Path $rel $doc) -Force }
}

Write-Host "== 3/4 计算哈希 =="
$exeSha256 = (Get-FileHash -Path $exeOut -Algorithm SHA256).Hash
$exeSha1   = (Get-FileHash -Path $exeOut -Algorithm SHA1).Hash
$msiMd5    = (Get-FileHash -Path $msi -Algorithm MD5).Hash
$msiSha256 = (Get-FileHash -Path $msi -Algorithm SHA256).Hash

# 发布前防线：内置安装包 MD5 必须与代码内期望值一致，否则拒绝出包
if ($msiMd5 -ne '184B26AF284EA9818B6E6F82CC90EAF5') {
    throw "内置安装包 MD5($msiMd5) 与代码内期望值不一致，请先核对 WhaleLauncher.cs 的 EmbeddedMsiExpectedMd5"
}

$sums = @(
    "$exeSha256  $exeName (SHA-256)",
    "$exeSha1  $exeName (SHA-1)",
    "$msiMd5  node-v24.19.0-x64.msi (MD5)",
    "$msiSha256  node-v24.19.0-x64.msi (SHA-256)"
)
$sumsFile = Join-Path $rel 'SHA256SUMS.txt'
[System.IO.File]::WriteAllLines($sumsFile, $sums, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "== 4/4 发布清单 =="
Write-Host "发布目录: $rel"
Get-ChildItem $rel | ForEach-Object { "  {0}  {1:N0} bytes" -f $_.Name, $_.Length }

Write-Host ""
Write-Host "================= 签名指引（P0：发布前必须） ================="
Write-Host "1) 代码签名（购买 OV/EV 证书后）:"
Write-Host "   signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f 你的证书.pfx /p 证书密码 `"$exeOut`""
Write-Host "2) 校验签名:"
Write-Host "   signtool verify /pa /v `"$exeOut`""
Write-Host "3) VirusTotal 多引擎扫描: https://www.virustotal.com（把报告链接贴进 Release）"
Write-Host "4) 上传 release-$Tag 目录到 GitHub Releases / 官网，并在 README 注明唯一官方渠道"
Write-Host ""
Write-Host "SHA256SUMS.txt 内容（随包发布）:"
$sums | ForEach-Object { "  " + $_ }
Write-Host ""
Write-Host "完成。"
