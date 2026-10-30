using System;
using System.Globalization;
using System.IO;
using System.Text;
using NuclearOptionChineseLocalizationPatch.Resources;

namespace NuclearOptionChineseLocalizationPatch.Diagnostics
{
    /// <summary>
    /// 把 <see cref="PerfProbe"/> 的每秒窗口落成 CSV。
    ///
    /// <para><b>为什么要落盘而不是看 F11 面板。</b>面板只有最近一秒的三行，人工读屏
    /// 既拿不到时间序列、也读不出小数点。而要回答「本地化到底每帧吃多少毫秒」，
    /// 需要的是<b>一段飞行内成百个窗口的分布</b>（中位数、分位、抖动），不是某一个瞬间。</para>
    ///
    /// <para><b>为什么探针读数能替代整帧 A/B。</b>独立记录插件量的是整帧时长，
    /// 而实测同一遍录制内、同高度巡航的 20 秒窗 P50 极差就有 1.0~2.0 ms ——
    /// 远超要检出的 0.3 ms，信号被工况漂移彻底遮住。探针只量<b>自己这段代码的墙钟</b>，
    /// 外部负载再怎么漂也不会进分子；再除以同窗口的帧数，就得到「每帧本地化耗时」。</para>
    ///
    /// <para><b>这个读数偏向哪边。</b>偏<b>高</b>：开启探针后每次赋值多两次
    /// <c>Stopwatch.GetTimestamp</c>（约 20~30 ns × 每帧调用次数），这部分算在被测的
    /// Total 段之外，但确实增加了帧时长；同时它会打乱 CPU 的指令缓存局部性。
    /// 因此读出来的 ms/帧 是<b>上界</b>，真实值只会更小。</para>
    ///
    /// <para><b>失败必须静默降级。</b>落盘失败（目录只读、磁盘满、文件被 Excel 锁住）
    /// 绝不能让热路径抛异常 —— 一个 <c>IOException</c> 从 <c>Settle</c> 里冒出去，
    /// 就是每秒一次的日志刷屏甚至补丁失效。所以全程 try/catch，一旦出错就把自己钉成
    /// 失效状态，不再重试。</para>
    /// </summary>
    internal static class ProbeDump
    {
        /// <summary>
        /// 每写多少行刷一次盘。取 1（每秒一次）：一次 <c>Flush</c> 只是把缓冲交给 OS，
        /// 几十微秒，占一秒的万分之一，且发生在被测段之外（Total 只包赋值本身）；
        /// 换来的是崩溃 / 强退时最多只丢最后一行，而不是九行。
        /// </summary>
        private const int FlushEveryRows = 1;

        private static StreamWriter _writer;
        private static string _path;
        private static bool _broken;
        private static int _rows;
        private static DateTime _openedAt;

        /// <summary>当前落盘文件路径（未开启或已失效时为 null）。</summary>
        internal static string CurrentPath => _writer != null ? _path : null;

        /// <summary>是否正在落盘。</summary>
        internal static bool Active => _writer != null;

        /// <summary>已写入的窗口行数。</summary>
        internal static int Rows => _rows;

        /// <summary>开始落盘。重复调用无副作用；失败后不再重试。</summary>
        internal static void Open()
        {
            if (_writer != null || _broken) return;
            try
            {
                string dir = PluginPaths.BaseDir;
                Directory.CreateDirectory(dir);
                _openedAt = DateTime.Now;
                _path = Path.Combine(dir, "probe-" + _openedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
                _writer = new StreamWriter(_path, false, new UTF8Encoding(false)) { AutoFlush = false };
                _writer.WriteLine(
                    "wall,win_s,frames,fps,calls,cps,ms_per_s,us_per_call,ms_per_frame," +
                    "ms_total,cd_total,ms_scopeof,cd_scopeof,ms_namelist,cd_namelist," +
                    "ms_template,cd_template,ms_scoped,cd_scoped,ms_core,cd_core," +
                    "gc0,gc1,gc2,kb_per_s,b_per_call,b_per_frame");
                _writer.Flush();
                _rows = 0;
                Log.Info("探针落盘已开始：" + _path);
            }
            catch (Exception e)
            {
                _broken = true;
                _writer = null;
                Log.Warn("探针落盘不可用（不影响翻译）：" + e.Message);
            }
        }

        /// <summary>结束落盘，并在日志里留一行汇总，方便事后定位文件。</summary>
        internal static void Close()
        {
            if (_writer == null) return;
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch
            {
                // 关闭失败没有补救价值，吞掉即可。
            }
            finally
            {
                _writer = null;
            }
            Log.Info($"探针落盘结束：{_path}（{_rows} 个窗口，{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}）");
        }

        /// <summary>
        /// 写一个已结算的窗口。由 <see cref="PerfProbe"/> 在 <c>Settle</c> 末尾调用。
        /// 参数全部是「本窗口」的量，不做任何换算 —— 换算口径统一留在 <see cref="PerfProbe"/> 里。
        /// </summary>
        internal static void WriteRow(
            float windowSeconds, int frames,
            int calls, float msPerSecond, float usPerCall,
            int gc0, int gc1, int gc2,
            float kbPerSecond, float bytesPerCall, float bytesPerFrame)
        {
            if (_writer == null) return;
            try
            {
                float fps = windowSeconds > 0f ? frames / windowSeconds : 0f;
                // 每帧本地化耗时：本窗口总毫秒 ÷ 本窗口帧数。这才是能与帧时长直接相减的量。
                float msPerFrame = frames > 0 ? msPerSecond / frames : 0f;

                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder(320);
                sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", inv)).Append(',')
                  .Append(windowSeconds.ToString("F3", inv)).Append(',')
                  .Append(frames).Append(',')
                  .Append(fps.ToString("F2", inv)).Append(',')
                  .Append(calls).Append(',')
                  .Append(calls > 0 && windowSeconds > 0f ? (calls / windowSeconds).ToString("F1", inv) : "0").Append(',')
                  .Append(msPerSecond.ToString("F4", inv)).Append(',')
                  .Append(usPerCall.ToString("F3", inv)).Append(',')
                  .Append(msPerFrame.ToString("F5", inv)).Append(',');

                AppendSegment(sb, PerfProbe.Seg.Total, inv);
                AppendSegment(sb, PerfProbe.Seg.ScopeOf, inv);
                AppendSegment(sb, PerfProbe.Seg.NameList, inv);
                AppendSegment(sb, PerfProbe.Seg.Template, inv);
                AppendSegment(sb, PerfProbe.Seg.Scoped, inv);
                AppendSegment(sb, PerfProbe.Seg.Core, inv);

                sb.Append(gc0).Append(',').Append(gc1).Append(',').Append(gc2).Append(',')
                  .Append(kbPerSecond.ToString("F1", inv)).Append(',')
                  .Append(bytesPerCall.ToString("F1", inv)).Append(',')
                  .Append(bytesPerFrame.ToString("F1", inv));

                _writer.WriteLine(sb.ToString());
                if (++_rows % FlushEveryRows == 0) _writer.Flush();
            }
            catch (Exception e)
            {
                _broken = true;
                try { _writer?.Dispose(); } catch { }
                _writer = null;
                Log.Warn("探针落盘中途失效（不影响翻译）：" + e.Message);
            }
        }

        private static void AppendSegment(StringBuilder sb, PerfProbe.Seg seg, CultureInfo inv)
        {
            int i = (int)seg;
            sb.Append(PerfProbe.SegMsPerSecond[i].ToString("F4", inv)).Append(',')
              .Append(PerfProbe.SegCallsPerSecond[i]).Append(',');
        }
    }
}
