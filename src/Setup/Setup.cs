using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Runtime.InteropServices;

static class SetupEntry
{
    const string Root = @"C:\ProgramData\RdpSessionBootstrap";
    static string originalSid = "";
    static string[] arguments = [];
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string existing, string? destination, uint flags);
    static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [STAThread]
    static int Main(string[] args)
    {
        arguments = args;
        ApplicationConfiguration.Initialize();
        string currentSid = WindowsIdentity.GetCurrent().User!.Value;
        int index = Array.IndexOf(args, "--original-sid");
        originalSid = index >= 0 && index + 1 < args.Length ? args[index + 1] : currentSid;
        if (currentSid != originalSid)
        {
            MessageBox.Show("提权后账号发生变化. 请使用当前账号自己的管理员权限安装或卸载.", "RDP Session Bootstrap");
            return 1;
        }
        if (Process.GetCurrentProcess().SessionId == 0)
        {
            return 1;
        }
        Application.Run(new SetupWindow());
        return 0;
    }

    sealed class SetupWindow : Form
    {
        readonly TextBox password = new() { UseSystemPasswordChar = true, Width = 390 };
        readonly Button primary = new() { Width = 100, Text = "安装" };
        readonly Button remove = new() { Width = 100, Text = "卸载" };
        readonly Label result = new() { AutoSize = false, Width = 430, Height = 55 };
        readonly bool uninstallMode = arguments.Contains("--uninstall") || arguments.Contains("--uninstall-helper");

        public SetupWindow()
        {
            Text = "RDP Session Bootstrap";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(475, 285);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 10);
            var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), FlowDirection = FlowDirection.TopDown, WrapContents = false };
            layout.Controls.Add(new Label { AutoSize = true, Text = "当前账号: " + WindowsIdentity.GetCurrent().Name });
            layout.Controls.Add(new Label { Width = 430, Height = 55, Text = "开机登录本机, 成功后 5 秒断开并保留会话.\n安装完成后, 下次开机生效." });
            layout.Controls.Add(new Label { AutoSize = true, Text = "本账号密码", Visible = !uninstallMode });
            password.Visible = !uninstallMode;
            password.Enabled = Elevated;
            layout.Controls.Add(password);
            var actions = new FlowLayoutPanel { Width = 430, Height = 43 };
            primary.Visible = !uninstallMode;
            primary.Enabled = Elevated;
            remove.Visible = uninstallMode || Directory.Exists(Root);
            remove.Enabled = Elevated;
            var cancel = new Button { Width = 100, Text = "关闭" };
            cancel.Click += (_, _) => Close();
            primary.Click += (_, _) => Install();
            remove.Click += (_, _) => BeginUninstall();
            actions.Controls.Add(primary); actions.Controls.Add(remove); actions.Controls.Add(cancel);
            layout.Controls.Add(actions); layout.Controls.Add(result); Controls.Add(layout);
            AcceptButton = uninstallMode ? remove : primary;
            Shown += (_, _) =>
            {
                if (!Elevated) BeginInvoke(Elevate);
                else if (arguments.Contains("--uninstall-helper")) BeginInvoke(FinishUninstall);
            };
        }

        void Elevate()
        {
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                if (uninstallMode) start.ArgumentList.Add("--uninstall");
                start.ArgumentList.Add("--original-sid"); start.ArgumentList.Add(originalSid);
                Process.Start(start)?.Dispose();
                Close();
            }
            catch (Exception e) { result.Text = "需要当前账号的管理员权限. " + e.Message; }
        }

        void Install()
        {
            primary.Enabled = false; remove.Enabled = false;
            using var secure = new SecureString();
            foreach (char value in password.Text) secure.AppendChar(value);
            password.Clear();
            try
            {
                Program.Install(["install", "--original-sid", originalSid], secure);
                result.Text = "安装完成. 下次开机生效, 本次没有登录或重启.";
                remove.Visible = true;
            }
            catch (Exception e) { ShowFailure(e); }
            finally { primary.Enabled = true; remove.Enabled = true; }
        }

        void BeginUninstall()
        {
            var copied = new List<string>();
            try
            {
                string temporary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RdpSessionBootstrap-Uninstall-" + Guid.NewGuid().ToString("N")));
                if (!temporary.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "RdpSessionBootstrap-Uninstall-", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("卸载临时目录验证失败.");
                Program.CopyDirectory(AppContext.BaseDirectory, temporary, copied);
                var start = new ProcessStartInfo(Path.Combine(temporary, "Setup.exe")) { UseShellExecute = false };
                start.ArgumentList.Add("--uninstall-helper");
                start.ArgumentList.Add("--original-sid"); start.ArgumentList.Add(originalSid);
                start.ArgumentList.Add("--wait-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(start)?.Dispose();
                Close();
            }
            catch (Exception e) { ShowFailure(new IOException(e.Message + "\n已复制文件:\n" + string.Join("\n", copied), e)); }
        }

        void FinishUninstall()
        {
            try
            {
                int index = Array.IndexOf(arguments, "--wait-pid");
                if (index < 0 || index + 1 >= arguments.Length) throw new IOException("卸载参数无效.");
                int pid = int.Parse(arguments[index + 1]);
                try
                {
                    using var previous = Process.GetProcessById(pid);
                    if (!previous.WaitForExit(15000)) throw new IOException("原安装程序尚未退出, 未删除安装目录.");
                }
                catch (ArgumentException) { }
                Program.Uninstall(["uninstall", "--original-sid", originalSid]);
                ScheduleHelperCleanup();
                result.Text = "卸载完成. 用户会话保留. 临时卸载文件将在下次开机清理.";
                remove.Enabled = false;
            }
            catch (Exception e) { ShowFailure(e); }
        }

        void ScheduleHelperCleanup()
        {
            string target = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string prefix = Path.GetFullPath(Path.GetTempPath()) + "RdpSessionBootstrap-Uninstall-";
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(target[prefix.Length..], "N", out _))
                throw new IOException("卸载已完成, 临时目录清理边界验证失败: " + target);
            var pending = Directory.GetFiles(target, "*", SearchOption.AllDirectories)
                .Concat(Directory.GetDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
                .Append(target);
            foreach (string path in pending)
                if (!MoveFileEx(path, null, 4))
                    throw new IOException("卸载已完成, 临时文件清理安排失败: " + path + "; Win32=" + Marshal.GetLastWin32Error());
        }

        void ShowFailure(Exception error)
        {
            result.Text = "操作停止. " + error.Message;
            using var details = new Form { Text = "操作未完成", StartPosition = FormStartPosition.CenterParent, Size = new Size(650, 450) };
            details.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                Dock = DockStyle.Fill, Text = error.Message });
            details.ShowDialog(this);
        }
    }
}
