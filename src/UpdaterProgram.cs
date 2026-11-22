// Copyright © 2026 Jason1993余. See LICENSE.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("小金 ADB 安裝助手更新程式")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyCopyright("© 2026 Jason1993余")]

namespace Jason1993Yu.AdbApkTool
{
    internal static class UpdaterProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length != 1 || args[0] != "--apply")
            {
                MessageBox.Show("請從「小金 ADB 安裝助手」的更新功能啟動本程式。\r\n© 2026 Jason1993余",
                    "更新程式", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.Run(new UpdaterForm());
        }
    }

    internal sealed class UpdaterForm : Form
    {
        private readonly Label message;
        private readonly ProgressBar progress;
        private readonly Button close;
        private bool working = true;

        internal UpdaterForm()
        {
            Text = "小金 ADB 安裝助手 — 安全更新";
            Font = new Font("Microsoft JhengHei UI", 10F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(600, 230);
            MinimumSize = new Size(560, 260);
            MaximizeBox = false;
            BackColor = Color.FromArgb(242, 246, 250);
            TableLayoutPanel panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
                Padding = new Padding(22) };
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            message = new Label { Text = "正在驗證更新資料…", Dock = DockStyle.Fill, AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft };
            progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee };
            close = new Button { Text = "關閉", Width = 110, Height = 34, Enabled = false, Anchor = AnchorStyles.Right };
            close.Click += delegate { Close(); };
            Label signature = new Label { Text = "© 2026 Jason1993余", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(65, 80, 96) };
            panel.Controls.Add(message, 0, 0);
            panel.Controls.Add(progress, 0, 1);
            panel.Controls.Add(close, 0, 2);
            panel.Controls.Add(signature, 0, 3);
            Controls.Add(panel);
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (working) e.Cancel = true;
            };
            Shown += async delegate { await RunAsync(); };
        }

        private async Task RunAsync()
        {
            IProgress<string> reporter = new Progress<string>(delegate(string text) { message.Text = text; });
            string installDirectory = null;
            try
            {
                installDirectory = await Task.Run(delegate { return PerformUpdate(reporter); });
                message.Text = "已完成，正在重新開啟程式…";
                ProcessStartInfo start = new ProcessStartInfo(Path.Combine(installDirectory, "ADB_APK_Tool.exe"));
                start.UseShellExecute = false;
                start.WorkingDirectory = installDirectory;
                Process process = Process.Start(start);
                if (process == null) throw new IOException("無法重新開啟主程式。");
                process.Dispose();
                working = false;
                Close();
            }
            catch (Exception ex)
            {
                // No automatic retries or process termination; leave backups and journal available.
                message.Text = ex.Message + "\r\n請關閉此視窗後重新開啟主程式；若需要修復，請保留更新備份。";
                progress.Style = ProgressBarStyle.Blocks;
                progress.Value = 0;
                working = false;
                close.Enabled = true;
            }
        }

        private static string PerformUpdate(IProgress<string> reporter)
        {
            string stage = UpdateFiles.CanonicalDirectory(AppDomain.CurrentDomain.BaseDirectory);
            UpdateFiles.EnsureNoReparsePoints(stage);
            UpdaterRequest request = UpdateFiles.ReadJson<UpdaterRequest>(Path.Combine(stage, "request.json"), 8192);
            string installDirectory = UpdateFiles.CanonicalDirectory(request.InstallDirectory);
            string storage = UpdateFiles.StorageDirectory(installDirectory);
            string stageId = Path.GetFileName(stage);
            if (!UpdateFiles.IsIdentifier(stageId) || !String.Equals(stage,
                Path.Combine(storage, "staging", stageId), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新暫存與程式位置不符合。");
            string envelope = UpdateFiles.ReadBoundedText(Path.Combine(stage, "envelope.json"), UpdateSecurity.MaxEnvelopeBytes);
            UpdateManifest manifest = UpdateSecurity.VerifyEnvelope(envelope, UpdateTrust.PublicKeyXml);
            string payload = Path.Combine(stage, "apply-" + Guid.NewGuid().ToString("N"));
            reporter.Report("正在驗證補丁簽章、完整性及檔案路徑…");
            List<string> files = UpdateSecurity.VerifyAndExtractPackage(Path.Combine(stage, "package.zip"), manifest,
                payload, CancellationToken.None);
            if (UpdateFiles.Sha256File(Assembly.GetExecutingAssembly().Location) !=
                UpdateFiles.Sha256File(Path.Combine(payload, "JasonUpdater.exe")))
                throw new System.Security.Cryptography.CryptographicException("更新程式自身驗證失敗。");
            if (!request.RecoverOnly)
            {
                string cache = Path.Combine(storage, "verified-manifest.json");
                if (File.Exists(cache))
                {
                    UpdateManifest known = UpdateSecurity.VerifyEnvelope(UpdateFiles.ReadBoundedText(cache,
                        UpdateSecurity.MaxEnvelopeBytes), UpdateTrust.PublicKeyXml);
                    UpdateSecurity.EnsureNotRolledBack(known, manifest);
                }
            }
            reporter.Report("等待主程式關閉。請勿啟動另一個安裝助手…");
            WaitForParent(request, installDirectory);
            using (InstallationLease lease = InstallationLease.TryAcquire(installDirectory))
            {
                if (lease == null) throw new IOException("另一個安裝助手或更新程式仍在使用此資料夾，請關閉後再試。");
                if (request.RecoverOnly)
                {
                    UpdateJournal journal = UpdateTransaction.ReadJournal(storage);
                    if (journal.StageId != stageId) throw new InvalidDataException("還原紀錄與更新暫存不符。");
                    UpdateTransaction.Recover(installDirectory, storage, reporter.Report);
                }
                else
                {
                    // Helper version equals the target release; compare the currently installed assembly instead.
                    Version installed = AssemblyName.GetAssemblyName(Path.Combine(installDirectory, "ADB_APK_Tool.exe")).Version;
                    Version desired = new Version(manifest.Version);
                    Version normalized = new Version(desired.Major, desired.Minor, Math.Max(0, desired.Build), Math.Max(0, desired.Revision));
                    if (installed >= normalized) throw new InvalidOperationException("主程式已更新或版本較新，不會覆蓋較新版本。");
                    UpdateTransaction.Apply(installDirectory, storage, stageId, payload, files, reporter.Report, null);
                }
            }
            return installDirectory;
        }

        private static void WaitForParent(UpdaterRequest request, string installDirectory)
        {
            if (request.ParentId <= 0 || request.ParentStartTicks <= 0)
                throw new InvalidDataException("主程式識別不正確。");
            Process parent;
            try { parent = Process.GetProcessById(request.ParentId); }
            catch (ArgumentException) { return; }
            using (parent)
            {
                if (parent.HasExited) return;
                if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks)
                    return; // Original parent exited; PID was reused. Never wait on or terminate the new process.
                string expected = Path.GetFullPath(Path.Combine(installDirectory, "ADB_APK_Tool.exe"));
                if (!String.Equals(Path.GetFullPath(parent.MainModule.FileName), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("等待中的程式並非安裝助手。");
                if (!parent.WaitForExit(90000)) throw new TimeoutException("主程式仍未關閉；未修改任何程式檔案。");
            }
        }
    }
}
