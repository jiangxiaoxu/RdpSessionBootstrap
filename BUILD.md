# 构建

在 Windows x64 上使用 PowerShell 7 和 .NET 10 SDK. 服务和安装程序发布为 self-contained, 目标机器无需单独安装 .NET. 构建脚本只编译和打包, 不安装或启动服务.

## 已有 FreeRDP 客户端

把已编译的 `sfreerdp.exe` 和三个许可证文件放在同一目录, 然后在仓库根目录运行:

```powershell
./build.ps1 -ClientPath 'D:/build/client-bin/sfreerdp.exe'
```

输出为 `artifacts/package/` 和 `artifacts/RdpSessionBootstrap-win-x64.zip`. `-SkipZip` 可省略 ZIP. 两个项目顺序发布到同一目录, 保留 `Setup.runtimeconfig.json` 和 `RdpSessionBootstrap.runtimeconfig.json`. 客户端及其许可证位于安装包的 `client/` 子目录. 生成目录不提交到 Git.

## 从源码构建 FreeRDP

需要 Visual Studio 2022 的 MSVC x64 C++ 工具链, Windows SDK, CMake 和 Git. 使用 Developer PowerShell, 使 `cmake` 可直接调用. 以下路径均为示例, `$repo` 应设置为本仓库实际路径, `$work` 为独立构建目录.

FreeRDP 源码: https://github.com/FreeRDP/FreeRDP/archive/refs/tags/3.32.2.zip

该 ZIP 的 SHA256: `49BDFE90DDFBC1152FF8EBBB2F81EDFF0C753533119FBFC76A5A5A8236F1B5D7`.

下载 ZIP, 使用 `Get-FileHash -Algorithm SHA256` 核对后解压到 `$work/src/FreeRDP-3.32.2`. 从 https://github.com/microsoft/vcpkg 获取 vcpkg, 放在 `$work/vcpkg`. 已验证的依赖版本为 OpenSSL `3.6.5#1`, zlib `1.3.2#2`, vcpkg tool `2026-09-26`. 原构建使用的 vcpkg ZIP 未记录 ports commit, 上述版本仅是已验证基线, 不保证字节级复现. 其他版本尚未验证. 客户端来源及校验值保存在 `third_party/freerdp/provenance.json`.

按以下顺序应用两个补丁并构建静态依赖. 每个原生命令成功后再继续, 非零退出码时停止.

```powershell
$repo = (Get-Location).Path
$work = 'D:/build/rdp-client'
$source = Join-Path $work 'src/FreeRDP-3.32.2'
$vcpkg = Join-Path $work 'vcpkg'

git -C $source apply (Join-Path $repo 'third_party/freerdp/windows-headless.patch')
git -C $source apply (Join-Path $repo 'third_party/freerdp/sample-no-channels.patch')
& "$vcpkg/bootstrap-vcpkg.bat" -disableMetrics
& "$vcpkg/vcpkg.exe" install openssl:x64-windows-static zlib:x64-windows-static
```

补丁使 Sample 客户端可以在 Windows 无界面运行, 并移除不需要的 channel 加载. 以下是已验证构建使用的 CMake 配置, 使用静态 OpenSSL/zlib 和 `/MT`, 关闭音视频, 重定向及图形客户端依赖:

```powershell
$options = @(
    '-S', $source, '-B', "$work/obj", '-G', 'Visual Studio 17 2022', '-A', 'x64',
    "-DCMAKE_TOOLCHAIN_FILE=$vcpkg/scripts/buildsystems/vcpkg.cmake",
    '-DVCPKG_TARGET_TRIPLET=x64-windows-static',
    '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded',
    "-DCMAKE_INSTALL_PREFIX=$work/install",
    '-DBUILD_SHARED_LIBS=OFF', '-DWITH_SAMPLE=ON',
    '-DWITH_CLIENT_WINDOWS=OFF', '-DWITH_OPUS=OFF', '-DWITH_CLIENT_SDL=OFF',
    '-DWITH_SERVER=OFF', '-DWITH_SHADOW=OFF', '-DWITH_PROXY=OFF',
    '-DWITH_PLATFORM_SERVER=OFF', '-DWITH_CHANNELS=OFF',
    '-DWITH_SMARTCARD_EMULATE=OFF', '-DWITH_SMARTCARD_PCSC=OFF',
    '-DWITH_WINPR_TOOLS=OFF', '-DWITH_FFMPEG=OFF', '-DWITH_DSP_FFMPEG=OFF',
    '-DWITH_VIDEO_FFMPEG=OFF', '-DWITH_OPENH264=OFF', '-DWITH_OPENSSL=ON',
    '-DWITH_MBEDTLS=OFF', '-DWITH_URIPARSER=OFF', '-DWITH_AAD=OFF',
    '-DWITH_PKCS11=OFF', '-DWITH_JPEG=OFF', '-DWITH_CAIRO=OFF',
    '-DWITH_RDTK=OFF', '-DWITH_MANPAGES=OFF', '-DBUILD_TESTING=OFF',
    '-DWITH_WINMM=OFF', '-DWITH_SWSCALE=OFF', '-DWITH_WEBVIEW=OFF',
    '-DWITH_MEDIA_FOUNDATION=OFF', '-DWITH_GFX_H264=OFF'
)
cmake @options
cmake --build "$work/obj" --config Release --target sfreerdp --parallel
```

整理客户端及其许可证后打包:

```powershell
$clientBin = Join-Path $work 'client-bin'
[void][IO.Directory]::CreateDirectory($clientBin)
Copy-Item "$work/obj/client/Sample/Release/sfreerdp.exe" $clientBin
Copy-Item "$source/LICENSE" "$clientBin/LICENSE-FreeRDP.txt"
Copy-Item "$vcpkg/installed/x64-windows-static/share/openssl/copyright" "$clientBin/LICENSE-OpenSSL.txt"
Copy-Item "$vcpkg/installed/x64-windows-static/share/zlib/copyright" "$clientBin/LICENSE-zlib.txt"
& "$repo/build.ps1" -ClientPath "$clientBin/sfreerdp.exe"
```

交付前可用 `sfreerdp.exe /version`, `/help` 和 MSVC `dumpbin /dependents` 检查版本及依赖. 已验证的客户端只依赖 Windows 系统 DLL. 完整安装和开机登录验收需要在安装界面输入真实账号密码, 编译成功不能替代这一验收.
