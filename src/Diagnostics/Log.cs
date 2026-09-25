using BepInEx.Logging;

namespace NuclearOptionChineseLocalizationPatch.Diagnostics
{
    /// <summary>
    /// 日志门面。默认静默掉调试级输出 —— 翻译路径在渲染期间被高频调用，
    /// 每条都写盘会直接拖慢帧率。
    /// </summary>
    internal static class Log
    {
        private static ManualLogSource _source;

        /// <summary>是否输出调试级日志（对应配置里的 VerboseLogging）。</summary>
        internal static bool Verbose;

        internal static void Bind(ManualLogSource source) => _source = source;

        internal static void Info(string message) => _source?.LogInfo(message);
        internal static void Warn(string message) => _source?.LogWarning(message);
        internal static void Error(string message) => _source?.LogError(message);

        internal static void Debug(string message)
        {
            if (Verbose) _source?.LogDebug(message);
        }
    }
}
