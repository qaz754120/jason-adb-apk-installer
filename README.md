# 小金 ADB 安裝助手

**版本：1.1.0 · 作者：Jason1993余**  
**分類：手機 → 安卓 → 安裝程式**

Windows 10／11 繁體中文 APK 安裝介面。選擇 APK、連接已授權的 Android 手機，再按「一鍵安裝」。

## 功能

- 瀏覽電腦上的 APK，或將 APK 拖進視窗。
- 自動偵測單一裝置；多台手機或模擬器時明確選擇目標。
- 等候手機 USB 偵錯授權，顯示中文進度及錯誤處理建議。
- 預設覆蓋安裝；手機支援時，自動加入舊 target SDK 相容參數。
- 進階提供測試 APK、嘗試降版及同一 App 的分割 APK 安裝。
- 內建小米／REDMI／POCO、Sony Xperia、Google Pixel、Samsung Galaxy 教學。
- 複製或另存安裝紀錄；右下角保留作者署名。
- 開啟時檢查官方簽署的版本資訊，可延後一週／一個月提醒。
- 落後至少三次正式發布，或低於最低支援序號時，須先更新才能安裝 APK。
- 下載驗證補丁、備份程式檔案，再重新啟動套用；保留既有 APK、ADB 與使用者資料。

## 使用方式

1. 取得本工具的 Windows 成品並完整解壓縮；從原始碼使用時先執行 `Build.cmd`。
2. 從 [Android 官方網站](https://developer.android.com/tools/releases/platform-tools) 取得 Windows Platform-Tools，依官方條款下載。
3. 將官方套件解壓，使本工具旁的 `platform-tools/adb.exe` 存在，並保留官方 DLL、NOTICE 等檔案。也可以在進階選項指定現有 `adb.exe`。
4. 開啟 `ADB_APK_Tool.exe`，按「瀏覽 APK」選檔。
5. 第一次連接時，按「手機設定教學」，依品牌開啟開發人員選項及 USB 偵錯，並在手機允許電腦授權。
6. 按「一鍵安裝」。

也可把單一 APK 放在程式旁或 `APK` 子資料夾，執行 `OneClickInstall.cmd`／`一鍵安裝.cmd`。多台裝置時仍須明確指定手機。

公開原始碼不包含 Google 預編譯的 Platform-Tools 或任何 APK。這些元件由使用者自行取得，適用各自的原始授權。

## 相容性與限制

需要 Windows 的相容 .NET Framework 4.x 執行環境及可用的 ADB USB 驅動。正式部署請使用仍受支援的 Windows 與 .NET Framework 4.8／4.8.1；程式碼維持 C# 5／4.5 API 相容，不代表建議安裝已停止支援的 4.5 執行環境。
手機需要開發人員選項與 USB 偵錯，不需要 Root。USB 授權必須由手機使用者本人確認。

`--bypass-low-target-sdk-block` 處理舊 target SDK 的安裝門檻，不能解決簽章不符、CPU 架構不符或手機系統低於 App 最低需求。
`-d` 降版仍受 Android 對可偵錯套件等條件限制。
取消電腦端指令不保證手機端安裝會撤回，請先確認手機結果再重試。

## 建置

在 Windows 執行 `Build.cmd`。它使用可用的 .NET Framework C# 編譯器，產生 `ADB_APK_Tool.exe` 和 `JasonUpdater.exe`；不要求提升為系統管理員。

程式碼維持 C# 5 相容，使用 Windows Forms；沒有額外 NuGet 套件。

## 從 v1.0.1 升級與後續更新

v1.0.1 本身沒有更新檢查功能，需要先手動升級一次：先結束手機安裝、關閉程式，再把 v1.1.0 **Patch ZIP** 的檔案解壓覆蓋到原程式資料夾。保留原本的 `platform-tools` 與 APK。第一次使用者則下載 **Windows ZIP**，並依上方方式準備官方 ADB。

之後程式每次啟動都會檢查更新。一般更新可選擇「1 週後再提醒」或「1 個月後再提醒」；延後通知期間仍會確認是否達到強制門檻。補丁下載驗證完成後，按「重新啟動套用更新」。

強制更新按正式發布序號計算，條件為 `最新版序號 - 目前序號 >= 3`，或低於簽署資料中的最低支援序號。v1.1.0 為序號 2，因此最新序號 4 時可延後，序號 5 時須更新。完全離線且尚無已知強制政策時可繼續使用；曾收到並驗證強制政策後，斷線不會解除該要求。

- [更新功能、離線規則與隱私說明](docs/UPDATES.md)
- [作者發布補丁與維護簽章](update/README.md)
- [未來商業販售規劃](docs/COMMERCIAL-ROADMAP.md)

## 作者與使用範圍

作者署名為 **Jason1993余**。本專案採用 [Jason1993余 非商業使用授權 1.0](LICENSE)，於 2026-11-21 生效。

- **允許：**個人學習、非商業研究、非營利教育與公益用途，以及非營利組織的非營利工作、政府機關公務內部使用；可在此範圍內免費使用、修改及免費分發。
- **商業用途須另取得作者書面授權：**包括公司內部業務、販售程式、付費下載、收費代裝／維修，以及整合至營利產品或付費服務。
- **必須保留：**`Jason1993余` 原作者署名、授權及第三方聲明；修改版須標示修改者與修改事實，不能冒充原版。

限制僅適用於作者有權授權的自製部分。ADB、Android SDK、DLL 等第三方元件維持各自原授權；本專案不能限縮使用者依第三方授權已取得的權利。
這是可檢視原始碼、附使用限制的授權；公開 GitHub 仍可檢視與分叉，條款不保證技術上防拷或阻止移除署名。
完整條件以隨附 `LICENSE` 為準。

授權問題可透過本專案 Issues 聯繫，無須在公開討論中填入私人信箱、電話、裝置序號或帳號憑證。

## 隱私

本工具沒有內建遙測、帳號登入或 APK／裝置資料上傳功能。ADB 操作在電腦與已授權裝置間進行。v1.1.0 起會透過 HTTPS 取得官方更新資料，並在使用者選擇更新後下載補丁；伺服器會收到正常連線所需的 IP、時間及請求網址，但不會收到手機序號、APK 路徑或安裝紀錄。
介面紀錄可能包含裝置序號與 APK 路徑；分享問題紀錄前，請先遮蔽這些內容。

## 驗證狀態

- 核心邏輯：19 項模擬程序測試通過。
- 裝置選擇：3 項回歸測試通過。
- v1.0：已收到使用者一次 Windows 真機安裝成功的回報。
- v1.0.1：完成重新編譯、作者署名與最小視窗布局檢查；安裝邏輯未變更。
- v1.1.0：更新簽章、政策、補丁檔案處理、失敗復原及介面閘門另有測試，詳見發布說明。Windows 真機的更新重啟與特定防毒／權限環境仍需實機確認。

上述測試不是所有手機、驅動或 APK 的相容性保證。

## 參考

- [Android Debug Bridge](https://developer.android.com/tools/adb)
- [Android 開發人員選項](https://developer.android.com/studio/debug/dev-options)
- [AOSP ADB 參數手冊](https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/docs/user/adb.1.md)
- [Android 14 行為變更](https://developer.android.com/about/versions/14/behavior-changes-all)

© 2026 Jason1993余
