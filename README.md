# Nuclear Option 简体中文汉化补丁

Nuclear Option 的**非官方**简体中文本地化插件，基于 BepInEx 5。

它在**运行期**把界面文本替换为中文，**不修改任何游戏本体文件**

> 本项目由玩家社区维护，与 Nuclear Option 的开发者无关；游戏内容与原文版权归其开发商所有。

---

|            |                                                   |
| ---------- | ------------------------------------------------- |
| **适配游戏版本** | 0.34.2                                            |
| **当前插件版本** | v1.5.4                                            |
| **词表规模**   | 4850 条（普通 4665 / 整段模板 127 / 拼接片段 58）              |
| **翻译覆盖**   | 座舱 HUD、任务编辑器、设置菜单、教程弹窗、机场与航路点名称、载具与武器描述、击杀战报与聊天信息 |
| **安装方式**   | 解压到 `BepInEx/plugins/`                            |
| **前提**     | 已安装 BepInEx 5（x64）                                |

词表中还包含**一部分作者自用第三方 mod** 的装备与单位翻译。原版玩家用不到这些词条，

- **数据与代码分离。** 译文是一份可读的 JSON，改完在游戏里按 `F11` 并重载即可生效，不必重启游戏。
- **自带诊断窗口。** `F11` 呼出设置与诊断窗口。

---

## 翻译质量声明（请务必读）

**目前所有译文均由 AI 翻译，作者正在实际游玩中逐批校对和修正。**

- 大部分文本能看懂，但**措辞可能生硬、术语可能不统一、个别句子可能翻错**；
- 译名口径（型号 + 汉化代号、缩写保持英文等）在逐步统一，前后批次可能有差异；
- 游戏更新会改动原文措辞，导致一部分词条**静默失效**（表现为个别句子仍是英文）。

**欢迎反馈。** 在游戏中按 `F11` 打开诊断窗口，「最近漏译」列表 + 插件目录下的  
`missing.json`（片段）与 `untranslated.json`（整段）就是漏翻工作清单；提交 issue 时  
附上这两份文件（或截图）最有效。词表是纯 JSON，也直接接受译文 PR。

### 目前可能的问题

按出现概率排列：

1. **个别句子仍是英文** —— 游戏更新改了原文，或该文本走了未覆盖的通路。这是常态，逐批修。
2. **译文措辞生硬或术语不统一** —— AI 翻译的历史遗留，实机游玩中逐步替换。
3. **极少数界面文本长度变化导致的显示溢出** —— 中文通常比英文短，但个别短槽位标签  
   （座舱 HUD 的 2 字槽位）译文过长会显示不全，发现一个修一个。
4. **第一次显示某个新汉字时轻微卡顿** —— 字体图集现场栅格化，一次性开销，之后不再发生。
5. **游戏更新后插件可能整体失效** —— 补丁目标方法被改名/移除时，对应补丁装不上，需要等适配。

---

## ⚠️ 仅限单人游戏

**本插件只在单人游戏下经过验证。多人游戏（官方服务器、自建服务器、局域网联机）存在  
不可预见的风险，请自行判断后再决定是否使用。**

> *没测过，而且本插件会改写游戏「读回」的文本，这在联机路径上可能出现法预见的行为。*

需要留意的三点：

- **未做多人测试** —— 所有验证都在单人环境完成。未测试不等于已证明安全。
- **注入式 DLL 与服务器规则** —— 部分服务器（尤其竞技 / 排位性质）把客户端注入视为  
  作弊或违规。**在要求客户端为原版的服务器上不要使用。**
- **联机前请先移除** `BepInEx/plugins/NuclearOptionChineseLocalizationPatch/` 整个目录。

完整风险分析（六条，按确定到不确定排列）见 [`docs/RISKS.md`](docs/RISKS.md)。

---

## 性能影响

**结论：开销很小，但不是零。** 默认配置下基本感觉不到，三处需要知道：

- **显存** —— 中文字体图集是主要占用，估算 **24–40 MB**。
- **每次文本赋值**都会走一遍翻译流水线，但绝大多数请求命中缓存或提前短路返回；  
  只有**第一次**遇到某条文本才会走完整流程。
- **兜底扫描** —— 它每隔一段时间遍历场景中所有文本组件，是可调节的周期开销（但是感觉开着也没啥用）。  
  需要在 F11 窗口里开启，间隔 0.1–5 秒实时可调；关闭它不影响翻译主体，  
  只影响「极少数一直保持英文的文本」。

> 以上结论来自代码路径分析，**不是 benchmark**，作者未做过帧时间实测。  
> 完整成本模型、显存估算依据与自行测量的方法见 [`docs/RISKS.md`](docs/RISKS.md)。

---

## 安装

1. 安装 [BepInEx 5](https://github.com/BepInEx/BepInEx/releases)（**x64**）到游戏根目录，  
   先启动一次游戏以生成 `BepInEx/plugins/`。
2. 把本插件的目录整个放进 `BepInEx/plugins/`（目录内容见下节「文件结构」）。
3. 启动游戏。

> ⚠️ **不要与其他汉化插件同时启用。** 多个插件会同时挂钩同一个文本组件，行为无法预测。  
> 安装前请先移除旧的中文插件目录。

> ⚠️ **升级插件版本时必须重启游戏，而不是重载词表。** 插件本体（DLL）只在启动时加载，  
> 重载只重读 JSON 数据。

## 使用

按 `F11` 呼出「设置与诊断」窗口：

| 区块 | 内容                                                    |
| -- | ----------------------------------------------------- |
| 操作 | 开关翻译（关闭会把屏上中文还原成英文）／重新载入词表／立即扫描场景／清空缓存／清空漏译记录         |
| 状态 | 逐帧宿主是否就绪、词表与排除名单条数、最近一次载入结果、场景与扫描次数、命中/漏译/缓存统计、防回写跟踪数 |
| 调节 | 兜底扫描开关（**默认关**）与间隔、是否记录最近命中、是否累积漏译、是否输出调试日志           |
| 诊断 | **最近命中**（原文 → 译文）与**最近漏译**（`<作用域>片段`）各 12 条           |

## 配置

首次启动后生成 `BepInEx/config/com.nuclearoption.zhcn.localization.cfg`。  
**配置只在启动时读取**，热重载词表不会重读配置。

| 分区          | 项                       | 默认      | 说明                                             |
| ----------- | ----------------------- | ------- | ---------------------------------------------- |
| General     | `Enabled`               | `true`  | 关闭后会把屏上已有的中文还原成原文                              |
| General     | `ToggleWindowHotkey`    | `F11`   | 呼出设置窗口的热键                                      |
| General     | `ReloadDataHotkey`      | 空       | 不打开窗口、直接热重载词表的热键。留空表示只用窗口按钮                    |
| Diagnostics | `VerboseLogging`        | `false` | 调试级日志。翻译在渲染路径上被高频调用，仅排障时开                      |
| Diagnostics | `LogMisses`             | `true`  | 把未翻译文本累积到 `missing.json` / `untranslated.json` |
| Performance | `ScanIntervalSeconds`   | `0`     | 兜底扫描间隔（秒）。`0` = **关闭兜底扫描**（默认）；> 0 时作为窗口滑块的初值  |
| Performance | `TranslationCacheLimit` | `20000` | 翻译结果缓存条目上限。`0` 表示禁用缓存（不推荐）                     |

---

## 文件结构

### 安装后：插件目录

`<游戏>/BepInEx/plugins/NuclearOptionChineseLocalizationPatch/`

```
├─ NuclearOptionChineseLocalizationPatch.dll   插件本体
├─ Newtonsoft.Json.dll                         JSON 解析依赖
├─ translation.json                            词表
├─ exclusions.json                             不翻译名单
├─ force_scopes.json                           强制作用域名单
├─ font.ttf                                    中文字体
├─ scopes/                                     分作用域词表
│   └─ TypeText.json
├─ missing.json                                ← 运行期生成（可随时删）
└─ untranslated.json                           ← 运行期生成（可随时删）
```

### 仓库结构

```
├─ src/                              插件源码（约 3100 行）
│   ├─ LocalizationPlugin.cs             入口：启动编排、配置、重载、开关
│   ├─ Core/                             翻译核心（与 Unity 无关，可单独测试）
│   │   ├─ TextLocalizer.cs                  流水线：A 整串 → B 句式 → B2 片段 → C 切片
│   │   ├─ LocalizationTable.cs              词表索引（普通 / 模板 / 片段 / 作用域）
│   │   ├─ ExclusionRules.cs                 不翻译名单
│   │   ├─ TextCanonicalizer.cs              模板键归一化
│   │   ├─ KeyScrubber.cs                    键清洗
│   │   └─ TokenPatterns.cs                  数值尾模式与噪声判定
│   ├─ Patching/                         Harmony 补丁与防回写
│   │   ├─ TmpPatches.cs                     TMP 打点（主战场）
│   │   ├─ LegacyUiPatches.cs                旧版 UI.Text 打点
│   │   ├─ PatchHelpers.cs                   补丁层公共入口
│   │   └─ RewriteGuard.cs                   防回写守护
│   ├─ Resources/                        宿主、路径、字体、设置窗口
│   ├─ Configuration/                    配置项定义
│   └─ Diagnostics/                      日志、漏译记录、自检
├─ data/                             词表与资源
│   ├─ translation.json                  词表（唯一事实来源）
│   ├─ exclusions.json                   不翻译名单
│   ├─ force_scopes.json                 强制作用域名单
│   ├─ scopes/                           分作用域词表
│   └─ fonts/font.ttf                    中文字体
├─ docs/
│   ├─ ARCHITECTURE.md               架构、数据契约与实现理由
│   └─ RISKS.md                      性能与多人游戏风险详述
├─ tools/
│   ├─ check_data.py                 词表自检（编码 / 键唯一性 / 键序 / 括号规范）
│   └─ review/                       翻译审核台（本机可视化校对工具，见其 README）
├─ README.md
├─ LICENSE                           MIT 许可
├─ THIRD-PARTY-NOTICES.md            第三方组件声明（字体 / 依赖 / 游戏原文）
└─ NuclearOptionChineseLocalizationPatch.csproj
```

### 运行时生成的文件

| 文件                                        | 位置                | 说明                               |
| ----------------------------------------- | ----------------- | -------------------------------- |
| `missing.json`                            | 插件目录              | 未翻译的**片段**（切片后的最小可翻译单元），补词表的工作清单 |
| `untranslated.json`                       | 插件目录              | 未翻译的**整段长文本**（≥ 80 字符）           |
| `LogOutput.log`                           | `BepInEx/`        | 启动与自检信息、补丁安装结果                   |
| `com.nuclearoption.zhcn.localization.cfg` | `BepInEx/config/` | 配置文件                             |

两个诊断文件**可以随时删除**，需要时会自动重建。

### 数据格式

词表是扁平的 `{ "原文": "译文" }`。除普通词条外还有五类特殊键：

| 前缀        | 用途                | 例子                            |
| --------- | ----------------- | ----------------------------- |
| `[Scope]` | 限定作用域，区分同名不同义的原文  | `[KillFeed]sank`              |
| `~`       | 整段模板（教程弹窗、含标签的长句） | `~<b>Return</b>\nAlkyon AB-4` |
| `>>`      | 拼接前缀片段            | `>>Warning : `                |
| `<<`      | 拼接后缀片段            | `<< sank`                     |
| `==`      | 拼接中段片段            | `== deployed at `             |

片段键的长度与空格**都是语义的一部分**，写错一个空格就永不命中。完整规则（归一化顺序、  
片段拼接语义、数值尾模式）见 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)。

---

## 从源码构建

需要 .NET SDK。仓库放在 `<游戏>/Git/<本仓库>` 时**零配置**即可构建：

```bash
dotnet build -c Release                 # 编译并部署 DLL
dotnet build -c Release -t:DeployData   # 全量同步数据文件到插件目录
```

游戏不在默认位置时，用 `-p:GameDir="D:\Games\Nuclear Option"`、设置环境变量  
`NUCLEAR_OPTION_DIR`，或复制 `GameDir.props.example` 为 `GameDir.props` 填入路径。

改完词表后跑一遍数据自检：

```bash
python tools/check_data.py
```

需要校对词表时，用自带的审核台：

```bash
python tools/review/review_server.py
```

## 反馈与贡献

- **报告漏翻**：附上 `missing.json` / `untranslated.json` 或 `F11` 窗口截图。
- **提交译文**：直接 PR 修改 `data/translation.json`；规模较大时先开 issue 说明口径。
- **改完词表**：跑 `python tools/check_data.py`，它会把编码、键唯一性、键序、  
  括号规范等问题一次列出。

## 授权

本项目采用 **MIT 许可**，全文见 [`LICENSE`](LICENSE)。

**在 MIT 之下，你可以自由地使用、修改、再分发本项目（包括商业用途），只需保留版权声明。**

各部分的权利归属：

| 组成                    | 许可              | 权利方                |
| --------------------- | --------------- | ------------------ |
| 代码、工具、文档              | MIT             | 本项目                |
| `data/` 下的译文          | MIT             | 本项目译者              |
| `data/fonts/font.ttf` | **SIL OFL 1.1** | Adobe（思源黑体 CN）     |
| `Newtonsoft.Json.dll` | **MIT**         | James Newton-King  |
| 游戏原文（词表的键）            | 不在本项目可授权范围内     | Nuclear Option 开发商 |

> 本项目与 Nuclear Option 的开发方无任何关联，亦未获其授权或背书。

> 词表中有部分词条可追溯至**前身项目**（HunterCHCL），该项目未附许可证。  
> 对本项目而言这部分权利不在自己手中，故单独作出声明，详见  
> [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) 第 6 节。

第三方组件的完整声明（含许可全文）见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

## 致谢

- [HunterCHCL](https://github.com/HunterCHCL) —— 前身汉化项目，本项目的翻译工作由此起步。
- [9138noms](https://github.com/9138noms) —— Nuclear Option 本地化工具链与游戏字符串清单，  
  帮助发现了运行期采集覆盖不到的文本。
