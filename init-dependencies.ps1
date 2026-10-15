#Requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory = (Join-Path $PSScriptRoot 'artifacts/dependencies'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Requires Git, CMake, and Visual Studio 2022/2026 with MSVC x64 and Windows SDK.
foreach ($tool in @('git', 'cmake')) { $null = Get-Command $tool -ErrorAction Stop }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path $vswhere)) { throw 'Install Visual Studio with the C++ desktop workload first.' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json
$code = $LASTEXITCODE
if ($code -ne 0 -or !$vs) { throw 'Visual Studio C++ x64 toolchain was not found.' }
$vs = @($vs)[0]
$major = ([version]$vs.installationVersion).Major
$generator = switch ($major) { 17 { 'Visual Studio 17 2022' } 18 { 'Visual Studio 18 2026' } default { throw "Unsupported Visual Studio version: $major" } }
$WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
$null = [IO.Directory]::CreateDirectory($WorkDirectory)
$commit = '357b1a6f8343064ca982a20771e420fcecfe7efc'
$vcpkg = Join-Path $WorkDirectory 'vcpkg'
$source = Join-Path $WorkDirectory 'src/FreeRDP-3.32.2'
$zip = Join-Path $WorkDirectory 'freerdp.zip'
if (!(Test-Path $zip)) { Invoke-WebRequest 'https://github.com/FreeRDP/FreeRDP/archive/refs/tags/3.32.2.zip' -OutFile $zip }
if ((Get-FileHash $zip).Hash -ne '49BDFE90DDFBC1152FF8EBBB2F81EDFF0C753533119FBFC76A5A5A8236F1B5D7') { throw 'FreeRDP archive SHA256 mismatch.' }
if (!(Test-Path $source)) { Expand-Archive $zip (Join-Path $WorkDirectory 'src') }
# Isolate git apply from the enclosing project and its ignored artifacts directory.
if (!(Test-Path "$source/.git")) {
    & git -C $source init --quiet
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw 'Cannot initialize the isolated FreeRDP source directory.' }
}
# FreeRDP's CMake Git helpers require HEAD even for this local build boundary.
& git -C $source rev-parse --verify HEAD 2>$null | Out-Null
$code = $LASTEXITCODE
if ($code -ne 0) {
    & git -C $source -c user.name=Build -c user.email=build@localhost commit --allow-empty --no-verify -m 'Local build source boundary'
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw 'Cannot initialize the FreeRDP build revision.' }
}
$vcpkgZip = Join-Path $WorkDirectory "vcpkg-$commit.zip"
if (!(Test-Path $vcpkgZip)) { Invoke-WebRequest "https://github.com/microsoft/vcpkg/archive/$commit.zip" -OutFile $vcpkgZip }
if ((Get-FileHash $vcpkgZip).Hash -ne '5C514B003C331A9F8F375AB61137CC48BC2B7B211B2221562C7830BC7941DF6C') { throw 'vcpkg archive SHA256 mismatch.' }
if (!(Test-Path $vcpkg)) {
    Expand-Archive $vcpkgZip $WorkDirectory
    Rename-Item (Join-Path $WorkDirectory "vcpkg-$commit") 'vcpkg'
    Set-Content (Join-Path $vcpkg '.bootstrap-commit') $commit
}
if (!(Test-Path "$vcpkg/.bootstrap-commit") -or (Get-Content "$vcpkg/.bootstrap-commit" -Raw).Trim() -ne $commit) {
    throw 'Cached vcpkg revision is unknown. Use a fresh WorkDirectory.'
}
foreach ($name in @('windows-headless.patch', 'sample-no-channels.patch')) {
    $patch = Join-Path $PSScriptRoot "third_party/freerdp/$name"
    & git -C $source apply --reverse --check $patch 2>$null
    $code = $LASTEXITCODE
    if ($code -eq 0) { continue }
    & git -C $source apply --check $patch
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "Cannot apply $name to the cached source." }
    & git -C $source apply $patch
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "Failed to apply $name." }
}
if (!(Test-Path "$vcpkg/vcpkg.exe")) {
    & "$vcpkg/bootstrap-vcpkg.bat" -disableMetrics
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw 'vcpkg bootstrap failed.' }
}
& "$vcpkg/vcpkg.exe" install openssl:x64-windows-static zlib:x64-windows-static
$code = $LASTEXITCODE
if ($code -ne 0) { throw 'Dependency build failed.' }
$options = @('-S', $source, '-B', "$WorkDirectory/obj", '-G', $generator, '-A', 'x64',
    "-DCMAKE_GENERATOR_INSTANCE=$($vs.installationPath)",
    "-DCMAKE_TOOLCHAIN_FILE=$vcpkg/scripts/buildsystems/vcpkg.cmake",
    '-DVCPKG_TARGET_TRIPLET=x64-windows-static', '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded',
    "-DCMAKE_INSTALL_PREFIX=$WorkDirectory/install", '-DBUILD_SHARED_LIBS=OFF', '-DWITH_SAMPLE=ON', '-DWITH_OPENSSL=ON')
foreach ($option in @('WITH_CLIENT_WINDOWS','WITH_OPUS','WITH_CLIENT_SDL','WITH_SERVER','WITH_SHADOW','WITH_PROXY',
    'WITH_PLATFORM_SERVER','WITH_CHANNELS','WITH_SMARTCARD_EMULATE','WITH_SMARTCARD_PCSC','WITH_WINPR_TOOLS',
    'WITH_FFMPEG','WITH_DSP_FFMPEG','WITH_VIDEO_FFMPEG','WITH_OPENH264','WITH_MBEDTLS','WITH_URIPARSER','WITH_AAD',
    'WITH_PKCS11','WITH_JPEG','WITH_CAIRO','WITH_RDTK','WITH_MANPAGES','BUILD_TESTING','WITH_WINMM','WITH_SWSCALE',
    'WITH_WEBVIEW','WITH_MEDIA_FOUNDATION','WITH_GFX_H264')) { $options += "-D$option=OFF" }
& cmake @options
$code = $LASTEXITCODE
if ($code -ne 0) { throw 'FreeRDP configure failed.' }
& cmake --build "$WorkDirectory/obj" --config Release --target sfreerdp --parallel
$code = $LASTEXITCODE
if ($code -ne 0) { throw 'FreeRDP build failed.' }
$client = "$WorkDirectory/obj/client/Sample/Release/sfreerdp.exe"
foreach ($argument in @('/version', '/help')) {
    & $client $argument
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "FreeRDP $argument check failed." }
}
$clientBin = Join-Path $WorkDirectory 'client-bin'
$null = [IO.Directory]::CreateDirectory($clientBin)
Copy-Item "$WorkDirectory/obj/client/Sample/Release/sfreerdp.exe" $clientBin
Copy-Item "$source/LICENSE" "$clientBin/LICENSE-FreeRDP.txt"
Copy-Item "$vcpkg/installed/x64-windows-static/share/openssl/copyright" "$clientBin/LICENSE-OpenSSL.txt"
Copy-Item "$vcpkg/installed/x64-windows-static/share/zlib/copyright" "$clientBin/LICENSE-zlib.txt"
Write-Host "Client ready: $clientBin/sfreerdp.exe"
