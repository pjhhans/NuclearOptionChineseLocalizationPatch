# 架构设计

本文档描述本插件的内部结构与设计取舍，是开发与维护的规格说明。

## 1. 定位

Nuclear Option 是 Unity + TextMeshPro 的游戏，界面文本几乎全部走
`TMP_Text.set_text` / `UnityEngine.UI.Text.set_text`。本插件在**渲染前**把英文原文
换成中文译文，不改动游戏资源文件。

三条设计约束：

1. **不修改游戏文件** —— 只做运行期拦截，卸载即还原。
2. **数据与代码分离** —— 词表是可热重载的外部 JSON，改词不用重编译。
3. **失败必须可降级** —— 任何环节出错都退回原文，宁可显示英文也不显示乱码。

## 2. 数据契约

数据契约是本工程的对外接口，**代码围绕它实现，而不是反过来**。

### 2.1 词表 `data/`（布局 v2：10 个文件）

键按前两个字符分四类，**并按前缀分文件存放**：

| 前缀 | 语义 | 运行时行为 |
| --- | --- | --- |
| 无 | 普通词条，形如 `[Scope]原文` 或 `原文` | 清洗后忽略大小写精确查表 |
| `~` | **整段模板** | 键 = 归一化原文；值里的 `\u0001` 按序回填原文的富文本标签 |
| `>>` | **前缀片段**（战报拼接） | 整体以它开头 → `译文 + 递归(尾部)` |
| `<<` | **后缀片段** | 整体以它结尾 → `递归(头部) + 译文` |
| `==` | **中段片段** | `左 == 键 右` → `递归(左) + 译文 + 递归(右)` |

| 文件 | 内容 | 当前条数 |
| --- | --- | --- |
| `translation.json` | 通用词条（无前缀键） | 3012 |
| `templates.json` | `~` | 129 |
| `fragments.json` | `>>` / `<<` / `==` | 61 |
| `scopes/*.json` | `[Scope]` 词条，按**语义域**分 7 个文件 | 2029 |

语义域 = `ui` 界面控件 / `units` 单位兵器 / `mission` 任务目标 / `hud` 座舱读数 /
`world` 地图战报 / `editor` 编辑器与多人 / `misc` 杂项（任务描述、摄像机、大厅等零散控件）。
**文件顺序即加载顺序**
（`translation` → `templates` → `fragments` → `scopes/*` 按名排序），
反向索引「先到先得」；顺序会决定同一个中文归给谁。

`[Scope]` 是可选前缀，用于区分同名不同义的原文（如 `[ActionReportText]Aborted Landing`）。
带作用域的键只有在该作用域下才命中；无作用域的键**全局命中**
（`LookupScoped` 未命中 → `LookupGlobal`）。

**读宽松、写确定**。加载器**按前缀分流，不关心键放在哪个文件** ——
`~`/`>>`/`<<`/`==` 归模板或片段，`[Scope]` 归该作用域，其余归通用词条。
于是历史布局、手改、局部迁移都不会坏（`scopes/TypeText.json` 那种「文件名即作用域名、
键不带前缀」的旧写法仍兼容）。写出时一律走 `tools/_table_layout.py::partition` ——
同一份数据永远产出同一套文件（幂等），`tools/split_table.py --check` 靠这一条做漂移检测。

**为什么按前缀分文件**（性能优先，不是照抄第三方包的 16 个主题文件）：前三类与运行时的
索引结构 **1:1 对应**（`_global` / `_templates` + 指纹 / 三张片段表），拆开后每个文件只喂
一种索引；最热的 `_global` 从 5000+ 缩到 3012 条，`LookupScoped` 的首次探测（每条要渲染的
文本都会走一次）落在几十~几百条的小表上。对方的 16 个主题文件是 XUnity.AutoTranslator
**按作用域懒加载**逼出来的形态 —— 我们一次性建索引，文件数量的意义只是启动时的打开次数。

**为什么按语义域分，而不是「一个作用域一个文件」**：Windows/NTFS **不区分大小写**，
而本仓库的作用域名里有 **6 组只差大小写**（`Text`/`text`、`Label`/`label`、`Title`/`title`、
`Header`/`header`、`CounterMeasureName`/`countermeasureName`、`SellLabel`/`sellLabel`），
另有带尾随空格的 `Text (TMP) `、`header ` —— 一个作用域一个文件会**直接丢数据**。
分成 7 个语义域文件后，作用域名只出现在**键的前缀**里，与文件名彻底解耦，
这两个坑一并消失。

**词表是唯一事实来源**：翻译逻辑只能根据上表实现，不得引入表外约定。

### 2.2 键清洗与归一化

两者**用途不同、规则不同**，混用会导致键分叉：

| 函数 | 用于 | 规则 |
| --- | --- | --- |
| `KeyScrubber.Scrub` | 词表键载入、普通查表 | 删零宽/BOM/LRM/RLM/`\r`；`\v`、`\t` → 空格 |
| `TextCanonicalizer.Canonicalize` | 模板键比对 | 标签 → `\u0001`；`\r\n`/`\r` → `\n`；`\v` → `\n`；`[ \t]+` 折叠；Trim |
| `TextCanonicalizer.Fingerprint` | 模板**回落**比对 | 在 `Canonicalize` 之上删掉全部 `\u0001`，再折叠 `[ \t]+`、Trim |

⚠️ **`Canonicalize` 里的 CR 那一步不能省**。词表键载入时会过 `Scrub`，而 `Scrub` 会
**删掉** `\r`；若归一化保留 `\r`，含 CRLF 的原文（`away.\r\nGrey = ...`）就会键分叉、
模板静默失效。统一成 `\n` 后两侧都不含 `\r`，差异消失。

**生成词表的脚本必须与 `Canonicalize` 逐字符一致。**

#### 为什么模板还需要「指纹回落」

教程弹窗正文里的 `<bind=X>` **由游戏在写入控件之前**就解析成三态：字形（`<sprite name="G">`）、
键位文本、或者**什么都不留**。于是进到插件手里的字符串，其**标签个数与词表键不一致** ——
带 3 个占位符的模板键，遇到只剩 1 个标签的实机字符串时逐字符比对必然失配。

症状极具误导性：**词条明明在表里、那一行却整行英文、日志干净、只留一条漏译记录**，
看起来像"缺词条"，其实是路径失效（补词条永远无效）。

`TryGetTemplate` 因此分两步：先精确匹配，失配后退到**去标签指纹**匹配
（`Fingerprint` 结果 ≥ 12 字符才参与；两个模板键指纹相同时整组置 `null` 作废 ——
无法判定用哪条译文时宁可漏翻也不翻错）。命中后仍走 `ExpandTemplate`，
译文里多余的占位符自动丢弃。运行期可在 `F11` 窗口与启动日志里看到
「模板指纹 N 条（回落命中 M）」，用来确认这条通路是否真的被用上。

#### 参数化模板 `{#}`

有一类文本除一个**变化的数字**外其余完全固定（如 `Requisition via Rank (3)`），
整串查不到、去数字后的指纹也各不相同。键与译文里用 `{#}` 表示「这里是一段变化的数字」：

- `TryGetTemplate` 的回落因此是**三级**：精确 → 指纹 → **参数化**（见 `LocalizationTable`）。
- 含 `{#}` 的键**不进**精确/指纹索引（真实文本里不会出现字面 `{#}`），走独立索引，互斥不打架。
- 匹配规则：`{#}` 至少吃一位数字（贪婪），`{#}` 之间的**固定段不得以数字开头**
  （否则前一个 `{#}` 贪婪吃位后，剩余数字会被误判进固定段）；整串必须被完整消耗。
- 命中后把捕获到的数字按顺序回填进译文的 `{#}`。
- 例：`~Requisition\n via Rank ({#})` → `申请需 {#} 级`。

### 2.3 排除名单 `data/exclusions.json`

```jsonc
{
  "scopes": ["countermeasureName", ...],  // 这些作用域整体不翻译
  "terms":  ["RCS", "NEZ", ...],          // 保持英文的术语
  "texts":  ["SPD 673", ...]              // 精确排除的整条文本
}
```

- `terms` 的放过条件是**严格**的：仅当文本就是该术语、或「术语 + 尾标点」、
  或「术语 + 分隔符 + 其余部分不含字母或含数字」时才整体放过。
  因此加了 `FS-3` 不会屏蔽 `FS-3 Ternion`（后者照常翻译）。
- `texts` 额外放过「以某项 + 空格开头」的读数（`SPD 673` 会连 `SPD 673 km/h` 一起放过）。

### 2.4 作用域分表 `data/scopes/*.json`

用于存放只在特定 UI 组件下才成立的键，避免污染全局表。

**作用域名取自键前缀**（`[ActionReportText]Aborted Landing` → 作用域 `ActionReportText`），
与**分表文件名解耦**：文件名只是**语义域**的归档单位（`ui` / `units` / `mission` / …，见 §2.1），
同一语义域里的键可以带任意作用域前缀，加载时一律按**前缀**归位。
历史写法「文件名即作用域名、键不带前缀」（如 `scopes/TypeText.json`）仍兼容，但新词条
一律写成带 `[Scope]` 前缀的形式。这样作用域名可自由使用大小写与尾随空格（Windows 文件名
不区分大小写，按作用域分文件会直接丢数据），分表也不必因作用域改名而重构。

## 3. 模块划分

```
src/
├─ LocalizationPlugin.cs        插件入口：启动编排、静态数据、热重载（§3.1）
├─ Configuration/
│   └─ ModSettings.cs           BepInEx ConfigEntry 封装
├─ Core/                        ── 零补丁依赖，可脱机单测（权威测试台只链接这一层）
│   ├─ KeyScrubber.cs           键清洗（§2.2）
│   ├─ TextCanonicalizer.cs     归一化（§2.2）
│   ├─ TokenPatterns.cs         正则集（数值/单位/型号/句式），热路径正则的唯一出处
│   ├─ ExclusionRules.cs        排除判定（§2.3）
│   ├─ LocalizationTable.cs     词表容器：载入、分类、三级回落查询
│   └─ TextLocalizer.cs         翻译流水线（§4；「最近命中」记录默认关，累计计数不受影响）
├─ Diagnostics/
│   ├─ Log.cs                   日志封装
│   ├─ MissLog.cs               漏译记录（默认不落盘；内存环形缓冲始终可用）
│   ├─ HarmonySelfTest.cs       自打补丁验证 Harmony 是否真生效
│   └─ RuntimeStatus.cs         运行期状态快照（供 F11 窗口显示）
├─ Patching/
│   ├─ TmpPatches.cs            TMP_Text 系列补丁
│   ├─ LegacyUiPatches.cs       UnityEngine.UI.Text 补丁
│   ├─ PatchHelpers.cs          补丁公共入口：查表、原地翻译、读回还原
│   └─ RewriteGuard.cs          防回写闪烁（§5.2）
├─ UiMods/                      ── 界面修整（几何/表格/滚动），只改观感不改翻译
│   ├─ RectGeom.cs              矩形几何工具 + RectPin 快照（无状态纯函数）
│   ├─ WidgetWheel.cs           滚轮轮询（描述滚动与下拉弹层共用）
│   ├─ WeaponInfoCardPatches.cs 信息卡：字段解析 + DisplayInfo Postfix
│   ├─ WeaponInfoCardTable.cs   信息卡参数区表格化（列几何注入 <pos>）
│   ├─ WeaponInfoCardStabilizer.cs 信息卡每帧几何稳定 + 描述滚动视图
│   ├─ PylonDropdownPatches.cs  挂架下拉：选项填充
│   ├─ PylonDropdownPopupPatches.cs 挂架下拉：弹层加宽 + 滚轮驱动装配
│   ├─ DropdownWheelDriver.cs   弹层滚轮 MonoBehaviour
│   └─ DropdownGeom.cs          下拉弹层几何（测量 / 滚动条方向 / 钳入画布）
├─ Hosting/
│   └─ PluginHost.cs            逐帧宿主：热键 / 兜底扫描 / 防回写驱动（§3.1）
├─ Ui/
│   └─ SettingsWindow.cs        设置与诊断窗口（F11 呼出，纯 IMGUI）
└─ Resources/
    ├─ PluginPaths.cs           数据文件路径解析
    └─ CjkFontProvider.cs       中文字体加载与 TMP 回退注册
```

**分层与依赖方向**（按变更原因分层，避免无关改动互相牵连）：

| 层 | 依赖 | 约束 |
| --- | --- | --- |
| `Core` | 仅 BCL + Newtonsoft | **零补丁依赖**，可脱离 Unity 单测 |
| `Diagnostics` | 仅 BCL | 日志与状态快照，任何层可写 |
| `Patching` | `Core` `Configuration` `Diagnostics` `Hosting` | 只做翻译链路的打点与还原 |
| `UiMods` | `Core` `Patching` `Diagnostics` | 界面几何/滚动，与翻译解耦 |
| `Hosting` | `Diagnostics` `Ui` | **叶子**：不反向引用 `Patching` |
| `Ui` | `Diagnostics` | F11 窗口 |
| `Resources` | 仅 BCL + Unity | 路径与字体 |

> **为什么 `Hosting` 是叶子**：宿主需要在每帧驱动「防回写补偿」，而防回写逻辑在 `Patching`。
> 若宿主直接 `using Patching`，就会形成 `Patching ⇄ Hosting` 双向依赖。改法是宿主只暴露
> `PluginHost.LateTick` 事件，由入口 `LocalizationPlugin` 单向接线（`LateTick += RewriteGuard.Tick`），
> 宿主对补丁层一无所知。

### 3.1 生命周期：为什么需要独立的逐帧宿主

**这是本工程最容易踩、也最隐蔽的坑，改动入口类前务必先读完本节。**

BepInEx 在「首个真实场景就绪**之前**」就加载插件（时序来自 `BepInEx_Manager` 的创建点）。
此时创建的一切 GameObject —— **包括 BepInEx 自己的管理器对象** —— 都会在第一个真实场景
加载时被 Unity 一并销毁，**即使调用过 `DontDestroyOnLoad`**。

后果极具误导性：

| 现象 | 原因 |
|---|---|
| BepInEx 日志显示插件加载成功、初始化日志齐全 | 它们都在 `Awake` 里执行，`Awake` 确实跑过了 |
| `SceneManager.sceneLoaded` 照常触发 | 它是**静态**事件，委托已捕获实例；在被销毁的实例上调用只用静态 Unity API 的方法仍然可行 |
| **`Update` 永不执行** → 热键无反应、兜底扫描停摆、防回写失效 | 承载 `Update` 的组件已被销毁 |
| 日志里没有任何异常 | 销毁是 Unity 的正常行为，不产生错误 |

**对策是三层：**

1. **入口类不自持逐帧逻辑。** `LocalizationPlugin` 只做启动编排，绝不写 `Update`。
2. **独立的延迟宿主 [`PluginHost`]。** 它由 `Ensure()` 创建，被销毁后能重建；
   重建触发点三处互为备份：
   - `Awake` 末尾试建一次（覆盖"场景已就绪才加载插件"的情形）；
   - **每次 `sceneLoaded`（`force: true`，跳过防抖）** —— 主要的复活点；
   - `PatchHelpers.TryLocalize` 的看门狗 —— 翻译命中的必经之路，也是最后一道机会。
3. **状态一律放静态属性。** 宿主会被销毁重建，任何存在它字段里的东西都会丢；
   词表 / 排除名单 / 局部化器全部挂在 `LocalizationPlugin` 的静态属性上。

**两条别加回来的"优化"：**

- **不要在 `OnDestroy` 里退订 `SceneManager.sceneLoaded`。** 入口对象会在首个真实场景
  加载时被销毁，一退订，此后所有场景加载事件都收不到 —— 而那正是宿主最重要的重建点。
  订阅的是静态方法，不依赖实例存活。
- **不要在 `OnDestroy` 里 `UnpatchSelf`。** 一执行就等于在游戏刚起来时把整条翻译链路拆掉，
  且日志上看不出任何异常。进程退出时 Unity 会连同补丁一起回收，无需手动卸载。

**存活判定必须走 `UnityEngine.Object` 重载的 `==`。** 只用 `ReferenceEquals` 的话，
「Unity 侧已销毁、而 `OnDestroy` 恰好没跑到」的实例会被永远判成"存在"，宿主再也建不起来 ——
就是上表那个故障的变体。该运算符同时覆盖 null / 存活 / 已销毁，且只做一次原生指针比较。

## 4. 翻译流水线

`TextLocalizer.Localize(text, scope)` 按下列顺序判定，**每一步都可能直接返回**：

```
0. 空串                    → 原样返回
1. 作用域在排除名单        → 原样返回
2. 命中 texts 排除 / 保留术语 → 原样返回
3. 命中 ~ 整段模板         → 展开模板返回
4. 原文已含中文且非多行     → 原样返回（幂等保护，防止二次翻译）
5. 原文不含 ASCII 字母      → 原样返回
6. 多行文本                → 逐行递归，全部行都翻出才算成功
7. 单行 → 进入 §4.1 三级判定
```

### 4.1 单行三级判定

**A 级 —— 整串精确查表**
剥掉全部富文本标签后清洗、Trim，查表；命中即返回。

**B 级 —— 句式与片段**
- 先试句式模式：`Booting …` / `Buy …` / `… set to …` / `Cleared to taxi to …` /
  `… Turret under pilot control`。这类句子的主干是固定词，变量是尾部的型号名，
  整串查表查不到，需要把主干取出来单独翻译再拼回。
- 再试片段拼接（§2.1 的 `>>`/`<<`/`==`）。**必须在分隔符切分之前**，
  否则固定短语会被 `:` `/` 切开，永远拼不上。

**C 级 —— 分隔符切分**
按 `DelimiterRegex` 把整行切成若干段，逐段处理：
- 单字符分隔符、纯噪声段 → 跳过
- 富文本标签块（`<...>`）→ 剥壳后对**内部正文**递归翻译，再原样填回
- 已含中文 / 保留术语 / 噪声 → 跳过
- 其余 → 先查表，再跑数值尾模式（§4.2）

**全部段落都"已解决"**（出中文、是噪声、或保持英文的术语）才算这一行翻好了。

### 4.2 数值尾模式表

HUD 读数每帧变化（`SPD 673`、`CAPACITOR 12 kJ`、`Rank 3`），整串永远匹配不上。
处理方式是**剥离尾部数值，翻译剩下的词，再把数值原样拼回**：

| 模式 | 例子 | 处理 |
| --- | --- | --- |
| `EndValueUnit` | `SPD 673 km/h` | 译 `SPD`，保留 `673 km/h` |
| `WordNumber` | `Rank 3` | 译 `Rank`，保留 `3` |
| `NumberUnitOnly` | `12 kJ` | 译单位 `kJ` |
| `EndQuantity` | `Heater x4` | 译 `Heater`，保留 `x4` |
| `EndRunway` | `Runway 09` | 译 `Runway`，保留 `09` |
| `EndPlusNumber` | `Rearmed +100%` | 前缀递归翻译，保留 `+100%` |
| `VersionFilter` | `Version 1.2.3` | 译前缀，保留版本号 |
| `ScoreNumber` | `Score 42` | 译 `Score` |

⚠️ **不要为这类读数补整串键**。整串键会在 A 级抢先命中，把 `Rank 0`~`Rank 6`
全部锁死成同一句、丢掉数字。这是本工程明确避免的陷阱。

### 4.3 噪声判定

`IsNoise` 用于识别"本来就不该翻译"的片段，避免把它计为漏译：

纯数值/数值+单位、`x4`、`CAPACITOR 12 kJ`、`150K TNT`、`AB12`（坐标）、
纯符号、`250 m <`（距离指示）。

## 5. 补丁层

### 5.1 打点

| 目标 | 时机 | 原因 |
| --- | --- | --- |
| `TMP_Text.set_text` | Prefix | 主入口，绝大多数 UI 文本经此 |
| `TMP_Text.SetText(string,bool)` | Prefix | 部分代码绕过属性直接调方法 |
| `TMP_Text.get_text` | Postfix | 游戏读回自身文本时还原成英文，避免中文被再次加工 |
| `TMP_Text.ParseInputText` | Prefix | TMP 内部解析入口，兜住 set_text 之外的路径 |
| `TextMeshProUGUI.OnEnable` / `TextMeshPro.OnEnable` | Postfix | 场景加载时文本被直接赋值，不经过 setter |
| `TextMeshProUGUI.OnPreRenderCanvas` / `TextMeshPro.OnPreRenderObject` | Prefix | 渲染前最后一次拦截机会 |
| `UI.Text.set_text` / `OnEnable` | Prefix/Postfix | 少数遗留 UI 仍用旧组件 |

### 5.2 防回写闪烁

游戏每帧可能把它自己的英文重新写回组件，与我们的中文来回拉锯，表现为闪烁。
两层防御：

1. **读回还原**：`get_text` 返回我们写下的中文；若游戏读到中文又写回来，
   流水线的"已含中文短路"（§4 第 4 步）会直接放过，不产生循环。
2. **`RewriteGuard`**：记录每个组件的原文/中文对应关系，检测"同一组件在
   短时间窗口内被回写超过阈值"的情况。超阈值即判定该组件不可控，
   **放弃补译**（GiveUp），避免无限拉锯。

`RewriteGuard` 的阈值策略：窗口内补回次数 ≤ 5 次/秒；总跟踪条目上限 4000
（超出则淘汰最旧），防止长时间游玩导致内存增长。

### 5.3 TMP 字体图集

中文常用字数千，TMP 默认 1024×1024 图集只放得下约百个 90px 字形。图集放满后
TMP 会重建并重新上传整张纹理，表现为周期性卡顿。因此使用
**2048×2048 + 多图集**，并在每次场景加载后把中文字体登记进
`TMP_Settings.fallbackFontAssets`。

## 6. 构建与部署

`csproj` 的 `GameDir` 采用四级解析，取第一个成立的：

1. `-p:GameDir="..."` 命令行参数
2. 环境变量 `NUCLEAR_OPTION_DIR`
3. 仓库根目录的 `GameDir.props`（`.gitignore` 忽略，供本机覆盖）
4. `$(MSBuildThisFileDirectory)` 的上两级 —— 仓库位于 `<游戏>/Git/<repo>` 时零配置生效

`ValidateGameDir` 挂在 `BeforeBuild`。挂在 `ResolveReferences` 会被先前产生的
MSB3245 警告刷屏淹没。

两个部署目标语义不同：

| 命令 | 行为 |
| --- | --- |
| `dotnet build -c Release` | 编译 + 推 DLL；数据文件仅在**目标不存在**时补（保用户热改进度） |
| `dotnet build -c Release -t:Rebuild` | 强制全量重编译 |
| `dotnet build -c Release -t:DeployData` | 全量覆盖同步数据文件 |

⚠️ **`DeployData` 不触发编译**。它的依赖链只拉起 `ValidateGameDir` 与部署目标，
不经过 `CoreCompile`，复制的是 `bin/` 里的**上一次**产物。改了 C# 必须：

```bash
dotnet build -c Release -t:Rebuild      # 先真正编译
dotnet build -c Release -t:DeployData   # 再同步数据
```

判断编译是否真发生，看控制台有没有 `<项目名> -> ...\x.dll` 那一行、耗时是否 > 2 秒。

**改 `.cs` 需重启游戏；只改 `.json` 按 F11 热重载。**

## 7. 授权

见 `README.md` 的「授权与来源」一节。
