// Copyright © 2026 Jason1993余. See LICENSE.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jason1993Yu.AdbApkTool
{
    [DataContract]
    public sealed class UpdateManifest
    {
        [DataMember(Name = "schema", IsRequired = true)] public int Schema { get; set; }
        [DataMember(Name = "appId", IsRequired = true)] public string AppId { get; set; }
        [DataMember(Name = "channel", IsRequired = true)] public string Channel { get; set; }
        [DataMember(Name = "version", IsRequired = true)] public string Version { get; set; }
        [DataMember(Name = "releaseSequence", IsRequired = true)] public int ReleaseSequence { get; set; }
        [DataMember(Name = "minimumSupportedSequence", IsRequired = true)] public int MinimumSupportedSequence { get; set; }
        [DataMember(Name = "publishedUtc", IsRequired = true)] public string PublishedUtc { get; set; }
        [DataMember(Name = "notes", IsRequired = true)] public string Notes { get; set; }
        [DataMember(Name = "packageUrl", IsRequired = true)] public string PackageUrl { get; set; }
        [DataMember(Name = "packageSize", IsRequired = true)] public long PackageSize { get; set; }
        [DataMember(Name = "packageSha256", IsRequired = true)] public string PackageSha256 { get; set; }
    }

    [DataContract]
    internal sealed class SignedUpdateEnvelope
    {
        [DataMember(Name = "payload", IsRequired = true)] public string Payload { get; set; }
        [DataMember(Name = "signature", IsRequired = true)] public string Signature { get; set; }
    }

    public sealed class UpdateCheckResult
    {
        public UpdateManifest Manifest { get; set; }
        public bool Available { get; set; }
        public bool Mandatory { get; set; }
        public bool ReminderDue { get; set; }
        public bool Online { get; set; }
        public bool RecoveryRequired { get; set; }
        public string Message { get; set; }
        internal string EnvelopeJson { get; set; }
    }

    [DataContract]
    internal sealed class UpdatePreferences
    {
        [DataMember(Name = "remindAfterUtc")] public string RemindAfterUtc { get; set; }
    }

    [DataContract]
    internal sealed class UpdaterRequest
    {
        [DataMember(Name = "installDirectory", IsRequired = true)] public string InstallDirectory { get; set; }
        [DataMember(Name = "parentId", IsRequired = true)] public int ParentId { get; set; }
        [DataMember(Name = "parentStartTicks", IsRequired = true)] public long ParentStartTicks { get; set; }
        [DataMember(Name = "recoverOnly", IsRequired = true)] public bool RecoverOnly { get; set; }
    }

    [DataContract]
    internal sealed class TransactionFile
    {
        [DataMember(Name = "path", IsRequired = true)] public string Path { get; set; }
        [DataMember(Name = "existed", IsRequired = true)] public bool Existed { get; set; }
        [DataMember(Name = "originalSha256")] public string OriginalSha256 { get; set; }
        [DataMember(Name = "newSha256", IsRequired = true)] public string NewSha256 { get; set; }
    }

    [DataContract]
    internal sealed class UpdateJournal
    {
        [DataMember(Name = "schema", IsRequired = true)] public int Schema { get; set; }
        [DataMember(Name = "backupId", IsRequired = true)] public string BackupId { get; set; }
        [DataMember(Name = "stageId", IsRequired = true)] public string StageId { get; set; }
        [DataMember(Name = "state", IsRequired = true)] public string State { get; set; }
        [DataMember(Name = "files", IsRequired = true)] public List<TransactionFile> Files { get; set; }
    }

    /// <summary>A per-installation mutex, acquired and disposed on the same thread.</summary>
    public sealed class InstallationLease : IDisposable
    {
        private Mutex mutex;
        private InstallationLease(Mutex value) { mutex = value; }
        public static InstallationLease TryAcquire(string installDirectory)
        {
            string name = "Local\\Jason1993Yu.AdbApkTool." + UpdateFiles.InstallationId(installDirectory);
            Mutex value = new Mutex(false, name);
            bool acquired = false;
            try
            {
                try { acquired = value.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (acquired) return new InstallationLease(value);
                value.Dispose();
                return null;
            }
            catch { value.Dispose(); throw; }
        }
        public void Dispose()
        {
            if (mutex == null) return;
            mutex.ReleaseMutex();
            mutex.Dispose();
            mutex = null;
        }
    }

    public sealed class UpdateManager
    {
        private readonly string installDirectory;
        private readonly string storageDirectory;
        private UpdateCheckResult trustedMemory;

        public UpdateManager(string installDirectory)
        {
            this.installDirectory = UpdateFiles.CanonicalDirectory(installDirectory);
            storageDirectory = UpdateFiles.StorageDirectory(this.installDirectory);
        }

        public bool HasPendingRecovery
        {
            get
            {
                string journalPath = Path.Combine(storageDirectory, "transaction.json");
                if (!File.Exists(journalPath)) return false;
                try { return UpdateFiles.ReadJson<UpdateJournal>(journalPath, 65536).State != "committed"; }
                catch { return true; }
            }
        }

        public UpdateCheckResult LoadCached()
        {
            UpdateCheckResult result = trustedMemory;
            if (result == null)
            {
                result = new UpdateCheckResult { Message = "尚未取得可驗證的版本資訊。" };
                string cachePath = Path.Combine(storageDirectory, "verified-manifest.json");
                try
                {
                    if (File.Exists(cachePath))
                    {
                        string envelope = UpdateFiles.ReadBoundedText(cachePath, UpdateSecurity.MaxEnvelopeBytes);
                        UpdateManifest manifest = UpdateSecurity.VerifyEnvelope(envelope, UpdateTrust.PublicKeyXml);
                        result = Result(manifest, envelope, false);
                        result.Message = "使用上次已驗證的版本資訊。";
                        trustedMemory = result;
                    }
                }
                catch { result.Message = "本機版本快取無法驗證；連網後會重新檢查。"; }
            }
            result.RecoveryRequired = HasPendingRecovery;
            result.ReminderDue = result.Available && (result.Mandatory || !IsSnoozed(DateTime.UtcNow));
            if (result.RecoveryRequired) result.Message = "上次更新尚未完成，請先還原備份，再使用安裝功能。";
            return result;
        }

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            return Task.Run(delegate
            {
                UpdateCheckResult cached = LoadCached();
                try
                {
                    byte[] bytes = UpdateNetwork.DownloadBytes(new Uri(UpdateTrust.FeedUrl),
                        UpdateSecurity.MaxEnvelopeBytes, 12000, true, null, cancellationToken);
                    string envelope = new UTF8Encoding(false, true).GetString(bytes);
                    UpdateManifest manifest = UpdateSecurity.VerifyEnvelope(envelope, UpdateTrust.PublicKeyXml);
                    UpdateSecurity.EnsureNotRolledBack(cached.Manifest, manifest);
                    UpdateCheckResult result = Result(manifest, envelope, true);
                    trustedMemory = result;
                    try
                    {
                        UpdateFiles.CreateSafeDirectory(storageDirectory);
                        UpdateFiles.WriteAtomic(Path.Combine(storageDirectory, "verified-manifest.json"), bytes);
                    }
                    catch
                    {
                        result.Message += " 本機無法儲存版本快取；本次仍依已驗證的資訊判斷。";
                    }
                    return result;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    cached.Online = false;
                    cached.Message = "更新檢查未完成（" + UpdateFiles.SafeError(ex) + "）。" +
                        (cached.Mandatory ? "已知此版本必須更新，安裝功能暫停。" : "可繼續使用；稍後可再次檢查。");
                    return cached;
                }
            }, cancellationToken);
        }

        public void Snooze(UpdateCheckResult result, bool oneMonth)
        {
            if (result == null || result.Mandatory || result.RecoveryRequired)
                throw new InvalidOperationException("必須更新或修復時不能延後提醒。");
            DateTime until = ReminderAfter(DateTime.UtcNow, oneMonth);
            UpdateFiles.CreateSafeDirectory(storageDirectory);
            UpdateFiles.WriteJsonAtomic(Path.Combine(storageDirectory, "preferences.json"),
                new UpdatePreferences { RemindAfterUtc = until.ToString("o", CultureInfo.InvariantCulture) });
            result.ReminderDue = false;
        }

        public static DateTime ReminderAfter(DateTime nowUtc, bool oneMonth)
        {
            return oneMonth ? nowUtc.AddMonths(1) : nowUtc.AddDays(7);
        }

        public Task<string> StageUpdateAsync(UpdateCheckResult result, IProgress<int> progress,
            CancellationToken cancellationToken)
        {
            return Task.Run(delegate
            {
                if (result == null || String.IsNullOrEmpty(result.EnvelopeJson))
                    throw new InvalidOperationException("尚未取得可驗證的更新資訊。");
                if (HasPendingRecovery) throw new InvalidOperationException("請先完成上次更新的還原。");
                UpdateManifest manifest = UpdateSecurity.VerifyEnvelope(result.EnvelopeJson, UpdateTrust.PublicKeyXml);
                UpdateSecurity.EnsureNotRolledBack(LoadCached().Manifest, manifest);
                if (manifest.ReleaseSequence <= UpdateTrust.ReleaseSequence)
                    throw new InvalidOperationException("沒有較新的正式版本可安裝。");
                string stage = Path.Combine(storageDirectory, "staging", Guid.NewGuid().ToString("N"));
                UpdateFiles.CreateSafeDirectory(stage);
                try
                {
                    UpdateFiles.WriteAtomic(Path.Combine(stage, "envelope.json"), Encoding.UTF8.GetBytes(result.EnvelopeJson));
                    string archive = Path.Combine(stage, "package.zip");
                    UpdateNetwork.DownloadFile(new Uri(manifest.PackageUrl), archive, manifest.PackageSize,
                        30000, progress, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    string payload = Path.Combine(stage, "payload");
                    UpdateSecurity.VerifyAndExtractPackage(archive, manifest, payload, cancellationToken);
                    PrepareRunner(stage, payload);
                    return stage;
                }
                catch
                {
                    try { Directory.Delete(stage, true); } catch { }
                    throw;
                }
            }, cancellationToken);
        }

        public void LaunchUpdater(string stagingDirectory)
        {
            Launch(stagingDirectory, false);
        }

        public void DiscardStagedUpdate(string stagingDirectory)
        {
            if (String.IsNullOrEmpty(stagingDirectory)) return;
            string stage = UpdateFiles.CanonicalDirectory(stagingDirectory);
            string id = Path.GetFileName(stage);
            if (!UpdateFiles.IsIdentifier(id) || !String.Equals(stage,
                Path.Combine(storageDirectory, "staging", id), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新暫存位置不正確。");
            string journalPath = Path.Combine(storageDirectory, "transaction.json");
            if (File.Exists(journalPath) && UpdateTransaction.ReadJournal(storageDirectory).StageId == id)
                throw new InvalidOperationException("這份暫存仍用於更新或還原，不能清除。");
            if (File.Exists(Path.Combine(stage, "request.json")))
                throw new InvalidOperationException("這份暫存已交給更新程式，不能清除。");
            if (Directory.Exists(stage)) UpdateFiles.DeleteSafeTree(stage);
        }

        public void LaunchRecovery()
        {
            UpdateJournal journal = UpdateTransaction.ReadJournal(storageDirectory);
            if (journal.State == "committed") throw new InvalidOperationException("此更新已完成，不需要還原。");
            string stage = Path.Combine(storageDirectory, "staging", journal.StageId);
            Launch(stage, true);
        }

        private void Launch(string stage, bool recoverOnly)
        {
            stage = UpdateFiles.CanonicalDirectory(stage);
            string stageId = Path.GetFileName(stage);
            if (!UpdateFiles.IsIdentifier(stageId) ||
                !String.Equals(stage, Path.Combine(storageDirectory, "staging", stageId), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新暫存位置不正確。");
            UpdateFiles.EnsureNoReparsePoints(stage);
            string envelope = UpdateFiles.ReadBoundedText(Path.Combine(stage, "envelope.json"), UpdateSecurity.MaxEnvelopeBytes);
            UpdateManifest manifest = UpdateSecurity.VerifyEnvelope(envelope, UpdateTrust.PublicKeyXml);
            if (!recoverOnly) UpdateSecurity.EnsureNotRolledBack(LoadCached().Manifest, manifest);
            string verifiedPayload = Path.Combine(stage, "launch-" + Guid.NewGuid().ToString("N"));
            UpdateSecurity.VerifyAndExtractPackage(Path.Combine(stage, "package.zip"), manifest,
                verifiedPayload, CancellationToken.None);
            PrepareRunner(stage, verifiedPayload);
            using (Process current = Process.GetCurrentProcess())
            {
                UpdateFiles.WriteJsonAtomic(Path.Combine(stage, "request.json"), new UpdaterRequest {
                    InstallDirectory = installDirectory, ParentId = current.Id,
                    ParentStartTicks = current.StartTime.ToUniversalTime().Ticks, RecoverOnly = recoverOnly
                });
            }
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(stage, "runner.exe"), "--apply");
            start.UseShellExecute = false;
            start.WorkingDirectory = stage;
            Process process = Process.Start(start);
            if (process == null) throw new IOException("無法啟動更新程式。");
            process.Dispose();
        }

        private static void PrepareRunner(string stage, string payload)
        {
            File.Copy(Path.Combine(payload, "JasonUpdater.exe"), Path.Combine(stage, "runner.exe"), true);
            string config = Path.Combine(payload, "JasonUpdater.exe.config");
            if (File.Exists(config)) File.Copy(config, Path.Combine(stage, "runner.exe.config"), true);
        }

        private UpdateCheckResult Result(UpdateManifest manifest, string envelope, bool online)
        {
            bool available = manifest.ReleaseSequence > UpdateTrust.ReleaseSequence;
            if (available && UpdateSecurity.NormalizedVersion(manifest.Version) <= UpdateSecurity.NormalizedVersion(UpdateTrust.Version))
                throw new InvalidDataException("新發布序號未使用較新的程式版本");
            bool mandatory = UpdateSecurity.IsMandatory(manifest, UpdateTrust.ReleaseSequence);
            return new UpdateCheckResult {
                Manifest = manifest, EnvelopeJson = envelope, Available = available, Mandatory = mandatory,
                ReminderDue = available && (mandatory || !IsSnoozed(DateTime.UtcNow)), Online = online,
                RecoveryRequired = HasPendingRecovery,
                Message = available ? (mandatory ? "此版本已達強制更新門檻。" : "已有新版本可更新。") : "目前已是最新正式版本。"
            };
        }

        private bool IsSnoozed(DateTime nowUtc)
        {
            try
            {
                string path = Path.Combine(storageDirectory, "preferences.json");
                if (!File.Exists(path)) return false;
                UpdatePreferences preferences = UpdateFiles.ReadJson<UpdatePreferences>(path, 4096);
                DateTime until;
                if (!DateTime.TryParse(preferences.RemindAfterUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out until)) return false;
                return until.ToUniversalTime() > nowUtc && until.ToUniversalTime() <= nowUtc.AddMonths(1).AddDays(1);
            }
            catch { return false; }
        }
    }

    internal static class UpdateSecurity
    {
        internal const int MaxEnvelopeBytes = 65536;
        internal const long MaxPackageBytes = 100L * 1024 * 1024;
        internal const long MaxExpandedBytes = 200L * 1024 * 1024;
        private static readonly Regex DigestPattern = new Regex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> RootFiles = new HashSet<string>(StringComparer.Ordinal) {
            "ADB_APK_Tool.exe", "JasonUpdater.exe", "ADB_APK_Tool.exe.config", "JasonUpdater.exe.config",
            "LICENSE", "README.md", "README-zh-TW.txt", "PHONE_SETUP-zh-TW.txt", "CHANGELOG.md",
            "RELEASE_NOTES.md", "UPDATES-zh-TW.txt", "UPDATE_OWNER_GUIDE.md", "OneClickInstall.cmd", "一鍵安裝.cmd"
        };
        private static readonly HashSet<string> Guides = new HashSet<string>(StringComparer.Ordinal) {
            "phone-guides/Common.txt", "phone-guides/Google.txt", "phone-guides/Samsung.txt",
            "phone-guides/Sony.txt", "phone-guides/Xiaomi.txt", "docs/UPDATES.md", "docs/COMMERCIAL-ROADMAP.md", "update/README.md"
        };

        internal static UpdateManifest VerifyEnvelope(string json, string publicKeyXml)
        {
            byte[] envelopeBytes = Encoding.UTF8.GetBytes(json ?? "");
            if (envelopeBytes.Length == 0 || envelopeBytes.Length > MaxEnvelopeBytes)
                throw new InvalidDataException("版本資訊大小不正確");
            SignedUpdateEnvelope envelope = UpdateFiles.Deserialize<SignedUpdateEnvelope>(envelopeBytes);
            byte[] payload = Convert.FromBase64String(envelope.Payload ?? "");
            byte[] signature = Convert.FromBase64String(envelope.Signature ?? "");
            if (payload.Length == 0 || payload.Length > MaxEnvelopeBytes / 2 || signature.Length < 256 || signature.Length > 512)
                throw new InvalidDataException("更新簽章格式不正確");
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(new CspParameters(24)))
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(publicKeyXml);
                if (!rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature))
                    throw new CryptographicException("更新簽章驗證失敗");
            }
            // Decode strictly before JSON parsing, to reject ambiguous replacement characters.
            new UTF8Encoding(false, true).GetString(payload);
            UpdateManifest manifest = UpdateFiles.Deserialize<UpdateManifest>(payload);
            ValidateManifest(manifest);
            return manifest;
        }

        internal static void ValidateManifest(UpdateManifest value)
        {
            Version parsed;
            DateTime published;
            if (value == null || value.Schema != 1 || value.AppId != UpdateTrust.AppId || value.Channel != "stable" ||
                !Version.TryParse(value.Version, out parsed) || parsed.Build < 0 || value.Version != parsed.ToString() ||
                value.ReleaseSequence < 1 || value.MinimumSupportedSequence < 1 ||
                value.MinimumSupportedSequence > value.ReleaseSequence ||
                value.PackageSize < 1 || value.PackageSize > MaxPackageBytes ||
                !IsDigest(value.PackageSha256) || value.Notes == null || value.Notes.Length > 8000 ||
                !DateTime.TryParse(value.PublishedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out published))
                throw new InvalidDataException("更新資訊內容不正確");
            Uri uri;
            if (!Uri.TryCreate(value.PackageUrl, UriKind.Absolute, out uri) || !UpdateNetwork.IsHttps(uri) ||
                !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || uri.Query.Length != 0 ||
                uri.Fragment.Length != 0 ||
                !Regex.IsMatch(uri.AbsolutePath, "^/qaz754120/jason-adb-apk-installer/releases/download/v" +
                    Regex.Escape(value.Version) + "/[A-Za-z0-9_.-]+\\.zip$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("更新檔來源不符合官方發布位置");
        }

        internal static bool IsMandatory(UpdateManifest value, int currentSequence)
        {
            return value != null && ((long)value.ReleaseSequence - currentSequence >= 3 ||
                currentSequence < value.MinimumSupportedSequence);
        }

        internal static void EnsureNotRolledBack(UpdateManifest known, UpdateManifest next)
        {
            if (known == null) return;
            if (next.ReleaseSequence < known.ReleaseSequence || next.MinimumSupportedSequence < known.MinimumSupportedSequence)
                throw new InvalidDataException("收到比已驗證紀錄更舊的更新政策");
            if (next.ReleaseSequence > known.ReleaseSequence && NormalizedVersion(next.Version) <= NormalizedVersion(known.Version))
                throw new InvalidDataException("新發布序號必須使用較新的程式版本");
            if (next.ReleaseSequence == known.ReleaseSequence &&
                (next.Version != known.Version || next.PackageSize != known.PackageSize || next.PackageUrl != known.PackageUrl ||
                 !String.Equals(next.PackageSha256, known.PackageSha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("同一發布序號的更新檔已變更，請作者發布新序號");
        }

        internal static bool IsDigest(string value) { return value != null && DigestPattern.IsMatch(value); }

        internal static Version NormalizedVersion(string version)
        {
            Version value = new Version(version);
            return new Version(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
        }

        internal static bool IsAllowedFile(string relative)
        {
            if (String.IsNullOrEmpty(relative) || relative.Length > 160 || relative.IndexOf('\\') >= 0 ||
                relative.IndexOf(':') >= 0 || relative.StartsWith("/", StringComparison.Ordinal) || relative.IndexOf("..", StringComparison.Ordinal) >= 0)
                return false;
            if (RootFiles.Contains(relative) || Guides.Contains(relative)) return true;
            if (!Regex.IsMatch(relative, "^components/[A-Za-z0-9][A-Za-z0-9_.-]*\\.dll$", RegexOptions.CultureInvariant)) return false;
            string component = relative.Substring("components/".Length).Split('.')[0];
            return !Regex.IsMatch(component, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        internal static List<string> VerifyAndExtractPackage(string archivePath, UpdateManifest manifest,
            string extractionDirectory, CancellationToken cancellationToken)
        {
            UpdateFiles.EnsureNoReparsePoints(archivePath);
            if (Directory.Exists(extractionDirectory)) throw new IOException("解壓縮目標已存在");
            List<string> paths = new List<string>();
            HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            using (FileStream file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (SHA256 hash = SHA256.Create())
                {
                    if (file.Length != manifest.PackageSize || !String.Equals(UpdateFiles.Hex(hash.ComputeHash(file)),
                        manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
                        throw new CryptographicException("更新檔大小或 SHA-256 驗證失敗");
                }
                file.Position = 0;
                UpdateFiles.CreateSafeDirectory(extractionDirectory);
                using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Read, true))
                {
                if (archive.Entries.Count > 110) throw new InvalidDataException("更新檔包含過多項目");
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = entry.FullName;
                    if (name == "phone-guides/" || name == "components/" || name == "docs/" || name == "update/")
                    {
                        if (entry.Length != 0 || !unique.Add(name)) throw new InvalidDataException("重複的更新目錄");
                        continue;
                    }
                    if (!IsAllowedFile(name) || !unique.Add(name) || paths.Count >= 100)
                        throw new InvalidDataException("更新檔包含不允許或重複的路徑");
                    // Always create regular files ourselves; never materialize ZIP links or permissions.
                    PropertyInfo attributesProperty = typeof(ZipArchiveEntry).GetProperty("ExternalAttributes");
                    if (attributesProperty != null)
                    {
                        int attributes = (int)attributesProperty.GetValue(entry, null);
                        int unixType = (attributes >> 16) & 0xF000;
                        if (unixType != 0 && unixType != 0x8000 || (attributes & 0x400) != 0)
                            throw new InvalidDataException("更新檔不得包含連結或特殊檔案");
                    }
                    if (entry.Length < 0 || entry.Length > MaxPackageBytes || total > MaxExpandedBytes - entry.Length)
                        throw new InvalidDataException("更新檔解壓縮大小超過限制");
                    total += entry.Length;
                    string destination = UpdateFiles.TargetPath(extractionDirectory, name);
                    UpdateFiles.CreateSafeDirectory(Path.GetDirectoryName(destination));
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[65536];
                        long written = 0;
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            written += count;
                            if (written > entry.Length || written > MaxPackageBytes)
                                throw new InvalidDataException("更新檔解壓縮長度不一致");
                            output.Write(buffer, 0, count);
                        }
                        if (written != entry.Length) throw new InvalidDataException("更新檔不完整");
                        output.Flush(true);
                    }
                    paths.Add(name);
                }
                }
            }
            if (!unique.Contains("ADB_APK_Tool.exe") || !unique.Contains("JasonUpdater.exe"))
                throw new InvalidDataException("更新檔缺少主程式或更新程式");
            AssemblyName mainAssembly = AssemblyName.GetAssemblyName(Path.Combine(extractionDirectory, "ADB_APK_Tool.exe"));
            AssemblyName updaterAssembly = AssemblyName.GetAssemblyName(Path.Combine(extractionDirectory, "JasonUpdater.exe"));
            if (!String.Equals(mainAssembly.Name, "ADB_APK_Tool", StringComparison.Ordinal) ||
                !String.Equals(updaterAssembly.Name, "JasonUpdater", StringComparison.Ordinal) ||
                mainAssembly.Version != NormalizedVersion(manifest.Version) || updaterAssembly.Version != NormalizedVersion(manifest.Version))
                throw new InvalidDataException("更新執行檔的組件識別不正確");
            return paths;
        }
    }

    internal static class UpdateNetwork
    {
        internal static bool IsHttps(Uri uri)
        {
            return uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && String.IsNullOrEmpty(uri.UserInfo);
        }

        private static bool Allowed(Uri uri, bool feed)
        {
            if (!IsHttps(uri)) return false;
            string host = uri.Host.ToLowerInvariant();
            if (feed) return host == "raw.githubusercontent.com" && uri.AbsolutePath == new Uri(UpdateTrust.FeedUrl).AbsolutePath;
            return host == "github.com" || host == "release-assets.githubusercontent.com" ||
                host == "objects.githubusercontent.com" || host == "github-releases.githubusercontent.com";
        }

        internal static byte[] DownloadBytes(Uri uri, long maximum, int timeout, bool feed,
            IProgress<int> progress, CancellationToken cancellationToken)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                Download(uri, stream, maximum, timeout, feed, progress, cancellationToken);
                return stream.ToArray();
            }
        }

        internal static void DownloadFile(Uri uri, string path, long expected, int timeout,
            IProgress<int> progress, CancellationToken cancellationToken)
        {
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                long actual = Download(uri, stream, expected, timeout, false, progress, cancellationToken);
                if (actual != expected) throw new InvalidDataException("更新檔下載長度不符");
                stream.Flush(true);
            }
        }

        private static long Download(Uri uri, Stream destination, long maximum, int timeout, bool feed,
            IProgress<int> progress, CancellationToken cancellationToken)
        {
            using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                budget.CancelAfter(feed ? 12000 : 600000);
                try { return DownloadCore(uri, destination, maximum, timeout, feed, progress, budget.Token); }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("更新連線總時間已超過限制");
                }
            }
        }

        private static long DownloadCore(Uri uri, Stream destination, long maximum, int timeout, bool feed,
            IProgress<int> progress, CancellationToken cancellationToken)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Allowed(uri, feed)) throw new InvalidDataException("更新連線被導向非官方或非 HTTPS 位置");
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
                request.AllowAutoRedirect = false;
                request.Timeout = timeout;
                request.ReadWriteTimeout = timeout;
                request.UserAgent = "JasonADBUpdater/" + UpdateTrust.Version;
                request.Accept = feed ? "application/json" : "application/octet-stream";
                request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                using (cancellationToken.Register(request.Abort))
                {
                    try
                    {
                        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                        {
                            int code = (int)response.StatusCode;
                            if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                            {
                                string location = response.Headers["Location"];
                                if (redirect == 5 || String.IsNullOrEmpty(location)) throw new IOException("更新連線重新導向過多");
                                uri = new Uri(uri, location);
                                continue;
                            }
                            if (code != 200 || response.ContentLength > maximum)
                                throw new InvalidDataException("更新伺服器回覆不符合預期");
                            using (Stream input = response.GetResponseStream())
                            {
                                byte[] buffer = new byte[65536];
                                long total = 0;
                                int count;
                                Stopwatch elapsed = Stopwatch.StartNew();
                                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    total += count;
                                    if (total > maximum) throw new InvalidDataException("更新下載超過大小限制");
                                    if (elapsed.Elapsed > TimeSpan.FromMinutes(feed ? 1 : 10)) throw new TimeoutException("更新下載逾時");
                                    destination.Write(buffer, 0, count);
                                    if (progress != null) progress.Report((int)Math.Min(100L, total * 100L / Math.Max(1L, maximum)));
                                }
                                cancellationToken.ThrowIfCancellationRequested();
                                return total;
                            }
                        }
                    }
                    catch (WebException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw;
                    }
                }
            }
            throw new IOException("更新連線重新導向失敗");
        }
    }

    internal static class UpdateFiles
    {
        internal static string CanonicalDirectory(string path)
        {
            string full = Path.GetFullPath(path);
            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length == 0 ? Path.GetPathRoot(full) : trimmed;
        }
        internal static string InstallationId(string directory)
        {
            using (SHA256 hash = SHA256.Create())
                return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(CanonicalDirectory(directory).ToUpperInvariant()))).Substring(0, 32);
        }
        internal static string StorageDirectory(string installDirectory)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jason1993Yu", "AdbApkTool", "updates", InstallationId(installDirectory));
        }
        internal static bool IsIdentifier(string value)
        {
            return value != null && Regex.IsMatch(value, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
        }
        internal static string TargetPath(string root, string relative)
        {
            if (!UpdateSecurity.IsAllowedFile(relative)) throw new InvalidDataException("不允許的更新目標");
            string canonical = CanonicalDirectory(root);
            string destination = Path.GetFullPath(Path.Combine(canonical, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新路徑超出目標目錄");
            EnsureNoReparsePoints(destination);
            return destination;
        }
        internal static void EnsureNoReparsePoints(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("更新路徑不得經過符號連結或接合點");
                }
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }
        internal static void CreateSafeDirectory(string path)
        {
            EnsureNoReparsePoints(path);
            Directory.CreateDirectory(path);
            EnsureNoReparsePoints(path);
        }
        internal static void DeleteSafeTree(string directory)
        {
            EnsureNoReparsePoints(directory);
            foreach (string entry in Directory.GetFileSystemEntries(directory))
            {
                EnsureNoReparsePoints(entry);
                if (Directory.Exists(entry)) DeleteSafeTree(entry);
                else File.Delete(entry);
            }
            Directory.Delete(directory);
        }
        internal static string ReadBoundedText(string path, int maximum)
        {
            EnsureNoReparsePoints(path);
            using (FileStream file = File.OpenRead(path))
            {
                if (file.Length > maximum) throw new InvalidDataException("本機更新資料過大");
                using (StreamReader reader = new StreamReader(file, new UTF8Encoding(false, true), true))
                    return reader.ReadToEnd();
            }
        }
        internal static T ReadJson<T>(string path, int maximum)
        {
            return Deserialize<T>(Encoding.UTF8.GetBytes(ReadBoundedText(path, maximum)));
        }
        internal static T Deserialize<T>(byte[] bytes)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (MemoryStream stream = new MemoryStream(bytes, false)) return (T)serializer.ReadObject(stream);
        }
        internal static byte[] Serialize<T>(T value)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return stream.ToArray();
            }
        }
        internal static void WriteJsonAtomic<T>(string path, T value) { WriteAtomic(path, Serialize(value)); }
        internal static void WriteAtomic(string path, byte[] bytes)
        {
            EnsureNoReparsePoints(path);
            CreateSafeDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static void AtomicCopy(string source, string destination)
        {
            EnsureNoReparsePoints(source);
            EnsureNoReparsePoints(destination);
            CreateSafeDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + ".update-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream input = File.OpenRead(source))
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static string Sha256File(string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream file = File.OpenRead(path)) return Hex(hash.ComputeHash(file));
        }
        internal static string Hex(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }
        internal static string SafeError(Exception error)
        {
            if (error is CryptographicException) return "簽章或檔案完整性驗證失敗";
            if (error is WebException || error is TimeoutException) return "網路無法連線或回覆逾時";
            if (error is UnauthorizedAccessException) return "資料夾沒有寫入權限";
            if (error is InvalidDataException || error is SerializationException || error is FormatException) return "更新資料格式或政策驗證失敗";
            return "更新資料暫時無法讀取";
        }
    }

    internal static class UpdateTransaction
    {
        internal static UpdateJournal ReadJournal(string storage)
        {
            UpdateJournal journal = UpdateFiles.ReadJson<UpdateJournal>(Path.Combine(storage, "transaction.json"), 65536);
            if (journal.Schema != 1 || !UpdateFiles.IsIdentifier(journal.BackupId) || !UpdateFiles.IsIdentifier(journal.StageId) ||
                (journal.State != "prepared" && journal.State != "applying" && journal.State != "committed") ||
                journal.Files == null || journal.Files.Count == 0 || journal.Files.Count > 100)
                throw new InvalidDataException("更新還原紀錄格式不正確");
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TransactionFile file in journal.Files)
                if (!UpdateSecurity.IsAllowedFile(file.Path) || !names.Add(file.Path) ||
                    !UpdateSecurity.IsDigest(file.NewSha256) || (file.Existed && !UpdateSecurity.IsDigest(file.OriginalSha256)))
                    throw new InvalidDataException("更新還原紀錄的檔案不正確");
            return journal;
        }

        internal static void ValidateInstallDirectory(string directory)
        {
            directory = UpdateFiles.CanonicalDirectory(directory);
            if (directory.StartsWith("\\\\", StringComparison.Ordinal) ||
                String.Equals(directory, Path.GetPathRoot(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(directory)) throw new IOException("請從已解壓縮的程式資料夾更新");
            UpdateFiles.EnsureNoReparsePoints(directory);
            string main = Path.Combine(directory, "ADB_APK_Tool.exe");
            if (!File.Exists(main) || AssemblyName.GetAssemblyName(main).Name != "ADB_APK_Tool")
                throw new InvalidDataException("找不到正確的主程式資料夾");
            string probe = Path.Combine(directory, ".jason-update-write-" + Guid.NewGuid().ToString("N"));
            using (FileStream stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Flush(true); }
            File.Delete(probe);
        }

        internal static void Apply(string installDirectory, string storage, string stageId, string payload,
            IList<string> paths, Action<string> report, Action<int> afterReplaceForTest)
        {
            ValidateInstallDirectory(installDirectory);
            string journalPath = Path.Combine(storage, "transaction.json");
            if (File.Exists(journalPath))
            {
                UpdateJournal previous = ReadJournal(storage);
                if (previous.State != "committed") throw new IOException("仍有尚未還原的更新紀錄");
                File.Delete(journalPath);
            }
            string backupId = Guid.NewGuid().ToString("N");
            string backup = Path.Combine(storage, "backups", backupId);
            UpdateFiles.CreateSafeDirectory(backup);
            UpdateJournal journal = new UpdateJournal { Schema = 1, BackupId = backupId, StageId = stageId,
                State = "prepared", Files = new List<TransactionFile>() };
            foreach (string relative in paths)
            {
                string destination = UpdateFiles.TargetPath(installDirectory, relative);
                string source = UpdateFiles.TargetPath(payload, relative);
                bool existed = File.Exists(destination);
                if (Directory.Exists(destination) || existed && (File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
                    throw new IOException("更新目標無法寫入；請改放在可寫入的程式資料夾");
                TransactionFile entry = new TransactionFile { Path = relative, Existed = existed,
                    NewSha256 = UpdateFiles.Sha256File(source) };
                if (existed)
                {
                    string backupFile = UpdateFiles.TargetPath(backup, relative);
                    UpdateFiles.AtomicCopy(destination, backupFile);
                    entry.OriginalSha256 = UpdateFiles.Sha256File(backupFile);
                }
                journal.Files.Add(entry);
            }
            UpdateFiles.WriteJsonAtomic(journalPath, journal);
            try
            {
                journal.State = "applying";
                UpdateFiles.WriteJsonAtomic(journalPath, journal);
                int replaced = 0;
                foreach (TransactionFile entry in journal.Files)
                {
                    if (report != null) report("正在套用更新：" + entry.Path);
                    string destination = UpdateFiles.TargetPath(installDirectory, entry.Path);
                    UpdateFiles.AtomicCopy(UpdateFiles.TargetPath(payload, entry.Path), destination);
                    if (UpdateFiles.Sha256File(destination) != entry.NewSha256)
                        throw new CryptographicException("寫入後驗證失敗");
                    replaced++;
                    if (afterReplaceForTest != null) afterReplaceForTest(replaced);
                }
                journal.State = "committed";
                UpdateFiles.WriteJsonAtomic(journalPath, journal);
            }
            catch (Exception updateError)
            {
                try { Recover(installDirectory, storage, report); }
                catch (Exception rollbackError)
                {
                    throw new IOException("更新未完成，備份已保留；自動還原也未完成，請重新開啟程式執行修復。", new AggregateException(updateError, rollbackError));
                }
                throw new IOException("更新未完成，已還原原有程式檔案。", updateError);
            }
            // Keep the durable backup and receipt. A committed journal is safe if cleanup is interrupted.
            try
            {
                UpdateFiles.WriteJsonAtomic(Path.Combine(storage, "last-backup.json"), journal);
                File.Delete(journalPath);
            }
            catch { }
        }

        internal static void Recover(string installDirectory, string storage, Action<string> report)
        {
            ValidateInstallDirectory(installDirectory);
            UpdateJournal journal = ReadJournal(storage);
            if (journal.State == "committed") return;
            string backup = Path.Combine(storage, "backups", journal.BackupId);
            // Validate every original before changing any destination.
            foreach (TransactionFile entry in journal.Files)
                if (entry.Existed && (!File.Exists(UpdateFiles.TargetPath(backup, entry.Path)) ||
                    UpdateFiles.Sha256File(UpdateFiles.TargetPath(backup, entry.Path)) != entry.OriginalSha256))
                    throw new CryptographicException("原有檔案備份不完整，無法自動還原");
            for (int i = journal.Files.Count - 1; i >= 0; i--)
            {
                TransactionFile entry = journal.Files[i];
                if (report != null) report("正在還原：" + entry.Path);
                string destination = UpdateFiles.TargetPath(installDirectory, entry.Path);
                if (entry.Existed)
                {
                    UpdateFiles.AtomicCopy(UpdateFiles.TargetPath(backup, entry.Path), destination);
                    if (UpdateFiles.Sha256File(destination) != entry.OriginalSha256)
                        throw new CryptographicException("還原後驗證失敗");
                }
                else if (File.Exists(destination)) File.Delete(destination);
            }
            File.Delete(Path.Combine(storage, "transaction.json"));
        }
    }
}
