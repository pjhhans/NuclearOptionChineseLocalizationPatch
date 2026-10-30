using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NuclearOptionChineseLocalizationPatch.Core;

namespace NuclearOptionChineseLocalizationPatch.Diagnostics
{
    /// <summary>
    /// 漏译记录。把"走过完整流水线却没翻出中文"的片段累积下来，供作者补词表用。
    ///
    /// <para>两个刻意的设计：</para>
    /// <list type="bullet">
    /// <item><b>只记片段、不记整段</b> —— 切片阶段每个未命中的段都会调用
    ///       <see cref="Record"/>，于是清单里天然是"最小可翻译单元"，
    ///       正好是补词表时要写的键。整段长文本另由 <see cref="RecordLong"/> 收集。</item>
    /// <item><b>节流写盘</b> —— 只有在脏了且距上次写盘超过间隔时才真正落盘，
    ///       并且写盘失败静默忽略。诊断功能绝不允许影响游戏。</item>
    /// </list>
    /// </summary>
    internal sealed class MissLog
    {
        private readonly HashSet<string> _fragments = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _longTexts = new HashSet<string>(StringComparer.Ordinal);
        private readonly string _fragmentPath;
        private readonly string _longTextPath;

        private DateTime _lastFlush = DateTime.UtcNow;
        private bool _dirty;

        /// <summary>整段留档的最短长度。短于此的走片段清单就够了。</summary>
        private const int LongTextThreshold = 80;

        internal MissLog(string dataDir)
        {
            _fragmentPath = Path.Combine(dataDir, "missing.json");
            _longTextPath = Path.Combine(dataDir, "untranslated.json");
        }

        internal int FragmentCount => _fragments.Count;
        internal int LongTextCount => _longTexts.Count;

        /// <summary>
        /// 是否记录。关掉后不再累积、也不写盘。
        ///
        /// <para><b>默认关闭</b>（启动时由配置 <c>LogMisses</c> 覆盖）。理由：运行期清单里必然混入
        /// 第三方文本（创意工坊物件名、玩家呼号、涂装名），噪声远多于真缺口；且写盘是常驻开销。
        /// 注意「最近漏译」的**内存环形缓冲**不受此开关影响（<see cref="Record"/> 里先入队再判），
        /// 所以关掉它只是不落盘，F11 窗口依然能看到最近 12 条。</para>
        /// </summary>
        internal bool Recording { get; set; } = false;

        private const int RecentCapacity = 12;
        private readonly Queue<string> _recent = new Queue<string>(RecentCapacity);

        /// <summary>
        /// 环里现有的键（**未截断**全量 key）。截断展示在 <see cref="SnapshotRecent"/> 做，
        /// 去重必须按全量比 —— 否则 52 字符之后才不同的两条长文本会互相顶掉。
        /// </summary>
        private readonly HashSet<string> _recentKeys = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>最近 N 条漏译片段，最新的在最前。窗口诊断区用。</summary>
        internal string[] SnapshotRecent()
        {
            var result = new string[_recent.Count];
            int i = 0;
            foreach (string s in _recent) result[i++] = s.Length <= 52 ? s : s.Substring(0, 52) + "…";
            return result;
        }

        internal void Record(string fragment, string scope)
        {
            if (string.IsNullOrEmpty(fragment)) return;

            string key = string.IsNullOrEmpty(scope) ? fragment : "[" + scope + "]" + fragment;

            // ★ 环形缓冲先去重、再过滤读数，然后才入环（2026-10-30 实机：F11「最近漏译」
            //   被逐帧刷新的文本刷屏 —— HUD 每帧 set_text 的漏译片段每帧都 Record 一次，
            //   12 个槽位全是同一条/同一族读数，真正的一次性漏译被瞬间挤出窗口）。
            //   ① 去重：已在环里的键不再入环 —— 同值重刷（含 A/B 交替刷）只占一个槽位，
            //      直到被 12 条**不同**漏译挤出为止；
            //   ② 读数：数值每帧都在变，去重对它无效（"M 0.05"/"M 0.06" 是不同的键），
            //      只能按形状拦 —— 与落盘清单共用 ReadoutOnly，环上额外加 ValueUnit/
            //      PrefixedReadout 两个数值形状。环本来就是给人眼扫缺口用的，
            //      把"永远补不出译文"的形状放进来只会掩埋真缺口。
            if (!_recentKeys.Contains(key) && !IsReadoutForRecent(fragment))
            {
                if (_recent.Count >= RecentCapacity) _recentKeys.Remove(_recent.Dequeue());
                _recent.Enqueue(key);
                _recentKeys.Add(key);
            }

            if (!Recording) return;
            if (IsReadout(fragment)) return;      // 纯读数永远补不出译文，不进清单
            if (_fragments.Add(key)) _dirty = true;
        }

        /// <summary>
        /// 纯读数判定（<c>M 0.08 G 0.2</c> / <c>×680</c> / <c>SN A814</c> / <c>▲ 4</c>）。
        /// 只影响清单、不影响翻译，所以判据可以放宽；但也不能太宽 ——
        /// <c>4X</c> 这类计数会被一起丢掉，那是可以接受的，它同样没有译文可补。
        /// </summary>
        private static bool IsReadout(string fragment)
        {
            return TokenPatterns.ReadoutOnly.IsMatch(fragment.Trim());
        }

        /// <summary>
        /// 环形缓冲用的**放宽版**读数判定：在落盘判据（ReadoutOnly）之外，
        /// 再加上「数值+短单位」（<c>673 km/h</c>）与「带前缀读数」（<c>x4</c>、<c>$1.96m</c>）。
        /// 这两类同样永远补不出译文，且若以 HUD 读数形式逐帧刷新，数值每帧都变，
        /// 去重拦不住 —— 只有按形状拦才有效。
        /// </summary>
        private static bool IsReadoutForRecent(string fragment)
        {
            string t = fragment.Trim();
            return TokenPatterns.ReadoutOnly.IsMatch(t)
                   || TokenPatterns.NoiseValueUnit.IsMatch(t)
                   || TokenPatterns.NoisePrefixedReadout.IsMatch(t);
        }

        /// <summary>整段长原文留档 —— 片段清单里的截断文本对不上运行时的键，只能靠它。</summary>
        internal void RecordLong(string original, string scope)
        {
            if (string.IsNullOrEmpty(original) || original.Length < LongTextThreshold) return;
            if (!Recording) return;
            string key = string.IsNullOrEmpty(scope) ? original : "[" + scope + "]" + original;
            if (_longTexts.Add(key)) _dirty = true;
        }

        /// <summary>清空全部记录。<b>刻意置脏</b>，好让随后的 <see cref="Flush"/> 把空数组写进磁盘。</summary>
        internal void Clear()
        {
            _fragments.Clear();
            _longTexts.Clear();
            _recent.Clear();
            _recentKeys.Clear();
            _dirty = true;
        }

        /// <summary>按需落盘（默认间隔 30 秒）。</summary>
        internal void FlushIfDue(int intervalSeconds = 30)
        {
            if (!_dirty) return;
            if ((DateTime.UtcNow - _lastFlush).TotalSeconds < intervalSeconds) return;
            Flush();
        }

        internal void Flush()
        {
            if (!_dirty) return;
            try
            {
                WriteJsonArray(_fragmentPath, _fragments);
                WriteJsonArray(_longTextPath, _longTexts);
                _dirty = false;
                _lastFlush = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Log.Debug("漏译记录写盘失败：" + ex.Message);
            }
        }

        private static void WriteJsonArray(string path, HashSet<string> items)
        {
            var sb = new StringBuilder(items.Count * 24 + 4);
            sb.Append("[\n");
            bool first = true;
            foreach (string item in items)
            {
                if (!first) sb.Append(",\n");
                sb.Append("  ").Append(JsonEscape(item));
                first = false;
            }
            sb.Append("\n]\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string JsonEscape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\u000B': sb.Append("\\u000b"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
