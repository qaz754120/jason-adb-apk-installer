// Copyright © 2026 Jason1993余. Run in an isolated temporary directory.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Jason1993Yu.AdbApkTool;

internal static class UpdateCoreTests
{
    private static int passed;
    private static string temporary, mainBinary, helperBinary;
    private static RSACryptoServiceProvider key;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: UpdateCoreTests.exe <ADB_APK_Tool.exe> <JasonUpdater.exe>");
            return 2;
        }
        mainBinary = Path.GetFullPath(args[0]);
        helperBinary = Path.GetFullPath(args[1]);
        temporary = Path.Combine(Path.GetTempPath(), "jason-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        key = new RSACryptoServiceProvider(3072, new CspParameters(24));
        key.PersistKeyInCsp = false;
        try
        {
            Test("valid RSA SHA-256 envelope", delegate {
                UpdateManifest value = Manifest();
                Assert(UpdateSecurity.VerifyEnvelope(Sign(value), key.ToXmlString(false)).ReleaseSequence == 5);
            });
            Test("tampered payload rejected", delegate {
                SignedUpdateEnvelope envelope = UpdateFiles.Deserialize<SignedUpdateEnvelope>(Encoding.UTF8.GetBytes(Sign(Manifest())));
                byte[] payload = Convert.FromBase64String(envelope.Payload);
                payload[10] ^= 1;
                envelope.Payload = Convert.ToBase64String(payload);
                Reject(delegate { UpdateSecurity.VerifyEnvelope(Encoding.UTF8.GetString(UpdateFiles.Serialize(envelope)), key.ToXmlString(false)); });
            });
            Test("unsigned envelope rejected", delegate {
                Reject(delegate { UpdateSecurity.VerifyEnvelope("{\"payload\":\"e30=\",\"signature\":\"\"}", key.ToXmlString(false)); });
            });
            Test("wrong app/channel/source rejected even when signed", delegate {
                UpdateManifest value = Manifest(); value.AppId = "another-app";
                Reject(delegate { UpdateSecurity.VerifyEnvelope(Sign(value), key.ToXmlString(false)); });
                value = Manifest(); value.Channel = "preview";
                Reject(delegate { UpdateSecurity.VerifyEnvelope(Sign(value), key.ToXmlString(false)); });
                value = Manifest(); value.PackageUrl = "https://example.invalid/patch.zip";
                Reject(delegate { UpdateSecurity.VerifyEnvelope(Sign(value), key.ToXmlString(false)); });
            });
            Test("release difference two optional; three mandatory", delegate {
                UpdateManifest value = Manifest(); value.ReleaseSequence = 4;
                Assert(!UpdateSecurity.IsMandatory(value, 2));
                value.ReleaseSequence = 5; Assert(UpdateSecurity.IsMandatory(value, 2));
                value.ReleaseSequence = 6; Assert(UpdateSecurity.IsMandatory(value, 2));
            });
            Test("minimum supported release overrides gap", delegate {
                UpdateManifest value = Manifest(); value.ReleaseSequence = 3; value.MinimumSupportedSequence = 3;
                Assert(UpdateSecurity.IsMandatory(value, 2));
                Assert(!UpdateSecurity.IsMandatory(value, 3));
            });
            Test("seven day and calendar month snoozes", delegate {
                DateTime january = new DateTime(2027, 1, 31, 12, 0, 0, DateTimeKind.Utc);
                Assert(UpdateManager.ReminderAfter(january, false) == new DateTime(2027, 2, 7, 12, 0, 0, DateTimeKind.Utc));
                Assert(UpdateManager.ReminderAfter(january, true) == new DateTime(2027, 2, 28, 12, 0, 0, DateTimeKind.Utc));
            });
            Test("mandatory result cannot be snoozed", delegate {
                UpdateManager manager = new UpdateManager(Path.Combine(temporary, "snooze"));
                Reject(delegate { manager.Snooze(new UpdateCheckResult { Mandatory = true, Available = true }, true); });
            });
            Test("cached mandatory remains mandatory without connectivity", delegate {
                UpdateManager manager = new UpdateManager(Path.Combine(temporary, "cached"));
                UpdateCheckResult known = new UpdateCheckResult { Manifest = Manifest(), Mandatory = true, Available = true, Online = false };
                typeof(UpdateManager).GetField("trustedMemory", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, known);
                UpdateCheckResult loaded = manager.LoadCached();
                Assert(loaded.Mandatory && loaded.ReminderDue && !loaded.Online);
            });
            Test("older release and lowered minimum rejected", delegate {
                UpdateManifest known = Manifest(), next = Manifest();
                next.ReleaseSequence = 4;
                Reject(delegate { UpdateSecurity.EnsureNotRolledBack(known, next); });
                known.MinimumSupportedSequence = 3; next = Manifest(); next.ReleaseSequence = 6;
                Reject(delegate { UpdateSecurity.EnsureNotRolledBack(known, next); });
            });
            Test("same release archive cannot change", delegate {
                UpdateManifest known = Manifest(), next = Manifest(); next.PackageSha256 = new string('a', 64);
                Reject(delegate { UpdateSecurity.EnsureNotRolledBack(known, next); });
                next = Manifest(); next.MinimumSupportedSequence = 3;
                UpdateSecurity.EnsureNotRolledBack(known, next);
            });
            Test("new sequence must advance semantic version", delegate {
                UpdateManifest known = Manifest(), next = Manifest(); next.ReleaseSequence = 6;
                Reject(delegate { UpdateSecurity.EnsureNotRolledBack(known, next); });
                next.Version = "1.5.0";
                UpdateSecurity.EnsureNotRolledBack(known, next);
            });
            Test("path traversal ADS user APK and ADB paths rejected", delegate {
                foreach (string path in new [] { "../ADB_APK_Tool.exe", "C:/ADB_APK_Tool.exe", "README.md:stream",
                    "phone-guides/../README.md", "phone-guides\\Google.txt", "platform-tools/adb.exe", "APK/user.apk", "/README.md", "components/CON.dll" })
                    Assert(!UpdateSecurity.IsAllowedFile(path));
            });
            Test("valid patch extracts required signed assemblies", delegate {
                string archive = MakeArchive("good", null);
                UpdateManifest value = ForArchive(archive);
                List<string> paths = UpdateSecurity.VerifyAndExtractPackage(archive, value,
                    Path.Combine(temporary, "extract-good"), CancellationToken.None);
                Assert(paths.Count == 3 && paths.Contains("ADB_APK_Tool.exe"));
            });
            Test("archive SHA mismatch rejected before extraction", delegate {
                string archive = MakeArchive("hash", null);
                UpdateManifest value = ForArchive(archive); value.PackageSha256 = new string('b', 64);
                Reject(delegate { UpdateSecurity.VerifyAndExtractPackage(archive, value,
                    Path.Combine(temporary, "extract-hash"), CancellationToken.None); });
                Assert(!Directory.Exists(Path.Combine(temporary, "extract-hash")));
            });
            Test("manifest and binary version mismatch rejected", delegate {
                string archive = MakeArchive("version", null);
                UpdateManifest value = ForArchive(archive); value.Version = "99.0.0";
                Reject(delegate { UpdateSecurity.VerifyAndExtractPackage(archive, value,
                    Path.Combine(temporary, "extract-version"), CancellationToken.None); });
            });
            Test("ZIP escape entry rejected", delegate {
                string archive = MakeArchive("escape", "../outside.txt");
                Reject(delegate { UpdateSecurity.VerifyAndExtractPackage(archive, ForArchive(archive),
                    Path.Combine(temporary, "extract-escape"), CancellationToken.None); });
                Assert(!File.Exists(Path.Combine(temporary, "outside.txt")));
            });
            Test("ZIP case collision rejected", delegate {
                string archive = MakeArchive("duplicate", "readme.md");
                Reject(delegate { UpdateSecurity.VerifyAndExtractPackage(archive, ForArchive(archive),
                    Path.Combine(temporary, "extract-duplicate"), CancellationToken.None); });
            });
            Test("cancelled extraction rejected", delegate {
                string archive = MakeArchive("cancel", null);
                CancellationTokenSource cancellation = new CancellationTokenSource(); cancellation.Cancel();
                Reject(delegate { UpdateSecurity.VerifyAndExtractPackage(archive, ForArchive(archive),
                    Path.Combine(temporary, "extract-cancel"), cancellation.Token); });
            });
            Test("handled write failure restores originals and preserves user files", delegate {
                string installation, payload, storage;
                TransactionFixture("rollback", out installation, out payload, out storage);
                Reject(delegate { UpdateTransaction.Apply(installation, storage, new string('1', 32), payload,
                    new [] { "README.md", "CHANGELOG.md" }, null, delegate(int count) { if (count == 2) throw new IOException("simulated write failure"); }); });
                Assert(File.ReadAllText(Path.Combine(installation, "README.md")) == "original");
                Assert(!File.Exists(Path.Combine(installation, "CHANGELOG.md")));
                Assert(File.ReadAllText(Path.Combine(installation, "APK", "user.apk")) == "private APK placeholder");
                Assert(!File.Exists(Path.Combine(storage, "transaction.json")));
            });
            Test("successful update retains backup and preserves ADB", delegate {
                string installation, payload, storage;
                TransactionFixture("success", out installation, out payload, out storage);
                UpdateTransaction.Apply(installation, storage, new string('2', 32), payload,
                    new [] { "README.md", "CHANGELOG.md" }, null, null);
                Assert(File.ReadAllText(Path.Combine(installation, "README.md")) == "updated");
                Assert(File.ReadAllText(Path.Combine(installation, "platform-tools", "adb.exe")) == "third-party placeholder");
                Assert(File.Exists(Path.Combine(storage, "last-backup.json")));
                Assert(!File.Exists(Path.Combine(storage, "transaction.json")));
            });
            Test("interrupted transaction recovery restores durable backup", delegate {
                string installation, payload, storage;
                TransactionFixture("interrupted", out installation, out payload, out storage);
                string backupId = new string('3', 32);
                string backup = Path.Combine(storage, "backups", backupId);
                Directory.CreateDirectory(backup);
                File.Copy(Path.Combine(installation, "README.md"), Path.Combine(backup, "README.md"));
                File.WriteAllText(Path.Combine(installation, "README.md"), "half applied");
                UpdateJournal journal = new UpdateJournal { Schema = 1, BackupId = backupId, StageId = new string('4', 32),
                    State = "applying", Files = new List<TransactionFile> { new TransactionFile { Path = "README.md", Existed = true,
                        OriginalSha256 = UpdateFiles.Sha256File(Path.Combine(backup, "README.md")),
                        NewSha256 = UpdateFiles.Sha256File(Path.Combine(payload, "README.md")) } } };
                UpdateFiles.WriteJsonAtomic(Path.Combine(storage, "transaction.json"), journal);
                UpdateTransaction.Recover(installation, storage, null);
                Assert(File.ReadAllText(Path.Combine(installation, "README.md")) == "original");
            });
            Console.WriteLine("PASS: " + passed + " update security/policy/transaction tests.");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL: " + error); return 1; }
        finally
        {
            key.Dispose();
            try { Directory.Delete(temporary, true); } catch { }
        }
    }

    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); } catch { rejected = true; }
        Assert(rejected);
    }
    private static UpdateManifest Manifest()
    {
        return new UpdateManifest { Schema = 1, AppId = UpdateTrust.AppId, Channel = "stable", Version = "1.4.0",
            ReleaseSequence = 5, MinimumSupportedSequence = 1, PublishedUtc = "2026-11-22T00:00:00Z",
            Notes = "Test release", PackageUrl = "https://github.com/qaz754120/jason-adb-apk-installer/releases/download/v1.4.0/test.zip",
            PackageSize = 1234, PackageSha256 = new string('0', 64) };
    }
    private static string Sign(UpdateManifest manifest)
    {
        byte[] payload = UpdateFiles.Serialize(manifest);
        return Encoding.UTF8.GetString(UpdateFiles.Serialize(new SignedUpdateEnvelope {
            Payload = Convert.ToBase64String(payload), Signature = Convert.ToBase64String(key.SignData(payload, CryptoConfig.MapNameToOID("SHA256")))
        }));
    }
    private static string MakeArchive(string name, string extra)
    {
        string path = Path.Combine(temporary, name + ".zip");
        using (FileStream file = File.Create(path))
        using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            AddFile(archive, "ADB_APK_Tool.exe", mainBinary);
            AddFile(archive, "JasonUpdater.exe", helperBinary);
            AddText(archive, "README.md", "Update test");
            if (extra != null) AddText(archive, extra, "rejected fixture");
        }
        return path;
    }
    private static void AddFile(ZipArchive archive, string name, string file)
    {
        using (Stream target = archive.CreateEntry(name).Open())
        using (Stream input = File.OpenRead(file)) input.CopyTo(target);
    }
    private static void AddText(ZipArchive archive, string name, string text)
    {
        using (Stream target = archive.CreateEntry(name).Open())
        { byte[] bytes = Encoding.UTF8.GetBytes(text); target.Write(bytes, 0, bytes.Length); }
    }
    private static UpdateManifest ForArchive(string path)
    {
        UpdateManifest manifest = Manifest();
        manifest.Version = AssemblyName.GetAssemblyName(mainBinary).Version.ToString(3);
        manifest.PackageSize = new FileInfo(path).Length;
        manifest.PackageSha256 = UpdateFiles.Sha256File(path);
        return manifest;
    }
    private static void TransactionFixture(string name, out string installation, out string payload, out string storage)
    {
        string root = Path.Combine(temporary, name);
        installation = Path.Combine(root, "app"); payload = Path.Combine(root, "payload"); storage = Path.Combine(root, "state");
        Directory.CreateDirectory(installation); Directory.CreateDirectory(payload); Directory.CreateDirectory(storage);
        File.Copy(mainBinary, Path.Combine(installation, "ADB_APK_Tool.exe"));
        File.WriteAllText(Path.Combine(installation, "README.md"), "original");
        Directory.CreateDirectory(Path.Combine(installation, "APK"));
        File.WriteAllText(Path.Combine(installation, "APK", "user.apk"), "private APK placeholder");
        Directory.CreateDirectory(Path.Combine(installation, "platform-tools"));
        File.WriteAllText(Path.Combine(installation, "platform-tools", "adb.exe"), "third-party placeholder");
        File.WriteAllText(Path.Combine(payload, "README.md"), "updated");
        File.WriteAllText(Path.Combine(payload, "CHANGELOG.md"), "new file");
    }
}
