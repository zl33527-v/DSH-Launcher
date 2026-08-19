# 构建 DSH启动器.exe（窗口版，内置 Node.js 安装包）
$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = 'E:\项目\DSH启动器\DSH启动器.exe'
$msi = Join-Path $src 'node-v24.19.0-x64.msi'
$art = Join-Path $src 'maid_sidebar.png'
if (-not (Test-Path $msi)) { Write-Host "缺少内置安装包: $msi"; exit 1 }
if (-not (Test-Path $art)) { Write-Host "缺少角色素材: $art"; exit 1 }
& $csc /nologo /target:winexe /optimize+ /codepage:65001 `
    "/win32icon:$src\whale.ico" `
    "/resource:$msi,node-v24.19.0-x64.msi" `
    "/resource:$art,maid_sidebar.png" `
    "/out:$out" `
    /r:System.dll /r:System.Core.dll /r:System.Net.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "$src\WhaleLauncher.cs"
if ($LASTEXITCODE -eq 0) {
    Write-Host "OK -> $out ($([math]::Round((Get-Item $out).Length/1MB,1)) MB)"
} else {
    Write-Host "BUILD FAILED (exit $LASTEXITCODE)"
    exit 1
}