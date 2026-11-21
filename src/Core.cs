using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Jason1993Yu.AdbApkTool
{
    public sealed class DeviceInfo
    {
        public string Serial { get; set; }
        public string State { get; set; }
        public string Model { get; set; }

        public override string ToString()
        {
            string state;
            switch (State)
            {
                case "device": state = "已連線"; break;
                case "unauthorized": state = "等待手機授權"; break;
                case "offline": state = "離線"; break;
                case "recovery": state = "復原模式"; break;
                case "sideload": state = "更新模式（無法一般安裝）"; break;
                case "bootloader": state = "Bootloader 模式"; break;
                case "no permissions": state = "沒有裝置存取權限"; break;
                case "authorizing": state = "正在取得授權"; break;
                case "connecting": state = "連線中"; break;
                default: state = State ?? "未知狀態"; break;
            }
            return (String.IsNullOrWhiteSpace(Model) ? "Android 裝置" : Model)
                + "  |  " + Serial + "  |  " + state;
        }
    }

    public sealed class InstallOptions
    {
        public bool Replace { get; set; }
        public bool AllowDowngrade { get; set; }
        public bool AllowTest { get; set; }
        public bool LowTarget { get; set; }
        public bool SplitMode { get; set; }

        public InstallOptions()
        {
            Replace = true;
        }
    }

    public sealed class CommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public bool TimedOut { get; set; }
        public bool Cancelled { get; set; }
        public bool OutputIncomplete { get; set; }
        public bool OutputTruncated { get; set; }

        // This property describes an INSTALL result. Query commands such as
        // "devices" should instead inspect ExitCode and their parsed output.
        public bool Success
        {
            get
            {
                string text = Output ?? String.Empty;
                return ExitCode == 0 && !TimedOut && !Cancelled
                    && !OutputIncomplete && !OutputTruncated
                    && Regex.IsMatch(text, @"^\s*Success\s*$", RegexOptions.Multiline)
                    && !Regex.IsMatch(text, @"\b(failure|error|failed)\b", RegexOptions.IgnoreCase);
            }
        }
    }

    public static class AdbCore
    {
        // Stay below CreateProcessW's 32767-character limit, including the exe.
        private const int MaxArgumentsLength = 28000;
        // A UTF-16 StringBuilder payload is bounded to approximately 2 MiB.
        private const int MaxOutputCharacters = 1024 * 1024;

        public static List<DeviceInfo> ParseDevices(string output)
        {
            List<DeviceInfo> result = new List<DeviceInfo>();
            if (String.IsNullOrEmpty(output)) return result;
            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("*", StringComparison.Ordinal)
                    || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("adb:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("adb.exe:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("warning:", StringComparison.OrdinalIgnoreCase)) continue;
                Match row = Regex.Match(line,
                    @"^(\S+)\s+(device|offline|unauthorized|recovery|sideload|bootloader|rescue|connecting|authorizing|no permissions)(?:\s+(.*))?$",
                    RegexOptions.CultureInvariant);
                if (!row.Success) continue;
                string detail = row.Groups[3].Value;
                Match model = Regex.Match(detail, @"(?:^|\s)model:(\S+)", RegexOptions.CultureInvariant);
                result.Add(new DeviceInfo
                {
                    Serial = row.Groups[1].Value,
                    State = row.Groups[2].Value,
                    Model = model.Success ? model.Groups[1].Value.Replace('_', ' ') : String.Empty
                });
            }
            return result;
        }

        public static List<string> BuildInstallArguments(string serial, IEnumerable<string> files, InstallOptions options)
        {
            if (String.IsNullOrWhiteSpace(serial))
                throw new ArgumentException("請先選擇一台已授權的 Android 裝置。", "serial");
            if (serial.Length > 512 || serial[0] == '-')
                throw new ArgumentException("裝置序號格式不正確，請重新掃描。", "serial");
            foreach (char c in serial)
                if (Char.IsWhiteSpace(c) || Char.IsControl(c) || c == '"')
                    throw new ArgumentException("裝置序號格式不正確，請重新掃描。", "serial");
            if (files == null) throw new ArgumentNullException("files");
            if (options == null) throw new ArgumentNullException("options");

            List<string> paths = new List<string>();
            HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                if (String.IsNullOrWhiteSpace(file))
                    throw new ArgumentException("APK 檔案路徑不得空白。", "files");
                string full = Path.GetFullPath(file);
                if (!String.Equals(Path.GetExtension(full), ".apk", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("請選擇 .apk 檔案；.xapk、.apks、.aab 不能直接安裝。", "files");
                if (!File.Exists(full))
                    throw new FileNotFoundException("找不到 APK，請確認檔案仍在原位置。", full);
                if (new FileInfo(full).Length == 0)
                    throw new InvalidDataException("APK 是空白檔案，請重新下載：" + Path.GetFileName(full));
                if (!unique.Add(full))
                    throw new ArgumentException("同一個 APK 被重複選取，請移除重複檔案。", "files");
                paths.Add(full);
                if (paths.Count > 200)
                    throw new ArgumentException("一次最多選擇 200 個 split APK。", "files");
            }
            if (!options.SplitMode && paths.Count != 1)
                throw new ArgumentException("一般安裝一次請選擇 1 個 APK；同一 App 的 base / split APK 請使用分割套件模式。", "files");
            if (options.SplitMode && paths.Count < 2)
                throw new ArgumentException("分割套件模式需要同一 App 的 base APK 與所需 split APK，至少 2 個檔案。", "files");

            List<string> args = new List<string>();
            args.Add("-s");
            args.Add(serial);
            args.Add(options.SplitMode ? "install-multiple" : "install");
            if (options.Replace) args.Add("-r");
            if (options.AllowDowngrade) args.Add("-d");
            if (options.AllowTest) args.Add("-t");
            if (options.LowTarget) args.Add("--bypass-low-target-sdk-block");
            args.AddRange(paths);
            if (JoinArguments(args).Length > MaxArgumentsLength)
                throw new ArgumentException("檔案路徑總長度超過 Windows 指令長度限制，請將 APK 移到較短的資料夾路徑。", "files");
            return args;
        }

        // Quote according to Windows' CommandLineToArgvW / CRT conventions.
        // All arguments are quoted; ampersands, percent signs and parentheses
        // stay literal because the process is never passed through cmd.exe.
        public static string QuoteArgument(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (value.IndexOf('\0') >= 0) throw new ArgumentException("指令參數包含無效字元。", "value");
            StringBuilder result = new StringBuilder();
            result.Append('"');
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    slashes++;
                }
                else if (c == '"')
                {
                    result.Append('\\', slashes * 2 + 1);
                    result.Append('"');
                    slashes = 0;
                }
                else
                {
                    result.Append('\\', slashes);
                    result.Append(c);
                    slashes = 0;
                }
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
            return result.ToString();
        }

        public static string JoinArguments(IEnumerable<string> arguments)
        {
            if (arguments == null) throw new ArgumentNullException("arguments");
            StringBuilder result = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (result.Length > 0) result.Append(' ');
                result.Append(QuoteArgument(argument));
            }
            return result.ToString();
        }

        public static string FindAdb(string baseDir)
        {
            List<string> paths = new List<string>();
            AddCandidate(paths, baseDir, "platform-tools", "adb.exe");
            AddCandidate(paths, baseDir, "adb.exe");
            AddCandidate(paths, Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"), "platform-tools", "adb.exe");
            AddCandidate(paths, Environment.GetEnvironmentVariable("ANDROID_HOME"), "platform-tools", "adb.exe");
            AddCandidate(paths, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe");
            foreach (string path in paths)
            {
                try { if (File.Exists(path)) return Path.GetFullPath(path); }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
                catch (System.Security.SecurityException) { }
            }
            return String.Empty;
        }

        private static void AddCandidate(List<string> paths, string root, params string[] parts)
        {
            if (String.IsNullOrWhiteSpace(root)) return;
            try
            {
                string current = root;
                foreach (string part in parts) current = Path.Combine(current, part);
                paths.Add(current);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        public static string ExplainFailure(string output)
        {
            string s = (output ?? String.Empty).ToUpperInvariant();
            if (s.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE")
                || s.Contains("INSTALL_PARSE_FAILED_INCONSISTENT_CERTIFICATES")
                || s.Contains("INSTALL_FAILED_SHARED_USER_INCOMPATIBLE")
                || s.Contains("SIGNATURES DO NOT MATCH"))
                return "APK 的簽章與手機上已安裝的版本不一致。請取得同一發行者、相同簽章的 APK。覆蓋或降版參數不能解決簽章衝突；自行解除安裝原 App 可能遺失資料，需先備份。";
            if (s.Contains("INSTALL_FAILED_VERSION_DOWNGRADE") || s.Contains("VERSION CODE DOWNGRADE"))
                return "這個 APK 的版本號比手機上現有版本低。可選擇「嘗試降版本」重試，但 -d 通常只允許可偵錯的 App 降版；一般正式版仍可能被系統拒絕。";
            if (s.Contains("INSTALL_FAILED_DEPRECATED_SDK_VERSION"))
                return "APK 的 target SDK 太舊，被新版 Android 阻擋。若你信任來源，可在裝置支援時選擇「舊版 App 相容安裝」重試；安裝成功也不代表能正常執行。";
            if ((s.Contains("UNKNOWN OPTION") || s.Contains("UNRECOGNIZED OPTION")) && s.Contains("BYPASS-LOW-TARGET-SDK-BLOCK"))
                return "這台裝置或目前的 ADB 不支援舊 target SDK 例外參數。請取消「舊版 App 相容安裝」後重試，或確認已使用適合此裝置的官方 Platform-Tools。";
            if (s.Contains("INSTALL_FAILED_NO_MATCHING_ABIS") || s.Contains("INSTALL_FAILED_CPU_ABI_INCOMPATIBLE"))
                return "APK 的 CPU 架構與手機不相容。請取得符合手機的 arm64-v8a、armeabi-v7a 或 x86 版本；只有 64 位元支援的手機無法靠安裝參數執行 32 位元 App。";
            if (s.Contains("INSTALL_FAILED_OLDER_SDK") || s.Contains("REQUIRES NEWER SDK VERSION"))
                return "手機的 Android 版本低於 APK 要求的最低版本（min SDK）。請改用支援此手機版本的 APK，或更新手機系統。";
            if (s.Contains("INSTALL_FAILED_NEWER_SDK"))
                return "APK 的 Android 版本限制與此手機不相容，請取得適用於這個 Android 版本的 APK。";
            if (s.Contains("INSTALL_FAILED_TEST_ONLY"))
                return "這是標記為 testOnly 的測試 APK。若此為你要安裝的測試版本，勾選「允許測試 APK」後重試。";
            if (s.Contains("INSTALL_FAILED_USER_RESTRICTED") || s.Contains("DISALLOW_INSTALL") || s.Contains("BLOCKED BY ADMIN"))
                return "手機拒絕了這次安裝。請解鎖手機並查看 USB 安裝確認；若有管理員、工作設定檔或家長監護限制，需由管理者調整允許範圍。";
            if (s.Contains("INSTALL_FAILED_VERIFICATION_FAILURE") || s.Contains("INSTALL_FAILED_VERIFICATION_TIMEOUT"))
                return "手機的安裝驗證失敗或逾時。請查看手機上的安全檢查提示，確認 APK 來源與完整性；工具不會關閉安全檢查。";
            if (s.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE") || s.Contains("NO SPACE LEFT ON DEVICE"))
                return "手機可用空間不足。請在手機釋放儲存空間後重試；安裝過程需要的空間可能大於 APK 檔案大小。";
            if (s.Contains("INSTALL_FAILED_MISSING_SPLIT") || s.Contains("MISSING SPLIT"))
                return "這個 App 缺少必要的 split APK。請使用「分割套件模式」，同時選取相同版本的 base APK 與所需分割檔。";
            if (s.Contains("INSTALL_FAILED_ALREADY_EXISTS"))
                return "手機上已安裝相同套件。若要更新，勾選「覆蓋安裝」後重試。";
            if (s.Contains("INSTALL_FAILED_INVALID_APK") || s.Contains("INSTALL_PARSE_FAILED") || s.Contains("INSTALL_FAILED_BAD_SIGNATURE"))
                return "APK 無法通過套件解析或簽章檢查，可能已損壞、簽署不完整，或 split 檔案版本不一致。請向原發行者重新取得完整 APK。";
            if (s.Contains("UNAUTHORIZED") || s.Contains("STILL AUTHORIZING"))
                return "電腦尚未取得手機授權。請解鎖手機，確認「允許 USB 偵錯」對話框中的電腦後按允許，再重新掃描。";
            if (s.Contains("DEVICE OFFLINE"))
                return "裝置處於 offline。請確認手機已開機解鎖，重新插接可傳輸資料的 USB 線，再掃描裝置。";
            if (s.Contains("MORE THAN ONE DEVICE") || s.Contains("MORE THAN ONE EMULATOR"))
                return "找到多台裝置或重複的連線。請重新掃描並明確選擇要安裝的手機。";
            if (s.Contains("NO DEVICES") || (s.Contains("DEVICE '") && s.Contains("NOT FOUND")) || s.Contains("DEVICE NOT FOUND"))
                return "找不到所選裝置。請確認 USB 偵錯已開啟、線材支援資料傳輸且 USB 驅動可用，再重新掃描。";
            if (s.Contains("ERROR: TIMEOUT"))
                return "等待 ADB 回應逾時，本次電腦端指令已要求停止。手機端安裝仍可能完成；請先在手機確認結果，再決定是否重試。";
            if (s.Contains("ERROR: CANCELLED"))
                return "已要求停止本次電腦端 ADB 指令。這不會回復已完成的變更；手機端安裝仍可能繼續，請先確認手機狀態。";
            if (s.Contains("ERROR: OUTPUT_INCOMPLETE") || s.Contains("ERROR: OUTPUT_TRUNCATED"))
                return "ADB 輸出不完整，無法可靠判定安裝結果。請先在手機確認 App 是否已安裝，並保留記錄以便診斷。";
            if (s.Contains("ERROR: START_FAILED"))
                return "ADB 無法啟動。請確認 adb.exe 與相符版本的 AdbWinApi.dll、AdbWinUsbApi.dll 位於同一資料夾，並檢查電腦是否允許執行。";
            return "安裝未取得完整的成功回覆。請查看下方 ADB 原始記錄與手機提示；可把完整錯誤訊息提供給我判讀。";
        }

        public static CommandResult Run(string adbPath, IEnumerable<string> arguments,
            int timeoutMs, CancellationToken cancellationToken, Action<string> log)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException("timeoutMs");
            string commandLine = JoinArguments(arguments);
            if (commandLine.Length > MaxArgumentsLength)
                throw new ArgumentException("ADB 指令超過 Windows 長度限制。", "arguments");

            CommandResult result = new CommandResult { ExitCode = -1, Output = String.Empty };
            object outputLock = new object();
            StringBuilder output = new StringBuilder();
            Action<string> capture = delegate(string line)
            {
                if (line == null) return;
                string captured;
                lock (outputLock)
                {
                    if (result.OutputTruncated) return;
                    int available = MaxOutputCharacters - output.Length;
                    string suffix = Environment.NewLine;
                    if (line.Length + suffix.Length > available)
                    {
                        const string marker = "\r\nerror: OUTPUT_TRUNCATED (ADB output limit reached)\r\n";
                        int take = Math.Max(0, available - marker.Length);
                        captured = line.Substring(0, Math.Min(take, line.Length)) + marker;
                        // available can be smaller than the marker after an
                        // exact previous append. Keep the cap strictly bounded.
                        if (captured.Length > available) captured = captured.Substring(0, available);
                        output.Append(captured);
                        result.OutputTruncated = true;
                    }
                    else
                    {
                        captured = line + suffix;
                        output.Append(captured);
                    }
                }
                if (log != null)
                {
                    // A logging/UI exception must not abandon a running client.
                    try { log(captured.TrimEnd('\r', '\n')); }
                    catch (Exception) { }
                }
            };

            if (cancellationToken.IsCancellationRequested)
            {
                result.Cancelled = true;
                capture("error: CANCELLED before starting ADB.");
                result.Output = output.ToString();
                return result;
            }

            using (ManualResetEvent stdoutDone = new ManualResetEvent(false))
            using (ManualResetEvent stderrDone = new ManualResetEvent(false))
            using (Process process = new Process())
            {
                bool started = false;
                try
                {
                    if (String.IsNullOrWhiteSpace(adbPath))
                        throw new FileNotFoundException("尚未選擇 adb.exe。");
                    string fullAdbPath = Path.GetFullPath(adbPath);
                    if (!File.Exists(fullAdbPath))
                        throw new FileNotFoundException("找不到 adb.exe。", fullAdbPath);
                    if (fullAdbPath.Length + commandLine.Length + 4 >= 32767)
                        throw new ArgumentException("ADB 路徑與參數總長度超過 Windows 限制。");

                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = fullAdbPath,
                        Arguments = commandLine,
                        WorkingDirectory = Path.GetDirectoryName(fullAdbPath),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) TrySignal(stdoutDone);
                        else capture(e.Data);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) TrySignal(stderrDone);
                        else capture(e.Data);
                    };
                    if (!process.Start()) throw new InvalidOperationException("ADB 程序未啟動。");
                    started = true;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    Stopwatch timer = Stopwatch.StartNew();
                    while (!process.WaitForExit(50))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            result.Cancelled = true;
                            capture("error: CANCELLED; the device may still finish an installation.");
                            StopClient(process, capture);
                            break;
                        }
                        if (timer.ElapsedMilliseconds >= timeoutMs)
                        {
                            result.TimedOut = true;
                            capture("error: TIMEOUT; the device may still finish an installation.");
                            StopClient(process, capture);
                            break;
                        }
                    }
                    if (process.HasExited) result.ExitCode = process.ExitCode;
                    else result.OutputIncomplete = true;

                    // BeginOutputReadLine drains both streams concurrently. EOF
                    // waits are finite in case another process inherited a pipe.
                    bool outputEnded = stdoutDone.WaitOne(3000);
                    bool errorEnded = stderrDone.WaitOne(outputEnded ? 3000 : 0);
                    if (!outputEnded || !errorEnded)
                    {
                        result.OutputIncomplete = true;
                        capture("error: OUTPUT_INCOMPLETE; ADB streams did not close in time.");
                        try { process.CancelOutputRead(); } catch (InvalidOperationException) { }
                        try { process.CancelErrorRead(); } catch (InvalidOperationException) { }
                    }
                }
                catch (Exception ex)
                {
                    capture((started ? "error: RUN_FAILED: " : "error: START_FAILED: ") + ex.Message);
                    if (started) StopClient(process, capture);
                    result.ExitCode = -1;
                }
                finally
                {
                    lock (outputLock) result.Output = output.ToString();
                }
            }
            return result;
        }

        private static void TrySignal(EventWaitHandle signal)
        {
            try { signal.Set(); }
            catch (ObjectDisposedException) { }
        }

        private static void StopClient(Process process, Action<string> capture)
        {
            try
            {
                if (!process.HasExited) process.Kill();
                if (!process.WaitForExit(3000))
                    capture("error: ADB client did not stop promptly; please check the device before retrying.");
            }
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                capture("error: Could not stop this ADB client: " + ex.Message);
            }
        }
    }
}
