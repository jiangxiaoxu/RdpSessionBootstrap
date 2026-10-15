using System.Diagnostics;
using System.Reflection;
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
        readonly TextBox password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Top };
        readonly Button primary = new() { Text = "安装", AutoSize = true, MinimumSize = new Size(112, 40) };
        readonly Button remove = new() { Text = "卸载", AutoSize = true, MinimumSize = new Size(92, 40) };
        readonly Label result = new() { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 12, 0, 0) };
        readonly ToolTip accountTip = new();
        readonly Label title;
        readonly Label description;
        readonly Label passwordLabel;
        readonly Label passwordHint;
        readonly Button cancel = new() { Text = "关闭", AutoSize = true, MinimumSize = new Size(92, 40), Margin = Padding.Empty };
        enum SetupMode { Install, Upgrade, Repair, Blocked }
        SetupMode mode;
        bool completed;
        readonly Version packageVersion = new(typeof(SetupEntry).Assembly.GetName().Version!.ToString(3));
        readonly bool uninstallMode = arguments.Contains("--uninstall") || arguments.Contains("--uninstall-helper");

        public SetupWindow()
        {
            SuspendLayout();
            string version = typeof(SetupEntry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            Text = "RDP Session Bootstrap " + version;
            Font = new Font("Microsoft YaHei UI", 10);
            BackColor = Color.FromArgb(247, 249, 252);
            ForeColor = Color.FromArgb(30, 41, 59);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 480);
            MinimumSize = new Size(580, 480);
            MaximizeBox = false;

            var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, Padding = new Padding(24) };
            var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void AddRow(Control control)
            {
                int row = content.RowCount++;
                content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                content.Controls.Add(control, 0, row);
            }
            Label AddText(string text, float size, Color color, Padding margin, FontStyle style = FontStyle.Regular)
            {
                var label = new Label { Text = text, AutoSize = true, Dock = DockStyle.Top, Font = new Font(Font.FontFamily, size, style), ForeColor = color, Margin = margin };
                AddRow(label);
                return label;
            }
            title = AddText(uninstallMode ? "卸载 RDP Session Bootstrap" : "设置开机登录", 20, ForeColor, new Padding(0, 0, 0, 12), FontStyle.Bold);
            description = AddText(uninstallMode ? "移除本机的开机登录配置和服务." : "开机时自动登录本机, 5 秒后断开连接并保留会话.", 10, Color.FromArgb(71, 85, 105), new Padding(0, 0, 0, 24));
            AddText("当前 Windows 账号", 9, Color.FromArgb(100, 116, 139), new Padding(0, 0, 0, 6));
            var account = new Label { Text = WindowsIdentity.GetCurrent().Name, AutoEllipsis = true, Dock = DockStyle.Top, Height = 26, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 20) };
            accountTip.SetToolTip(account, account.Text);
            AddRow(account);
            passwordLabel = AddText("账号密码", 10, ForeColor, new Padding(0, 0, 0, 8));
            passwordLabel.Visible = !uninstallMode;
            password.Visible = !uninstallMode;
            password.Enabled = Elevated;
            password.Margin = new Padding(3, 0, 3, 8);
            password.Font = new Font(Font.FontFamily, 12);
            AddRow(password);
            passwordHint = AddText("请输入该 Windows 账号的密码, 而非 Windows Hello PIN.", 9, Color.FromArgb(100, 116, 139), new Padding(0, 0, 0, 16));
            passwordHint.Visible = !uninstallMode;
            AddText("操作状态", 9, Color.FromArgb(100, 116, 139), new Padding(0, 8, 0, 0));
            result.Text = uninstallMode ? "准备卸载. 当前用户会话将保留." : "安装完成后, 下次开机生效. 本次不会登录或重启.";
            AddRow(result);
            scroll.Controls.Add(content);
            frame.Controls.Add(scroll, 0, 0);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, BackColor = Color.White, Padding = new Padding(24, 16, 24, 16), Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            primary.Visible = !uninstallMode;
            primary.Enabled = Elevated;
            primary.BackColor = Color.FromArgb(37, 99, 235);
            primary.ForeColor = Color.White;
            primary.FlatStyle = FlatStyle.Flat;
            primary.FlatAppearance.BorderSize = 0;
            primary.Margin = new Padding(12, 0, 0, 0);
            remove.Visible = uninstallMode || Directory.Exists(Root);
            remove.Enabled = Elevated;
            remove.Margin = new Padding(0, 0, 12, 0);
            var openLogs = new Button { Text = "打开日志文件夹", AutoSize = true, MinimumSize = new Size(132, 40), Margin = Padding.Empty };
            openLogs.Click += (_, _) => OpenLogs();
            cancel.Click += (_, _) => Close();
            primary.Click += (_, _) =>
            {
                if (completed) Close();
                else if (mode == SetupMode.Install) Install();
                else if (mode is SetupMode.Upgrade or SetupMode.Repair) Upgrade();
            };
            remove.Click += (_, _) => BeginUninstall();
            footer.Controls.Add(remove, 0, 0);
            footer.Controls.Add(openLogs, 1, 0);
            footer.Controls.Add(cancel, 3, 0);
            footer.Controls.Add(primary, 4, 0);
            frame.Controls.Add(footer, 0, 1);
            Controls.Add(frame);
            AcceptButton = uninstallMode ? remove : primary;
            CancelButton = cancel;
            if (Elevated && !uninstallMode)
            {
                try { ConfigureInstalledVersion(Program.GetInstalledVersion()); }
                catch (Exception e)
                {
                    mode = SetupMode.Blocked;
                    title.Text = "无法读取安装状态";
                    description.Text = "请检查现有安装后重新运行安装程序.";
                    passwordLabel.Visible = password.Visible = passwordHint.Visible = false;
                    primary.Enabled = false;
                    result.ForeColor = Color.FromArgb(185, 28, 28);
                    result.Text = "读取已安装版本失败: " + e.Message;
                }
            }
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(true);
            Shown += (_, _) =>
            {
                if (!Elevated) BeginInvoke(Elevate);
                else if (arguments.Contains("--uninstall-helper")) BeginInvoke(FinishUninstall);
                else if (!uninstallMode && mode == SetupMode.Install) password.Focus();
                else if (primary.Enabled && primary.Visible) primary.Focus();
            };
        }

        void OpenLogs()
        {
            try
            {
                if (Program.GetInstalledVersion() is null)
                {
                    MessageBox.Show(this, "尚未安装 RDP Session Bootstrap. 请先安装后再打开日志文件夹.", "日志文件夹", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string logDirectory = Program.PrepareLogDirectory();
                Process.Start(new ProcessStartInfo(logDirectory) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception e) { ShowFailure(e); }
        }

        void ConfigureInstalledVersion(Version? installed)
        {
            if (installed is null) return;
            var installedVersion = new Version(installed.ToString(3));
            int comparison = installedVersion.CompareTo(packageVersion);
            mode = comparison < 0 ? SetupMode.Upgrade : comparison == 0 ? SetupMode.Repair : SetupMode.Blocked;
            passwordLabel.Visible = password.Visible = passwordHint.Visible = false;
            remove.Visible = true;
            string versions = "已安装 " + installedVersion.ToString(3) + "  /  当前安装包 " + packageVersion.ToString(3);
            if (mode == SetupMode.Blocked)
            {
                title.Text = "无法降级安装";
                description.Text = versions;
                primary.Text = "不可降级";
                primary.Enabled = false;
                result.ForeColor = Color.FromArgb(185, 28, 28);
                result.Text = "已安装版本高于此安装包. 请使用相同或更高版本的安装程序.";
                return;
            }
            primary.Text = mode == SetupMode.Upgrade ? "升级" : "修复";
            title.Text = primary.Text + " RDP Session Bootstrap";
            description.Text = versions;
            result.Text = "保留现有账号、凭据和日志, 更新程序文件. 本次不会发起 RDP 连接.";
        }

        void Upgrade()
        {
            primary.Enabled = false;
            remove.Enabled = false;
            try
            {
                Program.Upgrade();
                ShowCompleted(false);
            }
            catch (Exception e) { ShowFailure(e); }
            finally { primary.Enabled = true; remove.Enabled = !completed; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) accountTip.Dispose();
            base.Dispose(disposing);
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
            catch (Exception e)
            {
                result.ForeColor = Color.FromArgb(185, 28, 28);
                result.Text = "需要当前账号的管理员权限. " + e.Message;
            }
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
                ShowCompleted(false);
            }
            catch (Exception e) { ShowFailure(e); }
            finally { primary.Enabled = true; remove.Enabled = !completed; }
        }

        void BeginUninstall()
        {
            var copied = new List<string>();
            try
            {
                string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))) + Path.DirectorySeparatorChar;
                string temporary = Path.GetFullPath(Path.Combine(temporaryRoot, "RdpSessionBootstrap-Uninstall-" + Guid.NewGuid().ToString("N")));
                if (!temporary.StartsWith(temporaryRoot + "RdpSessionBootstrap-Uninstall-", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("卸载临时目录验证失败.");
                Directory.CreateDirectory(temporary);
                DirectoryAccess.Apply(temporary);
                Program.CopyDirectory(AppContext.BaseDirectory, temporary, copied);
                DirectoryAccess.ApplyTree(temporary);
                var start = new ProcessStartInfo(Path.Combine(temporary, "Setup.exe")) { UseShellExecute = false, WorkingDirectory = temporary };
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
                ShowCompleted(true);
            }
            catch (Exception e) { ShowFailure(e); }
        }

        void ShowCompleted(bool uninstalled)
        {
            completed = true;
            title.Text = uninstalled ? "卸载完成" : mode == SetupMode.Upgrade ? "升级完成" : mode == SetupMode.Repair ? "修复完成" : "安装完成";
            description.Text = uninstalled ? "已移除本机的开机登录配置和服务."
                : mode == SetupMode.Install ? "开机登录已配置, 将在下次开机时生效."
                : "程序已更新至 " + packageVersion.ToString(3) + ", 下次开机生效. 现有账号、凭据和日志已保留.";
            password.Clear();
            passwordLabel.Visible = false;
            password.Visible = false;
            passwordHint.Visible = false;
            remove.Visible = false;
            remove.Enabled = false;
            cancel.Visible = false;
            result.ForeColor = Color.FromArgb(21, 128, 61);
            result.Text = uninstalled ? "用户会话已保留. 临时卸载文件将在下次开机清理." : "本次没有登录或重启. 可以关闭此窗口.";
            primary.Text = "完成";
            primary.Visible = true;
            primary.Enabled = true;
            AcceptButton = primary;
            CancelButton = primary;
            primary.Focus();
        }

        void ScheduleHelperCleanup()
        {
            string target = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)))
                + Path.DirectorySeparatorChar + "RdpSessionBootstrap-Uninstall-";
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
            result.ForeColor = Color.FromArgb(185, 28, 28);
            result.Text = "操作未完成. 请查看错误详情后重试.";
            using var details = new Form();
            details.SuspendLayout();
            details.Text = "操作未完成";
            details.StartPosition = FormStartPosition.CenterParent;
            details.Font = Font;
            details.ClientSize = new Size(640, 420);
            details.MinimumSize = new Size(480, 320);
            details.Padding = new Padding(20);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                WordWrap = true, Dock = DockStyle.Fill, Text = error.Message, Margin = new Padding(0, 0, 0, 16) }, 0, 0);
            var close = new Button { Text = "关闭", AutoSize = true, MinimumSize = new Size(92, 40), Anchor = AnchorStyles.Right, DialogResult = DialogResult.OK };
            layout.Controls.Add(close, 0, 1);
            details.Controls.Add(layout);
            details.AcceptButton = close;
            details.CancelButton = close;
            details.AutoScaleDimensions = new SizeF(96, 96);
            details.AutoScaleMode = AutoScaleMode.Dpi;
            details.ResumeLayout(true);
            details.ActiveControl = close;
            details.ShowDialog(this);
        }
    }
}
