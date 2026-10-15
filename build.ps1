#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ClientPath
)

$ErrorActionPreference = 'Stop'
$versionOutput = & dotnet msbuild (Join-Path $PSScriptRoot 'src/Service/RdpSessionBootstrap.csproj') -nologo -getProperty:Version
$versionExit = $LASTEXITCODE
if ($versionExit -ne 0) { throw "读取项目版本失败 (exit $versionExit)." }
$version = ($versionOutput -join "`n").Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "项目版本必须为 major.minor.patch: $version" }
$licenses = @('LICENSE-FreeRDP.txt', 'LICENSE-OpenSSL.txt', 'LICENSE-zlib.txt')
if (-not $PSBoundParameters.ContainsKey('ClientPath')) {
    $clientDirectory = Join-Path $PSScriptRoot 'artifacts/dependencies/client-bin'
    $ClientPath = Join-Path $clientDirectory 'sfreerdp.exe'
    $requiredClientFiles = @($ClientPath) + @($licenses | ForEach-Object { Join-Path $clientDirectory $_ })
    if ($requiredClientFiles | Where-Object { -not [IO.File]::Exists($_) }) {
        & (Join-Path $PSScriptRoot 'init-dependencies.ps1')
    }
}
$client = Get-Item -LiteralPath $ClientPath
if ($client.PSIsContainer -or $client.Name -ne 'sfreerdp.exe') {
    throw 'ClientPath 必须指向已编译的 sfreerdp.exe.'
}
foreach ($license in $licenses) {
    if (-not [IO.File]::Exists((Join-Path $client.DirectoryName $license))) {
        throw "客户端目录缺少 $license. 参见 BUILD.md."
    }
}

$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$package = [IO.Path]::GetFullPath((Join-Path $artifacts 'package'))
# 只清理本仓库固定的打包目录.
if (-not $package.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '打包目录超出 artifacts.'
}
if ([IO.Directory]::Exists($package)) { [IO.Directory]::Delete($package, $true) }
[void][IO.Directory]::CreateDirectory($package)

foreach ($project in @('src/Service/RdpSessionBootstrap.csproj', 'src/Setup/Setup.csproj')) {
    & dotnet publish (Join-Path $PSScriptRoot $project) -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $package
    $publishExit = $LASTEXITCODE
    if ($publishExit -ne 0) { throw "dotnet publish 失败: $project (exit $publishExit)." }
}
$clientPackage = Join-Path $package 'client'
[void][IO.Directory]::CreateDirectory($clientPackage)
Copy-Item -LiteralPath $client.FullName -Destination (Join-Path $clientPackage 'sfreerdp.exe')
foreach ($license in $licenses) {
    Copy-Item -LiteralPath (Join-Path $client.DirectoryName $license) -Destination $clientPackage
}
if ([IO.File]::Exists((Join-Path $PSScriptRoot 'README.md'))) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $package
}
foreach ($required in @('Setup.exe', 'Setup.runtimeconfig.json', 'RdpSessionBootstrap.exe', 'RdpSessionBootstrap.runtimeconfig.json', 'client/sfreerdp.exe')) {
    if (-not [IO.File]::Exists((Join-Path $package $required))) { throw "打包文件缺失: $required." }
}
Write-Host "安装包目录: $package"
$archive = Join-Path $artifacts 'payload.zip'
if ([IO.File]::Exists($archive)) { [IO.File]::Delete($archive) }
[IO.Compression.ZipFile]::CreateFromDirectory($package, $archive)
$bundle = Join-Path $artifacts 'bundle'
& dotnet publish (Join-Path $PSScriptRoot 'src/Bundle/Bundle.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false "-p:PackageArchive=$archive" -o $bundle
$publishExit = $LASTEXITCODE
if ($publishExit -ne 0) { throw "单文件安装器构建失败 (exit $publishExit)." }
$installer = Join-Path $artifacts "RdpSessionBootstrap-Setup-$version.exe"
Copy-Item -LiteralPath (Join-Path $bundle 'RdpSessionBootstrap-Setup.exe') -Destination $installer
Write-Host "单文件安装器: $installer"
