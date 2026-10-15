using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;

static class BundleEntry
{
    const string Title = "RDP Session Bootstrap";

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? temporary = null;
        try
        {
            string currentSid = WindowsIdentity.GetCurrent().User!.Value;
            string originalSid = currentSid;
            bool uninstall = false;
            bool hasSid = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--uninstall" && !uninstall)
                    uninstall = true;
                else if (args[i] == "--original-sid" && !hasSid && i + 1 < args.Length)
                {
                    originalSid = args[++i];
                    hasSid = true;
                }
                else
                    throw new ArgumentException("安装参数无效: " + args[i]);
            }

            if (originalSid != currentSid)
                throw new InvalidOperationException("提权后账号发生变化. 请使用当前账号自己的管理员权限安装或卸载.");
            if (Process.GetCurrentProcess().SessionId == 0)
                throw new InvalidOperationException("请在当前用户的交互会话中运行安装程序.");

            string[] launchArguments = uninstall
                ? ["--uninstall", "--original-sid", originalSid]
                : ["--original-sid", originalSid];

            bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            if (!elevated)
            {
                var elevate = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                foreach (string argument in launchArguments) elevate.ArgumentList.Add(argument);
                using var child = Process.Start(elevate) ?? throw new IOException("无法启动管理员安装程序.");
                child.WaitForExit();
                return child.ExitCode;
            }

            temporary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RdpSessionBootstrap-Setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            DirectoryAccess.Apply(temporary);
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.zip")
                ?? throw new IOException("安装包缺少内嵌文件."))
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
                archive.ExtractToDirectory(temporary);
            DirectoryAccess.ApplyTree(temporary);

            var start = new ProcessStartInfo(Path.Combine(temporary, "Setup.exe"))
            {
                UseShellExecute = false,
                WorkingDirectory = temporary
            };
            foreach (string argument in launchArguments) start.ArgumentList.Add(argument);
            using var setup = Process.Start(start) ?? throw new IOException("无法启动安装程序.");
            setup.WaitForExit();
            return setup.ExitCode;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return 1;
        }
        catch (Exception error)
        {
            MessageBox.Show("无法运行安装程序. " + error.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (temporary != null)
            {
                try { Directory.Delete(temporary, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    MessageBox.Show("安装程序已关闭, 但未能清理临时文件. 可以稍后删除此目录:\n" + temporary,
                        Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
    }
}
