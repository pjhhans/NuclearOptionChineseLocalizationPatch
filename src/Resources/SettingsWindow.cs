using System;
using NuclearOptionChineseLocalizationPatch.Core;
using NuclearOptionChineseLocalizationPatch.Diagnostics;
using NuclearOptionChineseLocalizationPatch.Patching;
using UnityEngine;

namespace NuclearOptionChineseLocalizationPatch.Resources
{
    /// <summary>
    /// 设置与诊断窗口（F11 呼出）。
    ///
    /// <para><b>它存在的理由不只是"设置"。</b>这个插件最难排查的一类问题是
    /// 「日志干净但界面上什么都没发生」—— 翻译明明没生效，而日志里看不出异常。
    /// 把状态和最近的实际翻译结果<b>直接画在屏幕上</b>，用户一眼就能判断：
    /// 宿主有没有活、词表有没有载入、某条文本到底是命中了还是漏掉了。</para>
    ///
    /// <para>纯 IMGUI 实现（<c>OnGUI</c> + <c>GUILayout</c>）：不依赖任何预制体或资源，
    /// 插件目录里只有 DLL 和数据文件也能工作。</para>
    /// </summary>
    internal static class SettingsWindow
    {
        internal static bool Visible { get; private set; }

        internal static void Toggle()
        {
            Visible = !Visible;
            Log.Info(Visible ? "设置窗口已打开。" : "设置窗口已关闭。");
        }

        /// <summary>没有窗口对象可以 Destroy，宿主重建时只需把可见性收起来。</summary>
        internal static void Hide()
        {
            Visible = false;
        }

        private static Rect _rect = new Rect(24f, 24f, 560f, 660f);
        private static Vector2 _scroll;
        private const int WindowId = 0x4E4F5A43; // "NOZC"

        /// <summary>实时生效的扫描间隔（秒）。初值取自配置。</summary>
        internal static float ScanInterval = 1f;

        /// <summary>兜底扫描总开关。默认关闭（配置 ScanIntervalSeconds = 0），窗口里可实时开合。</summary>
        internal static bool ScanEnabled;

        // ------------------------------------------------------------------ 每帧快照

        // IMGUI 一帧之内会为 Layout / Repaint / 各种输入事件多次调用 OnGUI。
        // 诊断区是**变长**的（逐条 Label），只要两个 pass 之间条数不一样，Unity 就抛
        // "GUILayout: Mismatched LayoutGroup"，窗口当场画坏 —— 而「窗口画坏」对正在
        // 验证"到底生效了没有"的人来说是一次假阴性，代价比多几行代码高得多。
        // 所以每帧第一次进入时取一份快照，之后整个帧只读这份快照，条数恒定。
        private static long _snapshotFrame = -1;
        private static long _hitTotal;
        private static long _missTotal;
        private static string[] _hitLines = new string[0];
        private static string[] _missLines = new string[0];

        private static void RefreshSnapshot()
        {
            // 注意：本 Unity 版本里 Time.frameCount 是 long，别写成 int。
            long frame = Time.frameCount;
            if (frame == _snapshotFrame) return;
            _snapshotFrame = frame;

            TextLocalizer localizer = LocalizationPlugin.Localizer;
            _hitLines = localizer == null ? new string[0] : localizer.SnapshotRecentHits();
            _hitTotal = localizer == null ? 0 : localizer.HitCount;

            MissLog missLog = LocalizationPlugin.MissLog;
            _missLines = missLog == null ? new string[0] : missLog.SnapshotRecent();
            _missTotal = missLog == null ? 0 : missLog.FragmentCount;
        }

        // ------------------------------------------------------------------ 字体

        private static Font _cjkFont;
        private static bool _fontResolved;

        /// <summary>
        /// IMGUI 用的是旧版 <see cref="Font"/>，跟 TMP 那套回退链完全无关 ——
        /// 不额外准备一个含中日韩字形的字体，窗口里的中文会全是方块。
        /// </summary>
        private static void ResolveFont()
        {
            if (_fontResolved) return;
            _fontResolved = true;
            try
            {
                _cjkFont = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑", "SimHei", "SimSun", "Noto Sans CJK SC" },
                    14);
            }
            catch (Exception ex)
            {
                Log.Warn("设置窗口：系统字体加载失败，窗口内中文可能显示为方块：" + ex.Message);
            }
        }

        // ------------------------------------------------------------------ 绘制入口

        internal static void Draw()
        {
            if (!Visible) return;

            ResolveFont();
            Font previous = GUI.skin.font;
            if (_cjkFont != null) GUI.skin.font = _cjkFont;
            try
            {
                _rect = GUI.Window(WindowId, _rect, DrawContents, "核选项 汉化 · 设置与诊断　　　F11 关闭");
            }
            finally
            {
                GUI.skin.font = previous;
            }
        }

        private static void DrawContents(int windowId)
        {
            RefreshSnapshot();

            GUILayout.BeginVertical();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            DrawActions();
            GUILayout.Space(6);
            DrawStatus();
            GUILayout.Space(6);
            DrawTuning();
            GUILayout.Space(6);
            DrawDiagnostics();

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        // ------------------------------------------------------------------ 操作

        private static void DrawActions()
        {
            GUILayout.Label("── 操作 ──");

            GUILayout.BeginHorizontal();

            bool enabled = LocalizationPlugin.Localizer != null && LocalizationPlugin.Localizer.Enabled;
            if (GUILayout.Button(enabled ? "关闭翻译（还原英文）" : "开启翻译", GUILayout.Height(26f)))
            {
                LocalizationPlugin.SetEnabled(!enabled);
            }

            if (GUILayout.Button("重新载入词表", GUILayout.Height(26f)))
            {
                LocalizationPlugin.ReloadFromDisk();
                LocalizationPlugin.ScanScene();
            }

            if (GUILayout.Button("立即扫描场景", GUILayout.Height(26f)))
            {
                LocalizationPlugin.ScanScene();
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("清空翻译缓存", GUILayout.Height(24f)))
            {
                LocalizationPlugin.ClearCaches();
                Log.Info("翻译缓存已清空。");
            }

            if (GUILayout.Button("清空漏译记录", GUILayout.Height(24f)))
            {
                LocalizationPlugin.MissLog?.Clear();
                LocalizationPlugin.MissLog?.Flush();
                Log.Info("漏译记录已清空并重写。");
            }

            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------ 状态

        private static void DrawStatus()
        {
            GUILayout.Label("── 状态 ──");

            TextLocalizer localizer = LocalizationPlugin.Localizer;
            LocalizationTable table = LocalizationPlugin.Table;
            ExclusionRules exclusions = LocalizationPlugin.Exclusions;
            MissLog missLog = LocalizationPlugin.MissLog;

            Row("逐帧宿主", RuntimeStatus.HostAlive
                ? $"已就绪（第 {RuntimeStatus.HostBuilds} 次创建）—— 热键/兜底扫描/防回写工作中"
                : "未就绪（等待下一个场景加载时重建）");

            Row("翻译开关", localizer == null ? "未初始化" : (localizer.Enabled ? "已启用" : "已关闭（正在还原英文）"));

            Row("词表", table == null
                ? "未载入"
                : $"普通 {table.GlobalCount} ／ 模板 {table.TemplateCount} ／ 片段 {table.FragmentCount} ／ 作用域 {table.ScopeCount}");

            Row("排除名单", exclusions == null
                ? "未载入"
                : $"scopes {exclusions.ScopeCount} ／ terms {exclusions.TermCount} ／ texts {exclusions.TextCount}");

            Row("最近载入", RuntimeStatus.LastReloadAt + " · " + RuntimeStatus.LastReloadDetail
                           + $"（共 {RuntimeStatus.ReloadCount} 次）");

            Row("场景", RuntimeStatus.LastScene + $"（共 {RuntimeStatus.SceneLoads} 次加载，兜底扫描 {RuntimeStatus.Scans} 次）");

            Row("运行统计", localizer == null
                ? "未初始化"
                : $"命中 {localizer.HitCount} ／ 漏译 {localizer.MissCount} ／ 缓存 {localizer.CacheCount} ／ 放过 {localizer.PassThroughCount}");

            Row("防回写", $"跟踪 {RewriteGuard.TrackedCount} 个组件，已放弃 {RewriteGuard.GivenUpCount} 个"
                          + (RewriteGuard.Disabled ? "（已停用）" : ""));

            Row("漏译记录", missLog == null
                ? "未初始化"
                : $"片段 {missLog.FragmentCount} 条 ／ 长文本 {missLog.LongTextCount} 条"
                  + (missLog.Recording ? "" : "（已暂停记录）"));

            Row("数据目录", RuntimeStatus.DataDir);
        }

        private static void Row(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + "：", GUILayout.Width(84f));
            GUILayout.Label(value);
            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------ 调参

        private static void DrawTuning()
        {
            GUILayout.Label("── 调节 ──");

            ScanEnabled = GUILayout.Toggle(ScanEnabled, " 兜底扫描（默认关；某些文本一直英文时打开）");
            if (ScanEnabled)
            {
                GUILayout.Label($"兜底扫描间隔：{ScanInterval:F2} 秒　（决定新出现的文本多久变中文）");
                ScanInterval = GUILayout.HorizontalSlider(ScanInterval, 0.1f, 5f);
                GUILayout.Label("界面稳定时扫描自动放慢（最长 8 倍间隔）；拖动视角时暂停，松开即恢复。");
            }

            TextLocalizer localizer = LocalizationPlugin.Localizer;
            if (localizer != null)
            {
                localizer.CaptureRecent = GUILayout.Toggle(localizer.CaptureRecent, " 记录最近命中（诊断区用）");
            }

            MissLog missLog = LocalizationPlugin.MissLog;
            if (missLog != null)
            {
                bool recording = GUILayout.Toggle(missLog.Recording, " 累积漏译到 missing.json / untranslated.json");
                missLog.Recording = recording;
            }

            bool verbose = GUILayout.Toggle(Log.Verbose, " 输出调试级日志（写盘频繁，仅排障时开）");
            Log.Verbose = verbose;
        }

        // ------------------------------------------------------------------ 诊断

        private static void DrawDiagnostics()
        {
            // 只读 RefreshSnapshot() 取的快照，绝不再直接问 Localizer / MissLog ——
            // 否则条数可能在两个 pass 之间变化，见快照区的说明。
            GUILayout.Label($"── 最近命中（原文 → 译文）　累计 {_hitTotal} 条 ──");
            if (_hitLines.Length == 0)
            {
                GUILayout.Label("　（还没有任何一条文本被翻成中文 —— 若界面上确实有英文，问题在补丁没被执行）");
            }
            else
            {
                for (int i = _hitLines.Length - 1; i >= 0; i--) GUILayout.Label("　" + _hitLines[i]);
            }

            GUILayout.Space(6);
            GUILayout.Label($"── 最近漏译（<作用域>片段）　累计 {_missTotal} 条 ──");
            if (_missLines.Length == 0)
            {
                GUILayout.Label("　（无）");
            }
            else
            {
                for (int i = _missLines.Length - 1; i >= 0; i--) GUILayout.Label("　" + _missLines[i]);
            }
        }
    }
}
