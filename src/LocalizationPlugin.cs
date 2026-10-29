using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearOptionChineseLocalizationPatch.Configuration;
using NuclearOptionChineseLocalizationPatch.Core;
using NuclearOptionChineseLocalizationPatch.Diagnostics;
using NuclearOptionChineseLocalizationPatch.Patching;
using NuclearOptionChineseLocalizationPatch.Resources;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NuclearOptionChineseLocalizationPatch
{
    /// <summary>
    /// 插件入口。只负责<b>启动编排</b>：载入数据、装补丁、注册场景事件，
    /// 然后把「每帧要做的事」交给 <see cref="PluginHost"/>。
    ///
    /// <para><b>本对象活不过第一个真实场景。</b>BepInEx 注入得比场景就绪更早，
    /// 那时创建的一切 GameObject（含 BepInEx 管理器）都会被首个真实场景加载销毁，
    /// 所以本类 <b>不做</b> <c>Update</c>、<b>不持有</b>任何需要跨场景存活的状态。</para>
    /// </summary>
    [BepInPlugin(Guid, PluginName, PluginVersion)]
    internal sealed class LocalizationPlugin : BaseUnityPlugin
    {
        internal const string Guid = "com.nuclearoption.zhcn.localization";
        internal const string PluginName = "Nuclear Option Chinese Localization Patch";
        internal const string PluginVersion = "1.7.3";

        // ------------------------------------------------------------------
        // 数据一律挂在静态属性上。
        //
        // 逐帧宿主会在场景加载时被 Unity 销毁重建，若把词表放在实例上，
        // 销毁后宿主就再也读不到它 —— 而且 MonoBehaviour 重载过的 == 会让
        // 「已被销毁但托管对象仍在」的实例判成 null，引发一连串隐蔽误判。
        // ------------------------------------------------------------------
        internal static ModSettings Settings { get; private set; }
        internal static LocalizationTable Table { get; private set; }
        internal static ExclusionRules Exclusions { get; private set; }
        internal static MissLog MissLog { get; private set; }
        internal static TextLocalizer Localizer { get; private set; }

        /// <summary>呼出设置窗口的热键（默认 F11）。</summary>
        internal static KeyCode WindowKey { get; private set; }

        /// <summary>不打开窗口、直接热重载词表的热键。默认未设置。</summary>
        internal static KeyCode ReloadKey { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Log.Bind(Logger);

            Settings = new ModSettings(Config);
            Log.Verbose = Settings.VerboseLogging.Value;

            Table = new LocalizationTable();
            Exclusions = new ExclusionRules();
            MissLog = new MissLog(PluginPaths.BaseDir);
            MissLog.Recording = Settings.LogMisses.Value;
            Localizer = new TextLocalizer(Table, Exclusions, MissLog);
            Localizer.Enabled = Settings.Enabled.Value;
            Localizer.SetCacheLimit(Settings.TranslationCacheLimit.Value);
            UiMods.PylonDropdownPatches.Enabled = Settings.WidenWeaponDropdown.Value;
            UiMods.WeaponInfoCardPatches.Enabled = Settings.FixWeaponInfoCard.Value;
            UiMods.ContributePanelPatches.Enabled = Settings.FixContributePanel.Value;

            RuntimeStatus.DataDir = PluginPaths.BaseDir;
            // 兜底扫描默认关闭（0）：补丁路径已覆盖绝大多数文本，周期性全内存枚举
            // 只为补「绕过 setter 直接写字段」的少数通路，不值得每个玩家常年在后台付这笔钱。
            // 需要时在 F11 窗口里打开即可，立即生效。
            Ui.SettingsWindow.ScanEnabled = Settings.ScanIntervalSeconds.Value > 0f;
            float configured = Settings.ScanIntervalSeconds.Value;
            Ui.SettingsWindow.ScanInterval = ClampInterval(configured > 0f ? configured : 1f);

            ReloadData(initial: true);
            CjkFontProvider.Load();
            WindowKey = ParseKeyCode(Settings.ToggleWindowHotkey.Value);
            ReloadKey = ParseKeyCode(Settings.ReloadDataHotkey.Value);
            ApplyPatches();
            // 打断 Patching ⇄ Hosting 双向依赖：宿主不引用补丁层，
            // 防回写补偿（必须晚于游戏自身刷新）改由入口接线注入。
            Hosting.PluginHost.LateTick += Patching.RewriteGuard.Tick;
            LogHarmonySelfTest();

            // 场景加载事件是逐帧宿主的主要重建点：首个真实场景加载会带走
            // 早于它创建的所有对象，宿主必须在这里活过来。
            SceneManager.sceneLoaded += OnSceneLoaded;
            Hosting.PluginHost.Ensure();

            Log.Info($"{PluginName} v{PluginVersion} 已启动。{PluginPaths.Describe()}");
            Log.Info($"按 {Settings.ToggleWindowHotkey.Value} 打开设置与诊断窗口。");
            Log.Info($"诊断状态：漏译记录 {(MissLog.Recording ? "开（写 missing.json / untranslated.json）" : "关")}"
                     + $"，最近命中 {(Localizer != null && Localizer.CaptureRecent ? "开" : "关")}"
                     + $"，兜底扫描 {(Ui.SettingsWindow.ScanEnabled ? "开" : "关")}"
                     + $"，调试日志 {(Log.Verbose ? "开" : "关")}"
                     + "。（默认只开翻译；需要报漏翻时请在 F11 窗口里打开「累积漏译」）");
        }

        private static float ClampInterval(float seconds)
        {
            if (seconds < 0.1f) return 0.1f;
            return seconds > 5f ? 5f : seconds;
        }

        /// <summary>（重新）载入词表与排除名单。窗口按钮与直接热重载都走这条路径。</summary>
        private static void ReloadData(bool initial)
        {
            string missing;
            if (!PluginPaths.IsReady(out missing))
            {
                Log.Error("缺少词表文件，插件不会翻译任何文本：" + missing);
                RuntimeStatus.LastReloadAt = RuntimeStatus.Stamp();
                RuntimeStatus.LastReloadOk = false;
                RuntimeStatus.LastReloadDetail = "缺少数据文件：" + missing;
                return;
            }

            bool tableOk = Table.Load(PluginPaths.BaseDir);
            Exclusions.LoadFile(PluginPaths.ExclusionsFile);
            Localizer.ClearCache();
            RewriteGuard.Clear();

            RuntimeStatus.LastReloadAt = RuntimeStatus.Stamp();
            RuntimeStatus.LastReloadOk = tableOk;
            RuntimeStatus.ReloadCount++;

            if (!tableOk)
            {
                RuntimeStatus.LastReloadDetail = initial ? "首次载入失败" : "失败（已中止，保留旧词表）";
                Log.Error(initial
                    ? "首次载入词表失败，本次启动将无译文可用。"
                    : "热重载已中止：词表未替换，界面维持现状（旧词表仍在工作）。");
                return;
            }

            RuntimeStatus.LastReloadDetail = initial ? "启动载入成功" : "热重载成功";
            Log.Info(
                $"词表已载入：共 {Table.TotalEntryCount} 条（大小写重复键已合并）—— 通用 {Table.GlobalCount} / 模板 {Table.TemplateCount} / " +
                $"片段 {Table.FragmentCount} / 作用域词条 {Table.ScopedEntryCount}（{Table.ScopeCount} 个作用域）；" +
                $"模板指纹 {Table.TemplateFingerprintCount} 条（回落匹配用）；" +
                $"排除名单 scopes {Exclusions.ScopeCount} / terms {Exclusions.TermCount} / texts {Exclusions.TextCount}" +
                (initial ? "" : "（热重载）"));
        }

        /// <summary>热重载入口，供逐帧宿主的（可选）直接热键调用。</summary>
        internal static void ReloadFromDisk() => ReloadData(initial: false);

        /// <summary>清空翻译缓存与防回写跟踪状态。</summary>
        internal static void ClearCaches()
        {
            Localizer?.ClearCache();
            RewriteGuard.Clear();
        }

        /// <summary>
        /// 开关翻译。
        ///
        /// <para>关闭时除了停用翻译，还要把屏幕上<b>已经显示成中文</b>的文本还原回英文 ——
        /// 否则用户按下按钮后什么都没变，会以为按钮坏了。还原走
        /// <see cref="PatchHelpers.RevertInPlace"/>（它不做「只写中文」的收敛性限制，
        /// 因为这里的目标恰恰是写回英文）。</para>
        /// </summary>
        internal static void SetEnabled(bool enabled)
        {
            if (Localizer == null) return;
            Localizer.Enabled = enabled;
            ClearCaches();
            Log.Info(enabled ? "翻译已启用。" : "翻译已关闭，正在还原英文。");

            if (enabled) ScanScene();
            else RevertScene();
        }

        /// <summary>把当前场景里所有已中文化的文本还原成英文。</summary>
        internal static void RevertScene()
        {
            try
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<TMP_Text>();
                for (int i = 0; i < all.Length; i++) PatchHelpers.RevertInPlace(all[i]);

                var legacy = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>();
                for (int i = 0; i < legacy.Length; i++) PatchHelpers.RevertInPlace(legacy[i]);
            }
            catch (Exception ex)
            {
                Log.Debug("还原失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 扫一遍场景里所有文本组件并原地翻译。
        ///
        /// <para>补丁覆盖不到的两类文本只能靠它：预制体自带的默认值（不经过任何 setter）、
        /// 以及绕过 setter 直接写字段的路径。它同时是 <see cref="PatchHelpers.LocalizeInPlace"/>
        /// 的驱动源，因此扫描间隔直接决定「新出现的文本多久变中文」。</para>
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<int> _blankLogged =
            new System.Collections.Generic.HashSet<int>();
        private static int _blankBudget = 40;

        /// <summary>已经尝试过「重建字体材质引用」的对象（每个只修一次，失败不重试）。</summary>
        private static readonly System.Collections.Generic.HashSet<int> _repairTried =
            new System.Collections.Generic.HashSet<int>();
        private static int _blankRuns;
        private const int BlankRuns = 20;

        /// <summary>空白巡检跑过几次（前 <see cref="BlankRuns"/> 次用快节奏）。</summary>
        internal static int BlankRunsDone => _blankRuns;

        /// <summary>
        /// 巡检是否还要继续。
        ///
        /// <para><b>为什么不再「跑够次数就彻底停」。</b>这轮巡检已经不只是诊断，它还负责
        /// **自愈**：把「有中文却一个字符都没画出来」的文本对象重建一次字体材质引用。
        /// 而这类对象往往出现在用户后来才打开的菜单里（环形菜单 / WMC 控制台 / 事件台），
        /// 40 秒窗口早就过了 —— 停掉巡检等于把自愈能力一起停掉。
        /// 所以改成：前 20 次 2 秒一次（覆盖进场景那阵），之后 10 秒一次长期低频跑。
        /// 代价是每 10 秒一次 FindObjectsOfTypeAll（数百个组件，≈0.3 ms），可忽略。</para>
        /// </summary>
        internal static bool BlankReportPending
        {
            get { return true; }
        }

        /// <summary>
        /// 驱动一次空白巡检。独立于兜底扫描开关 —— 实机里那个开关是默认关的，
        /// 挂进 ScanScene 等于永远不执行（第一次部署就踩了这个坑）。
        /// 只有 6 次机会、每次间隔 2 秒，跑完对长期性能零影响。
        /// </summary>
        internal static void RunBlankReport()
        {
            _blankRuns++;
            try
            {
                // ★ 先补回退链再巡检：mod 的字体是**用的时候才加载**的，
                //   场景加载那一刻扫一遍根本扫不到它们（环形菜单的 JetBrains Mono 就是）。
                //   这轮巡检每 10 秒跑一次，正好当回退链的补挂点。
                Resources.CjkFontProvider.Register();

                var all = UnityEngine.Resources.FindObjectsOfTypeAll<TMP_Text>();
                for (int i = 0; i < all.Length; i++)
                    ReportBlankText(all[i]);
            }
            catch (Exception ex)
            {
                Log.Debug("空白巡检失败：" + ex.Message);
            }

            // 快节奏窗口结束：报一次总数，之后转入 10 秒一次的长期自愈（不再刷日志）。
            if (_blankRuns == BlankRuns)
            {
                int found = 40 - _blankBudget;
                Log.Info(found > 0
                    ? $"[空白文本] 快检结束：{found} 个组件有中文却没渲染出宽度（明细见上），之后转入 10 秒一次的自愈巡检。"
                    : "[空白文本] 快检结束：没有发现「有中文但渲染为空」的组件，之后转入 10 秒一次的自愈巡检。");
            }
        }

        /// <summary>
        /// 「有中文却一个字符都没画出来」的巡检。
        ///
        /// <para>实机上出现过整块空白：字符串、译文、字形覆盖三者都正常
        /// （<c>字形覆盖</c> 诊断里每个字都是「＋」= 能动态加进图集），但组件上什么都看不到。
        /// 这类失败只有两种形状，靠矩形与渲染包围盒就能区分：</para>
        /// <list type="bullet">
        /// <item>矩形宽/高为 0 —— 上游排版把宽度算没了（第三方 mod 常按英文宽度排版，
        /// 中文走 fallback 时 <c>GetPreferredValues</c> 可能返回 0）。</item>
        /// <item>矩形正常但 <c>textBounds</c> 为 0 —— 文本没被 TMP 生成（解析/溢出模式/材质）。</item>
        /// </list>
        /// <para>每个组件只报一次，总预算 40 条，避免刷屏。</para>
        /// </summary>
        private static void ReportBlankText(TMP_Text t)
        {
            if (t == null || !t.gameObject.activeInHierarchy) return;

            string s = DisplayText(t);
            if (string.IsNullOrEmpty(s) || s.Length > 200) return;
            char firstCjk = FirstCjk(s);
            if (firstCjk == '\0') return;

            // 属性变更待重建时 bounds 还是上一轮的旧值（或 0）——
            // 定时巡检里跳过它（避免误报），**按需巡检**里则强制重建后再量，
            // 否则「刚 set_text 过、还没来得及生成网格」的组件永远量不到。
            if (t.havePropertiesChanged)
            {
                if (!_forceInspect) return;
                t.ForceMeshUpdate(true, true);
            }

            RectTransform rt = t.rectTransform;
            if (rt == null) return;
            Vector2 bounds = t.textBounds.size;
            // 判据只看「有没有画出东西」。**不能**把 rect 高度为 0 当成空白 ——
            // 实机里大量正常文本的 rect 高度是 0（靠 preferredHeight 撑开），
            // 按 rect 判会把它们全算成空白，然后被修复逻辑无谓重排（宽度 0 会触发换行，
            // 反而把本来好好的文本弄成竖排）。
            if (bounds.x >= 1f && bounds.y >= 1f) return;

            bool dumped = DumpText(t, s, firstCjk);
            if (dumped) Log.Info("[空白文本] ↑ 有中文但没渲染出来（矩形或包围盒为 0）。");

            // 定时巡检也自愈：只对「已经有中文、却什么都没画出来」的对象做一次
            // （_repairTried 保证每个对象一生只试一次，修不好也不会每两秒重排一次）。
            if (!_repairTried.Add(t.GetInstanceID())) return;
            Vector2 before = t.textBounds.size;
            RepairFontLinks(t);
            t.SetAllDirty();
            t.ForceMeshUpdate(true, true);
            Vector2 after = t.textBounds.size;
            if (dumped && after.x >= 1f)
            {
                Log.Info(string.Format("[空白文本]   自愈成功：bounds {0:F1}x{1:F1}（原 {2:F1}x{3:F1}）",
                    after.x, after.y, before.x, before.y));
            }
        }

        /// <summary>
        /// 读**真正上屏**的文本。
        ///
        /// <para><b>为什么不能直接用 <c>t.text</c>。</b>本插件把 <c>TMP_Text.text</c> 的 getter
        /// 改写成返回**英文原文**（<see cref="Patching.TmpPatches.TextGetterPostfix"/> →
        /// <c>RestoreOriginalOnRead</c>）。巡检要是读 <c>t.text</c>，拿到的永远是英文，
        /// 汉字一个都找不到 —— 于是「有中文却渲染为空」这条判据**一条都不会命中**，
        /// 自愈也永远不会触发。这正是 2026-10-29 那一轮「巡检说没问题、界面还是空」的原因。</para>
        ///
        /// <para>真正上屏的是 <c>m_text</c>（setter 补丁是 Prefix，只换入参，
        /// 原文 setter 照常把它写进 <c>m_text</c>）。取不到时退回 <c>textInfo</c> 里已解析的字符。</para>
        /// </summary>
        private static string DisplayText(TMP_Text t)
        {
            try
            {
                if (_mTextField == null)
                {
                    _mTextField = typeof(TMP_Text).GetField("m_text",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                }
                if (_mTextField != null)
                {
                    string v = _mTextField.GetValue(t) as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch { /* 反射失败就走下面的退路 */ }

            var info = t.textInfo;
            if (info != null && info.characterCount > 0)
            {
                var sb = new System.Text.StringBuilder(info.characterCount);
                for (int i = 0; i < info.characterCount; i++) sb.Append(info.characterInfo[i].character);
                return sb.ToString();
            }
            return t.text;
        }

        private static System.Reflection.FieldInfo _mTextField;

        /// <summary>对象自带的材质页清单（TMP 把回退字体当第二页塞进这里）。</summary>
        private static string MaterialsOf(TMP_Text t)
        {
            try
            {
                if (_mMatField == null)
                {
                    _mMatField = typeof(TMP_Text).GetField("m_sharedMaterials",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                }
                var list = _mMatField != null ? _mMatField.GetValue(t) as System.Collections.Generic.List<Material> : null;
                if (list == null) return "?";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < list.Count && i < 4; i++)
                {
                    if (i > 0) sb.Append(" | ");
                    sb.Append(list[i] != null ? list[i].name : "<null>");
                }
                return list.Count + " 页[" + sb + "]";
            }
            catch { return "?"; }
        }

        private static System.Reflection.FieldInfo _mMatField;

        /// <summary>回退链里的资产名（最多 3 个）——判定「我的中文字体到底在不在链上」。</summary>
        private static string FallbackNames(TMP_FontAsset fa)
        {
            var list = fa != null ? fa.fallbackFontAssetTable : null;
            if (list == null) return "无";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count && i < 3; i++)
            {
                if (i > 0) sb.Append(" , ");
                sb.Append(list[i] != null ? list[i].name : "<已销毁>");
            }
            return list.Count + " 个[" + sb + "]";
        }

        private static char FirstCjk(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch >= 0x3400 && ch <= 0x9FFF) return ch;
            }
            return '\0';
        }

        /// <summary>把一条中文文本的几何 + 字体证据打成两行日志；预算用尽返回 false。</summary>
        private static bool DumpText(TMP_Text t, string s, char firstCjk)
        {
            if (_blankBudget <= 0) return false;
            if (!_blankLogged.Add(t.GetInstanceID())) return false;
            _blankBudget--;

            RectTransform rt = t.rectTransform;
            Vector2 bounds = t.textBounds.size;
            var path = new System.Text.StringBuilder();
            Transform cur = rt;
            for (int depth = 0; depth < 4 && cur != null; depth++)
            {
                if (path.Length > 0) path.Append(" < ");
                path.Append(cur.name);
                cur = cur.parent;
            }
            Log.Info(string.Format("[空白文本] rect {0:F0}x{1:F0} @({2:F0},{3:F0}) bounds {4:F1}x{5:F1} 路径 {6} 「{7}{8}」",
                rt.rect.width, rt.rect.height,
                rt.anchoredPosition.x, rt.anchoredPosition.y,
                bounds.x, bounds.y, path,
                s.Length > 18 ? s.Substring(0, 18) : s, s.Length > 18 ? "…" : ""));

            // 字体侧的证据：这一条到底是「字形没有」还是「排版把宽度吃掉了」。
            // 只看汉字有没有、回退链挂没挂，就能一眼分开两类根因。
            TMP_FontAsset fa = t.font;
            int fb = (fa != null && fa.fallbackFontAssetTable != null) ? fa.fallbackFontAssetTable.Count : -1;
            bool hasGlyph = fa != null && fa.HasCharacter(firstCjk);
            TMP_FontAsset cjk = Resources.CjkFontProvider.Font;
            Log.Info(string.Format("[空白文本]   字体 {0}（自身含「{1}」={2}；回退 {3}）中文字体{4}含该字={5} 材质 {6} 已画字符 {7} 溢出模式={8} 自动换行={9} 字号={10:F0}",
                fa != null ? fa.name : "<null>", firstCjk, hasGlyph ? "是" : "否",
                FallbackNames(fa),
                cjk != null ? "" : "<未就绪，",
                cjk != null ? (cjk.HasCharacter(firstCjk) ? "是" : "否") : "?",
                MaterialsOf(t), t.textInfo != null ? t.textInfo.characterCount : -1,
                (int)t.overflowMode, t.enableWordWrapping, t.fontSize));
            return true;
        }

        /// <summary>
        /// 重建一个文本对象的**字体材质引用**（不换字体、不动文本内容）。
        ///
        /// <para><b>为什么需要它。</b>中文回退字体是插件在运行期挂进
        /// <c>font.fallbackFontAssetTable</c> 的。TMP 在渲染某个字符时，
        /// 如果它不在主字体里，会去回退表里找 —— 但找到之后要把它**当成第二个材质**
        /// 加进该文本对象的 <c>m_sharedMaterials</c> 才会真的画出来。
        /// 这一步发生在 <c>SetArraySizes</c> 期间；对「挂回退表之前就已经建好、
        /// 且之后再也没有被 set_text 过」的对象，材质表可能一直只有主字体那一页。</para>
        ///
        /// <para>实机表现就是本文件顶部那段现象：<b>同一个对象，拉丁字母正常、中文整条消失</b>
        /// （连省略号都没有），而字体诊断里每个汉字都是「能加进图集」。
        /// 重新赋一次 <c>font</c> 会走 <c>SetFontAsset</c> → 重建材质表，是最轻的强制刷新。</para>
        /// </summary>
        private static void RepairFontLinks(TMP_Text t)
        {
            try
            {
                TMP_FontAsset f = t.font;
                if (f == null) return;
                t.font = null;
                t.font = f;
            }
            catch (Exception ex)
            {
                Log.Debug("重建字体引用失败：" + ex.Message);
            }
        }

        /// <summary>按需巡检期间强制重建网格（定时巡检里不这么做，免得每两秒全场景重排）。</summary>
        private static bool _forceInspect;

        /// <summary>
        /// 立刻巡检「当前画面上有中文却渲染不出来」的组件（F11 按钮触发）。
        ///
        /// <para><b>为什么需要按需触发。</b>定时巡检只在进场景后的前 40 秒跑，
        /// 而这类故障往往只在某个菜单/面板被打开时才成立（环形菜单、WMC 控制台…）。
        /// 用户开着那个界面按一下按钮，日志里就会拿到矩形、包围盒、字体名、
        /// 回退链长度、字形有无 —— 一次到位，不用靠猜。</para>
        ///
        /// <para><b>顺带把「画出来了但被裁掉」这一类也捞出来。</b>只判 bounds 为 0 会漏掉
        /// 「文本画在矩形外面」的情况（第三方 mod 按英文高度排版，换中文后行高不够，
        /// 标签被顶到行外被父级 mask 裁掉）。所以按需模式会把**矩形与包围盒不重叠**
        /// 的中文组件也列出来，并带上 anchoredPosition 便于对账。</para>
        /// </summary>
        internal static void InspectBlankNow()
        {
            _blankRuns = 0;
            _blankBudget = 120;
            _blankLogged.Clear();
            _forceInspect = true;
            int blank = 0, outside = 0, repaired = 0;
            try
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<TMP_Text>();
                for (int i = 0; i < all.Length; i++)
                {
                    TMP_Text t = all[i];
                    if (t == null || !t.gameObject.activeInHierarchy) continue;
                    string s = DisplayText(t);
                    if (string.IsNullOrEmpty(s) || s.Length > 200) continue;
                    char cjk = FirstCjk(s);
                    if (cjk == '\0') continue;
                    if (t.havePropertiesChanged) t.ForceMeshUpdate(true, true);

                    RectTransform rt = t.rectTransform;
                    if (rt == null) continue;
                    float w = rt.rect.width, h = rt.rect.height;
                    Vector2 b = t.textBounds.size;

                    if (b.x >= 1f && b.y >= 1f)
                    {
                        // 画出来了：只有当包围盒明显**跑出**自身矩形时才可疑
                        // （高度不足被顶出、或宽度为 0 却画了字）。
                        bool outOfBox = b.y > h * 1.8f + 2f || b.x > w * 1.6f + 4f;
                        if (!outOfBox) continue;
                        if (!DumpText(t, s, cjk)) break;
                        outside++;
                        continue;
                    }

                    if (!DumpText(t, s, cjk)) break;
                    blank++;

                    // —— 顺手修一次并前后对比：这就是「根因是不是材质引用没重建」的判定器。
                    // 渲染侧的现象是「同一对象，拉丁字母正常、中文整条不见」，
                    // 说明对象本身没坏、字形也在（日志里含「接」=是），
                    // 缺的是**该对象材质/网格引用里没有中文回退那一页**
                    // （回退表是插件运行期才挂上去的，早于挂表的对象不会自己重建）。
                    // 重新赋一次 font 会走 SetFontAsset → 重建 m_sharedMaterials，
                    // 再 SetAllDirty + ForceMeshUpdate 强制重排；两条都做完还不行，就不是这条路。
                    Vector2 before = t.textBounds.size;
                    RepairFontLinks(t);
                    t.SetAllDirty();
                    t.ForceMeshUpdate(true, true);
                    Vector2 after = t.textBounds.size;
                    if (after.x >= 1f)
                    {
                        repaired++;
                        Log.Info(string.Format("[空白文本]   修复后 bounds {0:F1}x{1:F1}（原 {2:F1}x{3:F1}）—— 重建材质引用即可恢复",
                            after.x, after.y, before.x, before.y));
                    }
                    else
                    {
                        Log.Info(string.Format("[空白文本]   重建材质引用后仍为 {0:F1}x{1:F1} —— 不是材质引用问题",
                            after.x, after.y));
                    }
                }

                if (blank + outside == 0)
                {
                    Log.Info("[空白文本] 按需巡检：当前画面上没有「有中文却不显示/被裁」的组件。");
                }
                else
                {
                    Log.Info($"[空白文本] 按需巡检：空白 {blank} 个（其中 {repaired} 个重建材质引用后恢复）/ 疑似被裁 {outside} 个（明细见上）。");
                }
            }
            finally
            {
                _forceInspect = false;
            }
        }

        internal static int ScanScene()
        {
            if (Localizer == null || !Localizer.Enabled) return 0;

            RuntimeStatus.Scans++;

            // 顺带补登记字体回退链：TMP_Settings 在场景切换时可能被重新加载，
            // 回退链会被重置。搭这次扫描的顺风车最省事。
            CjkFontProvider.Register();

            // 返回值 = 本轮真正改写掉的组件数。兜底扫描的空闲退避以此为信号：
            // 翻到东西说明界面在变，退避清零；空手而归则逐步拉长间隔。
            int changes = 0;
            try
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<TMP_Text>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (Patching.PatchHelpers.LocalizeInPlace(all[i])) changes++;
                    ReportBlankText(all[i]);
                }

                var legacy = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>();
                for (int i = 0; i < legacy.Length; i++)
                    if (Patching.PatchHelpers.LocalizeInPlace(legacy[i])) changes++;
            }
            catch (Exception ex)
            {
                Log.Debug("兜底扫描失败：" + ex.Message);
            }
            return changes;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            RuntimeStatus.LastScene = scene.name;
            RuntimeStatus.SceneLoads++;
            Log.Info($"场景已加载：{scene.name}。");
            // 强制重建：这是宿主最主要的复活点，不能被防抖挡掉。
            Hosting.PluginHost.Ensure(force: true);
            Hosting.PluginHost.ResetScanBackoff();
            CjkFontProvider.Register();
            ScanScene();
        }

        private void ApplyPatches()
        {
            _harmony = new Harmony(Guid);

            // 逐个补丁类安装，而不是 PatchAll —— 游戏版本差异会让个别目标方法不存在
            // （例如某个方法被改名或移除），PatchAll 遇到这种会整体中断，
            // 导致本该生效的补丁也一起装不上。
            int ok = 0, failed = 0;
            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Warn($"补丁 {type.Name} 安装失败：{ex.Message}");
                }
            }
            Log.Info($"Harmony 补丁：{ok} 个类安装成功，{failed} 个失败。");
        }

        /// <summary>
        /// 一次性自检：给自己一个方法打补丁再调用，看返回值有没有被改写。
        /// 不牵扯任何游戏类型，所以能一刀切开「Harmony 在本环境没生效」与
        /// 「Harmony 正常、问题在补丁目标那一侧」。
        /// </summary>
        private static void LogHarmonySelfTest()
        {
            try
            {
                int ping = HarmonySelfTest.Ping();
                bool alive = ping == HarmonySelfTest.Expected;
                Log.Info($"[自检] Harmony 生效 = {alive}（期望 {HarmonySelfTest.Expected}，实得 {ping}）");
            }
            catch (Exception ex)
            {
                Log.Warn("[自检] 执行异常：" + ex.Message);
            }
        }

        private static KeyCode ParseKeyCode(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return KeyCode.None;
            try
            {
                return (KeyCode)Enum.Parse(typeof(KeyCode), name.Trim(), ignoreCase: true);
            }
            catch
            {
                Log.Warn("无法识别的热键名，热重载已禁用：" + name);
                return KeyCode.None;
            }
        }

        /// <summary>
        /// <b>两个刻意的省略，都别加回来。</b>
        ///
        /// <list type="number">
        /// <item><b>不退订 <c>SceneManager.sceneLoaded</c>。</b>本对象会在首个真实场景加载时
        /// 被 Unity 销毁，如果在这里退订，之后的场景加载事件就再也收不到 ——
        /// 而那是逐帧宿主最重要的重建点。订阅的是静态方法，不依赖本实例存活。</item>
        /// <item><b>不调用 <c>UnpatchSelf</c>。</b>一执行就等于在游戏刚起来时把整条翻译链路
        /// 拆掉，且日志上看不出任何异常。进程退出时 Unity 会连同补丁一起回收。</item>
        /// </list>
        /// </summary>
        private void OnDestroy()
        {
            MissLog?.Flush();
        }

        private void OnApplicationQuit()
        {
            MissLog?.Flush();
        }
    }
}
