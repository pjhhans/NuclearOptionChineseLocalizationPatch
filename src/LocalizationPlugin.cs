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

namespace NuclearOptionChineseLocalizationPatch
{
    /// <summary>
    /// 插件入口。生命周期很短：启动时载入词表、加载字体、装上补丁，
    /// 之后只有每帧的防回写检查与低频兜底扫描。
    /// </summary>
    [BepInPlugin(Guid, PluginName, PluginVersion)]
    internal sealed class LocalizationPlugin : BaseUnityPlugin
    {
        internal const string Guid = "com.nuclearoption.zhcn.localization";
        internal const string PluginName = "Nuclear Option Chinese Localization Patch";
        internal const string PluginVersion = "1.0.0";

        internal static LocalizationPlugin Instance { get; private set; }

        internal ModSettings Settings { get; private set; }
        internal LocalizationTable Table { get; private set; }
        internal ExclusionRules Exclusions { get; private set; }
        internal MissLog MissLog { get; private set; }
        internal TextLocalizer Localizer { get; private set; }

        private Harmony _harmony;
        private KeyCode _reloadKey;
        private float _nextScanTime;

        /// <summary>
        /// 兜底扫描间隔。「预制体默认文本」与「绕过所有 setter 直接写字段」这两类
        /// 是补丁覆盖不到的，靠周期性扫描兜住。间隔不能太短 ——
        /// 扫描会枚举全场景对象，本身有成本。
        /// </summary>
        private const float ScanIntervalSeconds = 5f;

        private void Awake()
        {
            Instance = this;
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
            _reloadKey = ParseKeyCode(Settings.ReloadHotkey.Value);
            ApplyPatches();

            _nextScanTime = Time.realtimeSinceStartup + ScanIntervalSeconds;
            Logger.LogInfo($"{PluginName} v{PluginVersion} 已启动。{PluginPaths.Describe()}");
        }

        /// <summary>（重新）载入词表与排除名单。热重载与启动都走这条路径。</summary>
        private void ReloadData(bool initial)
        {
            string missing;
            if (!PluginPaths.IsReady(out missing))
            {
                Logger.LogError("缺少词表文件，插件不会翻译任何文本：" + missing);
                return;
            }

            Table.Load(PluginPaths.BaseDir);
            Exclusions.LoadFile(PluginPaths.ExclusionsFile);
            Localizer.ClearCache();
            RewriteGuard.Clear();

            Logger.LogInfo(
                $"词表已载入：普通 {Table.GlobalCount} / 模板 {Table.TemplateCount} / " +
                $"片段 {Table.FragmentCount} / 作用域 {Table.ScopeCount}；" +
                $"排除名单 scopes {Exclusions.ScopeCount} / terms {Exclusions.TermCount} / texts {Exclusions.TextCount}" +
                (initial ? "" : "（热重载）"));
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
                    Logger.LogWarning($"补丁 {type.Name} 安装失败：{ex.Message}");
                }
            }
            Logger.LogInfo($"Harmony 补丁：{ok} 个类安装成功，{failed} 个失败。");
        }

        private void Update()
        {
            RewriteGuard.Tick();
            HandleReloadHotkey();
            HandlePeriodicScan();
            MissLog.FlushIfDue();
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

        private void HandleReloadHotkey()
        {
            if (_reloadKey == KeyCode.None) return;
            if (!Input.GetKeyDown(_reloadKey)) return;
            Logger.LogInfo("收到热重载请求。");
            ReloadData(initial: false);
        }

        private void HandlePeriodicScan()
        {
            if (Time.realtimeSinceStartup < _nextScanTime) return;
            _nextScanTime = Time.realtimeSinceStartup + ScanIntervalSeconds;
            if (!Localizer.Enabled) return;

            // 每次扫描顺带登记一次字体回退链：TMP_Settings 在场景切换时可能被重新加载，
            // 回退链会被重置，登记一次最省事的做法是搭低频扫描的顺风车。
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

        private void OnDestroy()
        {
            MissLog?.Flush();
            _harmony?.UnpatchSelf();
        }

        private void OnApplicationQuit()
        {
            MissLog?.Flush();
        }
    }
}
