# 构建

在 Windows x64 上准备 PowerShell 7, .NET 10 SDK, CMake, Git, 以及 Visual Studio 2022 或 2026 的 MSVC x64 C++ 工具链和 Windows SDK. 首次初始化需要联网下载源码和依赖. 脚本不会安装系统工具或启动本项目服务.

## Release 安装包

在仓库根目录运行:

```powershell
./build.ps1
```

没有本地客户端时, `build.ps1` 自动调用 `init-dependencies.ps1`, 从源码构建 FreeRDP 客户端并整理许可证. 随后发布服务和安装器, 输出:

- `artifacts/RdpSessionBootstrap-Setup-<版本>.exe`: 可单独下载和分发的完整安装程序.
- `artifacts/package/`, `artifacts/payload.zip`, `artifacts/bundle/`: 打包中间产物, 不需要交给用户.

外层 EXE 包含完整安装内容及运行时. 双击后先请求当前账号的管理员权限, 再解压至独立临时目录并启动安装界面, 界面退出后清理该目录. .NET 程序以 Release, win-x64, self-contained 发布, 目标机器无需另装 .NET. 构建不安装服务, 不发起 RDP 登录.

## 版本

项目版本统一由仓库根目录 `Directory.Build.props` 的 `Version` 定义, 初始为 `0.1.0`. 使用 `major.minor.patch` 三段数字. 后续发布只需更新这里, 再运行 `build.ps1`.

服务, 安装界面和单文件入口共用该版本. 程序的 ProductVersion, 安装界面及 Windows 卸载登记使用三段版本; AssemblyVersion, FileVersion 和 Windows manifest 使用追加 `.0` 的四段版本. 分发 EXE 文件名包含三段版本号. FreeRDP 等第三方依赖保留自己的独立版本.

## 单独初始化客户端依赖

```powershell
./init-dependencies.ps1
```

源码, 下载缓存, vcpkg 及编译产物均位于 `artifacts/dependencies/`. 客户端和三个许可证输出到 `artifacts/dependencies/client-bin/`. 重复运行会复用下载及构建缓存.

初始化固定使用 FreeRDP 3.32.2, 校验源码 ZIP 的 SHA256 为 `49BDFE90DDFBC1152FF8EBBB2F81EDFF0C753533119FBFC76A5A5A8236F1B5D7`, 再应用仓库中两个补丁. vcpkg 源码版本固定在初始化脚本中, OpenSSL 和 zlib 通过该版本构建为静态库. 客户端采用 MSVC Release 和静态 CRT, 关闭音视频, 重定向和图形客户端依赖.

原始构建记录位于 `third_party/freerdp/provenance.json`. 其中二进制 hash 和依赖版本仅代表原验证基线; 不同 MSVC 或依赖版本重新构建的二进制不承诺字节相同.

## 使用已有客户端

已有客户端时可跳过初始化. 把 `sfreerdp.exe` 和 `LICENSE-FreeRDP.txt`, `LICENSE-OpenSSL.txt`, `LICENSE-zlib.txt` 放在同一目录, 然后运行:

```powershell
./build.ps1 -ClientPath 'D:/build/client-bin/sfreerdp.exe'
```

客户端应由本仓库所用的 FreeRDP 版本和补丁构建. 补丁使 Sample 客户端可在 Windows 无界面运行并移除 channel 加载.

## 验证

初始化时检查客户端的 `/version` 和 `/help`. 完整安装和开机登录验收需要在安装界面输入真实账号密码. 编译成功不能替代冷启动, 五秒断开保留会话以及中途被其他 RDP 接管的验收, 具体见 README.
