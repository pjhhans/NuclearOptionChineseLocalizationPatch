using System;

namespace NuclearOptionChineseLocalizationPatch.Diagnostics
{
    /// <summary>
    /// 运行期状态快照，纯供设置窗口显示。
    ///
    /// <para><b>为什么要单独放一个类：</b>这些数据全都要在「宿主被销毁重建」之后仍然存在，
    /// 所以不能放在宿主实例的字段里；而它们又和词表、配置无关，塞进
    /// <c>LocalizationPlugin</c> 会让那个类越来越杂。这里是静态的，任何地方都能写，
    /// 窗口读它不需要拿到任何对象引用。</para>
    ///
    /// <para>写入方：<see cref="Resources.PluginHost"/> 与 <c>LocalizationPlugin</c>。
    /// 读取方：<see cref="Resources.SettingsWindow"/>。</para>
    /// </summary>
    internal static class RuntimeStatus
    {
        // ---- 宿主 ----

        /// <summary>宿主被创建过几次。正常启动至少 1 次，首个场景加载后会 +1（旧宿主被 Unity 销毁）。</summary>
        internal static int HostBuilds;

        /// <summary>宿主当前是否活着（由 PluginHost 每帧刷新）。</summary>
        internal static bool HostAlive;

        // ---- 词表载入 ----

        /// <summary>最近一次载入的时刻，形如 <c>15:31:02</c>。</summary>
        internal static string LastReloadAt = "（尚未载入）";

        internal static bool LastReloadOk;

        /// <summary>最近一次载入的结果描述，直接显示在窗口里。</summary>
        internal static string LastReloadDetail = "（尚未载入）";

        /// <summary>载入次数。含启动那次；每按一次窗口里的「重新载入词表」+1。</summary>
        internal static int ReloadCount;

        // ---- 场景 ----

        internal static string LastScene = "（未知）";
        internal static int SceneLoads;

        /// <summary>兜底扫描执行过多少次。</summary>
        internal static int Scans;

        // ---- 数据目录 ----

        internal static string DataDir = "（未知）";

        internal static string Stamp()
        {
            return DateTime.Now.ToString("HH:mm:ss");
        }
    }
}
