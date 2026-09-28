using System;
using System.Diagnostics;
using System.Text;

namespace NuclearOptionChineseLocalizationPatch.Diagnostics
{
    /// <summary>
    /// 性能探针：给热路径打点，量出「成本到底花在哪一段」，为是否值得做后续优化提供依据。
    ///
    /// <para><b>默认关闭、且不落盘</b>（与「记录最近命中」「调试日志」同属 F11 里的临时开关）。
    /// 关闭时 <see cref="Begin"/> 只做一次静态布尔读并返回 0，<see cref="End"/> 立即返回 ——
    /// 热路径上只多一次可被完美预测的分支，对帧时间无可测影响。</para>
    ///
    /// <para><b>为什么同时给"次数"和"耗时"。</b>名单判定、作用域查询这类子段单次只有几十纳秒，
    /// 而 <see cref="Stopwatch.GetTimestamp"/> 自己就要付相近量级 —— 若只看耗时，这些段的
    /// 绝对毫秒数会被显著放大（测量开销大于被测开销）。所以本探针把<b>每秒调用次数</b>
    /// 作为主数据（它未被计时开销污染），耗时列只用于看量级与相对关系。</para>
    ///
    /// <para><b>峰值为什么必须剔除样本。</b>GC 暂停期间任一时间戳差都会变成几毫秒，
    /// 只要它落在被测段内，"单次峰值"就会被钉成 GC 时长（实测出现过 5656 µs 的组件名推断
    /// —— 一次字符串取值不可能要 5 毫秒）。所以超过 <see cref="PeakPollutionUs"/> 的样本
    /// 不计入峰值，只累加一个<b>剔除计数</b>；那个计数同时是"本窗口被 GC 打断了几次"的旁证。
    /// 并且峰值<b>按窗口重置</b> —— 累计峰值一旦被最大值钉死，后面所有窗口都显示同一个数，
    /// 等于死数据。</para>
    ///
    /// <para><b>分配为什么不用堆差。</b>堆大小差在窗口内发生回收时会偏小、甚至为负
    /// （实测出过 −6970 KB/秒 这种作废读数）。这里改为<b>逐帧采样、只累加增长</b>：
    /// 回收造成的下降不计入，读数恒非负，是真实分配的<b>下界</b>（同一帧内分配又被回收的
    /// 那部分量不到）。同时给 <c>B/次赋值</c> 与 <c>B/帧</c> 两个视角 —— 前者是"按文本付的成本"，
    /// 后者是"每帧固定要付的成本"，两者混在一个 KB/秒 里没有跨场景可比性。</para>
    ///
    /// <para><b>不依赖 UnityEngine</b>：结算窗口固定 1 秒，由宿主每帧喂一个时间戳
    /// （<see cref="FrameTick"/>），分配的帧采样与帧计数也在那里顺带完成。
    /// 这样本类可以脱离 Unity 编译（测试台直链本文件），也便于写回归用例。</para>
    ///
    /// <para><b>已知局限（判读时注意）</b>：各段耗时是「子系统累计」，会包含递归调用内部的时间；
    /// 而 <see cref="Seg.Total"/> 只取最外层一次赋值的墙钟。因此各段之和**不要求**等于 Total，
    /// 两者应分开看：Total 给「每次赋值的固定成本」，分段给「成本花在哪」。</para>
    /// </summary>
    internal static class PerfProbe
    {
        /// <summary>是否开启采样（F11 开关，不落盘）。</summary>
        internal static bool Enabled { get; private set; }

        /// <summary>热路径分段。顺序即显示顺序。</summary>
        internal enum Seg
        {
            /// <summary>一次最外层赋值（含递归）的墙钟 —— 即「每次赋值的固定成本」。</summary>
            Total = 0,
            /// <summary>由组件名推断作用域（原生 name 编组 + 重名后缀判定）。</summary>
            ScopeOf,
            /// <summary>不翻译名单 / 保持英文术语的判定。</summary>
            NameList,
            /// <summary>整段模板查询（含归一化与两级回落）。</summary>
            Template,
            /// <summary>作用域分表查询。</summary>
            Scoped,
            /// <summary>结果缓存查询 + 主体流水线（<c>LocalizeCore</c>）。</summary>
            Core,
            Count
        }

        private const int SegCount = (int)Seg.Count;

        /// <summary>
        /// 超过这个单次耗时的样本一律判为受 GC / 系统抢占污染，不入峰值。
        /// 取 1000 µs：热路径上最贵的真实单次调用是「长模板的归一化」，量级几十 µs，
        /// 而 GC 暂停是毫秒级 —— 两侧差一个数量级，门限放在中间不会误杀。
        /// </summary>
        private const double PeakPollutionUs = 1000.0;

        /// <summary>自开启以来：耗时（tick）、调用次数、剔除污染后的单次峰值。</summary>
        private static readonly long[] _ticks = new long[SegCount];
        private static readonly long[] _calls = new long[SegCount];
        private static readonly long[] _peak = new long[SegCount];

        /// <summary>本窗口内的单次峰值与被剔除的污染样本数（每个窗口起窗时清零）。</summary>
        private static readonly long[] _winPeak = new long[SegCount];
        private static readonly int[] _winPolluted = new int[SegCount];

        /// <summary>当前 1 秒窗口起点的累计值，用来取增量。</summary>
        private static readonly long[] _baseTicks = new long[SegCount];
        private static readonly long[] _baseCalls = new long[SegCount];

        private static int _baseGc0, _baseGc1, _baseGc2;
        private static long _baseAllocSum, _allocSum;
        private static long _lastHeap;

        /// <summary>帧计数（<see cref="FrameTick"/> 每次调用加一）。用于把分配拆成「按帧 / 按次」。</summary>
        private static long _frames, _baseFrames;

        private static float _windowStart;
        private static bool _windowReady;

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        /// <summary>一个 tick 折合多少微秒（<see cref="Stopwatch.Frequency"/> 通常 10⁷ ⇒ 0.1 µs）。</summary>
        private static readonly double UsPerTick = 1000.0 / Stopwatch.Frequency * 1000.0;

        /// <summary>污染门限折成 tick。★ 换算方向别写反：tick 比微秒更细，除数应是 UsPerTick。</summary>
        private static readonly long PollutionTicks = (long)(PeakPollutionUs / UsPerTick);

        // ---- 对外快照（最近一个完整 1 秒窗口） ----

        internal static float WindowSeconds;
        internal static int CallsPerSecond;
        internal static float MsPerSecond;
        internal static float UsPerCall;
        internal static int Gc0PerSecond, Gc1PerSecond, Gc2PerSecond;
        internal static float KbPerSecond;

        /// <summary>本窗口分配 ÷ 赋值次数 —— 「按文本付的成本」。</summary>
        internal static float BytesPerCall;

        /// <summary>本窗口分配 ÷ 帧数 —— 「每帧固定要付的成本」（无帧数据时为 0）。</summary>
        internal static float BytesPerFrame;

        internal static readonly float[] SegMsPerSecond = new float[SegCount];
        internal static readonly int[] SegCallsPerSecond = new int[SegCount];

        /// <summary>本窗口各段单次峰值（微秒，已剔除污染样本）。</summary>
        internal static readonly float[] SegPeakUs = new float[SegCount];

        /// <summary>本窗口各段被剔除的污染样本数（GC / 系统抢占）。</summary>
        internal static readonly int[] SegPeakPolluted = new int[SegCount];

        /// <summary>开关采样。切换时重新起窗，避免把切换前的巨大间隔带进第一个窗口。</summary>
        internal static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled) return;
            Enabled = enabled;
            Reset();
            Log.Info(enabled
                ? "性能探针已开启（测量期间有额外计时开销，仅供取样）。"
                : "性能探针已关闭。");
        }

        /// <summary>清零累计与当前窗口。</summary>
        internal static void Reset()
        {
            Array.Clear(_ticks, 0, SegCount);
            Array.Clear(_calls, 0, SegCount);
            Array.Clear(_peak, 0, SegCount);
            Array.Clear(_winPeak, 0, SegCount);
            Array.Clear(_winPolluted, 0, SegCount);
            Array.Clear(_baseTicks, 0, SegCount);
            Array.Clear(_baseCalls, 0, SegCount);
            Array.Clear(SegMsPerSecond, 0, SegCount);
            Array.Clear(SegCallsPerSecond, 0, SegCount);
            Array.Clear(SegPeakUs, 0, SegCount);
            Array.Clear(SegPeakPolluted, 0, SegCount);

            _frames = _baseFrames = 0;
            _allocSum = _baseAllocSum = 0;
            _lastHeap = GC.GetTotalMemory(false);

            _windowReady = false;
            WindowSeconds = 0f;
            CallsPerSecond = 0;
            MsPerSecond = 0f;
            UsPerCall = 0f;
            Gc0PerSecond = Gc1PerSecond = Gc2PerSecond = 0;
            KbPerSecond = 0f;
            BytesPerCall = 0f;
            BytesPerFrame = 0f;
        }

        /// <summary>计时起点。关闭时返回 0，<see cref="End"/> 据此零成本跳过。</summary>
        internal static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0L;

        /// <summary>计时终点。起点的 0 值兜住「Begin 时开启、End 前刚被关掉」的边界。</summary>
        internal static void End(Seg seg, long start)
        {
            if (start == 0L) return;
            long delta = Stopwatch.GetTimestamp() - start;
            int i = (int)seg;
            _ticks[i] += delta;
            _calls[i]++;

            if (PollutionTicks > 0 && delta > PollutionTicks)
            {
                // 这段时间不属于被测代码，剔除；只记一次「本窗口被打断过」。
                _winPolluted[i]++;
                return;
            }
            if (delta > _peak[i]) _peak[i] = delta;
            if (delta > _winPeak[i]) _winPeak[i] = delta;
        }

        /// <summary>
        /// 宿主每帧喂一个时间戳（<c>Time.realtimeSinceStartup</c>）。内部自带 1 秒节流，
        /// 同时顺带完成分配的帧采样与帧计数（只在开启时做）。
        /// </summary>
        internal static void FrameTick(float nowSeconds)
        {
            if (!Enabled) return;

            _frames++;
            SampleAllocation();

            if (!_windowReady) { StartWindow(nowSeconds); return; }

            float dt = nowSeconds - _windowStart;
            if (dt < 1f) return;
            Settle(dt);
            StartWindow(nowSeconds);
        }

        /// <summary>
        /// 逐帧采样托管堆，<b>只累加增长量</b>：回收造成的下降不计入，读数恒非负。
        /// 结果是真实分配的<b>下界</b>（同一帧内分配又被回收的那部分量不到），
        /// 但它比"窗口两端的堆差"稳得多 —— 后者遇到回收会直接给负数。
        /// </summary>
        private static void SampleAllocation()
        {
            long now = GC.GetTotalMemory(false);
            long delta = now - _lastHeap;
            if (delta > 0) _allocSum += delta;
            _lastHeap = now;
        }

        private static void StartWindow(float now)
        {
            _windowStart = now;
            _windowReady = true;
            for (int i = 0; i < SegCount; i++)
            {
                _baseTicks[i] = _ticks[i];
                _baseCalls[i] = _calls[i];
                _winPeak[i] = 0;
                _winPolluted[i] = 0;
            }
            _baseFrames = _frames;
            _baseGc0 = GC.CollectionCount(0);
            _baseGc1 = GC.CollectionCount(1);
            _baseGc2 = GC.CollectionCount(2);
            _baseAllocSum = _allocSum;
            _lastHeap = GC.GetTotalMemory(false);
        }

        private static void Settle(float dt)
        {
            WindowSeconds = dt;

            for (int i = 0; i < SegCount; i++)
            {
                long tickDelta = _ticks[i] - _baseTicks[i];
                long callDelta = _calls[i] - _baseCalls[i];
                SegCallsPerSecond[i] = callDelta > int.MaxValue ? int.MaxValue : (int)callDelta;
                SegMsPerSecond[i] = (float)(tickDelta * MsPerTick / dt);
                SegPeakUs[i] = (float)(_winPeak[i] * MsPerTick * 1000.0);
                SegPeakPolluted[i] = _winPolluted[i];
            }

            CallsPerSecond = SegCallsPerSecond[(int)Seg.Total];
            MsPerSecond = SegMsPerSecond[(int)Seg.Total];
            UsPerCall = CallsPerSecond > 0 ? MsPerSecond / CallsPerSecond * 1000f : 0f;

            Gc0PerSecond = Round(GC.CollectionCount(0) - _baseGc0, dt);
            Gc1PerSecond = Round(GC.CollectionCount(1) - _baseGc1, dt);
            Gc2PerSecond = Round(GC.CollectionCount(2) - _baseGc2, dt);

            long allocDelta = _allocSum - _baseAllocSum;
            KbPerSecond = (float)(allocDelta / 1024.0 / dt);
            BytesPerCall = CallsPerSecond > 0 ? (float)(allocDelta / (double)CallsPerSecond) : 0f;

            long frameDelta = _frames - _baseFrames;
            BytesPerFrame = frameDelta > 0 ? (float)(allocDelta / (double)frameDelta) : 0f;
        }

        private static int Round(long count, float dt)
        {
            double perSecond = count / dt;
            if (perSecond > int.MaxValue) return int.MaxValue;
            return (int)Math.Round(perSecond);
        }

        // ------------------------------------------------------------------ 显示

        /// <summary>第一行：总量（赋值频次 / 全流程耗时 / GC / 分配及其两个视角）。</summary>
        internal static string SummaryLine()
        {
            if (!_windowReady || WindowSeconds <= 0f) return "　（采样中…保持窗口打开约 1 秒）";

            var sb = new StringBuilder(200);
            sb.Append("　赋值 ").Append(CallsPerSecond).Append(" 次/秒")
              .Append("　全流程 ").Append(MsPerSecond.ToString("F2")).Append(" ms/秒")
              .Append("（").Append(UsPerCall.ToString("F2")).Append(" µs/次）")
              .Append("　GC ").Append(Gc0PerSecond).Append('/').Append(Gc1PerSecond).Append('/').Append(Gc2PerSecond).Append(" 次/秒")
              .Append("　分配 ").Append(KbPerSecond.ToString("F0")).Append(" KB/秒（")
              .Append(BytesPerCall.ToString("F0")).Append(" B/次");
            if (BytesPerFrame > 0f) sb.Append(" · ").Append(BytesPerFrame.ToString("F0")).Append(" B/帧");
            sb.Append("）");
            return sb.ToString();
        }

        /// <summary>第二行：分段（次数为主、耗时为辅）。</summary>
        internal static string SegmentLine()
        {
            if (!_windowReady || WindowSeconds <= 0f) return "　（分段数据等待首个窗口结算）";

            var sb = new StringBuilder(240);
            sb.Append("　分段 次/秒 ");
            AppendCalls(sb, Seg.ScopeOf, "名字");
            AppendCalls(sb, Seg.NameList, "名单");
            AppendCalls(sb, Seg.Template, "模板");
            AppendCalls(sb, Seg.Scoped, "作用域");
            AppendCalls(sb, Seg.Core, "核心");

            sb.Append("　│　ms/秒 ");
            AppendMs(sb, Seg.ScopeOf, "名字");
            AppendMs(sb, Seg.NameList, "名单");
            AppendMs(sb, Seg.Template, "模板");
            AppendMs(sb, Seg.Scoped, "作用域");
            AppendMs(sb, Seg.Core, "核心");
            return sb.ToString();
        }

        private static void AppendCalls(StringBuilder sb, Seg seg, string label)
        {
            sb.Append(' ').Append(label).Append(' ').Append(SegCallsPerSecond[(int)seg]);
        }

        private static void AppendMs(StringBuilder sb, Seg seg, string label)
        {
            sb.Append(' ').Append(label).Append(' ').Append(SegMsPerSecond[(int)seg].ToString("F2"));
        }

        /// <summary>
        /// 第三行：本窗口各段单次峰值（微秒，已剔除 GC 污染样本），末尾给剔除总数。
        /// 峰值<b>按窗口</b>给 —— 累计峰值会被一次 GC 钉死，之后每个窗口都显示同一个数。
        /// </summary>
        internal static string PeakLine()
        {
            if (!_windowReady || WindowSeconds <= 0f) return string.Empty;

            var sb = new StringBuilder(200);
            sb.Append("　窗口峰值 µs ");
            AppendPeak(sb, Seg.ScopeOf, "名字");
            AppendPeak(sb, Seg.NameList, "名单");
            AppendPeak(sb, Seg.Template, "模板");
            AppendPeak(sb, Seg.Scoped, "作用域");
            AppendPeak(sb, Seg.Core, "核心");

            int polluted = 0;
            for (int i = 0; i < SegCount; i++) polluted += SegPeakPolluted[i];
            sb.Append("　│　剔除 GC 污染样本 ").Append(polluted).Append(" 次");
            return sb.ToString();
        }

        private static void AppendPeak(StringBuilder sb, Seg seg, string label)
        {
            sb.Append(' ').Append(label).Append(' ').Append(SegPeakUs[(int)seg].ToString("F2"));
        }

        // ---- 累计读数（自开启以来；与上面的「每秒快照」是两个视角） ----

        /// <summary>某段自开启以来的调用次数。</summary>
        internal static long CallCount(Seg seg) => _calls[(int)seg];

        /// <summary>某段自开启以来的累计耗时（毫秒）。</summary>
        internal static double ElapsedMs(Seg seg) => _ticks[(int)seg] * MsPerTick;

        /// <summary>某段自开启以来的单次峰值（微秒，已剔除 GC 污染样本）。</summary>
        internal static double PeakUs(Seg seg)
        {
            return _peak[(int)seg] * MsPerTick * 1000.0;
        }
    }
}
