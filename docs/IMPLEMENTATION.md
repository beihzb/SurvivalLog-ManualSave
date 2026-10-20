# 实现原理与安全边界

本文档面向想审计这个 mod 的人，说明它**具体调用了哪些游戏 API、挂在哪几个方法上、写了哪些文件**。

## 1. 运行环境

| 组件 | 版本 / 位置 |
|---|---|
| 游戏 | Unity 6000.2.7f2，IL2CPP，`GameAssembly.dll` + `global-metadata.dat`（metadata v31.1） |
| 加载器 | BepInEx 6.0.0-be.789，`winhttp.dll` 代理 + Doorstop，自带 `dotnet/` CoreCLR |
| 绑定层 | BepInEx 首次启动用 Cpp2IL 解析 IL2CPP，生成 `BepInEx\interop\*.dll`（178 个，可重建） |
| 插件 | `SLManualSave.dll`，net6.0，约 23 KB |

插件自身的依赖全部是 `Private=false`（不随插件分发）：`BepInEx.Core`、`BepInEx.Unity.IL2CPP`、`Il2CppInterop.Runtime`、`0Harmony`、游戏 interop（`HotUpdate`、`AOT`、`R3`、`ObservableCollections`、`Vuplex.WebView`、`UnityEngine.*`）。

## 2. 数据流

### 2.1 保存

```
[网页 OutSetting.html]
  点击「保存游戏」→ 读输入框 → window.parent.postMessage({type:'SAVE_MANUAL', data:{name}})
        ↓ Root.html 组装游戏自己的协议（字段分隔符 \x1E）
  3 \x1E OutSetting \x1E SAVE_MANUAL \x1E {"name":"..."}
        ↓ Vuplex 原生消息桥
[C# WebUILayer.OnMessageFromJS]（我挂的 Prefix 先看一眼）
        ↓ 命中 → ManualSave.Checkpoint(name)
        ↓ return false（吞掉，不让游戏去路由一个它不认识的动作）
```

`Checkpoint` 的步骤（每一步都有日志，失败会记录 step 名）：

1. `GameSaveManager.Instance` 非空、`IsInBattle == true`
2. `IsWorldSafeToPersist("SLManualSave")` —— 游戏自己的“世界是否可保存”判断
3. `GetGameSaveData().IsSubGame == false` —— 探索副本/子局内拒绝
4. `GameSaveManager.SaveGame()` —— **游戏自己的保存入口**（内部含 `[save-wipe]` 空壳世界保护）
5. 轮询当前档 `LastWriteTimeUtc` 最多 2 秒，确认已落盘
6. `GameSaveManager.GenerateNewSaveFileName()` 取新档名
7. `File.Copy(当前档 → 新档.tmp)`
8. 字节替换 `.tmp` 内的 `FileName` 字段（ASCII / UTF-16LE / UTF-16BE 三种编码各扫一遍）
9. `File.Move(.tmp → 新档)` + `File.SetLastWriteTime(新档, now)`
10. `GameSaveManager.AddHistoryEntry(新档名, 显示名)`
11. `GameSaveManager.PersistHistoryData()`
12. 写入插件自己的 `BepInEx\plugins\SLManualSave\save_names.txt`

**为什么第 8 步必要**：存档二进制内部记录了自身的文件名。复制后若不改，游戏读这个新档后继续游玩，自动保存会按内部记录写回**原档**。两个名字等长（`Save_yyyy_MM_dd_HH_mm_ss.bytes`），所以是原位替换。

### 2.2 列表显示

存档列表的数据来自 `Reducer_Web_MainUI`，最终序列化成 `WebUI_MainUI_SaveItem`：

| Hook | 类型 | 作用 |
|---|---|---|
| `Reducer_Web_MainUI.FillSaveInfoFromHistory(Data_Web_Save, HistoryChild)` | Postfix | 若该 `FileName` 在名字表里 → 覆盖 `RoleText` |
| `Reducer_Web_MainUI.FillSaveInfo(Data_Web_Save, GameSaveData)` | Postfix | 同上（“继续游戏”那一条） |
| `WebUI_MainUI.UpdateListView_Save(ObservableList<Data_Web_Save>)` | Prefix | 手动档没有进度时间元数据 → 用文件名解析出时间填 `Info1`（`MM/dd HH:mm`） |
| 同上 | Postfix | 诊断日志（打印前 12 条的字段） |

### 2.3 键盘 / 输入法

网页里的输入框获得焦点时发 `SL_INPUT_FOCUS`，插件调用：

- `WebUILayer.SetKeyboardEnabled(bool)` —— 底层是 Vuplex 的 `KeyboardEnabled`
- `GameCore.Scripts.ImeGuard.SetEnabled(bool)` —— 挂/摘 Windows 输入法上下文

安全网：`WebUILayer.HidePage` / `RemovePage` 后置钩子，页面一消失就强制关闭，避免键盘卡在 WebView 里导致游戏热键失灵。

## 3. Hook 总表

| # | 目标 | 时机 | 触发条件 | 失败时 |
|---|---|---|---|---|
| 1 | `WebUILayer.OnMessageFromJS(object, EventArgs<string>)` | Prefix | 消息含 `SAVE_MANUAL` / `SL_INPUT_FOCUS` | `return true` + 记日志 |
| 2 | `WebUILayer.HidePage(string)` / `RemovePage(string)` | Postfix | `pageId == "OutSetting"` | 记日志 |
| 3 | `Reducer_Web_MainUI.FillSaveInfoFromHistory` / `FillSaveInfo` | Postfix | 名字表命中 | 记 warning |
| 4 | `WebUI_MainUI.UpdateListView_Save` | Prefix + Postfix | 名字表命中 / 仅诊断 | 记 warning |

Harmony 的 `Prefix` 返回 `false` 表示跳过原方法，因此只有 1 的两条自定义消息会被“吃掉”，其他一律放行。

## 4. 文件读写范围

| 路径 | 操作 |
|---|---|
| `Saves\Save_*.bytes`（已有档） | 只读（`File.Copy` 源） |
| `Saves\Save_<新时间>.bytes` | 新建（`.tmp` → `Move`） |
| `Saves\HistorySave.bytes` | 通过游戏自己的 `PersistHistoryData()` 写入 |
| `BepInEx\plugins\SLManualSave\save_names.txt` | 新建/追加（UTF-8，`文件名<TAB>Base64(名字)`） |
| `BepInEx\LogOutput.log` | 追加日志 |

不删除任何游戏文件；只删自己产生的 `.tmp`。

## 5. 明确不做的事

- 不修改内存数值（金钱、血量、物品、时间）
- 不修改任何已有存档的内容
- 不联网、不读取 Steam 凭据、不上传数据
- 不 hook 战斗、结算、成就、重生逻辑
- 不替换游戏本体文件
- 不常驻后台线程（只有一个 `MonoBehaviour.Update` 轮询热键 + IMGUI 提示）

## 6. 兼容性风险

| 风险 | 表现 | 处理 |
|---|---|---|
| 游戏更新改了目标方法签名 | `PatchAll` 抛异常 → 功能失效，游戏正常 | 看日志 `failed to patch WebUI bridge` |
| 游戏更新覆盖 `OutSetting.html` | 按钮消失 | 重跑 `install-ui-patch.ps1` |
| 游戏更新改了 IL2CPP metadata | interop 需重新生成（BepInEx 自动做） | 无需干预 |
| 存档格式升级 | 新档与旧档版本不一致会触发游戏自身的迁移逻辑 | 建议先备份 |

## 7. 如何自己验证

1. 反编译 `SLManualSave.dll`（约 23 KB，ILSpy/dnSpy 直接可读）
2. 看 `BepInEx\LogOutput.log` 里的 `[SL Manual Save]` 行
3. `fc /b OutSetting.html.orig OutSetting.html` —— 差异只应在文件末尾的注入块
4. `tools\save_audit.ps1 -Action check` —— 校验索引↔文件、内部档名、垃圾文件
