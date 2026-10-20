# SurvivalLog ManualSave

给《Survival Log / 生存日志》增加**游戏内手动存档**：在 ESC 菜单里按一下，把当前进度另存为一个独立存档，**可以自己起名字**，之后在主菜单「读取存档」里能直接认出它并加载。

> An unofficial BepInEx plugin for *Survival Log* that adds a manual save-slot button (with custom naming) to the in-game pause menu.
> Tested with game build **1.1.19131** (Unity 6000.2.7f2) + **BepInEx 6.0.0-be.789** (IL2CPP).

---

## 功能

- **ESC 菜单新增「保存游戏」**：点一下弹出命名框，确认后立刻存为一个新档，不退出游戏
- **自定义名字**：写进存档列表（角色名那一栏），并带保存时间，形如 `我的存档 · 第41天 · 10/21 00:23`
- **保留快捷键**：`F5` 用默认名字快速存档，`F9` 把存档信息打到日志（排查用）
- **安全闸**：不在局内、世界未就绪、探索副本内、存档位满时都会拒绝并提示
- **原子写入**：先写 `.tmp` 再改名，避免写出半个存档

## 前置条件

| 依赖 | 说明 |
|---|---|
| 游戏 | Steam 版 *Survival Log*，版本 1.1.19131（其他版本可能有效，但未测试） |
| [BepInEx 6 (IL2CPP, win-x64)](https://builds.bepinex.dev/projects/bepinex_be) | 需要 be.7xx 以上（支持 Unity 6 / metadata v31） |
| .NET 6 SDK | 只在**自己编译**时需要 |

## 安装

1. **安装 BepInEx**：把 BepInEx 压缩包解压到游戏根目录（与 `SurvivalLog.exe` 同级），启动一次游戏，等 `BepInEx\interop\` 生成完（首次会花 40 秒左右），然后退出。
2. **放入插件**：把 `SLManualSave.dll` 放进 `BepInEx\plugins\`。
   - 或者自己编译：`powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -GameDir "X:\...\Survival Log"`
3. **打界面补丁**（ESC 菜单里的按钮）：
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\install-ui-patch.ps1 -GameDir "X:\...\Survival Log"
   ```
   脚本会在游戏自带的 `OutSetting.html` 末尾追加一段 `<script>`，并保留 `.orig` 原件。
4. 启动游戏 → 进档 → `ESC` → 点「保存游戏」。

> 也可以只装插件用 `F5` 快捷键存档，不打界面补丁——两者互相独立。

## 使用

| 操作 | 效果 |
|---|---|
| ESC → 保存游戏 | 弹出命名框（预填 `手动存档 月-日 时:分`），确认后存为新档 |
| `F5` | 用当前角色名作为显示名快速存档 |
| `F9` | 把当前档名、存档数、列表内容写进 `BepInEx\LogOutput.log` |

存档落在 `%USERPROFILE%\AppData\LocalLow\LLS\SLGame\Saves\`，文件名仍是游戏的时间戳格式（`Save_2026_10_21_00_23_18.bytes`），**自定义名字只作为显示名**——因为读档、排序、版本迁移都依赖文件名格式。

## 卸载

两件一起做，否则按钮还在但点了没反应：

1. 删 `BepInEx\plugins\SLManualSave.dll`
2. 还原菜单页：`scripts\install-ui-patch.ps1 -GameDir "..." -Restore`

彻底移除 BepInEx：删 `BepInEx\`、`dotnet\`、`winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`。

## 实现原理（简述）

- **BepInEx 6** 通过 `winhttp.dll` 代理注入 .NET 宿主，用 Cpp2IL + Il2CppInterop 生成 `interop` 程序集，让插件能直接调用游戏的 C# API。
- **保存**：调用游戏自己的 `GameSaveManager.SaveGame()` 落盘 → `GenerateNewSaveFileName()` → 复制当前档 → 字节级改写副本内部的 `FileName` 字段（让新档“认识”自己）→ `AddHistoryEntry()` + `PersistHistoryData()` 写索引。
- **界面**：`OutSetting.html` 里注入的脚本通过游戏自带的 WebView 消息桥（`postMessage` → `Root.html` → Vuplex）发送 `SAVE_MANUAL`，插件在 `WebUILayer.OnMessageFromJS` 前缀里拦截处理。
- **列表显示**：`Reducer_Web_MainUI.FillSaveInfoFromHistory` 后置钩子改写 `RoleText`，`UpdateListView_Save` 前置钩子补 `Info1`（时间）。

详细 hook 清单、调用到的游戏 API、文件读写范围与安全边界见 [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md)。

## 安全性

- 不修改任何**已有**存档：只做逐字节复制
- 不修改游戏本体文件（`GameAssembly.dll` / `UnityPlayer.dll` / 资源包）
- 不联网、不读 Steam 凭据、不上传数据
- 所有入口 try/catch，补丁失败只会让功能失效，不影响游戏运行
- 建议使用前把 `Saves` 整包备份；仓库自带 `tools/save_audit.ps1` 可做一致性体检：
  ```powershell
  powershell -ExecutionPolicy Bypass -File tools\save_audit.ps1 -Action check
  ```

## 已知限制

- 游戏更新会覆盖 `OutSetting.html`，需要重跑 `install-ui-patch.ps1`
- 游戏更新若改动 `OnMessageFromJS` 等目标方法签名，补丁会加载失败（日志有报错），**游戏本身不受影响**
- 局内不能直接读档（游戏自身的限制），读档仍需回主菜单
- 每个手动档约 600 KB，会随 Steam Auto-Cloud 上传，建议定期清理

## 排错

日志：`BepInEx\LogOutput.log`，搜索 `SL Manual Save`。

| 现象 | 可能原因 |
|---|---|
| 按钮不出现 | 游戏更新覆盖了页面 → 重跑界面补丁；或页面被缓存 → 重启游戏 |
| 按钮在但点了没反应 | 插件没加载（看日志有没有 `SL Manual Save`） |
| 输入框打不了字 | 键盘焦点没交出去（日志应有 `text input mode = True/False`） |
| 存档失败 | 看日志里 `checkpoint failed at step '...'`，step 名即出错环节 |

## License

MIT，见 [LICENSE](LICENSE)。

## 免责声明

本项目是非官方社区 mod，与游戏开发商/发行商无关，不包含任何游戏资源文件。
使用修改类 mod 可能违反游戏 EULA，请自行判断并承担风险。
