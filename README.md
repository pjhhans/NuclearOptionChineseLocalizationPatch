# Nuclear Option 简体中文汉化补丁

Nuclear Option 的非官方简体中文本地化插件。基于 BepInEx 5，在运行期把界面文本替换为
中文，**不改动任何游戏文件**，卸载即完全还原。

词表 4400+ 条，覆盖座舱 HUD、任务编辑器、设置菜单、教程弹窗、机场与航路点名称、
载具与武器描述、多人游戏界面等。

> **非官方**。本插件由玩家社区维护，与 Nuclear Option 的开发者无关；
> 游戏内容版权归其开发商所有。

## 特性

- **纯运行期替换** —— 不修改游戏资源，不影响存档，不触发完整性校验。
- **词表热重载** —— 改完词表按 `F11` 生效，不必重启游戏。
- **数据与代码分离** —— 词表是可读的 JSON，人人都能做贡献。
- **分层翻译流水线** —— 能整体翻译就整体翻，不能就切分、剥标签、只翻主干。
  富文本、动态读数、拼接战报都能正确处理（见 `docs/ARCHITECTURE.md`）。
- **自带中文字体回退** —— 不需要手动改游戏的字体资产。

## 安装

1. 安装 [BepInEx 5](https://github.com/BepInEx/BepInEx/releases)（x64）到游戏根目录，
   先启动一次游戏以生成 `BepInEx/plugins/`。
2. 把本插件的目录整个放进 `BepInEx/plugins/`：

   ```
   <游戏>/BepInEx/plugins/NuclearOptionChineseLocalizationPatch/
   ├─ NuclearOptionChineseLocalizationPatch.dll
   ├─ translation.json
   ├─ exclusions.json
   ├─ force_scopes.json
   ├─ font.ttf
   └─ scopes/
   ```

3. 启动游戏。

> ⚠️ **不要与其他汉化插件同时启用。** 多个插件会同时挂钩同一个文本组件，
> 行为无法预测。安装前请先移除旧的中文插件目录。

## 配置

首次启动后生成 `BepInEx/config/com.nuclearoption.zhcn.localization.cfg`：

| 项 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | `true` | 关闭后已显示的中文会还原成原文 |
| `ReloadHotkey` | `F11` | 重载词表的热键 |
| `VerboseLogging` | `false` | 调试日志。翻译在渲染路径上被高频调用，日常请关闭 |
| `LogMisses` | `true` | 把未翻译文本累积到 `missing.json` / `untranslated.json` |
| `TranslationCacheLimit` | `20000` | 翻译结果缓存条目上限 |

## 从源码构建

需要 .NET SDK。`GameDir`（游戏根目录）的解析链会自动适配，仓库放在
`<游戏>/Git/<本仓库>` 时**零配置**即可构建：

```bash
dotnet build -c Release                 # 编译并部署 DLL
dotnet build -c Release -t:Rebuild      # 强制全量重编译
dotnet build -c Release -t:DeployData   # 全量同步数据文件到插件目录
```

游戏不在默认位置时，任选一种方式指定：

```bash
dotnet build -c Release -p:GameDir="D:\Games\Nuclear Option"
# 或设置环境变量 NUCLEAR_OPTION_DIR
# 或复制 GameDir.props.example 为 GameDir.props 并填入路径
```

> ⚠️ **`-t:DeployData` 不触发编译**，它的依赖链不经过 `CoreCompile`，复制的是
> `bin/` 里的上一次产物。改了 `.cs` 必须先 `-t:Rebuild`，再 `-t:DeployData`。

## 目录结构

```
├─ src/                     插件源码
│   ├─ LocalizationPlugin.cs      入口：生命周期、配置、热重载
│   ├─ Core/                      翻译核心（与游戏无关，可单独测试）
│   ├─ Patching/                  Harmony 补丁与防回写
│   ├─ Resources/                 字体与路径
│   ├─ Configuration/             配置
│   └─ Diagnostics/               日志与漏译记录
├─ data/                    词表与资源
│   ├─ translation.json           词表（唯一事实来源）
│   ├─ exclusions.json            不翻译名单
│   ├─ force_scopes.json          强制作用域名单
│   ├─ scopes/                    分作用域词表
│   └─ fonts/font.ttf             中文字体
├─ docs/ARCHITECTURE.md     架构与数据契约说明
└─ tools/                   维护脚本
```

## 数据格式

词表是扁平的 `{ "原文": "译文" }`。除了普通词条，还有四类特殊键：

| 前缀 | 用途 | 例子 |
| --- | --- | --- |
| `[Scope]` | 限定作用域，区分同名不同义的原文 | `[ActionReportText]Aborted Landing` |
| `~` | 整段模板（教程弹窗、含标签的长句） | `~<b>Return</b>\nAlkyon AB-4` |
| `>>` | 战报前缀片段 | `>>has been captured by` |
| `<<` | 战报后缀片段 | `<<joined the game` |
| `==` | 战报中段片段 | `==destroyed` |

完整规则（含归一化顺序、片段拼接语义、数值尾模式）见
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)。

## 授权

- **代码**：本工程为原创实现，未沿用任何第三方源代码。
- **词表**：译文由译者撰写；其引用的游戏原文版权归 Nuclear Option 开发商所有。
- **字体**：思源黑体 CN（Source Han Sans CN），SIL Open Font License 1.1，
  许可信息已内嵌于字体文件的元数据中。

代码部分具备完整著作权，可以自由选择开源许可。**在确定许可之前，本仓库不放置
LICENSE 文件**，即默认「保留所有权利」。

## 致谢

- [HunterCHCL](https://github.com/HunterCHCL) —— 前身汉化项目，本项目的翻译工作由此起步。
- [9138noms](https://github.com/9138noms) —— Nuclear Option 本地化工具链与游戏字符串清单，
  帮助发现了运行期采集覆盖不到的文本。
