using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;

[assembly: AssemblyTitle("小金 ADB 安裝助手")]
[assembly: AssemblyDescription("繁體中文 APK 一鍵安裝工具")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyCopyright("© 2026 Jason1993余")]

namespace Jason1993Yu.AdbApkTool
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show("程式遇到問題：\r\n" + e.Exception.Message,
                    "小金 ADB 安裝助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new MainForm(args));
            return 0;
        }
    }

    public sealed class MainForm : Form
    {
        private readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        private readonly List<string> apkFiles = new List<string>();
        private readonly List<Control> locks = new List<Control>();
        private readonly Queue<string> pendingLog = new Queue<string>();
        private readonly object logGate = new object();
        private readonly System.Windows.Forms.Timer logTimer = new System.Windows.Forms.Timer();
        private readonly Color ink = Color.FromArgb(27, 45, 66);
        private readonly Color accent = Color.FromArgb(0, 119, 115);
        private TextBox adbPath, logBox;
        private ListBox apkList;
        private ComboBox devices;
        private Button install, cancel, browse, refresh;
        private Label status, fileHint, deviceHint;
        private CheckBox replace, autoOld, testOnly, downgrade, split;
        private ProgressBar progress;
        private TableLayoutPanel layout, advanced;
        private CancellationTokenSource cancellation;
        private bool busy, startImmediately, previewMode;
        private bool populatingDevices, guidePreview, advancedPreview;
        private string rememberedSerial;
        private string snapshotPath;
        private bool snapshotGuide;
        private int queuedChars;

        public MainForm(string[] args)
        {
            Text = "小金 ADB 安裝助手  |  v1.0.1";
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Font = new Font("Microsoft JhengHei UI", 10F);
            ForeColor = ink;
            BackColor = Color.FromArgb(242, 246, 250);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(860, 700);
            ClientSize = new Size(970, 730);
            Icon = SystemIcons.Application;
            AllowDrop = true;
            BuildUi();
            adbPath.Text = AdbCore.FindAdb(baseDir);
            foreach (string arg in args)
            {
                if (arg == "--one-click") startImmediately = true;
                else if (arg == "--snapshot-help") snapshotGuide = true;
                else if (arg == "--ui-preview") previewMode = true;
                else if (arg == "--guide-preview") guidePreview = true;
                else if (arg == "--advanced-preview") advancedPreview = true;
            }
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--snapshot" && i + 1 < args.Length)
                {
                    snapshotPath = Path.GetFullPath(args[++i]);
                    previewMode = true;
                }
                else if (!args[i].StartsWith("--", StringComparison.Ordinal)
                    && String.Equals(Path.GetExtension(args[i]), ".apk", StringComparison.OrdinalIgnoreCase))
                {
                    apkFiles.Add(Path.GetFullPath(args[i]));
                    startImmediately = true;
                }
            }
            if (apkFiles.Count == 0 && !previewMode) DiscoverApk();
            RefreshFileList();
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            apkList.AllowDrop = true;
            apkList.DragEnter += OnDragEnter;
            apkList.DragDrop += OnDragDrop;
            logTimer.Interval = 100;
            logTimer.Tick += delegate { DrainLog(); };
            logTimer.Start();
            Shown += async delegate
            {
                if (previewMode) { PrepareSnapshot(); return; }
                AppendLog("歡迎。按「瀏覽 APK」選檔，再按「一鍵安裝」。不需要手動輸入指令。");
                if (startImmediately) await InstallAsync();
                else if (!String.IsNullOrEmpty(adbPath.Text)) await RefreshDevicesAsync();
                else SetStatus("找不到 ADB。請完整解壓縮，或到進階選項選擇 adb.exe。", true);
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!busy) return;
                e.Cancel = true;
                if (cancellation != null) cancellation.Cancel();
                SetStatus("正在停止本次指令。完成後即可關閉視窗；手機端可能已開始安裝。", true);
            };
            FormClosed += delegate { logTimer.Dispose(); };
        }

        private Button MakeButton(string text, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = false;
            b.Width = width;
            b.Height = 34;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(199, 211, 222);
            b.BackColor = Color.White;
            b.ForeColor = ink;
            b.Cursor = Cursors.Hand;
            b.Margin = new Padding(4, 2, 4, 2);
            b.UseVisualStyleBackColor = false;
            return b;
        }

        private Label MakeLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(0, 2, 0, 2);
            return l;
        }

        private void BuildUi()
        {
            TableLayoutPanel shell = new TableLayoutPanel();
            shell.Dock = DockStyle.Fill;
            shell.ColumnCount = 1;
            shell.RowCount = 2;
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.Margin = Padding.Empty;
            Controls.Add(shell);

            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = ink;
            header.Margin = Padding.Empty;
            Label title = new Label();
            title.Text = "小金 ADB 安裝助手";
            title.Font = new Font(Font.FontFamily, 21, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.SetBounds(22, 8, 650, 44);
            Label sub = new Label();
            sub.Text = "選好 APK，按一次安裝。舊版 App 相容選項會自動判斷。";
            sub.ForeColor = Color.FromArgb(207, 224, 233);
            sub.SetBounds(24, 54, 760, 25);
            header.Controls.Add(title);
            header.Controls.Add(sub);
            shell.Controls.Add(header, 0, 0);

            layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(18, 12, 18, 12);
            layout.ColumnCount = 1;
            layout.RowCount = 9;
            layout.Margin = Padding.Empty;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            shell.Controls.Add(layout, 0, 1);

            FlowLayoutPanel helpRow = new FlowLayoutPanel();
            helpRow.Dock = DockStyle.Fill;
            helpRow.WrapContents = false;
            Button help = MakeButton("手機設定教學", 158);
            help.Click += delegate { ShowGuide(); };
            Label helpText = MakeLabel("第一次連接？依手機品牌查看 USB 偵錯與授權步驟。");
            helpText.Dock = DockStyle.None;
            helpText.Width = 650;
            helpText.Height = 34;
            helpRow.Controls.Add(help);
            helpRow.Controls.Add(helpText);
            layout.Controls.Add(helpRow, 0, 0);

            GroupBox filesBox = new GroupBox();
            filesBox.Text = "1  選擇要安裝的 APK";
            filesBox.Dock = DockStyle.Fill;
            filesBox.Padding = new Padding(10, 5, 10, 6);
            TableLayoutPanel fileLayout = new TableLayoutPanel();
            fileLayout.Dock = DockStyle.Fill;
            fileLayout.ColumnCount = 2;
            fileLayout.RowCount = 2;
            fileLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fileLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
            fileLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            fileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            apkList = new ListBox();
            apkList.Dock = DockStyle.Fill;
            apkList.HorizontalScrollbar = true;
            apkList.IntegralHeight = false;
            apkList.Font = new Font(Font.FontFamily, 9.5F);
            apkList.BackColor = Color.White;
            browse = MakeButton("瀏覽 APK…", 132);
            browse.Dock = DockStyle.Fill;
            browse.Click += delegate { ChooseApk(); };
            fileHint = MakeLabel("也可以直接把 .apk 拖進視窗。");
            fileHint.Font = new Font(Font.FontFamily, 9);
            fileLayout.Controls.Add(apkList, 0, 0);
            fileLayout.Controls.Add(browse, 1, 0);
            fileLayout.Controls.Add(fileHint, 0, 1);
            fileLayout.SetColumnSpan(fileHint, 2);
            filesBox.Controls.Add(fileLayout);
            layout.Controls.Add(filesBox, 0, 1);
            locks.Add(browse);
            locks.Add(apkList);

            GroupBox deviceBox = new GroupBox();
            deviceBox.Text = "2  連接手機";
            deviceBox.Dock = DockStyle.Fill;
            deviceBox.Padding = new Padding(10, 5, 10, 4);
            TableLayoutPanel deviceLayout = new TableLayoutPanel();
            deviceLayout.Dock = DockStyle.Fill;
            deviceLayout.ColumnCount = 2;
            deviceLayout.RowCount = 2;
            deviceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            deviceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
            deviceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            deviceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            devices = new ComboBox();
            devices.DropDownStyle = ComboBoxStyle.DropDownList;
            devices.Dock = DockStyle.Fill;
            devices.DropDownWidth = 730;
            devices.SelectedIndexChanged += delegate
            {
                DeviceInfo d = devices.SelectedItem as DeviceInfo;
                if (d != null && !populatingDevices) rememberedSerial = d.Serial;
                if (d != null) deviceHint.Text = d.State == "device"
                    ? "已選定這台裝置；可按「一鍵安裝」。"
                    : "請依裝置狀態處理；需要協助可開啟「手機設定教學」。";
            };
            refresh = MakeButton("重新整理裝置", 148);
            refresh.Dock = DockStyle.Fill;
            refresh.Click += async delegate { await RefreshDevicesAsync(); };
            deviceHint = MakeLabel("單一裝置自動選取；多台裝置時請指定要安裝的手機。");
            deviceHint.Font = new Font(Font.FontFamily, 9);
            deviceLayout.Controls.Add(devices, 0, 0);
            deviceLayout.Controls.Add(refresh, 1, 0);
            deviceLayout.Controls.Add(deviceHint, 0, 1);
            deviceLayout.SetColumnSpan(deviceHint, 2);
            deviceBox.Controls.Add(deviceLayout);
            layout.Controls.Add(deviceBox, 0, 2);
            locks.Add(devices);
            locks.Add(refresh);

            CheckBox showAdvanced = new CheckBox();
            showAdvanced.Text = "顯示進階選項（一般安裝不必調整）";
            showAdvanced.AutoSize = true;
            showAdvanced.Margin = new Padding(6, 6, 0, 0);
            showAdvanced.CheckedChanged += delegate
            {
                advanced.Visible = showAdvanced.Checked;
                layout.RowStyles[4].Height = showAdvanced.Checked ? 108 : 0;
            };
            layout.Controls.Add(showAdvanced, 0, 3);
            locks.Add(showAdvanced);

            advanced = new TableLayoutPanel();
            advanced.Dock = DockStyle.Fill;
            advanced.ColumnCount = 3;
            advanced.RowCount = 3;
            advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
            advanced.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            advanced.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            advanced.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            advanced.Visible = false;
            replace = MakeCheck("覆蓋安裝，保留原資料", true);
            autoOld = MakeCheck("自動相容舊版 App", true);
            testOnly = MakeCheck("允許測試 APK", false);
            downgrade = MakeCheck("嘗試降版本（可偵錯 App）", false);
            split = MakeCheck("同一 App 的分割 APK", false);
            advanced.Controls.Add(replace, 0, 0);
            advanced.Controls.Add(autoOld, 1, 0);
            advanced.Controls.Add(testOnly, 2, 0);
            advanced.Controls.Add(downgrade, 0, 1);
            advanced.Controls.Add(split, 1, 1);
            split.CheckedChanged += delegate { RefreshFileList(); };
            adbPath = new TextBox();
            adbPath.ReadOnly = true;
            adbPath.Dock = DockStyle.Fill;
            adbPath.Font = new Font(Font.FontFamily, 9);
            adbPath.Margin = new Padding(6, 6, 6, 0);
            Button locateAdb = MakeButton("選擇 adb.exe…", 175);
            locateAdb.Dock = DockStyle.Fill;
            locateAdb.Click += delegate { ChooseAdb(); };
            advanced.Controls.Add(adbPath, 0, 2);
            advanced.SetColumnSpan(adbPath, 2);
            advanced.Controls.Add(locateAdb, 2, 2);
            layout.Controls.Add(advanced, 0, 4);
            locks.Add(advanced);

            TableLayoutPanel action = new TableLayoutPanel();
            action.Dock = DockStyle.Fill;
            action.ColumnCount = 3;
            action.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 212));
            action.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            action.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            install = MakeButton("一鍵安裝", 200);
            install.Dock = DockStyle.Fill;
            install.BackColor = accent;
            install.ForeColor = Color.White;
            install.Font = new Font(Font.FontFamily, 13, FontStyle.Bold);
            install.FlatAppearance.BorderSize = 0;
            install.Click += async delegate { await InstallAsync(); };
            cancel = MakeButton("停止", 98);
            cancel.Dock = DockStyle.Fill;
            cancel.Enabled = false;
            cancel.Click += delegate
            {
                if (cancellation != null) cancellation.Cancel();
                cancel.Enabled = false;
                SetStatus("正在停止。手機端若已開始安裝，結果仍需確認。", true);
            };
            progress = new ProgressBar();
            progress.Dock = DockStyle.Fill;
            progress.Margin = new Padding(15, 17, 3, 17);
            progress.Style = ProgressBarStyle.Continuous;
            action.Controls.Add(install, 0, 0);
            action.Controls.Add(cancel, 1, 0);
            action.Controls.Add(progress, 2, 0);
            layout.Controls.Add(action, 0, 5);

            status = MakeLabel("準備就緒。選擇 APK 後即可開始。");
            status.Font = new Font(Font.FontFamily, 10, FontStyle.Bold);
            status.Padding = new Padding(7, 3, 7, 3);
            status.BackColor = Color.FromArgb(229, 238, 245);
            layout.Controls.Add(status, 0, 6);
            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Both;
            logBox.WordWrap = false;
            logBox.Dock = DockStyle.Fill;
            logBox.Font = new Font("Consolas", 9.5F);
            logBox.BackColor = Color.White;
            logBox.Margin = new Padding(3, 7, 3, 3);
            layout.Controls.Add(logBox, 0, 7);

            TableLayoutPanel footer = new TableLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.ColumnCount = 5;
            footer.RowCount = 1;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Button copy = MakeButton("複製紀錄", 108);
            copy.Dock = DockStyle.Fill;
            copy.Click += delegate
            {
                DrainLog();
                try { if (logBox.TextLength > 0) Clipboard.SetText(logBox.Text); }
                catch (Exception e) { AppendLog("無法複製：" + e.Message); }
            };
            Button save = MakeButton("另存紀錄", 108);
            save.Dock = DockStyle.Fill;
            save.Click += delegate { SaveLog(); };
            Button readme = MakeButton("完整說明", 108);
            readme.Dock = DockStyle.Fill;
            readme.Click += delegate { OpenReadme(); };
            Label note = MakeLabel("免安裝 · USB 偵錯授權 · 不需要 Root");
            note.Font = new Font(Font.FontFamily, 9);
            note.AutoEllipsis = true;
            note.Margin = new Padding(4, 2, 4, 2);
            Label signature = MakeLabel("© 2026 Jason1993余");
            signature.Name = "AuthorSignature";
            signature.Font = new Font(Font.FontFamily, 9, FontStyle.Bold);
            signature.TextAlign = ContentAlignment.MiddleRight;
            signature.Margin = new Padding(6, 2, 0, 2);
            signature.ForeColor = ink;
            footer.Controls.Add(copy, 0, 0);
            footer.Controls.Add(save, 1, 0);
            footer.Controls.Add(readme, 2, 0);
            footer.Controls.Add(note, 3, 0);
            footer.Controls.Add(signature, 4, 0);
            layout.Controls.Add(footer, 0, 8);
            AcceptButton = install;
        }

        private CheckBox MakeCheck(string text, bool value)
        {
            CheckBox box = new CheckBox();
            box.Text = text;
            box.Checked = value;
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(6, 2, 0, 0);
            box.Font = new Font(Font.FontFamily, 9.5F);
            return box;
        }

        private void DiscoverApk()
        {
            try
            {
                List<string> found = new List<string>(Directory.GetFiles(baseDir, "*.apk"));
                string folder = Path.Combine(baseDir, "APK");
                if (Directory.Exists(folder)) found.AddRange(Directory.GetFiles(folder, "*.apk"));
                if (found.Count == 1) apkFiles.Add(found[0]);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void RefreshFileList()
        {
            apkList.Items.Clear();
            foreach (string path in apkFiles) apkList.Items.Add(path);
            if (apkFiles.Count == 0)
                fileHint.Text = "可瀏覽任意資料夾，或直接將 .apk 拖進視窗。";
            else if (apkFiles.Count == 1)
                fileHint.Text = "已選取 1 個 APK。按「一鍵安裝」即可。";
            else
                fileHint.Text = "已選取 " + apkFiles.Count + " 個檔案。僅限同一 App 的 base / split APK，請勾選分割 APK。";
        }

        private bool ChooseApk()
        {
            using (OpenFileDialog picker = new OpenFileDialog())
            {
                picker.Title = "選擇 APK（多選僅供同一 App 的分割套件）";
                picker.Filter = "Android 安裝檔 (*.apk)|*.apk";
                picker.Multiselect = true;
                picker.CheckFileExists = true;
                if (picker.ShowDialog(this) != DialogResult.OK) return false;
                apkFiles.Clear();
                apkFiles.AddRange(picker.FileNames);
                RefreshFileList();
                if (apkFiles.Count > 1)
                    SetStatus("已選取多個 APK。若為同一 App 的分割套件，請在進階選項勾選分割 APK。", false);
                return true;
            }
        }

        private void ChooseAdb()
        {
            using (OpenFileDialog picker = new OpenFileDialog())
            {
                picker.Title = "選擇 Platform-Tools 中的 adb.exe";
                picker.Filter = "ADB 程式 (adb.exe)|adb.exe";
                picker.CheckFileExists = true;
                if (picker.ShowDialog(this) == DialogResult.OK)
                {
                    adbPath.Text = picker.FileName;
                    rememberedSerial = null;
                    devices.Items.Clear();
                    deviceHint.Text = "ADB 路徑已變更，安裝前會重新確認裝置。";
                }
            }
        }

        private bool EnsureAdb()
        {
            if (File.Exists(adbPath.Text)) return true;
            string found = AdbCore.FindAdb(baseDir);
            if (!String.IsNullOrEmpty(found)) { adbPath.Text = found; return true; }
            SetStatus("找不到 adb.exe。請完整解壓縮，或選擇現有的 adb.exe。", true);
            ChooseAdb();
            return File.Exists(adbPath.Text);
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (busy) return;
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;
            if (paths.Any(p => !String.Equals(Path.GetExtension(p), ".apk", StringComparison.OrdinalIgnoreCase) || !File.Exists(p)))
            {
                SetStatus("請拖入 .apk 檔案；XAPK、APKS、AAB 或資料夾無法直接安裝。", true);
                return;
            }
            apkFiles.Clear();
            apkFiles.AddRange(paths.Distinct(StringComparer.OrdinalIgnoreCase));
            RefreshFileList();
            SetStatus("APK 已載入。按「一鍵安裝」開始。", false);
        }

        private void SetBusy(bool value)
        {
            busy = value;
            install.Enabled = !value;
            foreach (Control control in locks) control.Enabled = !value;
            cancel.Enabled = value;
            progress.Style = value ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            progress.MarqueeAnimationSpeed = value ? 30 : 0;
            if (!value) progress.Value = 0;
        }

        private void SetStatus(string message, bool error)
        {
            status.Text = message;
            status.ForeColor = error ? Color.FromArgb(145, 57, 31) : ink;
            status.BackColor = error ? Color.FromArgb(255, 239, 228) : Color.FromArgb(229, 238, 245);
        }

        private void AppendLog(string text)
        {
            lock (logGate)
            {
                // Keep a blocked UI (for example an open file dialog) bounded.
                if (queuedChars > 240000) return;
                string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
                pendingLog.Enqueue(line);
                queuedChars += line.Length;
            }
        }

        private void DrainLog()
        {
            StringBuilder next = new StringBuilder();
            lock (logGate)
            {
                while (pendingLog.Count > 0) next.AppendLine(pendingLog.Dequeue());
                queuedChars = 0;
            }
            if (next.Length == 0) return;
            if (logBox.TextLength + next.Length > 500000)
                logBox.Text = "（顯示紀錄較長，較早內容已截短。）\r\n" + logBox.Text.Substring(Math.Max(0, logBox.TextLength - 180000));
            logBox.AppendText(next.ToString());
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        private async Task<CommandResult> RunAsync(string adb, List<string> args, int timeout,
            CancellationToken token, bool showOutput)
        {
            return await Task.Run(() => AdbCore.Run(adb, args, timeout, token, showOutput ? (Action<string>)AppendLog : null));
        }

        private static void CheckQuery(CommandResult result)
        {
            if (result.Cancelled) throw new OperationCanceledException();
            if (result.TimedOut) throw new IOException("讀取裝置逾時，請確認 USB 連接後再試。");
            if (result.ExitCode != 0 || result.OutputIncomplete || result.OutputTruncated)
                throw new IOException("ADB 無法讀取裝置：\r\n" + result.Output);
        }

        private void FillDevices(List<DeviceInfo> found, string preferred)
        {
            populatingDevices = true;
            try
            {
                devices.Items.Clear();
                foreach (DeviceInfo device in found) devices.Items.Add(device);
                if (!String.IsNullOrEmpty(preferred))
                {
                    rememberedSerial = preferred;
                    for (int i = 0; i < found.Count; i++)
                        if (found[i].Serial == preferred) { devices.SelectedIndex = i; return; }
                    devices.SelectedIndex = -1;
                    deviceHint.Text = "原先選定的手機未找到，請重新連接或明確選擇其他手機。";
                    return;
                }
                devices.SelectedIndex = found.Count == 1 ? 0 : -1;
                if (found.Count == 1) rememberedSerial = found[0].Serial;
                if (found.Count == 0) deviceHint.Text = "尚未找到手機。請確認傳輸線、USB 偵錯；可開啟上方教學。";
                else if (found.Count > 1) deviceHint.Text = "找到多台裝置。請在下拉選單指定要安裝的手機。";
            }
            finally { populatingDevices = false; }
        }

        private async Task RefreshDevicesAsync()
        {
            if (busy || !EnsureAdb()) return;
            DeviceInfo before = devices.SelectedItem as DeviceInfo;
            string selected = before == null ? rememberedSerial : before.Serial;
            string adb = adbPath.Text;
            cancellation = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                SetStatus("正在偵測手機…", false);
                CommandResult result = await RunAsync(adb, new List<string> { "devices", "-l" }, 15000, cancellation.Token, true);
                CheckQuery(result);
                List<DeviceInfo> found = AdbCore.ParseDevices(result.Output);
                FillDevices(found, selected);
                if (found.Count == 0) SetStatus("未找到手機。請開啟「手機設定教學」，完成後按重新整理裝置。", true);
                else if (found.Count == 1 && found[0].State == "unauthorized")
                    SetStatus("請解鎖手機，按「允許 USB 偵錯」。按一鍵安裝後，工具會等候授權。", true);
                else if (found.Count > 1 && devices.SelectedIndex < 0)
                    SetStatus("已找到多台裝置，請先在下拉選單指定手機。", false);
                else SetStatus("已找到裝置。選好 APK 後按「一鍵安裝」。", false);
            }
            catch (OperationCanceledException) { SetStatus("已停止偵測。", false); }
            catch (Exception e) { SetStatus("偵測失敗，請查看下方紀錄。", true); AppendLog(e.Message); }
            finally { SetBusy(false); cancellation.Dispose(); cancellation = null; }
        }

        private async Task<bool> SupportsLowTargetAsync(string adb, string serial, CancellationToken token)
        {
            CommandResult help = await RunAsync(adb,
                new List<string> { "-s", serial, "shell", "cmd", "package", "help" }, 15000, token, false);
            if (help.Cancelled) throw new OperationCanceledException();
            if (help.ExitCode == 0 && !help.TimedOut && !help.OutputIncomplete
                && help.Output.IndexOf("--bypass-low-target-sdk-block", StringComparison.Ordinal) >= 0) return true;
            CommandResult fallback = await RunAsync(adb,
                new List<string> { "-s", serial, "shell", "pm", "help" }, 15000, token, false);
            if (fallback.Cancelled) throw new OperationCanceledException();
            return fallback.ExitCode == 0 && !fallback.TimedOut && !fallback.OutputIncomplete
                && fallback.Output.IndexOf("--bypass-low-target-sdk-block", StringComparison.Ordinal) >= 0;
        }

        private async Task InstallAsync()
        {
            if (busy) return;
            if (apkFiles.Count == 0 && !ChooseApk()) return;
            if (!EnsureAdb()) return;
            string[] files = apkFiles.ToArray();
            InstallOptions options = new InstallOptions
            {
                Replace = replace.Checked,
                AllowDowngrade = downgrade.Checked,
                AllowTest = testOnly.Checked,
                SplitMode = split.Checked
            };
            bool oldCompatibility = autoOld.Checked;
            // Validate selected files before any phone command is sent.
            try { AdbCore.BuildInstallArguments("file-validation", files, options); }
            catch (Exception e) { SetStatus("請確認選取的 APK 與進階選項。", true); AppendLog(e.Message); return; }
            DeviceInfo selected = devices.SelectedItem as DeviceInfo;
            string preferred = selected == null ? rememberedSerial : selected.Serial;
            string adb = adbPath.Text;
            bool installationStarted = false;
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            SetBusy(true);
            AppendLog("──────── 開始一鍵安裝 ────────");
            try
            {
                SetStatus("正在偵測手機。尚未接線時，工具會等候 60 秒。", false);
                Stopwatch connecting = Stopwatch.StartNew();
                List<DeviceInfo> found;
                DeviceInfo target = null;
                string waitingMessage = String.Empty;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    CommandResult result = await RunAsync(adb, new List<string> { "devices", "-l" }, 15000, token, false);
                    CheckQuery(result);
                    found = AdbCore.ParseDevices(result.Output);
                    FillDevices(found, preferred);
                    if (preferred != null)
                        target = found.FirstOrDefault(d => d.Serial == preferred);
                    else if (found.Count == 1)
                    {
                        target = found[0];
                        // Once a target is observed, never switch to another device silently.
                        preferred = target.Serial;
                    }
                    else if (found.Count > 1)
                    {
                        SetStatus("找到多台裝置，請選擇目標手機後，再按一鍵安裝。", true);
                        AppendLog("多台裝置：已停止，等待你明確選擇要安裝的手機。");
                        return;
                    }
                    if (target != null && target.State == "device") break;
                    if (target != null && target.State != "unauthorized" && target.State != "authorizing"
                        && target.State != "offline" && target.State != "connecting")
                        throw new IOException("目前裝置狀態為「" + target.State + "」，請讓手機進入一般 Android 系統後再試。");
                    string message;
                    if (target != null && (target.State == "unauthorized" || target.State == "authorizing"))
                        message = "等待手機授權：解鎖手機並按「允許 USB 偵錯」。可開啟手機設定教學。";
                    else if (target != null && target.State == "offline")
                        message = "手機目前離線。請解鎖並重新插線；工具會繼續等候。";
                    else message = "等待手機連接：請接上可傳資料的 USB 線，並開啟 USB 偵錯。";
                    SetStatus(message, false);
                    if (message != waitingMessage) { AppendLog(message); waitingMessage = message; }
                    if (connecting.ElapsedMilliseconds >= 60000)
                    {
                        SetStatus("尚未連線或授權。請查看手機設定教學，完成後再按一鍵安裝。", true);
                        AppendLog("已等候 60 秒，尚未開始安裝。");
                        return;
                    }
                    await Task.Delay(1200, token);
                }
                string serial = target.Serial;
                AppendLog("目標裝置：" + target.ToString());
                SetStatus("已連接手機，正在檢查舊版 App 相容選項…", false);
                if (oldCompatibility)
                {
                    options.LowTarget = await SupportsLowTargetAsync(adb, serial, token);
                    AppendLog(options.LowTarget
                        ? "手機支援舊版 App 相容選項，將套用 --bypass-low-target-sdk-block。"
                        : "未確認手機支援舊版相容參數，先使用一般安裝方式。");
                }
                token.ThrowIfCancellationRequested();
                List<string> args = AdbCore.BuildInstallArguments(serial, files, options);
                AppendLog("執行：adb " + AdbCore.JoinArguments(args));
                SetStatus("正在安裝。請保持 USB 連接，並查看手機是否出現安裝確認。", false);
                installationStarted = true;
                CommandResult installed = await RunAsync(adb, args, 600000, token, true);
                if (installed.Cancelled) throw new OperationCanceledException();
                if (installed.TimedOut)
                    throw new IOException("等待安裝超過 10 分鐘。工具已停止本次指令，手機端可能仍在完成安裝；請先確認手機上的結果。");
                if (installed.Success)
                {
                    SetStatus("安裝成功！請到手機查看並開啟 App。", false);
                    status.BackColor = Color.FromArgb(220, 243, 230);
                    status.ForeColor = Color.FromArgb(22, 103, 65);
                    AppendLog("完成：ADB 回傳 Success，且結束碼為 0。");
                }
                else
                {
                    SetStatus("安裝未成功。下方已列出原因與處理方式。", true);
                    AppendLog("結束碼：" + installed.ExitCode);
                    if (installed.OutputIncomplete || installed.OutputTruncated)
                        AppendLog("輸出紀錄不完整，無法確認結果。請先查看手機是否已安裝成功。");
                    AppendLog("處理建議：" + AdbCore.ExplainFailure(installed.Output));
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus(installationStarted ? "已停止本次指令；請查看手機端是否已完成安裝。" : "已停止，尚未開始安裝。", false);
                AppendLog(installationStarted ? "取消不會自動回復手機端的安裝狀態；再次嘗試前請先檢查。" : "使用者停止作業。");
            }
            catch (Exception e)
            {
                SetStatus("作業未完成，請查看下方紀錄。", true);
                AppendLog(e.Message);
            }
            finally
            {
                SetBusy(false);
                cancellation.Dispose();
                cancellation = null;
                DrainLog();
            }
        }

        private void SaveLog()
        {
            DrainLog();
            using (SaveFileDialog picker = new SaveFileDialog())
            {
                picker.Title = "儲存本次安裝紀錄";
                picker.Filter = "文字檔 (*.txt)|*.txt";
                picker.FileName = "ADB-install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(picker.FileName, logBox.Text, new UTF8Encoding(true)); }
                catch (Exception e) { AppendLog("無法儲存紀錄：" + e.Message); }
            }
        }

        private void OpenReadme()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(baseDir, "README-zh-TW.txt")) { UseShellExecute = true });
            }
            catch (Exception e) { AppendLog("無法開啟說明：" + e.Message); }
        }

        private void ShowGuide()
        {
            // Modeless so USB authorization polling can continue underneath.
            SetupGuide guide = new SetupGuide(baseDir);
            guide.Show(this);
        }

        private void PrepareSnapshot()
        {
            startImmediately = false;
            SetStatus("示範畫面：尚未連接手機。選擇 APK 後即可開始。", false);
            AppendLog("介面預覽，不會連線或執行 ADB。");
            AppendLog("1. 瀏覽 APK → 2. USB 連接手機 → 3. 一鍵安裝。");
            AppendLog("不會設定手機？點選上方「手機設定教學」，依品牌查看步驟。");
            DrainLog();
            if (advancedPreview)
            {
                advanced.Visible = true;
                layout.RowStyles[4].Height = 108;
            }
            if (guidePreview) ShowGuide();
            if (String.IsNullOrEmpty(snapshotPath)) return;
            System.Windows.Forms.Timer capture = new System.Windows.Forms.Timer();
            capture.Interval = 700;
            capture.Tick += delegate
            {
                capture.Stop();
                Form surface = this;
                SetupGuide guide = null;
                if (snapshotGuide)
                {
                    guide = new SetupGuide(baseDir);
                    guide.Show(this);
                    surface = guide;
                }
                Application.DoEvents();
                using (Bitmap bmp = new Bitmap(surface.Width, surface.Height))
                {
                    surface.DrawToBitmap(bmp, new Rectangle(0, 0, surface.Width, surface.Height));
                    bmp.Save(snapshotPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                if (guide != null) guide.Close();
                capture.Dispose();
                Close();
            };
            capture.Start();
        }
    }

    public sealed class SetupGuide : Form
    {
        public SetupGuide(string baseDir)
        {
            Text = "手機設定教學  |  開發人員選項與 USB 偵錯";
            Font = new Font("Microsoft JhengHei UI", 10.5F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(810, 620);
            MinimumSize = new Size(650, 460);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(14);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            Controls.Add(root);
            Label heading = new Label();
            heading.Text = "請選擇你的手機品牌。\r\n需要的是「開發人員選項 ＋ USB 偵錯」，不用 Root 或手機管理員權限。";
            heading.Dock = DockStyle.Fill;
            heading.ForeColor = Color.FromArgb(27, 45, 66);
            heading.TextAlign = ContentAlignment.MiddleLeft;
            root.Controls.Add(heading, 0, 0);
            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            string[,] pages = new string[,] {
                { "小米／紅米／POCO", "Xiaomi.txt" },
                { "Sony", "Sony.txt" },
                { "Google Pixel", "Google.txt" },
                { "Samsung", "Samsung.txt" },
                { "連線與常見問題", "Common.txt" }
            };
            for (int i = 0; i < pages.GetLength(0); i++)
            {
                TabPage page = new TabPage(pages[i, 0]);
                page.Padding = new Padding(10);
                RichTextBox content = new RichTextBox();
                content.ReadOnly = true;
                content.Dock = DockStyle.Fill;
                content.BackColor = Color.White;
                content.BorderStyle = BorderStyle.None;
                content.Font = new Font("Microsoft JhengHei UI", 11);
                content.DetectUrls = true;
                string path = Path.Combine(baseDir, "phone-guides", pages[i, 1]);
                try { content.Text = File.ReadAllText(path, Encoding.UTF8); }
                catch (IOException) { content.Text = "找不到教學檔案。請完整解壓縮，保留 phone-guides 資料夾。\r\n\r\n共通步驟：啟用開發人員選項，開啟 USB 偵錯，接上 USB 線後在手機允許這台電腦。"; }
                catch (UnauthorizedAccessException) { content.Text = "無法讀取教學檔案，請確認資料夾存取權限。"; }
                content.LinkClicked += delegate(object sender, LinkClickedEventArgs e)
                {
                    Uri uri;
                    if (Uri.TryCreate(e.LinkText, UriKind.Absolute, out uri) && uri.Scheme == "https")
                        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                };
                page.Controls.Add(content);
                tabs.TabPages.Add(page);
            }
            root.Controls.Add(tabs, 0, 1);
            Button close = new Button();
            close.Text = "看完了，回到安裝";
            close.Anchor = AnchorStyles.Right;
            close.Size = new Size(190, 34);
            close.Click += delegate { Close(); };
            root.Controls.Add(close, 0, 2);
        }
    }
}
