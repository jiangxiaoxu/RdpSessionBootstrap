# RDP Session Bootstrap

Windows 开机登录辅助服务. 服务连接本机 `127.0.0.1:3389`, 使用安装时选定的当前账号登录. 确认新会话 Active 后等待 5 秒, 断开 RDP 并保留用户会话. 如果目标账号已有会话, 则跳过连接.

## 安装与卸载

解压构建出的安装包, 双击 `Setup.exe`. 安装界面自动显示当前账号, 用户只需输入本账号密码. 安装程序通过一次 `LogonUser` 并核对返回的 SID, 验证密码与当前账号匹配. UAC 提权必须保持同一账号, 不支持切换到另一管理员账号安装.

安装完成后服务设为自动启动, 下次开机生效. 安装过程不会立即发起 RDP 登录或重启系统. 本机必须已启用 RDP, 目标账号必须具有 RDP 登录权限.

通过 Windows 设置的"已安装的应用"卸载, 或在安装包中再次打开 `Setup.exe` 并点击"卸载". 卸载会停止本服务及其客户端, 删除安装文件和保存的凭据, 不注销用户会话. 卸载程序的临时副本在下次开机清理.

## 构建与维护

构建步骤见 [BUILD.md](BUILD.md). 源码使用 .NET 10, 安装包包含 Windows x64 runtime 和修改后的 FreeRDP sample client, 不要求用户另装 .NET.

- `src/Service`: Windows 服务, 凭据验证和安装/卸载核心.
- `src/Setup`: WinForms 图形安装器.
- `third_party/freerdp`: FreeRDP 补丁, 上游版本记录与第三方许可证.
- `build.ps1`: 从源码发布安装包, 输入为已编译的 `sfreerdp.exe`.

运行时安装目录为 `C:\ProgramData\RdpSessionBootstrap`. 服务使用 LocalSystem, 依赖 TermService. 密码经机器范围 DPAPI 加密保存, 安装目录访问权限限制为 SYSTEM 和 Administrators. 密码仅通过 stdin 传给客户端, 不写入命令行或日志.

每次服务启动只尝试一次认证, 不自动重试. 更改账号密码或本机 RDP certificate 后需要重新安装以更新保存的信息. 写入失败会报告已写入文件和失败目标, 不自动回滚.

源码仓库不包含机器配置, 凭据, 运行日志, 编译缓存或安装包二进制. 第三方许可证位于 [third_party/freerdp](third_party/freerdp).

## 验证边界

已完成 Windows x64 编译, 卸载测试, 源码审查以及本机 TLS/NLA 链路检查. 链路检查使用不存在的测试账号, 只确认进入认证阶段并返回预期登录失败. 真实密码安装, 冷启动登录和 5 秒后断开保留会话, 仍需在目标机器上验收.
