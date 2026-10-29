using System;
using System.Diagnostics;

namespace NuclearOptionChineseLocalizationPatch.Diagnostics
{
    /// <summary>
    /// 卡顿取证：只抓「这一帧明显比别人慢」的时刻，并把本帧我们自己的三块周期性作业耗时一起打出来。
    ///
    /// <para><b>为什么需要它。</b>玩家反馈「拖动视角每隔几秒卡一下」时，能造成这件事的候选很多：
    /// 我们的巡检 / 自愈扫 / 防回写、托管堆 GC、TMP 动态图集上传、游戏自己的加载。
    /// 光看代码只能排序、不能定案。这里做一件事：<b>把「卡的那一帧」和「我们是不是元凶」绑在一起记</b> ——
    /// 日志里三个作业都是 0.x ms 而帧仍是 40 ms，就说明不是我们，别再瞎改。</para>
    ///
    /// <para><b>成本。</b>每帧一次 <c>GC.CollectionCount</c> + 一次浮点比较（≈50 ns）；
    /// 三个作业各一次 <c>Stopwatch.GetTimestamp</c>（≈30 ns/次）。
    /// 只有超过阈值才写日志，总预算 24 行，用完自动闭嘴 —— 不会把日志刷成噪声。</para>
    ///
    /// <para><b>阈值为什么是「25 ms 且 2.5 倍近期均值」。</b>60 fps 是 16.7 ms，跑不满 40 fps
    /// 才会到 25 ms，那是肉眼能看出「顿一下」的量级；同时要求相对均值也异常，
    /// 避免在本来就 20 fps 的场面里刷屏。</para>
    /// </summary>
    internal static class FrameWatch
    {
        /// <summary>被计时的作业。顺序即日志顺序。</summary>
        internal enum Job
        {
            /// <summary><c>RunBlankReport</c> + 兜底扫描：补挂回退链、诊断报告、补翻（2~10 秒一次）。</summary>
            Periodic = 0,
            /// <summary><c>ScanBlankSlice</c>：滚动自愈 + 即时通道（每帧）。</summary>
            Sweep = 1,
            /// <summary><c>RewriteGuard.Tick</c>：防回写补偿（每帧）。</summary>
            Rewrite = 2,
            Count
        }

        private const int JobCount = (int)Job.Count;

        private static readonly string[] JobNames = { "巡检", "自愈扫", "防回写" };

        /// <summary>绝对下限：低于这个毫秒数的帧不算卡顿。</summary>
        private const float SpikeMs = 25f;

        /// <summary>相对下限：还要超过近期均值的这个倍数才算异常。</summary>
        private const float SpikeRatio = 2.5f;

        /// <summary>总共最多记这么多行，之后彻底停止（含一次「取证结束」提示）。</summary>
        private const int Budget = 24;

        private static readonly double[] JobMs = new double[JobCount];

        private static float _avgMs = 16.7f;
        private static int _frames;
        private static int _left = Budget;
        private static int _lastGc0 = -1;
        private static bool _done;

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        /// <summary>计时起点。永远返回真实值 —— 本探针默认开启，不能返回 0 当哨兵。</summary>
        internal static long Begin()
        {
            return Stopwatch.GetTimestamp();
        }

        /// <summary>计时终点。同一帧内多次调用取最大值。</summary>
        internal static void End(Job job, long start)
        {
            double ms = (Stopwatch.GetTimestamp() - start) * MsPerTick;
            int i = (int)job;
            if (ms > JobMs[i]) JobMs[i] = ms;
        }

        /// <summary>
        /// 每帧**开头**调用一次：结算上一帧（<paramref name="lastFrameSeconds"/> 是上一帧的实际耗时），
        /// 然后清空本帧的作业计时槽。
        ///
        /// <para>放在 Update 开头而不是结尾，是因为「上一帧花了多久」要在这一帧的 Update 里才拿得到，
        /// 而且此时上一帧的 LateUpdate（防回写）也已经跑完。</para>
        /// </summary>
        internal static void FrameStart(float lastFrameSeconds)
        {
            if (_done) return;

            float ms = lastFrameSeconds * 1000f;
            int gc0 = GC.CollectionCount(0);
            int gcDelta = _lastGc0 >= 0 ? gc0 - _lastGc0 : 0;
            _lastGc0 = gc0;

            // 前几帧的 dt 不可信（场景切换/首帧编译着色器），不做判定。
            if (_frames > 5 && ms > SpikeMs && ms > _avgMs * SpikeRatio)
            {
                _left--;
                Log.Info(string.Format(
                    "[卡顿] 本帧 {0:F1} ms（近期均值 {1:F1}，{2:F1}×）｜" +
                    "空白巡检 {3:F2} / 自愈扫 {4:F2} / 防回写 {5:F2} ms｜本帧 GC0 +{6}",
                    ms, _avgMs, ms / _avgMs,
                    JobMs[0], JobMs[1], JobMs[2], gcDelta));

                if (_left <= 0)
                {
                    _done = true;
                    Log.Info("[卡顿] 取证结束（已记满 " + Budget + " 条），后续不再记录。");
                }
            }
            else if (ms < SpikeMs)
            {
                // 只让正常帧参与均值，否则连卡几次会把阈值抬高、后面的卡顿就漏抓了。
                _avgMs += (ms - _avgMs) * 0.03f;
            }

            Array.Clear(JobMs, 0, JobCount);
            _frames++;
        }

        /// <summary>本帧是否还会记日志（供调用方决定要不要计时）。</summary>
        internal static bool Armed
        {
            get { return !_done; }
        }
    }
}
