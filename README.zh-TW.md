<div align="center">
  <img src="./src/TavernDesk.App/Assets/Icons/app-icon.png" width="112" alt="TavernDesk 圖示">
  <h1>TavernDesk</h1>
  <p>Windows 角色 AI 聊天與跑團用戶端。</p>
  <p>
    <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white" alt="Windows 10 和 11">
    <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET 10">
    <a href="./LICENSE"><img src="https://img.shields.io/badge/License-MIT-F4C430" alt="MIT 授權條款"></a>
  </p>
</div>

<p align="center">
  <a href="./README.md">English</a> ·
  <a href="./README.zh-CN.md">简体中文</a> ·
  <strong>繁體中文</strong> ·
  <a href="./README.ja-JP.md">日本語</a>
</p>

TavernDesk 是 Windows 上的角色 AI 聊天與跑團用戶端。匯入角色卡、選好模型，就能開始單聊或群聊；延續劇情時，可以編輯世界書、整理長期記憶，或用劇本開啟一局跑團。角色、對話和劇本儲存在本機。

## 開始使用

從 [Releases](https://github.com/linnnn89/New-tavern/releases/latest) 下載 `TavernDesk-Setup-x64.exe`，選擇安裝目錄後啟動。安裝包內含執行環境。解除安裝請使用開始功能表的捷徑，或安裝目錄中的 `Uninstall TavernDesk.cmd`。

1. 首次啟動時選擇介面語言。
2. 開啟 **設定 → AI 與模型**，新增服務商，填寫位址與金鑰，再更新或新增模型。
3. 在功能分配頁選擇聊天、群聊接力等功能及模型，儲存至對應功能。
4. 匯入或建立角色，點擊角色卡的聊天按鈕。首頁的最近對話可以回到上次的聊天。

倉庫也附帶可攜版本：完整下載並解壓縮後，執行根目錄的 `TavernDesk.exe`，保留旁邊的 `app/` 資料夾。最新原始碼的執行方法見下方「從原始碼建置」。

## 聊天與群聊

聊天頂欄可以切換氣泡與小說顯示。訊息支援編輯、重新生成、候選切換、建立分支與 JSONL 匯入匯出，也可以在獨立視窗開啟對話。向上閱讀歷史時暫停自動跟隨，點擊回到底部即可繼續跟隨新訊息。

傳送按鈕旁的選單可以選擇生成回覆或僅儲存訊息；生成中同一位置顯示停止。尚未分配模型時，提示列提供對應設定的入口。輸入區的預算與記憶摘要可以開啟右側檢查器。

群聊成員列顯示姓名，帶外框的頭像是下一位發言者。點擊角色下方的接話按鈕可以指定角色；完全自動接力按成員順序繼續。

![群聊成員與傳送模式](./docs/screenshots/group-turns.png)

## 核對上下文與記憶

檢查器可以查看人設、角色卡、世界書、記憶、歷史、檢索結果與 API 請求結構。預算色條顯示輸入各段的比例，點擊色段展開內容；預計總量包含輸出預留，剩餘預算另行列出。

![聊天與上下文檢查器](./docs/screenshots/chat-inspector.png)

長期記憶按角色、群聊或跑團儲存，可以編輯、壓縮與設定檢查點。聊天記憶草稿提供差異檢視，核對新增與刪除內容後再儲存。

![記憶草稿差異](./docs/screenshots/memory-diff.png)

世界書可以掛載到全域、角色、對話、劇本或單局跑團。條目名稱與正文可直接編輯、一起儲存；切換條目保留目前編輯，離開時可選擇儲存、放棄或取消。儲存後更新本機全文檢索，向量索引透過重建操作更新。角色卡支援 PNG、JSON 和 CHARX 格式。

## 用劇本開啟跑團

在劇本庫新建或匯入劇本，選取後開啟新局。安排 GM 與玩家、選擇回合流程，再為 AI 席位分配模型。每局儲存自己的參與者、事件記錄及公開／GM 記憶。

- 支援 AI 或真人 GM、真人玩家與最多四名 AI 玩家，也可旁觀。
- 提供協作圓桌、秘密同投與嚴格先攻三種流程。
- GM 與每個 AI 玩家可使用不同模型，行動骰子記錄在局內。
- 對局中可停止生成，並從失敗狀態重試。

劇本編輯會在本機保留恢復草稿。劇本庫標示未儲存草稿，可恢復編輯或丟棄；儲存劇本後清除恢復草稿，返回劇本庫會放棄目前編輯。原劇本已變更或刪除時，恢復內容另存為新劇本。

![劇本庫與草稿恢復](./docs/screenshots/campaign-recovery.png)

## 語音與介面設定

在 **設定 → 語音** 填寫 Fish Audio 位址、金鑰與預設音色；訊息旁的齒輪可以指定角色音色。點擊角色訊息的小喇叭生成並播放，再次點擊停止。進階合成參數預設收合，詳細參數見[語音設定說明](./docs/voice-settings.md)。朗讀支援一般單聊與群聊。

介面設定提供淺色、深色、Cupertino 與 Material 主題，以及簡體中文、繁體中文、英語和日語。語言變更後重新啟動生效。修改縮放時有十秒確認時間；確認後儲存，逾時或關閉則恢復原比例。窄視窗會收合檢查器，點擊預算或記憶摘要可暫時開啟。

<details>
<summary>更多介面截圖</summary>

窄視窗與右側檢查器：

![窄視窗聊天](./docs/screenshots/chat-narrow.png)
![窄視窗檢查器](./docs/screenshots/chat-narrow-inspector.png)

角色庫、世界書編輯與玩家人設：

![角色庫](./docs/screenshots/character-shelf-current.png)
![世界書編輯](./docs/screenshots/worldbook-editor.png)
![已儲存人設](./docs/screenshots/persona-list.png)

</details>

## 模型接入

| 接入方式 | 設定 |
| --- | --- |
| OpenRouter、矽基流動、DeepSeek | API Key 與模型 |
| LM Studio | 本機服務位址，預設 `http://127.0.0.1:6543` |
| Grok CLI | 在本機執行 `grok login` 完成訂閱登入 |
| 自訂服務商 | OpenAI Chat Completions 相容位址與選填金鑰 |

自訂位址填到服務根目錄、`/v1` 或 `/api/v1`，聊天路徑由軟體補齊。聊天、群聊接力及其他生成功能分別分配模型。

## 本機資料

預設資料目錄為 `%USERPROFILE%\Documents\TavernDesk`，儲存角色卡、聊天、記憶、劇本、附件與匯出檔案。服務商金鑰使用 Windows DPAPI 加密儲存。資料目錄可以在設定中變更，重新啟動後遷移，原目錄保留。

模型生成、Embedding 與語音請求傳送到你設定的服務。錯誤記錄位於 `%LOCALAPPDATA%\TavernDesk\logs`。設定中的 API 測試模式會將請求、回覆、耗時與 Token 用量儲存到軟體目錄的 `tests\output`，可在設定中開啟或清空。

## 從原始碼建置

需要 Windows 10/11 x64 與 [global.json](./global.json) 指定的 .NET SDK。

```powershell
git clone --branch "跑团记忆升级版" --single-branch https://github.com/linnnn89/New-tavern.git
cd New-tavern
dotnet restore TavernDesk.sln
& .\scripts\Test-Localization.ps1
dotnet build TavernDesk.sln -c Release --no-restore
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj -c Release --no-build
```

一般建置輸出至 `src/` 下的建置目錄，倉庫附帶的 `app/` 是獨立的發佈版本。打包使用 `scripts/Build-WindowsInstaller.ps1`；隔離測試與維護說明見[架構與維護指南](./docs/architecture.md)。

## 文件與授權

- [文件導覽](./docs/README.md)
- [架構與維護指南](./docs/architecture.md)
- [跑團規則與實作](./docs/campaign_mode_design.md)
- [跑團上下文與記憶](./docs/TavernDesk-R2-B-Campaign-Context-Budget.md)

採用 [MIT License](./LICENSE)，允許商業使用、修改與再散布。
