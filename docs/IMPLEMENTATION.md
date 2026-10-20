# 实现原理与安全边界

本文档说明该插件的挂钩点、调用的游戏接口、文件读写范围与失败处理策略，供安全审计使用。

## 1. 运行环境

| 组件 | 版本或位置 |
|---|---|
| 游戏运行时 | Unity 6000.2.7f2，IL2CPP，元数据 v31.1 |
| 加载器 | BepInEx 6.0.0-be.789（Doorstop + `winhttp.dll` 代理，自带 CoreCLR） |
| 类型绑定 | BepInEx 在首次启动时通过 Cpp2IL 解析 IL2CPP 元数据，生成 `BepInEx\interop\*.dll`（178 个，可由删档后重新生成） |
| 插件 | `SLManualSave.dll`，目标框架 net6.0，约 23 KB |

插件引用的程序集均以 `Private=false` 方式引用，不随插件分发：`BepInEx.Core`、`BepInEx.Unity.IL2CPP`、`Il2CppInterop.Runtime`、`0Harmony`，以及游戏生成的 `HotUpdate`、`AOT`、`R3`、`ObservableCollections`、`Vuplex.WebView`、`UnityEngine.*`。

## 2. 数据流

### 2.1 存档创建

```
[WebUI 页面 OutSetting.html]
  点击入口 → 读取输入框 → window.parent.postMessage({ type: 'SAVE_MANUAL', data: { name } })
        ↓ Root.html 按游戏协议封装，字段分隔符为 U+001E
  3 U+001E OutSetting U+001E SAVE_MANUAL U+001E {"name":"..."}
        ↓ Vuplex 原生消息桥
[C# WebUILayer.OnMessageFromJS]  ← 插件前置补丁
        ↓ 命中后交由 ManualSave.Checkpoint(name)
        ↓ 返回 false，跳过游戏自身的消息路由
```

`ManualSave.Checkpoint` 的执行顺序如下，每一步均记录日志；失败时日志包含 `step` 字段以标识失败环节：

1. 校验 `GameSaveManager.Instance` 非空且 `IsInBattle == true`；
2. 调用 `IsWorldSafeToPersist("SLManualSave")`，返回 `false` 时中止；
3. 校验 `GetGameSaveData().IsSubGame == false`，处于探索副本或子局时中止；
4. 调用 `GameSaveManager.SaveGame()` 写入当前状态；
5. 轮询当前存档文件的修改时间，最多等待 2 秒确认落盘；
6. 调用 `GenerateNewSaveFileName()` 生成新文件名；
7. 将当前存档复制为新文件名的 `.tmp` 临时文件；
8. 原位改写 `.tmp` 内部的 `FileName` 字段，分别按 ASCII、UTF-16LE、UTF-16BE 三种编码匹配；
9. 将 `.tmp` 重命名为正式文件，并设置修改时间；
10. 调用 `AddHistoryEntry(新文件名, 显示名)`；
11. 调用 `PersistHistoryData()` 写入索引；
12. 将显示名写入插件的 `save_names.txt`。

第 8 步的必要性：存档二进制内部记录了自身文件名。若副本保留原名称，游戏读取该副本后继续游玩时，自动存档会依据内部记录写回原始文件。由于新旧文件名长度一致，该改写为定长原位替换。

### 2.2 读取列表字段

读取列表的数据由 `Reducer_Web_MainUI` 组装，最终序列化为 `WebUI_MainUI_SaveItem`。插件通过以下补丁写入名称与时间字段：

| 目标方法 | 补丁类型 | 作用 |
|---|---|---|
| `Reducer_Web_MainUI.FillSaveInfoFromHistory(Data_Web_Save, HistoryChild)` | Postfix | 若该存档文件名存在于名称映射中，覆盖 `RoleText` |
| `Reducer_Web_MainUI.FillSaveInfo(Data_Web_Save, GameSaveData)` | Postfix | 同上，用于“继续游戏”条目 |
| `WebUI_MainUI.UpdateListView_Save(ObservableList<Data_Web_Save>)` | Prefix | 手动存档槽缺少进度时间元数据，使用文件名解析的时间填充 `Info1`（格式 `MM/dd HH:mm`） |
| 同上 | Postfix | 输出诊断日志（前 12 条记录） |

### 2.3 键盘与输入法

页面内输入框获得或失去焦点时发送 `SL_INPUT_FOCUS` 消息。插件据此调用：

- `WebUILayer.SetKeyboardEnabled(bool)`：控制 Vuplex WebView 的键盘输入转发；
- `GameCore.Scripts.ImeGuard.SetEnabled(bool)`：挂载或释放 Windows 输入法上下文。

为避免页面关闭后键盘焦点仍滞留于 WebView，插件对 `WebUILayer.HidePage` 与 `RemovePage` 附加后置补丁，当 `pageId` 为 `OutSetting` 时强制关闭键盘与输入法转发。

## 3. 挂钩点总表

| 序号 | 目标方法 | 补丁类型 | 触发条件 | 异常处理 |
|---|---|---|---|---|
| 1 | `WebUILayer.OnMessageFromJS(object, EventArgs<string>)` | Prefix | 消息包含 `SAVE_MANUAL` 或 `SL_INPUT_FOCUS` | 返回 `true` 并记录日志 |
| 2 | `WebUILayer.HidePage(string)`、`RemovePage(string)` | Postfix | `pageId == "OutSetting"` | 记录日志 |
| 3 | `Reducer_Web_MainUI.FillSaveInfoFromHistory`、`FillSaveInfo` | Postfix | 名称映射命中 | 记录警告 |
| 4 | `WebUI_MainUI.UpdateListView_Save` | Prefix / Postfix | 名称映射命中 / 诊断输出 | 记录警告 |

Harmony 约定：Prefix 返回 `false` 表示跳过原方法。本插件仅在序号 1 的两类自定义消息上返回 `false`，其余消息一律返回 `true`，交由游戏原有逻辑处理。

## 4. 文件读写范围

| 路径 | 操作 |
|---|---|
| `Saves\Save_*.bytes`（既有存档） | 只读，作为复制源 |
| `Saves\Save_<新时间戳>.bytes` | 新建（`.tmp` → 重命名） |
| `Saves\HistorySave.bytes` | 通过 `PersistHistoryData()` 写入 |
| `BepInEx\plugins\SLManualSave\save_names.txt` | 新建或更新，UTF-8，格式为 `文件名<TAB>Base64(名称)` |
| `BepInEx\LogOutput.log` | 追加日志 |

插件不删除任何游戏文件，仅清理自身产生的 `.tmp` 临时文件。

## 5. 明确排除的操作

- 不修改运行期内存数值（金钱、生命值、物品数量、时间）；
- 不修改任何既有存档的内容；
- 不进行网络通信，不读取 Steam 凭据，不上传数据；
- 不对战斗、结算、成就、重生等逻辑添加挂钩；
- 不替换游戏二进制文件或资源包；
- 不创建常驻后台线程；运行时仅包含一个 `MonoBehaviour.Update` 的按键轮询与一个 IMGUI 提示。

## 6. 兼容性风险

| 风险 | 表现 | 处理方式 |
|---|---|---|
| 游戏更新改变目标方法签名 | 补丁加载失败，功能失效，游戏正常运行 | 日志记录 `failed to patch WebUI bridge` |
| 游戏更新覆盖 `OutSetting.html` | 菜单入口消失 | 重新执行 `install-ui-patch.ps1` |
| 游戏更新改变 IL2CPP 元数据 | interop 程序集需重新生成 | BepInEx 自动处理 |
| 存档格式升级 | 新旧存档版本不一致，触发游戏自身的迁移逻辑 | 建议操作前备份 |

## 7. 验证方法

1. 反编译 `SLManualSave.dll`（约 23 KB）核对实现；
2. 查看 `BepInEx\LogOutput.log` 中的 `[SL Manual Save]` 记录；
3. 对比 `OutSetting.html` 与 `OutSetting.html.orig`，差异应仅限文件末尾的注入区块；
4. 执行 `tools\save_audit.ps1 -Action check`，校验索引与文件的一致性、内部文件名匹配及残留文件。
