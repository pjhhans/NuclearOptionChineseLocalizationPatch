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
        internal const string PluginVersion = "1.1.0";

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
            Localizer = new TextLocalizer(Table, Exclusions, MissLog);
            Localizer.Enabled = Settings.Enabled.Value;
            Localizer.SetCacheLimit(Settings.TranslationCacheLimit.Value);

            ReloadData(initial: true);
            CjkFontProvider.Load();
            ReloadKey = ParseKeyCode(Settings.ReloadHotkey.Value);
            ApplyPatches();
            LogHarmonySelfTest();

            // 场景加载事件是逐帧宿主的主要重建点：首个真实场景加载会带走
            // 早于它创建的所有对象，宿主必须在这里活过来。
            SceneManager.sceneLoaded += OnSceneLoaded;
            PluginHost.Ensure();

            Log.Info($"{PluginName} v{PluginVersion} 已启动。{PluginPaths.Describe()}");
        }

        /// <summary>（重新）载入词表与排除名单。热重载与启动都走这条路径。</summary>
        private static void ReloadData(bool initial)
        {
            string missing;
            if (!PluginPaths.IsReady(out missing))
            {
                Log.Error("缺少词表文件，插件不会翻译任何文本：" + missing);
                return;
            }

            Table.Load(PluginPaths.BaseDir);
            Exclusions.LoadFile(PluginPaths.ExclusionsFile);
            Localizer.ClearCache();
            RewriteGuard.Clear();

            Log.Info(
                $"词表已载入：普通 {Table.GlobalCount} / 模板 {Table.TemplateCount} / " +
                $"片段 {Table.FragmentCount} / 作用域 {Table.ScopeCount}；" +
                $"排除名单 scopes {Exclusions.ScopeCount} / terms {Exclusions.TermCount} / texts {Exclusions.TextCount}" +
                (initial ? "" : "（热重载）"));
        }

        /// <summary>热重载入口，供逐帧宿主的热键分支调用。</summary>
        internal static void ReloadFromDisk() => ReloadData(initial: false);

        /// <summary>
        /// 扫一遍场景里所有文本组件并原地翻译。
        ///
        /// <para>补丁覆盖不到的两类文本只能靠它：预制体自带的默认值（不经过任何 setter）、
        /// 以及绕过 setter 直接写字段的路径。它同时是 <see cref="PatchHelpers.LocalizeInPlace"/>
        /// 的驱动源，因此扫描间隔直接决定「新出现的文本多久变中文」。</para>
        /// </summary>
        internal static void ScanScene()
        {
            if (Localizer == null || !Localizer.Enabled) return;

            // 顺带补登记字体回退链：TMP_Settings 在场景切换时可能被重新加载，
            // 回退链会被重置。搭这次扫描的顺风车最省事。
            CjkFontProvider.Register();

            try
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<TMP_Text>();
                for (int i = 0; i < all.Length; i++) PatchHelpers.LocalizeInPlace(all[i]);

                var legacy = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>();
                for (int i = 0; i < legacy.Length; i++) PatchHelpers.LocalizeInPlace(legacy[i]);
            }
            catch (Exception ex)
            {
                Log.Debug("兜底扫描失败：" + ex.Message);
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Log.Info($"场景已加载：{scene.name}。");
            // 强制重建：这是宿主最主要的复活点，不能被防抖挡掉。
            PluginHost.Ensure(force: true);
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
