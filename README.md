<div align="center">
  <img src="./src/TavernDesk.App/Assets/Icons/app-icon.png" width="112" alt="TavernDesk icon">
  <h1>TavernDesk</h1>
  <p>Character AI chat, editable memory, and tabletop campaigns for Windows.</p>
  <p>
    <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white" alt="Windows 10 and 11">
    <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET 10">
    <a href="./LICENSE"><img src="https://img.shields.io/badge/License-MIT-F4C430" alt="MIT License"></a>
  </p>
</div>

<p align="center">
  <strong>English</strong> ·
  <a href="./README.zh-CN.md">简体中文</a> ·
  <a href="./README.zh-TW.md">繁體中文</a> ·
  <a href="./README.ja-JP.md">日本語</a>
</p>

TavernDesk is a Windows app for character AI conversations and tabletop campaigns. Import a character card, choose a model, and start a private conversation or group chat. Worldbooks and editable memory help carry the story forward; scenarios let you start a campaign with a GM and players. Characters, conversations, and scenarios are stored locally.

## Get started

Download `TavernDesk-Setup-x64.exe` from [Releases](https://github.com/linnnn89/New-tavern/releases/latest), choose an installation folder, and launch the app. The installer includes the runtime. To uninstall, use the Start menu shortcut or `Uninstall TavernDesk.cmd` in the installation folder.

1. Choose an interface language on first launch.
2. Open **Settings → AI & Models**, add a provider, enter its address and key, then refresh or add models.
3. Select a function such as chat or group chat relay, choose its model, and save the assignment.
4. Import or create a character and click Chat on its card. Recent conversations on the home page take you back to previous chats.

The repository also includes a portable build. Extract the complete download and run `TavernDesk.exe`, keeping the adjacent `app/` folder. To run the latest source, follow “Build from source” below.

## Conversations and group chat

Switch between bubble and novel display in the chat header. Messages support editing, regeneration, alternate replies, branching, and JSONL import/export. Conversations can also open in separate windows. Scrolling up pauses automatic following; the return-to-bottom button resumes it.

The menu beside Send lets you generate a reply or save only your message. Stop appears in the same position during generation. If a model is missing, the notice links directly to its assignment page. Budget and memory summaries beside the composer open the inspector.

Group members show their names, and a ring marks the next speaker. Click Speak below a member to select that character. Automatic relay continues in member order.

![Group members and send modes](./docs/screenshots/group-turns.png)

## Context and memory

The inspector shows persona, character card, worldbook, memory, history, retrieval results, and the API request structure. Its composition bar shows proportions within the input; click a segment to inspect its content. The estimated total includes reserved output, with remaining capacity shown separately.

![Chat and context inspector](./docs/screenshots/chat-inspector.png)

Long-term memory belongs to a character, group, or campaign. You can edit it, compress it, and create checkpoints. Chat memory drafts show additions and removals for review before saving.

![Memory draft diff](./docs/screenshots/memory-diff.png)

Worldbooks can apply globally or to a character, conversation, scenario, or campaign. Edit an entry’s name and body together; switching entries keeps pending edits, and leaving offers Save, Discard, or Cancel. Saving refreshes local full-text search; the rebuild action updates the vector index. Character cards support PNG, JSON, and CHARX.

## Start a campaign

Create or import a scenario in the library, select it, and start a new game. Assign the GM and players, choose a turn flow, and select models for AI seats. Each campaign keeps its own participants, events, and public/GM memory.

- AI or human GM, human players, up to four AI players, and observer mode.
- Collaborative roundtable, secret simultaneous submission, and strict initiative flows.
- Separate models for the GM and each AI player, with action dice recorded in the game.
- Stop generation or retry a failed turn from the campaign controls.

Scenario edits are kept as local recovery drafts. The library marks pending drafts so you can restore or discard them. Saving clears the recovery draft; returning to the library discards the current edits. If the original scenario changed or was deleted, recovery saves a new scenario.

![Scenario library and draft recovery](./docs/screenshots/campaign-recovery.png)

## Speech and appearance

In **Settings → Speech**, enter a Fish Audio endpoint, key, and default voice. The gear beside a message sets a character voice. Click the speaker on a character message to generate and play audio; click again to stop. Advanced synthesis options are collapsed by default. See [speech settings](./docs/voice-settings.md) for details. Playback is available in ordinary and group chats.

Appearance settings include light, dark, Cupertino, and Material themes, with Simplified Chinese, Traditional Chinese, English, and Japanese interfaces. Language changes take effect after restarting. Scale changes give you ten seconds to confirm; confirmation saves the scale, while closing or timing out restores it. Narrow windows collapse the inspector, which can still open from the composer summaries.

<details>
<summary>More screenshots</summary>

Narrow chat and inspector:

![Narrow chat](./docs/screenshots/chat-narrow.png)
![Narrow inspector](./docs/screenshots/chat-narrow-inspector.png)

Character library, worldbook editor, and personas:

![Character library](./docs/screenshots/character-shelf-current.png)
![Worldbook editor](./docs/screenshots/worldbook-editor.png)
![Saved personas](./docs/screenshots/persona-list.png)

</details>

## Model providers

| Connection | Setup |
| --- | --- |
| OpenRouter, SiliconFlow, DeepSeek | API key and model |
| LM Studio | Local server address; default `http://127.0.0.1:6543` |
| Grok CLI | Run `grok login` locally for subscription login |
| Custom provider | OpenAI Chat Completions-compatible address and optional key |

Enter custom addresses at the service root, `/v1`, or `/api/v1`; the app adds the chat path. Chat, group chat relay, and other generation functions have separate model assignments.

## Local data

The default workspace is `%USERPROFILE%\Documents\TavernDesk`, containing character cards, conversations, memory, scenarios, attachments, and exports. Provider keys are encrypted with Windows DPAPI. Changing the workspace in Settings migrates it on the next launch and keeps the original folder.

Model generation, embedding, and speech requests go to your configured services. Error logs are in `%LOCALAPPDATA%\TavernDesk\logs`. API test mode in Settings saves requests, replies, timings, and token usage to `tests\output` under the application folder; Settings can open or clear that folder.

## Build from source

Use Windows 10/11 x64 and the .NET SDK specified in [global.json](./global.json).

```powershell
git clone --branch "跑团记忆升级版" --single-branch https://github.com/linnnn89/New-tavern.git
cd New-tavern
dotnet restore TavernDesk.sln
& .\scripts\Test-Localization.ps1
dotnet build TavernDesk.sln -c Release --no-restore
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj -c Release --no-build
```

Normal builds write to build folders under `src/`; the bundled `app/` is a separate published build. Package with `scripts/Build-WindowsInstaller.ps1`. See the [architecture and maintenance guide](./docs/architecture.md) for isolated testing and development.

## Documentation and license

- [Documentation index](./docs/README.md)
- [Architecture and maintenance](./docs/architecture.md)
- [Campaign rules and implementation](./docs/campaign_mode_design.md)
- [Campaign context and memory](./docs/TavernDesk-R2-B-Campaign-Context-Budget.md)

Released under the [MIT License](./LICENSE), allowing commercial use, modification, and redistribution.
