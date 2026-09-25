using BepInEx.Configuration;

namespace NuclearOptionChineseLocalizationPatch.Configuration
{
    /// <summary>
    /// 插件配置。全部项都可以在 <c>BepInEx/config/&lt;GUID&gt;.cfg</c> 里改，
    /// 改动在下次启动生效（热重载只重读词表，不重读配置）。
    /// </summary>
    internal sealed class ModSettings
    {
        internal readonly ConfigEntry<bool> Enabled;
        internal readonly ConfigEntry<bool> VerboseLogging;
        internal readonly ConfigEntry<string> ToggleWindowHotkey;
        internal readonly ConfigEntry<string> ReloadDataHotkey;
        internal readonly ConfigEntry<int> TranslationCacheLimit;
        internal readonly ConfigEntry<bool> LogMisses;
        internal readonly ConfigEntry<float> ScanIntervalSeconds;

        internal ModSettings(ConfigFile config)
        {
            Enabled = config.Bind(
                "General", "Enabled", true,
                "是否启用翻译。关闭后已显示的中文会被还原成原文。");

            ToggleWindowHotkey = config.Bind(
                "General", "ToggleWindowHotkey", "F11",
                "显示/隐藏设置窗口的热键。留空则只能用配置文件控制。");

            ReloadDataHotkey = config.Bind(
                "General", "ReloadDataHotkey", "",
                "不打开窗口、直接重新载入词表的热键。留空表示只通过窗口里的按钮重载。");

            VerboseLogging = config.Bind(
                "Diagnostics", "VerboseLogging", false,
                "输出调试级日志。翻译路径在渲染期间被高频调用，日常使用请保持关闭。");

            LogMisses = config.Bind(
                "Diagnostics", "LogMisses", true,
                "把未翻译的文本累积到 missing.json / untranslated.json，供补词表用。");

            ScanIntervalSeconds = config.Bind(
                "Performance", "ScanIntervalSeconds", 1f,
                "兜底扫描间隔（秒），范围 0.1~5。它决定「新出现的文本多久变中文」。");

            TranslationCacheLimit = config.Bind(
                "Performance", "TranslationCacheLimit", 20000,
                "翻译结果缓存条目上限。0 表示禁用缓存（不推荐）。");
        }
    }
}
