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
        internal readonly ConfigEntry<bool> WidenWeaponDropdown;

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
                "Diagnostics", "LogMisses", false,
                "把未翻译的文本累积到 missing.json / untranslated.json，供补词表用。"
                + "默认关闭：插件分不清游戏原文与第三方文本（创意工坊物件名、玩家呼号等会被一并记进来），"
                + "清单噪声很大，且写盘本身是常驻开销。"
                + "需要向作者报漏翻、或自己补词表时再打开（也可在 F11 窗口里临时勾选）。");

            ScanIntervalSeconds = config.Bind(
                "Performance", "ScanIntervalSeconds", 0f,
                "兜底扫描间隔（秒）。0 = 关闭（默认，省性能；大多数文本由补丁路径覆盖）。"
                + "0.1~5 = 周期性扫描场景补翻漏网文本；界面稳定时还会自动放慢。"
                + "若发现某些文本开机后一直是英文，可在 F11 窗口里临时打开扫描。");

            TranslationCacheLimit = config.Bind(
                "Performance", "TranslationCacheLimit", 20000,
                "翻译结果缓存条目上限。0 表示禁用缓存（不推荐）。");

            WidenWeaponDropdown = config.Bind(
                "UI", "WidenWeaponDropdown", true,
                "挂架武器下拉列表宽度自适应：弹出列表装不下中文武器名时自动加宽到刚好够"
                + "（收起态按钮不变；超出屏幕右缘由 TMP 自带的翻转逻辑处理）。"
                + "顺带把该列表滚动条方向规范化为 BottomToTop——游戏预制体方向是反的，属游戏 bug。");
        }
    }
}
