param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releaseRoot = Join-Path $repoRoot 'releases'
$releaseDir = Join-Path $releaseRoot $Version
$vsCodeDir = Join-Path $repoRoot 'src\AutoCommit.Vscode'
$dotnetLocal = Join-Path $env:LOCALAPPDATA 'AutoCommit\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $dotnetLocal) { $dotnetLocal } else { 'dotnet' }

if (Test-Path -LiteralPath $releaseDir) {
    throw "发布目录已存在：$releaseDir。请使用新版本号，避免覆盖已发布的安装包。"
}

function Invoke-Checked {
    param([string]$File, [string[]]$Arguments, [string]$Directory)
    Push-Location $Directory
    try {
        & $File @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$File 执行失败，退出码 $LASTEXITCODE：$($Arguments -join ' ')"
        }
    } finally {
        Pop-Location
    }
}

function Set-VsixVersion {
    param([string]$Manifest, [string]$Value)
    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.Load($Manifest)
    $identity = $document.GetElementsByTagName('Identity').Item(0)
    if ($null -eq $identity) { throw "VSIX 清单缺少 Identity：$Manifest" }
    $identity.SetAttribute('Version', $Value)
    $document.Save($Manifest)
}

$sdkVersion = & $dotnet --version
if ($LASTEXITCODE -ne 0 -or [int]($sdkVersion.Split('.')[0]) -lt 8) {
    throw '发布需要 .NET 8 或更新版本的 SDK。'
}

Set-VsixVersion (Join-Path $repoRoot 'src\AutoCommit.Vsix.VS2019\source.extension.vsixmanifest') $Version
Set-VsixVersion (Join-Path $repoRoot 'src\AutoCommit.Vsix\source.extension.vsixmanifest') $Version
$currentVsCodeVersion = (Get-Content -LiteralPath (Join-Path $vsCodeDir 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json).version
if ($currentVsCodeVersion -ne $Version) {
    Invoke-Checked -File 'npm.cmd' -Arguments @('version', $Version, '--no-git-tag-version', '--ignore-scripts') -Directory $vsCodeDir
}

Invoke-Checked -File $dotnet -Arguments @('build', 'AutoCommit.sln', '-c', 'Release', '-t:Rebuild', '--nologo') -Directory $repoRoot
Invoke-Checked -File $dotnet -Arguments @('test', 'tests\AutoCommit.Tests\AutoCommit.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore', '--nologo') -Directory $repoRoot
Invoke-Checked -File 'npm.cmd' -Arguments @('ci', '--no-audit', '--no-fund') -Directory $vsCodeDir
Invoke-Checked -File 'npm.cmd' -Arguments @('test') -Directory $vsCodeDir
Invoke-Checked -File 'npm.cmd' -Arguments @('run', 'package') -Directory $vsCodeDir

$packages = @(
    @{ Source = (Join-Path $repoRoot 'src\AutoCommit.Vsix.VS2019\bin\Release\net472\AutoCommit.Vsix.VS2019.vsix'); Name = "AutoCommit-VS2019-$Version.vsix" },
    @{ Source = (Join-Path $repoRoot 'src\AutoCommit.Vsix\bin\Release\net472\AutoCommit.Vsix.vsix'); Name = "AutoCommit-VS2022-$Version.vsix" },
    @{ Source = (Join-Path $vsCodeDir "autocommit-$Version.vsix"); Name = "AutoCommit-VSCode-$Version.vsix" }
)
foreach ($package in $packages) {
    if (-not (Test-Path -LiteralPath $package.Source)) { throw "缺少安装包：$($package.Source)" }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in $packages) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.Source)
    try {
        if ($package.Name -like '*VSCode*') {
            $entry = $archive.GetEntry('extension/package.json')
            if ($null -eq $entry) { throw "安装包缺少 package.json：$($package.Source)" }
            $reader = New-Object System.IO.StreamReader($entry.Open())
            try { $actualVersion = (ConvertFrom-Json $reader.ReadToEnd()).version }
            finally { $reader.Dispose() }
        } else {
            $entry = $archive.GetEntry('extension.vsixmanifest')
            if ($null -eq $entry) { throw "安装包缺少 VSIX 清单：$($package.Source)" }
            $reader = New-Object System.IO.StreamReader($entry.Open())
            try { [xml]$manifest = $reader.ReadToEnd() }
            finally { $reader.Dispose() }
            $actualVersion = $manifest.PackageManifest.Metadata.Identity.Version
        }
        if ($actualVersion -ne $Version) {
            throw "安装包版本不匹配：$($package.Source)，实际 $actualVersion，期望 $Version"
        }
    } finally {
        $archive.Dispose()
    }
}

New-Item -ItemType Directory -Path $releaseDir | Out-Null
$checksums = @()
foreach ($package in $packages) {
    $target = Join-Path $releaseDir $package.Name
    Copy-Item -LiteralPath $package.Source -Destination $target
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums += "$hash  $($package.Name)"
}
Set-Content -LiteralPath (Join-Path $releaseDir 'SHA256SUMS.txt') -Value $checksums -Encoding ascii

$releaseNotes = @"
AutoCommit $Version 本地批量发布
开发者：Jason Jia

安装包：
- AutoCommit-VS2019-$Version.vsix：Visual Studio 2019
- AutoCommit-VS2022-$Version.vsix：Visual Studio 2022 17.14
- AutoCommit-VSCode-$Version.vsix：VS Code 桌面版

三种安装包互不通用。SHA-256 校验值见 SHA256SUMS.txt。
发布门槛：Visual Studio Release 重建、.NET 自动化测试、VS Code Node 测试与 VS Code VSIX 打包均通过。
当前仍需在 Visual Studio、VS Code 和真实 TFVC 服务中完成 GUI 与服务器联调。此目录为本地发布，不代表已上传扩展市场。
"@
Set-Content -LiteralPath (Join-Path $releaseDir 'README.txt') -Value $releaseNotes -Encoding utf8

$latest = @"
# AutoCommit 最新本地发布：$Version

| 环境 | 安装包 |
| --- | --- |
| Visual Studio 2019 | [下载](./$Version/AutoCommit-VS2019-$Version.vsix) |
| Visual Studio 2022 17.14 | [下载](./$Version/AutoCommit-VS2022-$Version.vsix) |
| VS Code 桌面版 | [下载](./$Version/AutoCommit-VSCode-$Version.vsix) |

校验值见 [SHA256SUMS.txt](./$Version/SHA256SUMS.txt)。Visual Studio 与 VS Code 的 VSIX 不能互相安装。安装包仅在本仓库本地发布，未完成 GUI 和 TFVC 服务器联调。
"@
Set-Content -LiteralPath (Join-Path $releaseRoot 'LATEST.md') -Value $latest -Encoding utf8
Write-Host "已发布 $Version 到 $releaseDir"
