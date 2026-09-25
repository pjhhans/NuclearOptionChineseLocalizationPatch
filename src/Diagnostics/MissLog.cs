using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

        internal void Record(string fragment, string scope)
        {
            if (string.IsNullOrEmpty(fragment)) return;
            string key = string.IsNullOrEmpty(scope) ? fragment : "[" + scope + "]" + fragment;
            if (_fragments.Add(key)) _dirty = true;
        }

        /// <summary>整段长原文留档 —— 片段清单里的截断文本对不上运行时的键，只能靠它。</summary>
        internal void RecordLong(string original, string scope)
        {
            if (string.IsNullOrEmpty(original) || original.Length < LongTextThreshold) return;
            string key = string.IsNullOrEmpty(scope) ? original : "[" + scope + "]" + original;
            if (_longTexts.Add(key)) _dirty = true;
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
