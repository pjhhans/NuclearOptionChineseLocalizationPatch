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
        internal const string PluginVersion = "1.5.4";

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

            RuntimeStatus.DataDir = PluginPaths.BaseDir;
            // 兜底扫描默认关闭（0）：补丁路径已覆盖绝大多数文本，周期性全内存枚举
            // 只为补「绕过 setter 直接写字段」的少数通路，不值得每个玩家常年在后台付这笔钱。
            // 需要时在 F11 窗口里打开即可，立即生效。
            SettingsWindow.ScanEnabled = Settings.ScanIntervalSeconds.Value > 0f;
            float configured = Settings.ScanIntervalSeconds.Value;
            SettingsWindow.ScanInterval = ClampInterval(configured > 0f ? configured : 1f);

            ReloadData(initial: true);
            CjkFontProvider.Load();
            WindowKey = ParseKeyCode(Settings.ToggleWindowHotkey.Value);
            ReloadKey = ParseKeyCode(Settings.ReloadDataHotkey.Value);
            ApplyPatches();
            LogHarmonySelfTest();

            // 场景加载事件是逐帧宿主的主要重建点：首个真实场景加载会带走
            // 早于它创建的所有对象，宿主必须在这里活过来。
            SceneManager.sceneLoaded += OnSceneLoaded;
            PluginHost.Ensure();

            Log.Info($"{PluginName} v{PluginVersion} 已启动。{PluginPaths.Describe()}");
            Log.Info($"按 {Settings.ToggleWindowHotkey.Value} 打开设置与诊断窗口。");
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
                $"词表已载入：普通 {Table.GlobalCount} / 模板 {Table.TemplateCount} / " +
                $"片段 {Table.FragmentCount} / 作用域 {Table.ScopeCount}；" +
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
                    if (Patching.PatchHelpers.LocalizeInPlace(all[i])) changes++;

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
            PluginHost.Ensure(force: true);
            PluginHost.ResetScanBackoff();
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
