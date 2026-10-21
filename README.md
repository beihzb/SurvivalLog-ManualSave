# Survival Log ManualSave

**摘要**：本项目为《Survival Log》提供手动存档槽功能。插件在游戏暂停菜单中新增“保存游戏”入口，允许将当前进度另存为独立存档文件，并为其指定显示名称。项目不修改任何既有存档、游戏二进制文件或资源文件。

**Abstract**: An unofficial BepInEx 6 plugin for *Survival Log* that provides manual save slots. It creates an independent copy of the current save, assigns a user-defined display name, and registers it with the game's own save index. No existing save data, game binaries, or assets are modified.

---

## 1. 项目范围

### 1.1 背景

游戏原生存档机制包含三类行为：按固定间隔的自动存档、退出时的补充存档、以及跨越游戏内日期时写入的日快照（`Save_*.snapN.bytes`，用于重生回溯）。运行期间不提供手动存档入口；读档操作被限制在主菜单的读取列表中执行。

### 1.2 提供的能力

| 能力 | 说明 |
|---|---|
| 手动存档槽 | 将当前进度写为独立存档文件，不中断当前游戏会话 |
| 自定义显示名称 | 名称作为存档条目属性保存，在读取列表中替代默认角色名显示 |
| 存档列表集成 | 通过游戏自身的索引接口注册条目，与原生存档并列展示 |
| 快捷操作 | `F5` 以默认名称创建存档槽；`F9` 输出存档状态至日志 |

### 1.3 非目标

本项目明确**不**提供以下能力，相关限制源于游戏设计或技术边界：

- 不提供游戏运行期间的读档功能（游戏在运行期禁止加载存档）；
- 不修改生命值、金钱、物品数量等运行期数值；
- 不修改成就判定、成就状态或 Steam 统计数据；
- 不绕过游戏的存档完整性校验与数据来源校验。

---

## 2. 兼容性

| 组件 | 已验证版本 |
|---|---|
| 游戏 | Steam 版《Survival Log》，构建 1.1.19131 |
| Unity 运行时 | 6000.2.7f2 |
| IL2CPP 元数据 | v31.1 |
| 依赖加载器 | BepInEx 6.0.0-be.789（Unity.IL2CPP，win-x64） |
| 编译环境 | .NET 6 SDK |
| 操作系统 | Windows 11（x64） |

以上为唯一经过验证的组合。其他游戏版本、加载器版本或运行环境下未经验证，不承诺兼容性。游戏更新后，若相关方法签名发生变化，插件可能无法加载；此情形不影响游戏本体运行，详见第 8 节。

---

## 3. 实现概要

插件不重新实现存档序列化，而是调用游戏自身的公开接口完成写入与索引维护：

1. 调用 `GameSaveManager.SaveGame()` 将当前世界状态写入当前存档文件；
2. 调用 `GameSaveManager.GenerateNewSaveFileName()` 生成新存档文件名；
3. 复制当前存档文件，并原位改写副本内部的 `FileName` 字段（两种名称长度一致，为定长替换）；
4. 调用 `GameSaveManager.AddHistoryEntry()` 与 `PersistHistoryData()` 将新条目写入索引；
5. 界面元素通过游戏内置 WebView 消息桥发送 `SAVE_MANUAL` 消息，插件在 `WebUILayer.OnMessageFromJS` 的前置补丁中拦截并处理；
6. 读取列表中的显示名称与时间字段由 `Reducer_Web_MainUI.FillSaveInfoFromHistory` 与 `WebUI_MainUI.UpdateListView_Save` 的后置/前置补丁写入。

完整的挂钩点、调用接口、文件读写范围与失败处理逻辑见 [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md)。

---

## 4. 安装

安装过程不依赖任何脚本，仅包含两项操作：将 `SLManualSave.dll` 复制到插件目录；可选地向一个 HTML 文件追加一段代码。仓库中的脚本仅用于自动化重复操作，不是安装的必要条件。

### 4.1 前置条件

- 已安装 **BepInEx 6（IL2CPP，win-x64）** 至游戏根目录；
- 已至少启动一次游戏，使 `BepInEx\interop\` 生成完成（该过程通常需要数十秒）。

BepInEx 为独立前置依赖：本仓库与发布压缩包均不包含 BepInEx 的二进制文件，需由使用者自行安装。

### 4.2 手动安装（默认方式）

**步骤 1：部署插件**

将 `dist\SLManualSave.dll` 复制到以下路径（`plugins` 目录不存在时自行创建）：

```
<游戏目录>\BepInEx\plugins\SLManualSave.dll
```

**步骤 2（可选）：添加暂停菜单入口**

省略此步骤时，插件仍可通过 `F5` 创建存档槽，仅缺少菜单入口。

1. 备份 `SurvivalLog_Data\StreamingAssets\WebUI\UI\OutSetting\OutSetting.html`；
2. 以文本编辑器打开该文件；
3. 将 `ui-patch\save_button.html` 的全部内容插入到文件中**最后一个** `</body>` 标签之前；
4. 以 **UTF-8（无 BOM）** 编码保存。

该文件本身为 UTF-8 无 BOM 编码；保存时应确认编码一致，否则界面文字可能显示异常。

**步骤 3（可选）：校验文件完整性**

`dist\SLManualSave.dll` 的 SHA-256：

```
8E560743352C7E2DB202946D8B3FC83DB417BF64688145E5B0EF54EB21BE606E
```

### 4.3 脚本安装（可选）

脚本适用于重复部署或需要还原界面补丁的场景：

```powershell
# 编译并部署插件
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -GameDir "X:\...\Survival Log"

# 注入暂停菜单入口（追加注入区块并备份原文件）
powershell -ExecutionPolicy Bypass -File scripts\install-ui-patch.ps1 -GameDir "X:\...\Survival Log"
```

- `build.ps1` 编译 `src\SLManualSave` 并将产物复制到 `BepInEx\plugins\`；
- `install-ui-patch.ps1` 追加界面区块，首次执行时生成 `OutSetting.html.orig`，具备幂等性（重复执行仅替换自身注入的区块）；
- 若已设置环境变量 `SURVIVALLOG_DIR`，可省略 `-GameDir` 参数。

### 4.4 界面补丁的维护

游戏更新会覆盖 `OutSetting.html`，导致菜单入口消失。更新后按 4.2 步骤 2 重新插入，或执行 `install-ui-patch.ps1`。插件本体与快捷键不受影响。

---

## 5. 使用

| 操作 | 行为 | 约束 |
|---|---|---|
| 暂停菜单 →「保存游戏」 | 打开命名对话框，确认后创建存档槽 | 仅在游戏局内可用 |
| `F5` | 以当前角色名作为显示名创建存档槽 | 同上 |
| `F9` | 将当前存档文件名、存档数量与列表内容写入日志 | 仅用于诊断 |

自定义名称仅作为**显示名**保存。存档文件名必须保持游戏的时间戳格式（`Save_yyyy_MM_dd_HH_mm_ss.bytes`），因为读档、排序与版本迁移均依赖该格式。

---

## 6. 数据与文件位置

| 路径 | 读/写 | 说明 |
|---|---|---|
| `Saves\Save_*.bytes`（既有存档） | 读 | 仅作为复制源 |
| `Saves\Save_<新时间戳>.bytes` | 写 | 新建存档槽；采用 `.tmp` → 重命名方式提交 |
| `Saves\HistorySave.bytes` | 写 | 经游戏自身的 `PersistHistoryData()` 写入 |
| `BepInEx\plugins\SLManualSave\save_names.txt` | 写 | 插件侧的显示名映射（UTF-8，`文件名<TAB>Base64(名称)`） |
| `BepInEx\LogOutput.log` | 写 | 运行日志 |

插件不删除任何游戏文件；仅清理自身产生的临时文件。

---

## 7. 安全性与数据完整性

| 方面 | 措施 |
|---|---|
| 写入原子性 | 新存档先写入 `.tmp`，完成内部字段改写后再重命名提交，避免产生不完整文件 |
| 提前校验 | 写入前依次检查 `IsInBattle`、`IsWorldSafeToPersist()`、`GetGameSaveData().IsSubGame`，任一不满足即中止 |
| 落盘确认 | 调用 `SaveGame()` 后轮询文件修改时间，最多等待 2 秒确认写入完成 |
| 异常隔离 | 所有入口包裹异常捕获；失败仅记录日志与界面提示，不中断游戏 |
| 索引一致性 | 先落文件、后写索引；文件写入失败时不会产生孤立索引条目 |
| 既有数据 | 不修改任何既有存档内容；复制为逐字节操作 |

建议在首次使用前完整备份 `Saves` 目录。仓库附带的 `tools/save_audit.ps1` 可用于校验存档目录一致性：

```powershell
powershell -ExecutionPolicy Bypass -File tools\save_audit.ps1 -Action check
```

该脚本检查索引与文件的双向对应、内部文件名一致性、残留临时文件与重复内容，并输出相对基线的变更清单。

---

## 8. 已知限制

1. **运行期不可读档**：读档仍需返回主菜单并从列表中选择，此限制由游戏实现决定；
2. **文件名不可自定义**：自定义名称仅影响显示；文件命名规则由游戏的索引与迁移逻辑约束；
3. **更新敏感**：游戏更新可能覆盖界面补丁，或改变插件挂钩的方法签名；后者会导致插件加载失败，游戏本体不受影响；
4. **云存档开销**：每个存档槽约占 600 KB，并随 Steam 自动云同步上传，建议定期清理；
5. **仅支持单一存档目录**：插件不处理多账户或多存档目录场景。

---

## 9. 诊断

日志位置：`<游戏目录>\BepInEx\LogOutput.log`，筛选关键字 `SL Manual Save`。

| 现象 | 可能原因 | 处理 |
|---|---|---|
| 菜单中无入口 | 界面补丁缺失或被覆盖 | 按 4.2 步骤 2 重新插入，或执行 `install-ui-patch.ps1` |
| 入口存在但无响应 | 插件未加载 | 检查日志是否包含插件加载记录 |
| 命名输入框无法输入 | 键盘焦点未转交 | 日志应出现 `text input mode = True/False`，据此定位 |
| 存档失败 | 前置校验未通过或文件写入失败 | 查看 `checkpoint failed at step '...'`，`step` 字段标识失败环节 |

---

## 10. 卸载

以下两项须同时执行，否则菜单入口会残留为无响应状态：

1. 删除 `<游戏目录>\BepInEx\plugins\SLManualSave.dll`；
2. 还原菜单页面：将 `OutSetting.html.orig` 复制回 `OutSetting.html`（若安装时使用脚本，亦可执行 `install-ui-patch.ps1 -Restore`）。

彻底移除 BepInEx：删除 `BepInEx\`、`dotnet\`、`winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`。

---

## 11. 从源码构建

```powershell
$env:SURVIVALLOG_DIR = "X:\...\Survival Log"
dotnet build src\SLManualSave\SLManualSave.csproj -c Release
```

编译产物位于 `src\SLManualSave\bin\Release\SLManualSave.dll`。项目引用的游戏程序集由 BepInEx 在首次启动时生成，均以 `Private=false` 引用，不随插件分发。

---

## 12. 版本

| 版本 | 说明 |
|---|---|
| 1.0.0 | 手动存档槽、自定义显示名、列表名称与时间字段覆盖、写入前置校验、原子写入、存档审计工具 |

---

## 13. 许可与免责声明

本项目以 MIT 许可证发布，详见 [LICENSE](LICENSE)。

本项目为非官方社区修改，与游戏开发商及发行商无关联，不包含任何游戏资源文件。修改类工具可能受游戏最终用户许可协议或平台条款约束，使用者应自行确认合规性并承担相应风险。如权利方提出异议，本项目将立即停止分发。
