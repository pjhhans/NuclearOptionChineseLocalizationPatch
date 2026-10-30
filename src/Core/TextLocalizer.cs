using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using NuclearOptionChineseLocalizationPatch.Diagnostics;

namespace NuclearOptionChineseLocalizationPatch.Core
{
    /// <summary>
    /// 翻译流水线。整个插件的语义都集中在这里，其余模块只负责"把文本送进来"和"把结果写回去"。
    ///
    /// <para><b>为什么不能简单地做一次查表。</b>游戏文本的形态极其杂：
    /// <c>&lt;color=#FF0000FF&gt;LEFT TURBINE FAILURE&lt;/color&gt;</c> 带富文本；
    /// <c>Faction funds: $1.96m</c> 是标签加动态读数；
    /// <c>Rank 3</c> 是词加数字；
    /// <c>K92 Highway Strip has been captured by 玩家名</c> 前后都是变量；
    /// 左上角战报还是"读回拼接写回"的累积块。
    /// 任何一种单一策略都覆盖不了，所以采用分层判定：<b>能整体翻就整体翻，
    /// 不行就切、就剥、就只翻主干再把变量拼回去</b>。</para>
    ///
    /// <para><b>收敛性</b>：每次进入 <see cref="LocalizeCore"/> 都先判"已含中文 → 直接返回"，
    /// 且所有递归调用的输入都严格变短（切片 / 剥标签 / 去尾缀），因此必然终止。
    /// 只写回确实含中文的结果，绝不把一种英文换成另一种英文 —— 否则会 A→B→A 来回改写、每帧抖动。</para>
    /// </summary>
    internal sealed class TextLocalizer
    {
        private readonly LocalizationTable _table;
        private readonly ExclusionRules _exclusions;
        private readonly MissLog _missLog;

        /// <summary>文本 → 结果。命中过作用域的文本不会走到这里，所以可以只按文本做键。</summary>
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.Ordinal);

        private int _cacheLimit = 20000;

        internal bool Enabled { get; set; } = true;
        internal long PassThroughCount { get; private set; }
        internal long MissCount { get; private set; }
        internal long HitCount { get; private set; }
        internal int CacheCount => _cache.Count;

        /// <summary>
        /// 是否记录「最近命中（原文 → 译文）」滚动列表，供 F11 窗口的诊断区显示。
        ///
        /// <para><b>默认关</b>，与本项目「安装后只开翻译」的默认一致：正常游玩用不到它，
        /// 而每条命中都要做一遍字符串裁剪再入队。排查「某处为什么没翻」时，在 F11 窗口里
        /// 临时勾上再复现即可。</para>
        ///
        /// <para>关掉只影响这份滚动列表；<see cref="HitCount"/> 累计数照常统计，
        /// 窗口里的「累计 N 条」仍然有效。</para>
        /// </summary>
        internal bool CaptureRecent { get; set; } = false;

        private const int RecentCapacity = 12;
        private readonly Queue<string[]> _recentHits = new Queue<string[]>(RecentCapacity);

        /// <summary>递归深度。只有最外层调用才记进「最近命中」，否则子片段会把列表刷满。</summary>
        private int _depth;

        /// <summary>
        /// 最近 N 条「原文 → 译文」，最新的在最前。
        ///
        /// <para>这是排查「某处为什么没翻」最直接的工具：窗口里能当场看到一条文本
        /// 到底是被命中了（进了这个列表）还是被漏掉了（进漏译列表）。</para>
        /// </summary>
        internal string[] SnapshotRecentHits()
        {
            var result = new string[_recentHits.Count];
            int i = 0;
            foreach (string[] pair in _recentHits) result[i++] = pair[0] + "  →  " + pair[1];
            return result;
        }

        private void RecordHit(string original, string translated)
        {
            HitCount++;
            if (!CaptureRecent) return;
            if (_recentHits.Count >= RecentCapacity) _recentHits.Dequeue();
            _recentHits.Enqueue(new[] { Clip(original), Clip(translated) });
        }

        /// <summary>
        /// 压平成单行摘要：换行/回车/垂直制表转空格、连续空格折叠成一个，超长截断。
        /// 单趟 <see cref="System.Text.StringBuilder"/> 扫描 —— 旧版用
        /// <c>while (Contains("  ")) Replace(...)</c>，每轮重扫全文，长文本上是 O(n²)。
        /// </summary>
        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            var sb = new System.Text.StringBuilder(s.Length);
            bool prevSpace = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                // 只把这三种换行符与空格当空白（制表符保持原样，与旧行为一致）
                bool isSpace = c == ' ' || c == '\n' || c == '\r' || c == '\u000B';
                if (isSpace)
                {
                    if (prevSpace) continue; // 连续空白折叠成一个
                    sb.Append(' ');
                    prevSpace = true;
                }
                else
                {
                    sb.Append(c);
                    prevSpace = false;
                }
            }

            string flat = sb.ToString();
            return flat.Length <= 42 ? flat.Trim() : flat.Substring(0, 42).Trim() + "…";
        }

        internal TextLocalizer(LocalizationTable table, ExclusionRules exclusions, MissLog missLog)
        {
            _table = table;
            // 通配模板捕获到的实值（如 "Maris Airport" / "AWAITING FORCES"）也要出中文，
            // 否则整句会半中半英。只走精确查表，不递归模板 —— 递归会放大成本且容易自激。
            _table.CaptureTranslator = TranslateCapture;
            _exclusions = exclusions;
            _missLog = missLog;
        }

        internal void ClearCache() => _cache.Clear();

        internal void SetCacheLimit(int limit)
        {
            _cacheLimit = limit < 0 ? 0 : limit;
            if (_cacheLimit == 0) _cache.Clear();
        }

        // ================================================================== 主入口

        /// <summary>
        /// 把 <paramref name="text"/> 翻成中文。无法翻译时**原样返回**，
        /// 调用方据此判断是否需要写回（通常是 <c>结果 != 原文 &amp;&amp; 结果含中文</c>）。
        ///
        /// <para><paramref name="scope"/> 由补丁层从组件名推断，用于区分同名不同义的原文。</para>
        ///
        /// <para>本方法是<b>薄壳</b>，只负责「最近命中」记录；真正的判定在
        /// <see cref="LocalizeInner"/>。递归调用走的也是本方法，所以用
        /// <c>_depth</c> 保证只有最外层的那次被记录。</para>
        ///
        /// <para><b>递归调用不做重复的排除名单判定</b>：片段/模板拆开后每一小段都直接走本方法，
        /// 若每段都重跑一遍排除规则，既慢、又可能把「拼起来才成句」的片段误排除。</para>
        ///
        /// <para><b>性能探针</b>（<see cref="PerfProbe"/>，默认关）：只对<b>最外层</b>那次调用计时，
        /// 得到「一次赋值的固定成本」。关掉探针时 <c>Begin</c> 返回 0，下面整段退化为一次判断。</para>
        ///
        /// <para><b>兜底 catch</b>：本地化是**旁挂**能力，任何意外都绝不能抛回游戏代码。
        /// 一次越界的外抛会打断调用方整条初始化（2026-10-29 实机：TMP set_text 抛异常 →
        /// <c>AvBayPlate.Set</c> 中断 → <c>AvConsole.Finish</c> 中断 → MFD 面板 install failed，
        /// 半成品面板留在场景里永不销毁）。所以这里一律吞掉、返回原文、只记一次日志。
        /// 有 try/catch 但不抛异常时代价≈0（运行时没有额外指令）。</para>
        /// </summary>
        /// <summary>
        /// 通配段（<c>{*}</c> 捕获到的运行时实值）回译。
        ///
        /// <para>顺序：<b>结构化格式先转</b>（BRA 方位报读、钟点方位、跑道号），
        /// 再走<b>精确查表</b>（<c>Maris Airport</c> → 马里斯机场、
        /// <c>AWAITING FORCES</c> → 等待部队）。两条都不命中就原样返回 ——
        /// 呼号、玩家名这类本来就不该动。</para>
        ///
        /// <para><b>为什么结构化优先。</b>台词里的 <c>&lt;bra&gt;</c> 是
        /// <c>Core/Math/Bra.cs</c> 拼出来的 <c>BRA 090, 40 kilometres, 1500 metres, hot</c>，
        /// 每次数字都不同，永远不可能有精确键；它占 118 条台词，不做规则就等于 118 条半中半英。
        /// <c>&lt;clock&gt;</c> 同理（<c>three o'clock</c>）。</para>
        /// </summary>
        private string TranslateCapture(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            string structured = CaptureRules.Convert(s);
            if (structured != null) return structured;

            // 「SW OF Maris Airport」这类**方位 + 地名**：结构由 CaptureRules 认，
            // 但地名本身要查表，所以拼装在能拿到词表的地方做
            // （玩家名/呼号同样形如「X OF Y」，CaptureRules 里已有严格边界，不会误伤）。
            string place, dirCn;
            if (CaptureRules.TrySplitSector(s, out place, out dirCn))
            {
                // 地名没词条就整条放弃 —— 拼成「SW OF Maris Airport西南」比不翻更糟。
                string placeCn = _table.LookupGlobal(place);
                if (placeCn != null) return placeCn + dirCn;
            }

            // 通配捕获段经常带着 kill feed 的阵营圆点（原文形如
            // 「·Surf Class Patrol Boat shot down ·T/A-30 Compass」，模板把
            // 「·名字」整段捕获）。带着「·」查表必败，而且回填后本来就该把
            // 圆点留在原位 —— 所以剥掉两端的空白与单字符分隔符、只拿中间查表，
            // 译文两侧再原样接回。
            int start = 0, end = s.Length;
            while (start < end && IsEdgeNoise(s[start])) start++;
            while (end > start && IsEdgeNoise(s[end - 1])) end--;
            if (end - start < 2) return s;                     // 剥完只剩圆点/空白，没东西可查

            string core = s.Substring(start, end - start);
            string hit = _table.LookupGlobal(core);
            if (hit == null) return s;
            return s.Substring(0, start) + hit + s.Substring(end);
        }

        /// <summary>捕获段两端可以剥掉的噪声字符：空白与单字符分隔符（圆点、短划等）。</summary>
        private static bool IsEdgeNoise(char c)
        {
            return char.IsWhiteSpace(c) || TokenPatterns.LoneDelimiters.IndexOf(c) >= 0;
        }

        internal string Localize(string text, string scope)
        {
            _depth++;
            long probeTotal = PerfProbe.Begin();
            try
            {
                string result = LocalizeInner(text, scope);
                if (_depth == 1 && !ReferenceEquals(result, text) && result != text && HasChinese(result))
                {
                    RecordHit(text, result);
                }
                return result;
            }
            catch (Exception ex)
            {
                // 只在外层记一次（内层只是原样上抛），否则一条文本会刷出十几条日志。
                if (_depth == 1) NoteFailure(ex, text, scope);
                return text;
            }
            finally
            {
                if (_depth == 1) PerfProbe.End(PerfProbe.Seg.Total, probeTotal);
                _depth--;
            }
        }

        /// <summary>已记录的异常种类数（同一个 类型+首行堆栈 只报一次）。</summary>
        private int _failureKinds;

        /// <summary>累计被兜底吞掉的次数 —— F11 窗口里能看到，非 0 说明流水线仍有未修的洞。</summary>
        internal long FailureCount { get; private set; }

        private readonly HashSet<string> _failureSeen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 记一次流水线异常。同一种（类型 + 首行堆栈）只写一条日志，
        /// 避免「每帧都炸同一个洞」把 LogOutput 刷爆、把真正的线索淹掉。
        /// </summary>
        private void NoteFailure(Exception ex, string text, string scope)
        {
            FailureCount++;
            string stack = ex.StackTrace ?? string.Empty;
            int cut = stack.IndexOf('\n');
            string head = cut > 0 ? stack.Substring(0, cut) : stack;
            string key = ex.GetType().Name + "|" + head;
            if (_failureKinds >= 20 || !_failureSeen.Add(key)) return;
            _failureKinds++;
            Log.Error(
                "[本地化] 流水线异常已兜底（返回原文）：" + ex.GetType().Name + "：" + ex.Message
                + "\n  作用域=" + scope + "  原文=" + Fmt(text)
                + "\n  " + head.Trim());
        }

        private static string Fmt(string s)
        {
            if (s == null) return "<null>";
            if (s.Length > 120) s = s.Substring(0, 120) + "…";
            return "\"" + s.Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }

        private string LocalizeInner(string text, string scope)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // ① 不翻译名单：必须在最前面。这些文本应当永远保持游戏写入的原样，
            //    连"关闭翻译时的反向还原"都不该碰它们。
            if (_exclusions.IsExcluded(text, scope)) return text;

            // ② 保持英文的术语（写在固定宽度槽位里的缩写）。
            if (_exclusions.IsKeptTerm(text)) return text;

            // ③ 翻译关闭时：把已显示的中文还原回原文，而不是放着不管。
            if (!Enabled) return Restore(text);

            // ④ 整段模板。必须在"已含中文就短路"**之前** ——
            //    这类文本早先若被按分隔符切成片段翻过，会留下半中半英的残局；
            //    让"已含中文"先短路的话，那个状态就永远修不回来了。
            string template;
            if (_table.TryGetTemplate(text, out template))
            {
                return TextCanonicalizer.ExpandTemplate(text, template);
            }

            // ⑤ 幂等短路：已经含中文的（通常是自己上次写的结果被读回来了）直接放过。
            //    ★ 例外是多行「累积块」：战报的写法是「读回 text → 拼接 → 写回」，
            //      整块一旦带上中文，后续追加的英文就永远翻不了。多行必须逐行判定。
            if (HasChinese(text) && !IsMultiLine(text)) return text;

            // ⑥ 快速路径：不含 ASCII 字母的（纯数值/符号/单位）不可能命中词条。
            //    HUD 上大量每帧刷新的读数走这条路，省掉后面全套正则开销。
            if (!HasAsciiLetter(text)) return text;

            // ⑦ 作用域查询：优先本作用域词条。
            if (!string.IsNullOrEmpty(scope))
            {
                string scoped = _table.LookupScoped(scope, text);
                if (scoped != null) return scoped;

                // 强制作用域的语义是「只用本作用域词条 + 全局精确词条，禁止切分/句式/模式」，
                // 而不是把全局精确词条也一起屏蔽 —— 后者会让没逐条登记的文本永远翻不了。
                if (_table.IsForceScoped(scope))
                {
                    return _table.LookupGlobal(text) ?? text;
                }
            }

            // 「核心 + 缓存」段：缓存命中时只量一次字典查表，未命中时把主体流水线整段计入。
            long probeCore = PerfProbe.Begin();

            string cached;
            if (_cache.TryGetValue(text, out cached))
            {
                PerfProbe.End(PerfProbe.Seg.Core, probeCore);
                return cached;
            }

            string result = LocalizeCore(text, scope);

            if (_cacheLimit > 0)
            {
                if (_cache.Count >= _cacheLimit) _cache.Clear();
                _cache[text] = result;
            }
            PerfProbe.End(PerfProbe.Seg.Core, probeCore);
            return result;
        }

        // ================================================================== 主体

        private string LocalizeCore(string text, string scope)
        {
            // 多行：逐行翻译后原样重组（分隔符本身也要保留）。
            if (IsMultiLine(text))
            {
                string[] lines = text.Split(new[] { '\n', '\u000B' });
                MatchCollection separators = TokenPatterns.LineBreak.Matches(text);
                var sb = new StringBuilder(text.Length + 32);
                bool changed = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length > 0)
                    {
                        string translated = Localize(line, scope);
                        if (!ReferenceEquals(translated, line) && translated != line) changed = true;
                        else if (translated == line) PassThroughCount++;
                        line = translated;
                    }
                    sb.Append(line);
                    if (i < separators.Count) sb.Append(separators[i].Value);
                }

                string joined = sb.ToString();
                return changed ? joined : text;
            }

            return LocalizeLine(text, scope);
        }

        /// <summary>单行处理：A 整串 → B 句式 → B2 片段 → C 切片。</summary>
        private string LocalizeLine(string text, string scope)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // 通讯面板的台词引号：「<< 台词 >>」。< 是标签字符，正文包在 << >> 里时
            // 标签正则会把整段正文当标签删掉（A 阶段查的是剥标签后的残壳，必不中）。
            // 剥壳 → 翻内芯 → 原样包回。内芯已含中文则走下方幂等短路，天然防抖。
            if (text.Length > 6 && text[0] == '<' && text[1] == '<'
                && text[text.Length - 1] == '>' && text[text.Length - 2] == '>')
            {
                string inner = text.Substring(2, text.Length - 4).Trim();
                if (inner.Length > 0)
                {
                    string innerTrans = Localize(inner, scope);
                    if (!ReferenceEquals(innerTrans, inner) && innerTrans != inner)
                        return "<< " + innerTrans + " >>";
                }
            }

            if (HasChinese(text)) return text;
            if (_exclusions.IsKeptTerm(text)) return text;
            if (IsNoise(text.Trim())) return text;

            bool isForceScoped = _table.IsForceScoped(scope);

            // ---------------------------------------------------------- A 整串
            string stripped = TokenPatterns.Tag.Replace(text, string.Empty);
            string trimmed = stripped.Trim();
            string key = KeyScrubber.Scrub(trimmed).Trim();

            if (!string.IsNullOrEmpty(scope))
            {
                string scopedHit = _table.LookupScoped(scope, key);
                if (scopedHit != null && CanReplace(text, stripped))
                    return text.Replace(stripped, scopedHit);
            }

            if (!isForceScoped)
            {
                string whole = _table.LookupGlobal(key);
                if (whole != null && CanReplace(text, stripped))
                    return text.Replace(stripped, whole);
            }

            // ---------------------------------------------------------- A2 编号行
            // 「01  Read the status card on the ramp: …」这类铭牌 / 常备命令行，
            // 整行与词条只差一个两位序号。**必须在切片之前认领**：
            // 正文里的冒号是分隔符，切片会先把行切碎，序号规则再也看不到完整正文。
            // 2026-10-30 实机：同一块常备命令 02/03 有译文、01/04 永远英文，
            // 唯一差别就是正文里有没有冒号（有冒号的被切碎，没有的走到 A2）。
            Match nline = TokenPatterns.NumberedLine.Match(text);
            if (nline.Success)
            {
                string nbody = nline.Groups[2].Value.Trim();
                string nbodyTrans = Localize(nbody, scope);
                if (nbodyTrans != nbody)
                    return text.Substring(0, nline.Groups[2].Index) + nbodyTrans;
            }

            // ---------------------------------------------------------- B 句式
            string sentence = TrySentence(text, scope);
            if (sentence != null) return sentence;

            // ---------------------------------------------------------- B2 片段
            // 必须早于 C：否则切片会先把固定短语那半截翻掉，留下半中半英再也修不回来。
            if (!isForceScoped)
            {
                string fragment = ConcatFragments(text, scope);
                if (!string.IsNullOrEmpty(fragment) && fragment != text && HasChinese(fragment))
                {
                    return fragment;
                }
            }

            // ---------------------------------------------------------- C 切片
            return SliceAndRebuild(text, scope, isForceScoped);
        }

        /// <summary>
        /// A 阶段能否真的落地这次替换。
        ///
        /// <para><b>为什么需要这个判定。</b>A 阶段拿的是<b>剥掉标签后</b>的 <c>stripped</c>，
        /// 但执行的是 <c>text.Replace(stripped, 译文)</c>。<b>标签夹在中间时，
        /// <c>stripped</c> 并不是 <c>text</c> 的连续子串</b>，Replace 会静默地什么都不做 ——
        /// 而方法却<b>照旧 return</b>，于是这一行被判成「已处理」，
        /// 后面的 B2 片段拼接与 C 切片统统走不到。</para>
        ///
        /// <para>表现就是最难查的那一类故障：<b>词条明明有，整行却一个中文都不出</b>，
        /// 而且不报错、不进漏译清单。典型形状是「彩色词 + 固定短语 + 彩色词」
        /// （<c>&lt;color=…&gt;甲&lt;/color&gt; has been captured by &lt;color=…&gt;乙&lt;/color&gt;</c>）——
        /// 击杀 feed / 聊天 feed 恰好全是这种形状。</para>
        ///
        /// <para><b>为什么不是简单地「结果没变就往下走」。</b>词表里有一批
        /// <b>identity 键</b>（<c>原文 -&gt; 同一原文</c>，如 <c>[TargetCode]BLD</c>），
        /// 它们的作用正是「查到但译文相同 ⇒ 不算漏译」，用来压掉漏译清单的噪声。
        /// 若按「结果没变就继续」处理，这些键就失效、噪声会重新涌回来。
        /// 所以判据是<b>「stripped 到底是不是 text 的子串」</b>：
        /// 是（无标签，或标签在两端）→ 照旧返回，行为与以前逐字一致；
        /// 不是（标签夹在中间）→ 那次 Replace 必然是空操作，放行到后续阶段才是正确的。</para>
        /// </summary>
        private static bool CanReplace(string text, string stripped)
        {
            if (stripped.Length == 0) return false;
            return text.IndexOf(stripped, StringComparison.Ordinal) >= 0;
        }

        /// <summary>特殊句式：主干是固定词、变量在尾部，整串查不到，需要拆开分别处理。</summary>
        private string TrySentence(string text, string scope)
        {
            string trimmed = text.Trim();

            Match m = TokenPatterns.TurretControl.Match(trimmed);
            if (m.Success)
            {
                string head = m.Groups[1].Value.Trim();
                string turret = m.Groups[2].Value;
                string control = m.Groups[3].Value;
                return (head.Length == 0 ? string.Empty : Localize(head, scope) + " ")
                       + LocalizeToken(turret, scope) + " " + LocalizeToken(control, scope);
            }

            m = TokenPatterns.BootingSentence.Match(text);
            if (m.Success)
            {
                return LocalizeToken(m.Groups[1].Value, scope) + " "
                       + Localize(m.Groups[2].Value, scope) + m.Groups[3].Value;
            }

            m = TokenPatterns.BuySentence.Match(text);
            if (m.Success)
            {
                return LocalizeToken(m.Groups[1].Value, scope) + " "
                       + Localize(m.Groups[2].Value, scope);
            }

            m = TokenPatterns.SetToSentence.Match(text);
            if (m.Success)
            {
                return Localize(m.Groups[1].Value, scope) + " "
                       + LocalizeToken(m.Groups[2].Value, scope) + " "
                       + Localize(m.Groups[3].Value, scope);
            }

            m = TokenPatterns.TaxiToSentence.Match(text);
            if (m.Success)
            {
                return LocalizeToken(m.Groups[1].Value, scope) + " "
                       + Localize(m.Groups[2].Value, scope);
            }

            return null;
        }

        /// <summary>
        /// 片段拼接：<c>&gt;&gt;</c> 前缀 / <c>&lt;&lt;</c> 后缀 / <c>==</c> 中段。
        /// 命中后把固定短语换成译文，再**递归翻译剩余部分**（变量可能是单位名等，也需要翻译）。
        /// 没命中返回 null，交给切片逻辑。
        /// </summary>
        /// <summary>
        /// 片段递归层数上限。
        ///
        /// <para><b>为什么需要这个上限。</b>片段命中后会递归调用 <see cref="Localize"/>，
        /// 而递归进来的文本**又从头遍历整张片段表**。一旦某个片段在长句里频繁命中
        /// （典型是 `" IN "`、`" IS "`、`" TO "` 这类通用短词被误当成词缀），
        /// 每条长句就会展开成 2^深度 次全表扫描 —— 深度上限是 8，等于每条 8×片段数。</para>
        ///
        /// <para>2026-10-29 实测事故：片段表被灌入 30 余个通用短词后，
        /// 进对局瞬间（缓存全冷）数千条文本各展开上千次匹配，帧率塌到个位数，
        /// 同时文本被改写成半中半英导致界面错位。</para>
        ///
        /// <para>两层足够：「后缀 + 中缀 + 主体」这类最常见的三段拼接正好用完两层。
        /// 超限的文本退回切片，宁可少翻一处，也不让一条文本引爆 CPU。</para>
        /// </summary>
        private const int MaxFragmentDepth = 2;

        private int _fragmentDepth;

        private string ConcatFragments(string text, string scope)
        {
            if (text.Length < 4) return null;
            if (_fragmentDepth >= MaxFragmentDepth) return null;

            // 命中后的递归都要走这里：进一层、退一层，异常也不会把计数弄脏。
            string Recurse(string part)
            {
                if (string.IsNullOrEmpty(part)) return part;
                _fragmentDepth++;
                try { return Localize(part, scope); }
                finally { _fragmentDepth--; }
            }

            // 用 &lt; 而不是 &lt;= 做长度判定：整段**恰好就是**固定短语时
            // （"K92 Highway Strip has been captured by "）也要命中，此时剩余为空串，
            // 递归返回空，结果就是纯译文。
            foreach (LocalizationTable.Fragment f in _table.PrefixFragments)
            {
                if (text.Length < f.Key.Length) continue;
                if (string.CompareOrdinal(text, 0, f.Key, 0, f.Key.Length) != 0) continue;
                // TrimStart：游戏拼串常带多余空格（"Rearmed " + " 100% complete"），
                // 直接拼进译文会留下难看的空隙。
                return f.Value + Recurse(text.Substring(f.Key.Length).TrimStart());
            }

            foreach (LocalizationTable.Fragment f in _table.SuffixFragments)
            {
                if (text.Length < f.Key.Length) continue;
                int at = text.Length - f.Key.Length;
                if (string.CompareOrdinal(text, at, f.Key, 0, f.Key.Length) != 0) continue;
                return Recurse(text.Substring(0, at).TrimEnd()) + f.Value;
            }

            foreach (LocalizationTable.Fragment f in _table.InfixFragments)
            {
                if (text.Length < f.Key.Length) continue;
                int at = text.IndexOf(f.Key, StringComparison.Ordinal);
                if (at < 0) continue;
                return Recurse(text.Substring(0, at))
                       + f.Value
                       + Recurse(text.Substring(at + f.Key.Length));
            }

            return null;
        }

        /// <summary>C 阶段：按分隔符切片，逐段处理，最后原样拼回。</summary>
        /// <summary>
        /// 把「标签 + 正文」交替的一段逐个正文段翻译，标签原样保留。
        ///
        /// <para>用于懒惰配对切出来的混合段：<c>&lt;/mark&gt; ECONOMY: … SHOPS  &lt;color=#00F0FF&gt;+++</c>。
        /// 这类段整体查表必然失败（中间夹着标签），但它**内部**的正文是标准词条。</para>
        /// </summary>
        private string TranslateTaggedRun(string part, string scope, ref bool changed)
        {
            var sb = new StringBuilder(part.Length + 16);
            int pos = 0;
            foreach (Match m in TokenPatterns.Tag.Matches(part))
            {
                if (m.Index > pos)
                    sb.Append(TranslateRun(part.Substring(pos, m.Index - pos), scope, ref changed));
                sb.Append(m.Value);
                pos = m.Index + m.Length;
            }
            if (pos < part.Length) sb.Append(TranslateRun(part.Substring(pos), scope, ref changed));
            return sb.ToString();
        }

        /// <summary>翻译一段**不含标签**的正文，保留原有的首尾空白与分隔符。</summary>
        private string TranslateRun(string run, string scope, ref bool changed)
        {
            string t = run.Trim();
            if (t.Length == 0) return run;
            if (t == TokenPatterns.LoneDelimiterStrings) return run;
            if (HasChinese(t) || IsNoise(t)) return run;
            if (_exclusions.IsKeptTerm(t) || _exclusions.IsExcluded(t, scope)) return run;

            string translated = Localize(t, scope);
            if (translated == t) return run;

            int at = run.IndexOf(t, StringComparison.Ordinal);
            if (at < 0) return run;
            changed = true;
            return run.Substring(0, at) + translated + run.Substring(at + t.Length);
        }

        private string SliceAndRebuild(string text, string scope, bool isForceScoped)
        {
            string[] parts = TokenPatterns.Delimiter.Split(text);
            bool changed = false;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrEmpty(part)) continue;

                // 单字符分隔符本身
                if (part.Length == 1 && TokenPatterns.LoneDelimiters.IndexOf(part[0]) >= 0) continue;
                // 多字符的纯符号分隔符（新闻条的 `+++`）同样原样跳过
                if (part == TokenPatterns.LoneDelimiterStrings) continue;

                // ---- 富文本标签块
                //   ★ 只有「以**开**标签开头、以标签结尾」的段才是整块标签。
                //     以闭合标签开头的段（`</mark> ECONOMY: … <color=#00F0FF>`）不是 ——
                //     它是懒惰配对切出来的尾巴，中间夹着正文，必须走下面的纯文本分支。
                //     2026-10-30 实机：新闻滚动条多条拼接时**只有第一条**永远英文，
                //     就是它：首条的尾段恰好以 `</mark>` 开头、以 `<color=…>` 结尾，
                //     被误判成标签块 → 去掉标签后中间夹着 `+++` 分隔符 → 查表必失败；
                //     后续条目没有这个形状，所以照常翻译。
                if (part[0] == '<' && part[part.Length - 1] == '>'
                    && !(part.Length > 1 && part[1] == '/'))
                {
                    // 剥掉标签、取内部正文递归翻译，再原样填回。
                    // ★ 不要用"开闭标签必须完全对称"的正则匹配整块：TMP 的闭合标签**不重复属性**
                    //   （<color=#FF0000FF>…</color>），任何带属性的标签都会被那种正则漏掉，
                    //   然后被静默跳过 —— 表现就是 <color=…> 里的英文一直不翻。
                    //   剥壳写法既不依赖闭合形式，也天然支持嵌套。
                    string inner = TokenPatterns.Tag.Replace(part, string.Empty);
                    string innerTrim = inner.Trim();
                    if (innerTrim.Length > 0
                        && !HasChinese(innerTrim)
                        && !IsNoise(innerTrim)
                        && !_exclusions.IsKeptTerm(innerTrim))
                    {
                        string innerTranslated = Localize(innerTrim, scope);
                        if (innerTranslated != innerTrim)
                        {
                            // ★ 用 IndexOf 定位后拼接，不用 Replace：
                            //   innerTrim 是 Trim 过的，若段内有内部标签（`A<b>B</b>C`），
                            //   "ABC" 就不是 part 的连续子串，Replace 会静默空操作，
                            //   表现是「句子已经翻好了，界面却还是英文」。
                            int at = part.IndexOf(innerTrim, StringComparison.Ordinal);
                            if (at >= 0)
                            {
                                parts[i] = part.Substring(0, at) + innerTranslated
                                           + part.Substring(at + innerTrim.Length);
                                changed = true;
                            }
                        }
                    }
                    continue;
                }

                // ---- 纯文本段
                string piece = part.Trim();

                // ★★ 纯空白段必须在这里就短路。
                //   切分是按「分隔符字符」做的，两头带空格是常态：
                //   `"… · " + 空串 + " · …"`（AvBayPlate 的 `" · " + AvAmbient.Maker + " · "`，
                //   Maker 为空时）切出来就是一段单独的空格；`"   "` 这种纯缩进同理。
                //   part 非空 ⇒ 上面的 IsNullOrEmpty 拦不住，Trim 后 piece 却成了空串，
                //   接下来 `piece[0]` 直接 IndexOutOfRangeException。
                //   **这个异常会穿透 TMP 的 set_text 抛回调用方**，把整条 UI 构建流程打断 ——
                //   2026-10-29 实机：BS mod 的 RAD/SQD 之后的 6 个 MFD 面板
                //   （OPS/MISC/EVN/COM/UAV/WMC）全部 install failed，
                //   面板半成品留在场景里不销毁，表现为「界面错位 + 有个 w 面板永远挂在屏幕上」。
                if (piece.Length == 0) continue;

                // ★ 边缘残留标签要先剥掉。
                //   成对标签的分隔规则是**懒惰**匹配（<[^>]+>.*?</[^>]+>），它只吃到第一个
                //   闭合标签为止，于是同一行剩下的部分会带着尾巴 `</mark>` 落到这里；
                //   此时 part 首字符是 '<' 但末字符不是 '>'，上面那条"整块都是标签"的判据
                //   不成立，于是带着 `</mark> RUMOR: …` 去查表 —— 必然查不中。
                //   表现就是新闻滚动条那种「小标签块被翻（[传闻]），正文整段英文」。
                //   只剥**两端**的标签（不剥中间），剥完仍是 part 的连续子串，
                //   后面照旧能 part.Replace(piece, 译文)。
                if (piece[0] == '<' || piece[piece.Length - 1] == '>')
                {
                    int cut = 0;
                    while (cut < piece.Length && piece[cut] == '<')
                    {
                        int gt = piece.IndexOf('>', cut);
                        if (gt < 0) break;
                        cut = gt + 1;
                        while (cut < piece.Length && char.IsWhiteSpace(piece[cut])) cut++;
                    }
                    int end = piece.Length;
                    while (end > cut && piece[end - 1] == '>')
                    {
                        int lt = piece.LastIndexOf('<', end - 1);
                        if (lt < cut) break;
                        end = lt;
                        while (end > cut && char.IsWhiteSpace(piece[end - 1])) end--;
                    }
                    if (cut >= end) continue;
                    piece = piece.Substring(cut, end - cut);
                }

                // ★ 剥了两端还剩标签（`</mark> … SHOPS  <color=#00F0FF>+++`）：
                //   说明这一段是「标签 + 正文」交替，套不进任何单条词条。
                //   逐个把标签之间的正文送去翻译，标签原样写出。
                //   2026-10-30 实机：新闻滚动条多条拼接时**只有第一条**永远英文，就是这种形状 ——
                //   首条尾段既不是纯标签块（正文夹在中间），也不以标签收尾（尾巴是 `+++`）。
                if (piece.IndexOf('<') >= 0)
                {
                    parts[i] = TranslateTaggedRun(part, scope, ref changed);
                    continue;
                }

                if (piece.Length == 0 || HasChinese(piece)) continue;
                // 术语片段保持英文，且**不记入漏译** —— 它不是漏译，是刻意保留。
                if (_exclusions.IsKeptTerm(piece)) continue;

                // ★ 名单里的片段同样跳过、且**不记入漏译**。
                //   排除判定此前只在 LocalizeInner 入口对**整条**文本做过一次；切片之后
                //   每个 piece 都是全新的字符串，不再判一次的话，"StartObjective"、
                //   "placeholder"、"{MissionManager.CurrentMission.Name}" 这类已经在名单里的
                //   片段就会被当成新漏译反复记录 —— 表现为「名单里明明登记了，
                //   漏译清单里却永远清不掉」，白占清单位置、掩盖真正的缺口。
                if (_exclusions.IsExcluded(piece, scope)) continue;

                if (!string.IsNullOrEmpty(scope))
                {
                    string scopedHit = _table.LookupScoped(scope, piece);
                    if (scopedHit != null)
                    {
                        parts[i] = part.Replace(piece, scopedHit);
                        changed = true;
                        continue;
                    }
                }
                if (isForceScoped) continue;

                string whole = _table.LookupGlobal(piece);
                if (whole != null)
                {
                    parts[i] = part.Replace(piece, whole);
                    changed = true;
                    continue;
                }

                // ★ 片段级通配模板。战报行被分隔符切碎（" - "、括号、冒号），整行级
                //   模板再也看不到完整句型 —— 2026-10-30 实机：「OPERATION … SECURED -
                //   +150 FUNDS…」被 " - " 切成两段、两段都翻不出；「X HAS BEEN CAPTURED
                //   BY +」被本体的 " +" 续行标记截尾。模板的匹配语义本就是「整段形状」，
                //   用在切片后的最小片段上同样成立；TryGetTemplate 内部先做规范化，
                //   通配段的回译走 CaptureTranslator（与整行级一致）。
                string wildHit;
                if (_table.TryGetTemplate(piece, out wildHit))
                {
                    parts[i] = part.Replace(piece, wildHit);
                    changed = true;
                    continue;
                }

                // ★ 片段级片段拼接（前后缀）。Result 行「X TAKEN - THE LINE MOVES UP」
                //   被 " - " 切开后，后缀片段（`<< TAKEN`）在整行级 B2 看不到
                //   （行尾是 "UP" 不是 "TAKEN"）—— 只有把 ConcatFragments 下放到
                //   片段层，"X TAKEN"／"X SCOUTED" 这类最小片段才能命中。
                //   深度由 _fragmentDepth（MaxFragmentDepth）兜底，不会无限递归。
                string pieceFrag = ConcatFragments(piece, scope);
                if (pieceFrag != null && pieceFrag != piece && HasChinese(pieceFrag))
                {
                    parts[i] = part.Replace(piece, pieceFrag);
                    changed = true;
                    continue;
                }

                // ★ 交给尾缀模式的必须是 **Trim 过** 的 piece，不能是 part。
                //   尾缀模式（VersionSuffix / ValueUnitSuffix / WordPlusNumber / ScoreSuffix…）
                //   全部以 ^…$ 锚定，而 part 可能带首尾空白（HUD 读数的 prefab 常写成
                //   "Score 0.0  "），这样会**全部失配**、随后落到下面的漏译分支，
                //   读数就永远翻不出来，还白记一条 missing。
                //   替换仍走 part.Replace，把原有的首尾空白保留下来（拼接串对空格敏感）。
                bool handled;
                string patterned = ApplyTailPatterns(piece, out handled, scope);
                if (patterned != piece)
                {
                    parts[i] = part.Replace(piece, patterned);
                    changed = true;
                }
                else if (!handled && !IsNoise(piece))
                {
                    MissCount++;
                    _missLog.Record(piece, scope);
                }
            }

            return changed ? string.Concat(parts) : text;
        }

        /// <summary>
        /// 尾缀数值模式：把「可翻译的词」与「必须原样保留的数值」拆开，
        /// 只翻译词、数值原样拼回。HUD 读数每帧变化，整串永远匹配不上，只能这样处理。
        ///
        /// <para><paramref name="handled"/> 为 true 表示这条文本已被某个模式认领
        /// （即便最终没翻出中文），此时**不应**再记入漏译 —— 前缀已经单独记过了，
        /// 重复记会让漏译清单里全是截断的碎片。</para>
        /// </summary>
        /// <summary>八方位 → 中文方位后缀。<c>N</c> 用「以北」而不是「北」，读起来是"某地以北"。</summary>
        private static string CompassSuffix(string octant)
        {
            switch (octant)
            {
                case "N": return "以北";
                case "NE": return "东北";
                case "E": return "以东";
                case "SE": return "东南";
                case "S": return "以南";
                case "SW": return "西南";
                case "W": return "以西";
                case "NW": return "西北";
                default: return null;
            }
        }

        /// <summary>
        /// 方位地标语序重排：<c>ASSAULT SW OF MARIS AIRPORT</c> → <c>MARIS AIRPORT 西南突击</c>。
        ///
        /// <para>返回 null 表示「这条不认领」，让调用方继续走别的尾缀模式。
        /// 判据刻意保守：<b>事件类型（组 1）没译出来就整条不动</b> ——
        /// 宁可整句保持英文，也不要产出"马里机场西南 STRIKE"这种半中半英。</para>
        /// </summary>
        private string CompassReorder(Match m, string scope)
        {
            string suffix = CompassSuffix(m.Groups[2].Value);
            if (suffix == null) return null;

            string place = m.Groups[3].Value.Trim();
            if (place.Length == 0) return null;

            string head = m.Groups[1].Value.Trim();
            string headPart = string.Empty;
            if (head.Length > 0)
            {
                string headTrans = Localize(head, scope);
                if (headTrans == head) return null;
                headPart = headTrans;
            }

            // 地名本身可能已被词表覆盖（本体的机场/城镇名），递归查一次即可；
            // 查不到就保持英文，位置照样重排 —— 「X 以北」比「N OF X」好读。
            string placePart = Localize(place, scope);

            // 地名末尾是拉丁字母/数字时补一格空格，避免 "AIRPORT西南突击" 挤在一起
            char last = placePart[placePart.Length - 1];
            bool latinTail = (last >= 'A' && last <= 'Z') || (last >= 'a' && last <= 'z') || (last >= '0' && last <= '9');
            return placePart + (latinTail ? " " : string.Empty) + suffix + headPart;
        }

        private string ApplyTailPatterns(string text, out bool handled, string scope)
        {
            handled = false;

            // 铭牌段落的两位序号（"01  OPEN a front for its map…"）。必须放在最前面：
            // 它要求「数字 + 至少两个空格 + 字母开头」，判据最窄，先认领就不会被后面的
            // 数值规则抢走（WordPlusNumber 只允许「纯字母词 + 数字」，本来也吃不下这行，
            // 但它会把 01 当词部之外的残留，最终仍落到漏译分支）。
            Match nline = TokenPatterns.NumberedLine.Match(text);
            if (nline.Success)
            {
                handled = true;                       // 认领：正文自身的漏译由递归那一层记
                string body = nline.Groups[2].Value.Trim();
                string bodyTrans = Localize(body, scope);
                if (bodyTrans != body)
                {
                    // 序号与它后面的空格原样保留（WordWrap 的悬挂缩进靠这两格对齐）
                    return text.Substring(0, nline.Groups[2].Index) + bodyTrans;
                }
                return text;
            }

            // 数量/百分比前缀（"3X SPEARHEAD MBT" / "44% OWN" / "+35% SUPPORT COST" / "#1 COVER …"）。
            // 前缀原样保留、正文递归查表 —— 正文本身通常就有词条，差的只是那个数字。
            Match qp = TokenPatterns.QuantityPrefix.Match(text);
            if (qp.Success)
            {
                handled = true;
                string qbody = qp.Groups[2].Value.TrimEnd();
                string qbodyTrans = Localize(qbody, scope);
                if (qbodyTrans != qbody)
                    return qp.Groups[1].Value + text.Substring(qp.Groups[1].Length,
                                                              qp.Groups[2].Index - qp.Groups[1].Length) + qbodyTrans;
                return text;
            }

            // 数量后缀（"GUN ×680" / "AGR-24 ×8" / "POWER ×1.00"）：词在前、读数在后。
            Match qs = TokenPatterns.TimesSuffix.Match(text);
            if (qs.Success)
            {
                handled = true;
                string qhead = qs.Groups[1].Value.Trim();
                string qheadTrans = Localize(qhead, scope);
                if (qheadTrans != qhead)
                    return text.Substring(0, qs.Groups[1].Index) + qheadTrans + text.Substring(qs.Groups[1].Index + qs.Groups[1].Length);
                return text;
            }

            // 秒数紧贴 S（"2S OLD"）—— 词表里的 ` S OLD` 片段要求有空格，这个形状漏掉。
            Match so = TokenPatterns.SecondsOld.Match(text);
            if (so.Success)
            {
                handled = true;
                return so.Groups[1].Value + " 秒前";
            }

            // 方位地标（"SW OF MARIS AIRPORT" / "ASSAULT SW OF MARIS AIRPORT"）。
            // 中文语序是「地名 + 方位 + 事件」，与英文**相反**，靠键值替换做不到，
            // 只能整条认领后重排。放在编号行之后、费用尾缀之前：判据要求八方位全大写，
            // 比后面那些数值规则更窄，先认领不会抢走别人的串。
            Match cp = TokenPatterns.CompassOfPlace.Match(text);
            if (cp.Success)
            {
                string reordered = CompassReorder(cp, scope);
                if (reordered != null)
                {
                    handled = true;
                    return reordered;
                }
            }

            // 补给战报的费用归因尾缀（"Rearmed … - cost: $45.3k by Munitions Bunker" 里
            // " - cost: " 之后剩下的那一截）。必须放在最前面：它是**唯一**一条允许碰 " by " 的规则，
            // 而且靠金额锚定，绝不会误伤散文里的 " by "。
            Match ebu = TokenPatterns.CostByUnitSuffix.Match(text);
            if (ebu.Success)
            {
                handled = true;
                string amount = ebu.Groups[1].Value.Trim();
                string unit = ebu.Groups[2].Value.Trim();
                string unitTrans = string.IsNullOrEmpty(scope) ? null : _table.LookupScoped(scope, unit);
                unitTrans = unitTrans ?? _table.LookupGlobal(unit);
                if (unitTrans == null || unitTrans == unit)
                {
                    // 单位名本身还没词条：把**它**记进漏译清单（而不是整条金额串），
                    // 这样清单里是可直接补的短标签，不是一串带着动态读数的碎片。
                    // 名单里的单位名同样不记（理由见 SliceAndRebuild 的说明）。
                    if (!_exclusions.IsExcluded(unit, scope))
                    {
                        MissCount++;
                        _missLog.Record(unit, scope);
                    }
                    return text;
                }
                // 与词表既有口径一致：`由弹药库 $45.3k`（先归因方、后金额）。
                return "由" + unitTrans + " " + amount;
            }

            if (text.IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Match m = TokenPatterns.VersionSuffix.Match(text);
                if (m.Success)
                {
                    handled = true;
                    return ReplacePrefix(text, m.Groups[1].Value.Trim(), scope);
                }
            }

            Match er = TokenPatterns.RunwaySuffix.Match(text);
            if (er.Success)
            {
                handled = true;
                return ReplacePrefix(text, er.Groups[1].Value.Trim(), scope);
            }

            Match evu = TokenPatterns.ValueUnitSuffix.Match(text);
            if (evu.Success)
            {
                handled = true;
                string prefix = evu.Groups[1].Value.Trim();
                if (IsNoise(prefix)) return text;
                return ReplacePrefix(text, prefix, scope);
            }

            Match eq = TokenPatterns.QuantitySuffix.Match(text);
            if (eq.Success)
            {
                handled = true;
                return ReplacePrefix(text, eq.Groups[1].Value.Trim(), scope);
            }

            Match wn = TokenPatterns.WordPlusNumber.Match(text);
            if (wn.Success)
            {
                handled = true;
                return ReplacePrefix(text, wn.Groups[1].Value.Trim(), scope);
            }

            // 数字在前、词在后（`47 CARDS`、`350 FUNDS`、`0 SORTIES`）。
            // 与上一条互为镜像；只在词真的查到译文时才认领，否则留给漏译分支，
            // 免得把 `12 kJ` 这类纯单位读数吞掉（它们本来就该由噪声规则挡下）。
            Match nw = TokenPatterns.NumberWord.Match(text);
            if (nw.Success)
            {
                string nwWord = nw.Groups[2].Value.Trim();
                string nwTrans = _table.LookupGlobal(nwWord);
                if (string.IsNullOrEmpty(nwTrans) && !string.IsNullOrEmpty(scope))
                    nwTrans = _table.LookupScoped(scope, nwWord);
                if (!string.IsNullOrEmpty(nwTrans) && nwTrans != nwWord)
                {
                    handled = true;
                    return nw.Groups[1].Value + " " + nwTrans;
                }
            }

            // 型号 + 数量（`RAM-45 5`）。放在 WordPlusNumber 之后：纯字母的词
            // 已经由上一条认领，这里只捞带连字符/斜杠/数字的型号。
            Match mc = TokenPatterns.ModelPlusCount.Match(text);
            if (mc.Success)
            {
                handled = true;
                return ReplacePrefix(text, mc.Groups[1].Value.Trim(), scope);
            }

            Match nu = TokenPatterns.NumberUnitOnly.Match(text);
            if (nu.Success)
            {
                string unit = nu.Groups[2].Value;
                string unitTrans = _table.LookupGlobal(unit);
                if (unitTrans != null)
                {
                    handled = true;
                    return text.Replace(unit, unitTrans);
                }
                // 不置 handled：单位本身可能就是噪声（m / km），让它流向最后的噪声判定。
            }

            Match sc = TokenPatterns.ScoreSuffix.Match(text);
            if (sc.Success)
            {
                handled = true;
                string prefix = sc.Groups[1].Value;            // "Score "
                string literal = text.Substring(prefix.Length); // "123.456"
                return LocalizeToken(prefix.Trim(), scope) + " " + literal;
            }

            Match epn = TokenPatterns.PlusNumberSuffix.Match(text);
            if (epn.Success)
            {
                string prefix = epn.Groups[1].Value.Trim();
                string wordCount = prefix;
                int words = wordCount.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                if (words <= 4)
                {
                    handled = true;
                    string trans = Localize(prefix, scope);
                    // 重新拼合，保留 "+" 与数值的原样
                    return trans + text.Substring(epn.Groups[1].Length);
                }
            }

            // 多句拼接（放在最后：前面所有模式都没认领才走这里）。
            string chained = TrySentenceChain(text, scope);
            if (chained != null)
            {
                handled = true;
                return chained;
            }

            return text;
        }

        /// <summary>
        /// 多句直接拼起来的段落：<c>履历句 + " " + 习惯句 + " " + 电台习惯句</c>。
        ///
        /// <para><b>为什么需要。</b><c>PilotIdentity.Background</c> 就是把三个数组元素用一个空格
        /// 串成整段的，每句单独都在词表里、整段却不在 —— 而且句号不是分隔符（把它加进
        /// <c>Delimiter</c> 会把 <c>1.00</c>、<c>3.5 KM</c> 切碎），于是整段永远是英文。
        /// 这里在<b>漏译路径的末尾</b>按 <c>". "</c> 切句、逐句查表、句号原样拼回。</para>
        ///
        /// <para><b>性能</b>：只有「前面所有模式都没命中」的文本才会走到这里，
        /// 一遍 <c>IndexOf</c> 扫描（O(n)）+ 每句一次字典查询；命中的句子会被 20,000 条
        /// 结果缓存兜住，同一段只算一次。实测这条路径的成本与相邻的
        /// <c>VersionSuffix</c>/<c>ValueUnitSuffix</c> 同量级（百纳秒级），
        /// 折算每帧 1–6 µs，占比 0.03% 以下。</para>
        ///
        /// <para><b>判据保守</b>：句段必须 ≥ 8 字符、整条最多切 6 句、且至少要有一句真的
        /// 译出中文才认领 —— 否则返回 null，让调用方照旧记漏译（不改变原有行为）。</para>
        /// </summary>
        private string TrySentenceChain(string text, string scope)
        {
            int first = text.IndexOf(". ", StringComparison.Ordinal);
            if (first < 8) return null;

            var sb = new StringBuilder(text.Length + 32);
            bool changed = false;
            int start = 0;
            int segments = 0;

            while (segments < 6)
            {
                int cut = text.IndexOf(". ", start, StringComparison.Ordinal);
                if (cut < 0) break;
                if (cut - start < 8) return null;      // 句段太短：多半不是句子（"e.g. " 之类）

                // ★ 句号要算进句子里：词表的键是带句号的（"…conversion."），
                //   少取一个字符就永远差一位，查不中。
                string sentence = text.Substring(start, cut - start + 1).Trim();
                if (sentence.Length == 0) return null;
                string translated = Localize(sentence, scope);
                if (translated == sentence) translated = sentence;
                sb.Append(translated);
                // 句号已在句段里；译文若把句号改成了「。」就没必要再补，
                // 否则会出现 "。. " 这种双句号。
                char lastChar = translated[translated.Length - 1];
                if (lastChar != '.' && lastChar != '\u3002') sb.Append('.');
                sb.Append(' ');
                changed |= translated != sentence;
                start = cut + 2;
                segments++;
            }

            if (start >= text.Length) return changed ? sb.ToString().TrimEnd() : null;
            string tail = text.Substring(start).Trim();
            if (tail.Length < 8) return null;
            string tailTrans = Localize(tail, scope);
            sb.Append(tailTrans == tail ? tail : tailTrans);
            changed |= tailTrans != tail;

            return changed ? sb.ToString() : null;
        }

        private string ReplacePrefix(string text, string prefix, string scope)
        {
            if (prefix.Length == 0) return text;
            // 名单里的前缀不翻译、也不记漏译。理由与 SliceAndRebuild 里的同名判定一致：
            // 排除判定只作用于整条文本，尾缀模式剥出来的前缀是**新字符串**，
            // 不在这里补判的话，名单条目会被当成漏译反复记下来。
            if (_exclusions.IsExcluded(prefix, scope)) return text;

            // ★ 术语（kept term）同样不记漏译。少了这一句，「AT-145 x2」「AGM-99 (HE)」
            //   这类「术语 + 尾缀」会走 VersionSuffix / ValueUnitSuffix / QuantitySuffix
            //   剥出前缀 `AT-145`、`AGM-99`，然后被记成漏译 —— 表现为
            //   「清单里永远是这几个缩写，补不掉也翻不出」，而它们本来就是要保持英文的。
            //   （pieces 路径早就判了术语，见 SliceAndRebuild；这里是对称补漏。）
            if (_exclusions.IsKeptTerm(prefix)) return text;

            string scoped = string.IsNullOrEmpty(scope) ? null : _table.LookupScoped(scope, prefix);
            string trans = scoped ?? _table.LookupGlobal(prefix);
            if (trans != null) return text.Replace(prefix, trans);
            MissCount++;
            _missLog.Record(prefix, scope);
            return text;
        }

        /// <summary>翻译一个词：查表，查不到原样返回。</summary>
        private string LocalizeToken(string token, string scope)
        {
            if (string.IsNullOrEmpty(token)) return token;
            string key = token.Trim();
            if (key.Length == 0) return token;

            if (!string.IsNullOrEmpty(scope))
            {
                string scoped = _table.LookupScoped(scope, key);
                if (scoped != null) return scoped;
            }
            return _table.LookupGlobal(key) ?? token;
        }

        /// <summary>递归翻译一段文本（不再做排除名单判定，因为调用方已判过）。</summary>

        /// <summary>用词表的反向索引反查原文。只覆盖普通词条；模板 / 片段拼接的结果不在词表里。</summary>
        internal bool TryReverseLookup(string chinese, out string original)
        {
            original = null;
            if (string.IsNullOrEmpty(chinese)) return false;
            string plain = TokenPatterns.Tag.Replace(chinese, string.Empty).Trim();
            if (plain.Length == 0) return false;
            return _table.TryReverse(plain, out original);
        }

        /// <summary>翻译关闭时的反向还原：把已显示的中文换回原文。</summary>
        private string Restore(string text)
        {
            string stripped = TokenPatterns.Tag.Replace(text, string.Empty);
            string cleaned = stripped.Trim();
            if (!HasChinese(cleaned)) return text;

            string original;
            if (_table.TryReverse(cleaned, out original))
            {
                return text.Replace(stripped, original);
            }
            return text;
        }

        // ================================================================== 判定工具

        /// <summary>是否含 CJK 汉字或 CJK 标点。</summary>
        internal static bool HasChinese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c >= '\u4e00' && c <= '\u9fff') || (c >= '\u3000' && c <= '\u303f')) return true;
            }
            return false;
        }

        internal static bool HasAsciiLetter(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return true;
            }
            return false;
        }

        internal static bool IsMultiLine(string s)
        {
            return s != null && (s.IndexOf('\n') >= 0 || s.IndexOf('\u000B') >= 0);
        }

        /// <summary>
        /// 是否是"本来就不该翻译"的片段。用于避免把纯数值/坐标计入漏译，
        /// 也避免为它们白白走一遍正则。
        /// </summary>
        internal static bool IsNoise(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            if (s.Length <= 1) return true;

            return TokenPatterns.NoiseValueUnit.IsMatch(s)
                || TokenPatterns.NoisePrefixedReadout.IsMatch(s)
                || TokenPatterns.NoiseTechnicalCode.IsMatch(s)
                || TokenPatterns.NoiseCoordinate.IsMatch(s)
                || TokenPatterns.NoisePureSymbols.IsMatch(s)
                || TokenPatterns.NoiseExplosive.IsMatch(s)
                || TokenPatterns.NoiseDistanceIndicator.IsMatch(s);
        }
    }
}
