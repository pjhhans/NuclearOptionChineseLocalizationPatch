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
        internal readonly ConfigEntry<string> ReloadHotkey;
        internal readonly ConfigEntry<int> TranslationCacheLimit;
        internal readonly ConfigEntry<bool> LogMisses;

        internal ModSettings(ConfigFile config)
        {
            Enabled = config.Bind(
                "General", "Enabled", true,
                "是否启用翻译。关闭后已显示的中文会被还原成原文。");

            ReloadHotkey = config.Bind(
                "General", "ReloadHotkey", "F11",
                "重新载入词表的热键（改词表后按它即可生效，无需重启游戏）。留空则禁用热重载。");

            VerboseLogging = config.Bind(
                "Diagnostics", "VerboseLogging", false,
                "输出调试级日志。翻译路径在渲染期间被高频调用，日常使用请保持关闭。");

            LogMisses = config.Bind(
                "Diagnostics", "LogMisses", true,
                "把未翻译的文本累积到 missing.json / untranslated.json，供补词表用。");

            TranslationCacheLimit = config.Bind(
                "Performance", "TranslationCacheLimit", 20000,
                "翻译结果缓存条目上限。0 表示禁用缓存（不推荐）。");
        }
    }
}
