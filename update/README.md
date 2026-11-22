# 更新發布資料

作者：**Jason1993余**

本目錄的 `stable.json` 是官方穩定版更新資訊。程式的來源設定與驗證公鑰位於 `src/UpdateTrust.cs`。**這裡只放公開更新資料，不放簽署私鑰、權杖或個人裝置資料。**

## 版本的兩種編號

`version` 是使用者看到的版本，例如 `1.1.0`；`releaseSequence` 是每次正式發布增加 1 的序號。初始對照為 v1.0.1 → 1、v1.1.0 → 2。預覽版不要占用正式通道的序號，也不要用跳號來製造「落後三版」。已發布的版本、序號與補丁應維持不變；修正有問題的發布時另發一個新序號。

客戶端會在以下任一條件成立時要求更新：

```text
latest.releaseSequence - installed.releaseSequence >= 3
installed.releaseSequence < latest.minimumSupportedSequence
```

最低支援序號用於作者另有必要的提早更新政策，不可高於本次最新版序號。提高最低支援序號會影響使用者，即使使用者尚未落後三版，也可能被要求升級；應在版本說明中交代原因。客戶端不接受把已知的最低支援政策降回去，修正政策應使用經過檢視的新發布流程。

## 已簽署的格式

更新資料的外層為 JSON：

```json
{
  "payload": "<完整 UTF-8 JSON 位元組的 Base64>",
  "signature": "<RSA PKCS#1 v1.5／SHA-256 簽章的 Base64>"
}
```

簽章覆蓋解碼後的完整 `payload` 位元組，不是外層 JSON 的縮排，也不是挑部分欄位重新串接。內容修改後必須重新簽署，不能手動改掉 Base64 中的版號。

payload 欄位如下：

| 欄位 | 用途 |
| --- | --- |
| `schema` | 格式版本，目前為 `1`。 |
| `appId` | 固定 `jason-adb-apk-installer`，避免用錯產品更新。 |
| `channel` | 正式通道固定 `stable`。 |
| `version` | 與程式、標籤及補丁一致的版號。 |
| `releaseSequence` | 正式發布序號。 |
| `minimumSupportedSequence` | 最低可繼續使用的發布序號。 |
| `publishedUtc` | 發布 UTC 時間。 |
| `notes` | 此次更新的使用者說明。 |
| `packageUrl` | 本專案 GitHub Release 的 HTTPS 補丁下載網址。 |
| `packageSize` | 補丁實際位元組數。 |
| `packageSha256` | 最終 ZIP 的 SHA-256，64 個十六進位字元。 |

不要把帳號、客戶清單、裝置識別碼或私人檔案路徑加入公開 payload。正式 feed 的網址固定在原始碼內，不提供一般使用者輸入任意更新網址的欄位。

## 每次發布的順序

1. 更新 `src/UpdateTrust.cs` 的 `Version` 與 `ReleaseSequence`，並使 `src/Program.cs` 與 `src/UpdaterProgram.cs` 的組件版號，以及 `src/app.manifest` 版號一致。準備必要的最低支援序號與變更說明；保留 `Jason1993余` 署名和已核定授權。
2. 執行 `Build.cmd` 建置 `ADB_APK_Tool.exe` 與 `JasonUpdater.exe`，完成與本次修改相關的檢查。完整執行檔可覆蓋前一版的自有檔案；不要假設每位使用者都有安裝中間版本。
3. 產生公開補丁 ZIP。ZIP 中的檔案路徑以應用程式根目錄為基準，不再包一層版本資料夾。只納入更新程式允許的自有檔案；不要放 `APK`、`platform-tools`、安裝紀錄、本機狀態、簽署私鑰或 GitHub 憑證。
4. 決定檔案最終內容後計算 ZIP 長度與 SHA-256，生成 payload，再使用作者保管的私鑰離線簽署。
5. 先建立 GitHub Release 草稿、附上補丁及一般使用者下載包，確認檔案名稱、大小與雜湊，然後發布 Release。
6. 確認正式補丁網址已能下載並與簽署資料一致後，**最後**才更新本目錄的 `stable.json`。若先更新 feed，正在開啟程式的使用者可能被導向尚未存在的補丁。
7. 從既有含更新功能的版本驗證檢查、提醒與實際套用流程，確認發布資訊與來源一致。

GitHub 的不可變更發布可以鎖定已發布的標籤與附加檔案，適合後續採用。若啟用這個功能，應先把所有資產附到草稿再正式發布。此專案是否已啟用，以 GitHub 實際設定與頁面顯示為準，本文件不代表已替帳號啟用。[GitHub 官方說明](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)

### 使用簽署工具

公開原始碼包含 `tools/Sign-Update.ps1`；它不包含私鑰。補丁 ZIP 至少要有根目錄的 `ADB_APK_Tool.exe` 與 `JasonUpdater.exe`。先完成建置與壓縮，再從專案根目錄以 Windows PowerShell 執行。以下是未來 v1.2.0／序號 3 的範例路徑，請換成自己的實際檔案：

```powershell
.\tools\Sign-Update.ps1 `
  -PackagePath 'D:\Releases\Jason_ADB_APK_Installer_v1.2.0_Patch.zip' `
  -SigningKeyPath 'D:\Private\update-signing-key.xml' `
  -Version 1.2.0 `
  -ReleaseSequence 3 `
  -MinimumSupportedSequence 1 `
  -NotesPath 'D:\Releases\notes.txt'
```

工具預設輸出 `update/stable.json`，會檢查原始碼版號／序號、兩個執行檔的組件版號、補丁允許的檔案與大小，以及私鑰是否對應程式內建的公鑰；連結或特殊檔案也會被拒絕。私鑰必須放在專案資料夾之外。生成成功只代表本機更新資料已備妥，尚未上傳；仍須依前述順序先發布 Release 資產、後發布 feed。

如果 PowerShell 執行原則不允許該腳本，請先檢視腳本來源，依電腦管理政策處理。此工具不要求關閉防護、不自動改執行原則，也不要求把私鑰貼到指令輸出或公開頁面。

## 簽署金鑰

更新公鑰可以公開，私鑰只能交給授權發布者保管。私鑰必須與一般下載包及 GitHub 儲存庫分開保存；建議另有加密離線備份。保留私鑰的人可以製作既有客戶端信任的更新，因此不要透過公開 issue、release、公開聊天附件或測試套件散布它。

遺失私鑰時，不能只在 feed 裡放新公鑰來讓舊版信任它。需要可由既有金鑰驗證的輪替流程，或由使用者手動安裝內建新公鑰的可信新版本。懷疑私鑰外洩時也要另外處理信任切換；刪除公開更新檔不等於已撤銷被複製的私鑰。

自行修改／分發本程式時，須保留原作者與修改標示，並為自己的更新服務使用自己的金鑰與來源設定。不得宣稱修改版是官方原版，亦無權使用官方作者的簽署私鑰。

## 商業版之後如何延伸

目前這個公開 `stable` 通道處理版本更新，不包含客戶購買狀態。商業版可以再加入獨立更新通道與伺服器授權服務，但不應把客戶購買資訊放進公開 feed，也不應把授權伺服器的秘密嵌入 EXE。見 [商業販售規劃](../docs/COMMERCIAL-ROADMAP.md)。
