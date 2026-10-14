#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ClientPath,
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'
$client = Get-Item -LiteralPath $ClientPath
if ($client.PSIsContainer -or $client.Name -ne 'sfreerdp.exe') {
    throw 'ClientPath 必须指向已编译的 sfreerdp.exe.'
}
$licenses = @('LICENSE-FreeRDP.txt', 'LICENSE-OpenSSL.txt', 'LICENSE-zlib.txt')
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
    & dotnet publish (Join-Path $PSScriptRoot $project) -c Release -r win-x64 --self-contained true -o $package
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
if (-not $SkipZip) {
    $archive = Join-Path $artifacts 'RdpSessionBootstrap-win-x64.zip'
    if ([IO.File]::Exists($archive)) { [IO.File]::Delete($archive) }
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $archive)
    Write-Host "安装包 ZIP: $archive"
}
