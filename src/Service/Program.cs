using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

public static class Program
{
    const string Root = @"C:\ProgramData\RdpSessionBootstrap";
    const string Name = "RdpSessionBootstrap";
    static readonly string CredentialPath = Path.Combine(Root, "private", "credential.dpapi");
    static readonly string ConfigPath = Path.Combine(Root, "private", "config.json");
    static readonly string ClientPath = Path.Combine(Root, "client", "sfreerdp.exe");
    static readonly ManualResetEventSlim Stop = new(false);
    static IntPtr statusHandle;
    static readonly Native.ServiceMain MainCallback = ServiceMain;
    static readonly Native.Handler HandlerCallback = Control;
    record Config(string Username, string Domain, string AccountSid, string CertificateSha256);
    static Config? activeConfig;
    static RunLog? runLog;
    record Session(int Id, string User, string Sid, int State);

    static int Main(string[] args) { Console.OutputEncoding = Encoding.UTF8;
        try
        {
            switch (args.FirstOrDefault()?.ToLowerInvariant())
            {
                case "install": return Install(args);
                case "uninstall": return Uninstall(args);
                case "status": case "dry-run": Status(); return 0;
                case "service":
                    Native.ServiceTable[] table = [new() { name = Name, main = MainCallback }, new()];
                    if (!Native.StartServiceCtrlDispatcher(table)) throw new System.ComponentModel.Win32Exception();
                    return 0;
                default:
                    Console.WriteLine("Usage: RdpSessionBootstrap.exe install | uninstall | status | dry-run | service");
                    return 2;
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }

    static int ElevateIfNeeded(string command, string[] args)
    {
        if (command == "install" && (Process.GetCurrentProcess().SessionId == 0 || !Environment.UserInteractive))
            throw new Exception("安装和卸载必须由当前登录用户在交互终端运行.");
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new Exception("无法取得当前用户 SID.");
        if (args.Length == 3 && args[1] == "--original-sid")
        {
            if (args[2] != sid) throw new Exception("UAC 提权后用户已改变. 请使用当前用户自己的管理员权限, 不支持替代管理员安装.");
        }
        else if (args.Length != 1) throw new Exception("安装参数无效.");
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return -1;
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add(command); info.ArgumentList.Add("--original-sid"); info.ArgumentList.Add(sid);
        using var elevated = Process.Start(info) ?? throw new Exception("管理员提权已取消.");
        elevated.WaitForExit(); return elevated.ExitCode;
    }

    static void CheckExternalPackage()
    {
        string installed = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(AppContext.BaseDirectory).StartsWith(installed, StringComparison.OrdinalIgnoreCase))
            throw new Exception("请使用安装包中的 Setup.exe, 不要从安装目录执行此命令.");
    }

    static string CertificateFingerprint()
    {
        using var store = new X509Store("Remote Desktop", StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certificates = store.Certificates.Cast<X509Certificate2>()
            .Where(c => c.NotBefore <= DateTime.Now && c.NotAfter > DateTime.Now).ToArray();
        if (certificates.Length != 1) throw new Exception("无法唯一确定本机 RDP certificate, 安装已停止.");
        return certificates[0].GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
    }

    static byte[] ReadAndValidatePassword(Config account)
    {
        if (Console.IsInputRedirected) throw new Exception("密码必须在交互终端隐藏输入.");
        Console.WriteLine("安装账号: " + account.Domain + @"\" + account.Username);
        Console.Write("本账号密码: ");
        using var secure = new SecureString();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (secure.Length > 0) secure.RemoveAt(secure.Length - 1); continue; }
            if (!char.IsControl(key.KeyChar)) secure.AppendChar(key.KeyChar);
        }
        Console.WriteLine();
        return ValidatePassword(secure, account);
    }

    static byte[] ValidatePassword(SecureString secure, Config account)
    {
        IntPtr pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        char[] chars = new char[secure.Length];
        byte[]? bytes = null;
        try
        {
            Marshal.Copy(pointer, chars, 0, chars.Length);
            bytes = Encoding.UTF8.GetBytes(chars);
            if (bytes.Length == 0 || bytes.Length > 510 || bytes.Contains((byte)10) || bytes.Contains((byte)13))
                throw new Exception("密码必须为 1-510 个 UTF-8 字节, 且不含换行.");
            if (!Native.LogonUser(account.Username, account.Domain, pointer, 3, 0, out IntPtr token))
                throw new Exception("密码验证失败, 未启用自动登录. Win32=" + Marshal.GetLastWin32Error() + ". 请重新运行安装程序, 不会自动重试.");
            try
            {
                using var validated = new WindowsIdentity(token);
                if (validated.User?.Value != account.AccountSid) throw new Exception("用户名和密码对应的账号 SID 不匹配, 安装停止.");
            }
            finally { Native.CloseHandle(token); }
            return bytes;
        }
        catch { if (bytes != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); throw; }
        finally { Array.Clear(chars); Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }

    public static void CopyDirectory(string source, string destination, List<string> written)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(file));
            try { File.Copy(file, target, true); written.Add(target); } catch (Exception e) { throw new IOException("write_failed/recovery_required: " + target, e); }
        }
        foreach (string directory in Directory.GetDirectories(source))
        {
            string leaf = Path.GetFileName(directory);
            if (leaf is "client" or "source" or "payload") continue;
            CopyDirectory(directory, Path.Combine(destination, leaf), written);
        }
    }

    static Native.ServiceProcessStatus? QueryOwnServiceStatus()
    {
        IntPtr manager = Native.OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            IntPtr service = Native.OpenService(manager, Name, 4);
            if (service == IntPtr.Zero) { if (Marshal.GetLastWin32Error() == 1060) return null; throw new System.ComponentModel.Win32Exception(); }
            try { if (!Native.QueryServiceStatusEx(service, 0, out Native.ServiceProcessStatus status, Marshal.SizeOf<Native.ServiceProcessStatus>(), out _)) throw new System.ComponentModel.Win32Exception(); return status; }
            finally { Native.CloseServiceHandle(service); }
        }
        finally { Native.CloseServiceHandle(manager); }
    }

    static void StopOwnService()
    {
        var status = QueryOwnServiceStatus();
        if (status == null) return;
        Process? process = null;
        try { if (status.Value.processId != 0) process = Process.GetProcessById((int)status.Value.processId); }
        catch (ArgumentException) { }
        using (process)
        {
            if (status.Value.status.state != 1)
            {
                RunSc("stop", Name);
                for (int i = 0; i < 30 && QueryOwnServiceStatus()?.status.state != 1; i++) Thread.Sleep(1000);
                if (QueryOwnServiceStatus()?.status.state != 1) throw new Exception("服务停止超时. 未删除程序或凭据.");
            }
            if (process != null && !process.WaitForExit(10000)) throw new Exception("服务进程尚未退出. 未删除程序或凭据.");
        }
    }

    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RdpSessionBootstrap";

    public static Version? GetInstalledVersion()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKey);
        if (key == null)
        {
            if (new[] { "app", "client", "private" }.Any(folder => Directory.Exists(Path.Combine(Root, folder))))
                throw new IOException("检测到已有安装文件, 但卸载登记缺失. 请先修复安装登记或卸载后重装.");
            return null;
        }
        string? value = key.GetValue("DisplayVersion") as string;
        if (!Version.TryParse(value, out var version) || version.Build < 0 || version.Revision > 0)
            throw new IOException("已有安装的版本登记无效, 无法安全判断升级方向.");
        return new Version(version.Major, version.Minor, version.Build);
    }

    public static void Upgrade()
    {
        CheckExternalPackage();
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
            Process.GetCurrentProcess().SessionId == 0 || !Environment.UserInteractive)
            throw new Exception("升级必须由当前登录用户以管理员权限运行.");
        Version installed = GetInstalledVersion() ?? throw new Exception("尚未安装, 请先安装.");
        string currentText = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Version current = Version.Parse(currentText);
        if (current.Build < 0 || current.Revision > 0) throw new IOException("安装包版本必须为三段版本号.");
        current = new Version(current.Major, current.Minor, current.Build);
        if (installed > current) throw new Exception($"已安装版本 {installed} 高于安装包版本 {current}. 不支持降级.");
        Config config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? throw new Exception("已有安装配置为空.");
        if (string.IsNullOrWhiteSpace(config.AccountSid) || identity.User?.Value != config.AccountSid)
            throw new Exception("升级必须使用原安装账号, 当前用户 SID 与已保存的账号不一致.");
        if (!File.Exists(CredentialPath)) throw new IOException("已有安装的凭据文件缺失, 请重新安装.");
        if (QueryOwnServiceStatus() == null) throw new IOException("已有安装的服务登记缺失, 无法升级.");
        foreach (string relative in new[] { "RdpSessionBootstrap.exe", "RdpSessionBootstrap.dll", "RdpSessionBootstrap.deps.json", "RdpSessionBootstrap.runtimeconfig.json", "Setup.exe", "Setup.dll", "Setup.deps.json", "Setup.runtimeconfig.json", "client\\sfreerdp.exe", "client\\LICENSE-FreeRDP.txt", "client\\LICENSE-OpenSSL.txt", "client\\LICENSE-zlib.txt" })
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, relative))) throw new IOException("安装包缺少文件: " + relative);
        DirectoryAccess.Apply(Root, config.AccountSid, traversalOnly: true);
        DirectoryAccess.Apply(Path.Combine(Root, "private"));
        DirectoryAccess.ApplyPrivateFile(ConfigPath);
        DirectoryAccess.ApplyPrivateFile(CredentialPath);
        LogDirectory.Prepare(Path.Combine(Root, "logs"), config.AccountSid);

        string work = Path.Combine(Root, "upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        DirectoryAccess.Apply(work);
        string app = Path.Combine(Root, "app"), client = Path.Combine(Root, "client");
        bool appSaved = false, clientSaved = false, appPlaced = false, clientPlaced = false;
        bool preserveRecovery = false;
        try
        {
            // Finish all package reads before stopping the service or replacing its files.
            // Protect the staged app explicitly so its account read access survives the move into Root.
            Directory.CreateDirectory(Path.Combine(work, "app"));
            DirectoryAccess.Apply(Path.Combine(work, "app"), config.AccountSid);
            CopyDirectory(AppContext.BaseDirectory, Path.Combine(work, "app"), new List<string>());
            CopyDirectory(Path.Combine(AppContext.BaseDirectory, "client"), Path.Combine(work, "client"), new List<string>());
            DirectoryAccess.ApplyTree(Path.Combine(work, "app"), config.AccountSid);
            DirectoryAccess.ApplyTree(Path.Combine(work, "client"));
            StopOwnService();
            if (Directory.Exists(app)) { Directory.Move(app, Path.Combine(work, "old-app")); appSaved = true; }
            if (Directory.Exists(client)) { Directory.Move(client, Path.Combine(work, "old-client")); clientSaved = true; }
            Directory.Move(Path.Combine(work, "app"), app); appPlaced = true;
            Directory.Move(Path.Combine(work, "client"), client); clientPlaced = true;
            using var uninstall = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKey, true)
                ?? throw new IOException("升级时无法打开卸载登记.");
            RunSc("config", Name, "start=", "auto");
            uninstall.SetValue("DisplayVersion", currentText);
        }
        catch (Exception error)
        {
            try
            {
                if (clientPlaced) Directory.Delete(client, true);
                if (clientSaved) Directory.Move(Path.Combine(work, "old-client"), client);
                if (appPlaced) Directory.Delete(app, true);
                if (appSaved) Directory.Move(Path.Combine(work, "old-app"), app);
            }
            catch (Exception rollbackError)
            {
                preserveRecovery = true;
                throw new IOException($"升级失败: {error.Message}. 恢复旧程序也失败: {rollbackError.Message}. 请保留恢复目录 {work}. 凭据和日志未修改.", error);
            }
            throw new IOException("升级失败, 原程序已保留或恢复, 未启动服务. 凭据和日志未修改. " + error.Message, error);
        }
        finally
        {
            if (!preserveRecovery)
            {
                try { Directory.Delete(work, true); }
                catch (Exception e) { Console.Error.WriteLine("临时升级文件未清理: " + work + ". " + e.Message); }
            }
        }
    }

    public static int Install(string[] args, SecureString? suppliedPassword = null)
    {
        int elevation = ElevateIfNeeded("install", args); if (elevation >= 0) return elevation;
        CheckExternalPackage();
        if (GetInstalledVersion() != null) throw new Exception("已安装 RDP Session Bootstrap, 请使用升级或修复操作以保留已有凭据.");
        var identity = WindowsIdentity.GetCurrent();
        string name = identity.Name;
        int slash = name.IndexOf('\\');
        if (slash <= 0) throw new Exception("当前账号不是 DOMAIN\\Username 格式.");
        var config = new Config(name[(slash + 1)..], name[..slash], identity.User!.Value, CertificateFingerprint());
        string clientSource = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "client"));
        if (!File.Exists(Path.Combine(clientSource, "sfreerdp.exe"))) throw new Exception("安装包缺少 RDP client.");
        byte[] secret = suppliedPassword == null ? ReadAndValidatePassword(config) : ValidatePassword(suppliedPassword, config);
        var written = new List<string>();
        try
        {
            StopOwnService();
            Directory.CreateDirectory(Root);
            DirectoryAccess.Apply(Root, config.AccountSid, traversalOnly: true);
            foreach (string folder in new[] { "app", "client", "private", "logs" })
            { string path = Path.Combine(Root, folder); Directory.CreateDirectory(path); DirectoryAccess.Apply(path, folder == "app" ? config.AccountSid : null); }
            LogDirectory.Prepare(Path.Combine(Root, "logs"), config.AccountSid);
            CopyDirectory(AppContext.BaseDirectory, Path.Combine(Root, "app"), written);
            CopyDirectory(clientSource, Path.Combine(Root, "client"), written);
            DirectoryAccess.ApplyTree(Path.Combine(Root, "app"), config.AccountSid);
            DirectoryAccess.ApplyTree(Path.Combine(Root, "client"));
            try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config), new UTF8Encoding(false)); written.Add(ConfigPath); } catch (Exception e) { throw new IOException("write_failed/recovery_required: " + ConfigPath, e); }
            try { File.WriteAllBytes(CredentialPath, Protect(secret, false)); written.Add(CredentialPath); } catch (Exception e) { throw new IOException("write_failed/recovery_required: " + CredentialPath, e); }
            DirectoryAccess.ApplyPrivateFile(ConfigPath);
            DirectoryAccess.ApplyPrivateFile(CredentialPath);
            if (QueryOwnServiceStatus() == null)
                RunSc("create", Name, "binPath=", "\"" + Path.Combine(Root, "app", "RdpSessionBootstrap.exe") + "\" service", "start=", "auto", "obj=", "LocalSystem", "depend=", "TermService", "DisplayName=", "RDP Session Bootstrap");
            else RunSc("config", Name, "binPath=", "\"" + Path.Combine(Root, "app", "RdpSessionBootstrap.exe") + "\" service", "start=", "auto", "obj=", "LocalSystem", "depend=", "TermService");
            RunSc("description", Name, "Local RDP logon for the installation account on boot; disconnect 5 seconds after a new session becomes active.");
            using (var uninstall = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RdpSessionBootstrap"))
            {
                uninstall.SetValue("DisplayName", "RDP Session Bootstrap");
                uninstall.SetValue("DisplayVersion", typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
                uninstall.SetValue("InstallLocation", Root);
                uninstall.SetValue("UninstallString", "\"" + Path.Combine(Root, "app", "Setup.exe") + "\" --uninstall");
                uninstall.SetValue("DisplayIcon", Path.Combine(Root, "app", "Setup.exe"));
                uninstall.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                uninstall.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            Console.WriteLine("安装完成. 已配置开机自动启动, 本次没有发起 RDP 登录或重启. 请下次开机后查看 logs 目录中的本次运行日志.");
            return 0;
        }
        catch (Exception error)
        {
            throw new IOException(error.Message + Environment.NewLine +
                "安装停止. 已成功写入以下文件, 未自动回滚:" + Environment.NewLine +
                string.Join(Environment.NewLine, written), error);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
    }

    public static int Uninstall(string[] args)
    {
        int elevation = ElevateIfNeeded("uninstall", args); if (elevation >= 0) return elevation;
        CheckExternalPackage();
        string target = Path.GetFullPath(Root);
        if (!string.Equals(target, @"C:\ProgramData\RdpSessionBootstrap", StringComparison.OrdinalIgnoreCase))
            throw new Exception("卸载目录验证失败.");
        StopOwnService();
        if (QueryOwnServiceStatus() != null) RunSc("delete", Name);
        Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RdpSessionBootstrap", false);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Console.WriteLine("卸载完成. 已删除本服务, 凭据和安装目录, 未注销任何用户 session. 安装包和构建材料保留.");
        return 0;
    }
    static Config CheckClientAndConfig()
    {
        if (!File.Exists(ClientPath)) throw new Exception("RDP client 尚未部署: " + ClientPath);
        Config config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? throw new Exception("配置为空.");
        if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Domain) || string.IsNullOrWhiteSpace(config.AccountSid)) throw new Exception("安装账号配置无效.");
        _ = new SecurityIdentifier(config.AccountSid);
        if (config.CertificateSha256.Length != 64 || !config.CertificateSha256.All(Uri.IsHexDigit))
            throw new Exception("本机 RDP certificate SHA256 配置无效.");
        return config;
    }

    public static string PrepareLogDirectory()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
            Process.GetCurrentProcess().SessionId == 0 || !Environment.UserInteractive)
            throw new Exception("准备日志目录需要当前登录用户的管理员权限.");
        if (GetInstalledVersion() == null || !Directory.Exists(Root))
            throw new IOException("尚未安装, 无法打开日志目录.");
        Config config;
        try
        {
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath))
                ?? throw new JsonException("安装配置为空.");
            if (string.IsNullOrWhiteSpace(config.AccountSid)) throw new JsonException("安装账号 SID 缺失.");
            _ = new SecurityIdentifier(config.AccountSid);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new IOException("无法读取有效的安装配置, 日志目录权限未修改. " + e.Message, e);
        }
        if (identity.User?.Value != config.AccountSid)
            throw new Exception("请使用原安装账号打开日志目录, 当前用户 SID 与已保存的账号不一致.");
        string directory = Path.Combine(Root, "logs");
        LogDirectory.Prepare(directory, config.AccountSid);
        return directory;
    }

    static void Status()
    {
        if (File.Exists(ConfigPath)) { activeConfig = CheckClientAndConfig(); Console.WriteLine("固定目标: 127.0.0.1 / " + activeConfig.Domain + @"\" + activeConfig.Username); }
        Console.WriteLine("Client: " + (File.Exists(ClientPath) ? "present" : "missing"));
        Console.WriteLine("Credential: " + (File.Exists(CredentialPath) ? "enrolled" : "not enrolled"));
        foreach (Session s in Sessions().Where(IsTarget)) Console.WriteLine($"已有目标账号 session {s.Id}, state={s.State}; service 将跳过登录.");
        if (File.Exists(ConfigPath)) Console.WriteLine("Certificate config: present");
        Console.WriteLine("dry-run/status 不进行认证或解密.");
    }

    static void ServiceMain(int argc, IntPtr argv)
    {
        statusHandle = Native.RegisterServiceCtrlHandler(Name, HandlerCallback);
        if (statusHandle == IntPtr.Zero) return;
        Report(2);
        Report(4);
        try
        {
            runLog = new RunLog(Path.Combine(Root, "logs"));
            Bootstrap();
        }
        catch (Exception e) { Log("failed: " + e.Message); }
        finally { Report(1); }
    }

    static void Control(uint code)
    {
        if (code is 1 or 5) { Report(3); Stop.Set(); }
        else if (code == 4) Report(4);
    }
    static void Report(uint state)
    {
        Native.ServiceStatus s = new() { type = 16, state = state, controls = state == 4 ? 5u : 0u, waitHint = 10000 };
        Native.SetServiceStatus(statusHandle, ref s);
    }
    static void Bootstrap()
    {
        Log("boot attempt started");
        activeConfig = CheckClientAndConfig();
        if (Sessions().Any(IsTarget)) { Log("skipped: existing target account session; no authentication attempted"); return; }
        if (!File.Exists(CredentialPath)) { Log("skipped: credential not enrolled"); return; }
        Config config = CheckClientAndConfig();
        bool ready = false;
        for (int i = 0; i < 60 && !Stop.IsSet; i++)
        {
            try { using var tcp = new TcpClient(); if (tcp.ConnectAsync("127.0.0.1", 3389).Wait(1000) && tcp.Connected) { ready = true; break; } } catch { }
            Stop.Wait(1000);
        }
        if (!ready || Stop.IsSet) { Log("failed: local RDP listener unavailable within bounded wait"); return; }
        // 就绪期间用户自行登录时不抢占其会话.
        if (Sessions().Any(IsTarget)) { Log("skipped: target account session appeared before authentication"); return; }
        byte[] secret = Protect(File.ReadAllBytes(CredentialPath), true);
        Process? child = null;
        try
        {
        var psi = new ProcessStartInfo(ClientPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "/v:127.0.0.1", "/u:" + config.Username, "/d:" + config.Domain, "/sec:nla", "/from-stdin:force", "/cert:deny,fingerprint:sha256:" + config.CertificateSha256, "/log-level:OFF" }) psi.ArgumentList.Add(arg);
        child = Process.Start(psi) ?? throw new Exception("RDP client start failed");
        // 不把 client output 写入日志,避免第三方输出泄漏凭据或认证数据.
        Task stdout = child.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        Task stderr = child.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            child.StandardInput.BaseStream.Write(secret);
            child.StandardInput.BaseStream.WriteByte(10);
            child.StandardInput.BaseStream.Flush();
            child.StandardInput.Close();
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
        Log("one authentication attempt submitted");
        Session? created = null;

        for (int i = 0; i < 120 && !Stop.IsSet && !child.HasExited; i++)
        {
            created = Sessions().FirstOrDefault(s => IsTarget(s) && s.State == 0);
            if (created != null) break;
            Stop.Wait(1000);
        }

        if (created != null)
        {
            Log($"new target account session {created.Id} active; waiting 5 seconds before disconnect");
            var delay = Stopwatch.StartNew();
            while (delay.ElapsedMilliseconds < 5000 && !Stop.IsSet && !child.HasExited)
                Stop.Wait((int)Math.Clamp(5000 - delay.ElapsedMilliseconds, 0, 100));
            if (child.HasExited) Log("local RDP client exited during login delay; no retry");
            else if (Stop.IsSet) Log("service stopped during login delay");
            else Log("login delay completed; closing local RDP client");
        }
        else Log("failed: no target account session observed; no retry");
        // 其他 RDP 连接可能已接管同一 session. 只关闭本次 client, 不按 session ID 断开连接.
        if (!child.HasExited) child.Kill(true);
        if (!child.WaitForExit(5000)) throw new TimeoutException("local RDP client did not exit within cleanup wait");
        Log("boot attempt finished; login observed=" + (created != null) + "; client exit=" + child.ExitCode);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
            if (child != null)
            {
                try
                {
                    if (!child.HasExited) { child.Kill(true); child.WaitForExit(5000); }
                }
                catch (Exception e) { Log("local client cleanup failed: " + e.Message); }
                finally { child.Dispose(); }
            }
        }
    }

    static bool IsTarget(Session s) => s.Id != 0 && s.Sid == activeConfig?.AccountSid;
    static List<Session> Sessions()
    {
        var result = new List<Session>();
        if (!Native.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr data, out int count))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            int size = Marshal.SizeOf<Native.WtsSession>();
            for (int i = 0; i < count; i++)
            {
                var s = Marshal.PtrToStructure<Native.WtsSession>(data + i * size);
                if (Native.WTSQuerySessionInformation(IntPtr.Zero, s.id, 5, out IntPtr user, out int length))
                {
                    try
                    {
                        string username = Marshal.PtrToStringUni(user) ?? "";
                        string domain = "";
                        if (Native.WTSQuerySessionInformation(IntPtr.Zero, s.id, 7, out IntPtr domainData, out int domainLength))
                        { try { domain = Marshal.PtrToStringUni(domainData) ?? ""; } finally { Native.WTSFreeMemory(domainData); } }
                        string sid = "";
                        if (username.Length > 0)
                        { try { sid = ((SecurityIdentifier)new NTAccount(domain, username).Translate(typeof(SecurityIdentifier))).Value; } catch (IdentityNotMappedException) { } }
                        result.Add(new(s.id, username, sid, s.state));
                    }
                    finally { Native.WTSFreeMemory(user); }
                }
            }
        }
        finally { Native.WTSFreeMemory(data); }
        return result;
    }

    static byte[] Protect(byte[] bytes, bool decrypt)
    {
        var input = new Native.Blob { length = bytes.Length, data = Marshal.AllocHGlobal(bytes.Length) };
        Native.Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.data, bytes.Length);
            bool ok = decrypt ? Native.CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : Native.CryptProtectData(ref input, "RDP session bootstrap", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 5, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            byte[] result = new byte[output.length]; Marshal.Copy(output.data, result, 0, result.Length); return result;
        }
        finally
        {
            for (int i = 0; i < input.length; i++) Marshal.WriteByte(input.data, i, 0);
            Marshal.FreeHGlobal(input.data);
            if (output.data != IntPtr.Zero) { for (int i = 0; i < output.length; i++) Marshal.WriteByte(output.data, i, 0); Native.LocalFree(output.data); }
        }
    }
    static void RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd(), error = p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception("Service configuration failed: " + output + error);
    }
    static void Log(string message) => runLog?.Write(message);
}

static class Native
{
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool LogonUser(string user, string domain, IntPtr password, int type, int provider, out IntPtr token);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError=true)] public static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceProcessStatus status, int size, out int needed);
    [DllImport("advapi32.dll")] public static extern bool CloseServiceHandle(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate void ServiceMain(int argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate void Handler(uint code);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct ServiceTable { [MarshalAs(UnmanagedType.LPWStr)] public string? name; public ServiceMain? main; }
    [StructLayout(LayoutKind.Sequential)] public struct ServiceStatus { public uint type, state, controls, win32Exit, serviceExit, checkpoint, waitHint; }
    [StructLayout(LayoutKind.Sequential)] public struct ServiceProcessStatus { public ServiceStatus status; public uint processId, flags; }
    [StructLayout(LayoutKind.Sequential)] public struct WtsSession { public int id; public IntPtr name; public int state; }
    [StructLayout(LayoutKind.Sequential)] public struct Blob { public int length; public IntPtr data; }
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool StartServiceCtrlDispatcher([In] ServiceTable[] table);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr RegisterServiceCtrlHandler(string name, Handler handler);
    [DllImport("advapi32.dll", SetLastError=true)] public static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
    [DllImport("wtsapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr data, out int count);
    [DllImport("wtsapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool WTSQuerySessionInformation(IntPtr server, int id, int cls, out IntPtr data, out int count);
    [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr data);
    [DllImport("crypt32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError=true)] public static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr data);
}
