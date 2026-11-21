小金 ADB 安裝助手 v1.0.1｜公開版使用說明
作者署名：Jason1993余
分類：手機 → 安卓 → 安裝程式

這是 Windows 10／11 的繁體中文 APK 安裝介面。選擇 APK、接上已授權
的 Android 手機，再按「一鍵安裝」。本工具需要相容的 .NET Framework
4.x 執行環境，以及 Windows 可以辨識手機的 ADB USB 驅動。

一、先準備官方 ADB

本公開版不附 Google 預編譯的 Platform-Tools，也不附任何 APK。
請自行從 Android 官方頁面取得 Windows Platform-Tools，依官方條款下載：
https://developer.android.com/tools/releases/platform-tools

解壓官方套件，使本工具旁有 platform-tools\adb.exe；保留套件內原有
DLL、NOTICE 等檔案。若已有 ADB，可在本工具「顯示進階選項」內按
「選擇 adb.exe」，指定現有檔案。

二、一般安裝

1. 完整解壓縮本工具，保留 phone-guides 資料夾及所有說明檔。
2. 開啟 ADB_APK_Tool.exe，按「瀏覽 APK」選取一個 .apk；亦可拖進視窗。
3. 用可傳資料的 USB 線連接手機並解鎖手機。
4. 第一次使用時，按「手機設定教學」，選小米／紅米／POCO、Sony、
   Google Pixel 或 Samsung。依步驟開啟「開發人員選項」及「USB 偵錯」。
5. 手機跳出「允許 USB 偵錯？」時，確認是此次連接的電腦，再本人允許。
   這裡不需要 Root 或開啟手機裝置管理員權限。
6. 有多部手機或模擬器時，明確選擇要安裝的裝置，再按「一鍵安裝」。
7. 等候安裝結果，並留意手機是否另有安裝確認。完成後在手機開啟 App。

更快的方式：把單一 APK 放在程式旁或 APK 子資料夾，雙擊
OneClickInstall.cmd／一鍵安裝.cmd，即可自動選檔及開始偵測。
一鍵流程會等候手機連線或授權最多 60 秒；多台裝置仍須明確選擇。

三、安裝選項

- 預設覆蓋安裝（-r）；正常更新保留原有資料，但這不是備份功能。
- 預設自動相容舊版 App：手機支援時加入 --bypass-low-target-sdk-block。
- 測試 APK（-t）、嘗試降版（-d）預設關閉，可在進階選項自行選擇。
  降版仍受 Android 對可偵錯套件等條件限制，不保證所有 App 都可降版。
- 同一 App 的 base／split APK：選齊檔案並勾選「同一 App 的分割 APK」。
  不能把不同 App 混在同一次分割安裝。
- 本版直接接受 .apk，不直接處理 .xapk、.apks 或 .aab。

舊 target SDK 相容參數不能修正 APK 簽章不符、CPU 架構不符或手機
Android 版本低於 App 最低需求。安裝成功也不保證 App 能正常執行。

四、連線或安裝失敗

找不到手機：確認 USB 偵錯、手機已解鎖、資料線可傳資料，再換 USB 孔。
若缺少 ADB USB 驅動，使用手機品牌的官方驅動；安裝權限依電腦規定。
unauthorized／未授權：請先查看手機，允許此次電腦的 USB 偵錯授權。
offline／離線：等待手機完成開機，重新插線，再重新整理裝置。

手機若有額外的 USB 安裝或系統封鎖設定，依品牌教學與手機提示處理。
受管理裝置的限制須交由有權管理的人員處理。
程式會在下方顯示安裝錯誤及處理建議，可「複製紀錄」或「另存紀錄」。
取消或逾時不等於撤回手機端的安裝；再次嘗試前先查看手機結果。

五、原始碼與隱私

若取得的是原始碼，可在 Windows 執行 Build.cmd 建立主程式。
它使用可用的 .NET Framework C# 編譯器，不需要額外 NuGet 套件。
公開成品與原始碼均不附個人 APK、裝置資料、實機截圖或登入憑證。
本工具沒有內建遙測、帳號登入或自動上傳功能。
安裝紀錄可能包含裝置序號及本機檔案路徑；分享紀錄前請先遮蔽。

六、作者與使用範圍

作者署名：Jason1993余
本專案採用「Jason1993余 非商業使用授權 1.0」，生效日期 2026-11-21。

允許個人學習、非商業研究、非營利教育與公益用途，以及非營利組織
的非營利工作、政府機關之公務內部使用；可在此範圍內免費使用、
修改及免費分發。
公司內部業務、販售、付費下載、收費代裝／維修、營利產品整合及
其他商業用途，必須另取得作者書面授權。
必須保留 Jason1993余 原作者署名、授權及第三方聲明；修改版須標示
修改者與修改事實，不能冒充作者原版。

以上僅適用於作者有權授權的自製部分；ADB、Android SDK、DLL 等
第三方元件維持各自原授權。本專案不能限縮既有第三方授權權利。
這是可檢視原始碼、附使用限制的授權；公開平台仍可檢視與分叉，
條款不保證技術上防拷或阻止移除署名。完整條件以隨附 LICENSE 為準。

v1.0.1 新增右下角作者署名，安裝邏輯未變更。已完成編譯與介面檢查；
個別手機、驅動與 APK 的相容性仍依實際連線結果為準。

官方參考：
https://developer.android.com/tools/adb
https://developer.android.com/studio/debug/dev-options
https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/docs/user/adb.1.md

© 2026 Jason1993余
