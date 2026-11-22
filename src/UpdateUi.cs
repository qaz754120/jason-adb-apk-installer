// Copyright © 2026 Jason1993余. See LICENSE for permitted use.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Jason1993Yu.AdbApkTool
{
    public sealed partial class MainForm
    {
        private readonly CancellationTokenSource updateLifetime = new CancellationTokenSource();
        private UpdateManager updateManager;
        private UpdateCheckResult updateResult;
        private Button updateSettings;
        private bool initialUpdateCheckComplete, updateChecking, updateGateRequired;
        private bool closingForUpdate, updateStateUnavailable, updatePreview, mandatoryUpdatePreview;

        private bool CanUseInstaller
        {
            get { return initialUpdateCheckComplete && !updateChecking && !updateGateRequired && !closingForUpdate; }
        }

        private void InitializeUpdates()
        {
            initialUpdateCheckComplete = previewMode;
            if (!previewMode) updateManager = new UpdateManager(baseDir);
            ApplyUpdateControlState();
        }

        private void ApplyUpdateControlState()
        {
            bool enabled = !busy && CanUseInstaller;
            if (install != null) install.Enabled = enabled;
            foreach (Control control in locks) control.Enabled = enabled;
            if (updateSettings != null)
            {
                updateSettings.Enabled = !busy && !updateChecking && !closingForUpdate;
                updateSettings.Text = updateChecking ? "正在檢查更新…" : updateGateRequired ? "必須更新／修復" : "更新與提醒";
            }
        }

        private async Task CheckForUpdatesAsync(bool manual)
        {
            if (IsDisposed || Disposing || busy || updateChecking || closingForUpdate) return;
            if (previewMode)
            {
                ShowPreviewDialog(mandatoryUpdatePreview);
                return;
            }
            updateChecking = true;
            ApplyUpdateControlState();
            SetStatus("正在檢查程式更新，完成後即可開始安裝…", false);
            try
            {
                // A verified cached mandatory policy also applies without Internet access.
                updateResult = updateManager.LoadCached();
                updateGateRequired = updateResult.Mandatory || updateResult.RecoveryRequired;
                if (!updateResult.RecoveryRequired)
                    updateResult = await updateManager.CheckAsync(updateLifetime.Token);
                if (IsDisposed || Disposing || updateLifetime.IsCancellationRequested) return;
                updateStateUnavailable = false;
                updateGateRequired = updateResult.Mandatory || updateResult.RecoveryRequired;
                AppendLog("程式更新：" + updateResult.Message);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                if (IsDisposed || Disposing || updateLifetime.IsCancellationRequested) return;
                // Networking failures are handled by UpdateManager. An unexpected local-state
                // failure must not let an unfinished update or a known policy be bypassed.
                updateStateUnavailable = true;
                updateGateRequired = true;
                AppendLog("無法安全讀取更新狀態：" + e.Message);
            }
            finally
            {
                initialUpdateCheckComplete = true;
                updateChecking = false;
                if (!IsDisposed && !Disposing) ApplyUpdateControlState();
            }
            if (IsDisposed || Disposing || updateLifetime.IsCancellationRequested) return;
            if (updateStateUnavailable)
            {
                SetStatus("更新狀態無法讀取。請按「必須更新／修復」重試，或重新下載官方完整版本。", true);
                return;
            }
            if (manual || updateGateRequired || (updateResult.Available && updateResult.ReminderDue))
                ShowUpdateDialog();
            if (IsDisposed || Disposing || closingForUpdate) return;
            if (updateGateRequired)
                SetStatus(updateResult.RecoveryRequired ? "上次程式更新尚未完成，請先還原。" : "目前版本已達更新門檻，完成更新後才能安裝 APK。", true);
            else if (updateResult != null && !updateResult.Online)
                SetStatus("暫時無法連線檢查更新；目前可依已確認的版本狀態使用。", false);
            else SetStatus("更新檢查完成。選擇 APK 後即可開始。", false);
        }

        private void ShowUpdateDialog()
        {
            if (busy || IsDisposed || Disposing || updateResult == null) return;
            using (UpdateDialog dialog = new UpdateDialog(updateManager, updateResult, false,
                delegate { return !busy && !IsDisposed && !Disposing; }))
            {
                dialog.ShowDialog(this);
                updateResult = dialog.Result;
                updateGateRequired = updateResult != null && (updateResult.Mandatory || updateResult.RecoveryRequired);
                ApplyUpdateControlState();
                if (dialog.RestartRequested)
                {
                    closingForUpdate = true;
                    Close();
                }
                else if (dialog.ExitRequested) Close();
            }
        }

        private void ShowUpdatePreviewIfRequested()
        {
            if (updatePreview) ShowPreviewDialog(mandatoryUpdatePreview);
        }

        private void ShowPreviewDialog(bool mandatory)
        {
            UpdateCheckResult demo = new UpdateCheckResult
            {
                Manifest = new UpdateManifest
                {
                    Schema = 1, AppId = UpdateTrust.AppId, Channel = "stable",
                    Version = mandatory ? "1.4.0" : "1.2.0",
                    ReleaseSequence = UpdateTrust.ReleaseSequence + (mandatory ? 3 : 1),
                    MinimumSupportedSequence = mandatory ? UpdateTrust.ReleaseSequence + 1 : 1,
                    Notes = "介面示範：改善安裝相容性與連線提示。\r\n\r\n這個畫面不會連線、下載、儲存提醒或修改檔案。"
                },
                Available = true, Mandatory = mandatory, ReminderDue = true, Online = true,
                Message = mandatory ? "示範：目前落後 3 次正式發布，必須先更新。" : "示範：發現新版，可選擇稍後提醒。"
            };
            UpdateDialog dialog = new UpdateDialog(null, demo, true, delegate { return false; });
            dialog.Show(this);
        }
    }

    internal sealed class UpdateDialog : Form
    {
        private readonly UpdateManager manager;
        private readonly bool preview;
        private readonly Func<bool> mayApply;
        private UpdateCheckResult result;
        private CancellationTokenSource operation;
        private bool working, closeAfterCancel;
        private string stagedDirectory;
        private Label heading, summary, versions, reminder, activity;
        private TextBox notes;
        private ProgressBar progress;
        private Button apply, retry, releasePage, week, month, later, close;

        public UpdateCheckResult Result { get { return result; } }
        public bool RestartRequested { get; private set; }
        public bool ExitRequested { get; private set; }
        private bool Required { get { return result != null && (result.Mandatory || result.RecoveryRequired); } }

        public UpdateDialog(UpdateManager manager, UpdateCheckResult result, bool preview, Func<bool> mayApply)
        {
            this.manager = manager;
            this.result = result;
            this.preview = preview;
            this.mayApply = mayApply;
            Text = "程式更新與提醒  |  小金 ADB 安裝助手";
            Font = new Font("Microsoft JhengHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(710, 550);
            MinimumSize = new Size(690, 540);
            BackColor = Color.FromArgb(242, 246, 250);
            ForeColor = Color.FromArgb(27, 45, 66);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            BuildUi();
            RenderResult();
            FormClosing += OnClosing;
        }

        private void BuildUi()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(20, 15, 20, 10);
            layout.ColumnCount = 1;
            layout.RowCount = 9;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(layout);

            heading = LabelFor("");
            heading.Font = new Font(Font.FontFamily, 17F, FontStyle.Bold);
            layout.Controls.Add(heading, 0, 0);
            summary = LabelFor("");
            summary.Padding = new Padding(10, 3, 10, 3);
            layout.Controls.Add(summary, 0, 1);
            versions = LabelFor("");
            versions.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            layout.Controls.Add(versions, 0, 2);
            notes = new TextBox();
            notes.Dock = DockStyle.Fill;
            notes.Multiline = true;
            notes.ReadOnly = true;
            notes.ScrollBars = ScrollBars.Vertical;
            notes.BackColor = Color.White;
            notes.Font = new Font(Font.FontFamily, 10F);
            notes.Margin = new Padding(3, 4, 3, 4);
            layout.Controls.Add(notes, 0, 3);
            reminder = LabelFor("");
            reminder.Font = new Font(Font.FontFamily, 9F);
            layout.Controls.Add(reminder, 0, 4);
            activity = LabelFor("");
            activity.Font = new Font(Font.FontFamily, 9F);
            activity.AutoEllipsis = true;
            layout.Controls.Add(activity, 0, 5);
            progress = new ProgressBar();
            progress.Dock = DockStyle.Fill;
            progress.Margin = new Padding(3, 4, 3, 4);
            layout.Controls.Add(progress, 0, 6);

            TableLayoutPanel actions = new TableLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.ColumnCount = 1;
            actions.RowCount = 2;
            actions.Margin = Padding.Empty;
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            FlowLayoutPanel first = new FlowLayoutPanel();
            first.Dock = DockStyle.Fill;
            first.WrapContents = false;
            first.Margin = Padding.Empty;
            apply = ButtonFor("下載並更新", 212);
            apply.BackColor = Color.FromArgb(0, 119, 115);
            apply.ForeColor = Color.White;
            apply.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            apply.Click += async delegate { await ApplyAsync(); };
            retry = ButtonFor("重新檢查", 124);
            retry.Click += async delegate { await RetryAsync(); };
            releasePage = ButtonFor("官方發布頁", 124);
            releasePage.Click += delegate { OpenReleasePage(); };
            first.Controls.Add(apply);
            first.Controls.Add(retry);
            first.Controls.Add(releasePage);
            FlowLayoutPanel second = new FlowLayoutPanel();
            second.Dock = DockStyle.Fill;
            second.WrapContents = false;
            second.Margin = Padding.Empty;
            week = ButtonFor("1 週後再提醒", 145);
            week.Click += delegate { Snooze(false); };
            month = ButtonFor("1 個月後再提醒", 155);
            month.Click += delegate { Snooze(true); };
            later = ButtonFor("下次開啟再說", 140);
            later.Click += delegate { Close(); };
            close = ButtonFor("關閉", 124);
            close.Click += delegate
            {
                if (working)
                {
                    operation.Cancel();
                    activity.Text = "正在取消，請稍候…";
                    close.Enabled = false;
                }
                else Close();
            };
            second.Controls.Add(week);
            second.Controls.Add(month);
            second.Controls.Add(later);
            second.Controls.Add(close);
            actions.Controls.Add(first, 0, 0);
            actions.Controls.Add(second, 0, 1);
            layout.Controls.Add(actions, 0, 7);
            Label signature = LabelFor("© 2026 Jason1993余");
            signature.Name = "UpdateAuthorSignature";
            signature.TextAlign = ContentAlignment.MiddleRight;
            signature.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            layout.Controls.Add(signature, 0, 8);
        }

        private Label LabelFor(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(3, 1, 3, 1) };
        }

        private Button ButtonFor(string text, int width)
        {
            Button button = new Button { Text = text, Width = width, Height = 36,
                Margin = new Padding(3, 3, 3, 3), FlatStyle = FlatStyle.Flat,
                BackColor = Color.White, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = Color.FromArgb(199, 211, 222);
            return button;
        }

        private void RenderResult()
        {
            bool recovery = result != null && result.RecoveryRequired;
            bool available = result != null && result.Available;
            bool mandatory = result != null && result.Mandatory;
            heading.Text = recovery ? "先還原上次未完成的更新" : mandatory ? "請更新後繼續使用" : available ? "有新版本可用" : "程式更新與提醒";
            summary.BackColor = Required ? Color.FromArgb(255, 235, 221) : Color.FromArgb(226, 239, 246);
            summary.Text = recovery
                ? "偵測到上次更新尚未完成。請關閉本程式並還原備份，完成後會重新開啟。"
                : mandatory
                    ? "目前版本已落後至少 3 次正式發布，或低於作者設定的最低支援版本。完成更新前，APK 安裝功能暫停。"
                    : result != null ? result.Message : "按「重新檢查」取得最新版本資訊。";
            versions.Text = "目前 v" + UpdateTrust.Version + "（第 " + UpdateTrust.ReleaseSequence + " 次發布）"
                + (result != null && result.Manifest != null
                    ? "    最新 v" + result.Manifest.Version + "（第 " + result.Manifest.ReleaseSequence + " 次）" : "    最新版本尚未確認");
            notes.Text = recovery
                ? "還原會針對上次更新的程式檔案復原備份。請等候還原完成，期間不要搬動程式資料夾。\r\n\r\n若還原失敗，可從官方發布頁下載完整程式到新的資料夾。"
                : result != null && result.Manifest != null && !String.IsNullOrWhiteSpace(result.Manifest.Notes)
                    ? result.Manifest.Notes : "目前沒有可顯示的新版說明。";
            reminder.Text = Required
                ? "必須更新時不提供延後提醒。你仍可重新檢查、查看發布頁或關閉程式。"
                : "提醒只在開啟程式時出現。可延後 1 週或 1 個月；每次開啟仍會確認最低支援版本。";
            if (preview) reminder.Text = "介面預覽：所有按鈕都不會下載、連網或變更檔案。";
            activity.Text = preview ? "示範畫面" : result != null && !result.Online
                ? result.Manifest != null ? "目前使用上次已驗證的更新資訊；連網後可重新檢查。" : "尚未取得版本資訊；連網後可重新檢查。"
                : "下載後會先驗證內容，再讓你重新啟動套用。";
            apply.Text = recovery ? "還原並重新啟動" : stagedDirectory != null ? "重新啟動套用更新" : "下載並更新";
            apply.Enabled = !working && (recovery || available);
            retry.Enabled = !working && !recovery;
            releasePage.Enabled = !working;
            week.Visible = month.Visible = later.Visible = !Required && available;
            week.Enabled = month.Enabled = later.Enabled = !working && stagedDirectory == null;
            close.Text = working ? "取消下載" : Required ? "關閉程式" : "關閉";
            close.Enabled = true;
        }

        private async Task RetryAsync()
        {
            if (working || preview || manager == null) return;
            try { DiscardUnusedStage(); }
            catch (Exception e)
            {
                MessageBox.Show(this, "無法清理先前下載的補丁，請先套用或關閉後再試：\r\n" + e.Message,
                    "程式更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetWorking(true, "正在檢查最新版本…", false);
            try
            {
                result = await manager.CheckAsync(operation.Token);
                stagedDirectory = null;
            }
            catch (OperationCanceledException) { activity.Text = "已取消檢查。"; }
            catch (Exception e)
            {
                MessageBox.Show(this, "檢查未完成：\r\n" + e.Message, "程式更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally { FinishWorking(); }
        }

        private async Task ApplyAsync()
        {
            if (working || preview || manager == null) return;
            if (!mayApply())
            {
                activity.Text = "請先等候手機安裝作業完成。";
                return;
            }
            if (result.RecoveryRequired)
            {
                try
                {
                    manager.LaunchRecovery();
                    RestartRequested = true;
                    Close();
                }
                catch (Exception e)
                {
                    MessageBox.Show(this, "無法啟動還原：\r\n" + e.Message + "\r\n\r\n可從官方發布頁重新下載完整版本。",
                        "程式還原", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }
            if (stagedDirectory != null)
            {
                try
                {
                    manager.LaunchUpdater(stagedDirectory);
                    RestartRequested = true;
                    Close();
                }
                catch (Exception e)
                {
                    MessageBox.Show(this, "無法啟動更新，現有程式尚未關閉：\r\n" + e.Message,
                        "程式更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }
            if (!result.Available) return;
            SetWorking(true, "正在下載並驗證補丁…", true);
            bool downloaded = false;
            string message = null;
            try
            {
                Progress<int> report = new Progress<int>(delegate(int percent)
                {
                    if (IsDisposed || Disposing || !working) return;
                    progress.Value = Math.Max(0, Math.Min(100, percent));
                    activity.Text = percent < 100 ? "正在下載並驗證補丁… " + percent + "%" : "下載完成，正在驗證更新內容…";
                });
                stagedDirectory = await manager.StageUpdateAsync(result, report, operation.Token);
                downloaded = true;
            }
            catch (OperationCanceledException) { message = "已取消下載，現有版本未變更。"; }
            catch (Exception e) { message = "下載或驗證失敗，現有版本未變更。"; MessageBox.Show(this, message + "\r\n" + e.Message, "程式更新", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { FinishWorking(); }
            if (IsDisposed || Disposing) return;
            if (downloaded)
            {
                progress.Value = 100;
                activity.Text = "補丁已下載且驗證完成。按「重新啟動套用更新」後會關閉本程式。";
            }
            else if (message != null) activity.Text = message;
        }

        private void SetWorking(bool value, string text, bool downloading)
        {
            working = value;
            operation = new CancellationTokenSource();
            progress.Style = downloading ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            progress.Value = 0;
            RenderResult();
            activity.Text = text;
            close.Text = downloading ? "取消下載" : "取消檢查";
        }

        private void FinishWorking()
        {
            working = false;
            if (operation != null) { operation.Dispose(); operation = null; }
            progress.Style = ProgressBarStyle.Continuous;
            RenderResult();
            if (closeAfterCancel) Close();
        }

        private void Snooze(bool oneMonth)
        {
            if (working || Required || preview || manager == null) return;
            try
            {
                manager.Snooze(result, oneMonth);
                Close();
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "無法儲存提醒時間：\r\n" + e.Message, "更新提醒", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void OpenReleasePage()
        {
            if (preview) return;
            try { Process.Start(new ProcessStartInfo(UpdateTrust.RepoUrl + "/releases") { UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show(this, "無法開啟發布頁：" + e.Message, "程式更新", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        private void DiscardUnusedStage()
        {
            if (preview || manager == null || RestartRequested || stagedDirectory == null) return;
            manager.DiscardStagedUpdate(stagedDirectory);
            stagedDirectory = null;
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (working)
            {
                e.Cancel = true;
                closeAfterCancel = true;
                operation.Cancel();
                activity.Text = "正在取消，完成後會關閉更新視窗…";
                return;
            }
            if (!RestartRequested)
            {
                try { DiscardUnusedStage(); }
                catch (Exception error)
                {
                    MessageBox.Show(this, "尚未使用的補丁暫時無法清理，現有程式未變更。\r\n" + error.Message,
                        "程式更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            ExitRequested = !preview && Required && !RestartRequested;
        }
    }
}
