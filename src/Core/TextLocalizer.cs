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

        internal TextLocalizer(LocalizationTable table, ExclusionRules exclusions, MissLog missLog)
        {
            _table = table;
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
        /// </summary>
        internal string Localize(string text, string scope)
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

            string cached;
            if (_cache.TryGetValue(text, out cached)) return cached;

            string result = LocalizeCore(text, scope);

            if (_cacheLimit > 0)
            {
                if (_cache.Count >= _cacheLimit) _cache.Clear();
                _cache[text] = result;
            }
            return result;
        }

        // ================================================================== 主体

        private string LocalizeCore(string text, string scope)
        {
            // 多行：逐行翻译后原样重组（分隔符本身也要保留）。
            if (IsMultiLine(text))
            {
                string[] lines = text.Split(new[] { '\n', '\u000B' });
                MatchCollection separators = Regex.Matches(text, @"[\n\v]");
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
                if (scopedHit != null && stripped.Length > 0) return text.Replace(stripped, scopedHit);
            }

            if (!isForceScoped)
            {
                string whole = _table.LookupGlobal(key);
                if (whole != null && stripped.Length > 0) return text.Replace(stripped, whole);
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
                return (head.Length == 0 ? string.Empty : LocalizePart(head, scope) + " ")
                       + LocalizeToken(turret, scope) + " " + LocalizeToken(control, scope);
            }

            m = TokenPatterns.BootingSentence.Match(text);
            if (m.Success)
            {
                return LocalizeToken(m.Groups[1].Value, scope) + " "
                       + LocalizePart(m.Groups[2].Value, scope) + m.Groups[3].Value;
            }

            m = TokenPatterns.BuySentence.Match(text);
            if (m.Success)
            {
                return LocalizeToken(m.Groups[1].Value, scope) + " "
                       + LocalizePart(m.Groups[2].Value, scope);
            }

            m = TokenPatterns.SetToSentence.Match(text);
            if (m.Success)
            {
                return LocalizePart(m.Groups[1].Value, scope) + " "
                       + LocalizeToken(m.Groups[2].Value, scope) + " "
                       + LocalizePart(m.Groups[3].Value, scope);
            }

            m = TokenPatterns.TaxiToSentence.Match(text);
            if (m.Success)
            {
                return LocalizeToken(m.Groups[1].Value, scope) + " "
                       + LocalizePart(m.Groups[2].Value, scope);
            }

            return null;
        }

        /// <summary>
        /// 片段拼接：<c>&gt;&gt;</c> 前缀 / <c>&lt;&lt;</c> 后缀 / <c>==</c> 中段。
        /// 命中后把固定短语换成译文，再**递归翻译剩余部分**（变量可能是单位名等，也需要翻译）。
        /// 没命中返回 null，交给切片逻辑。
        /// </summary>
        private string ConcatFragments(string text, string scope)
        {
            if (text.Length < 4) return null;

            // 用 &lt; 而不是 &lt;= 做长度判定：整段**恰好就是**固定短语时
            // （"K92 Highway Strip has been captured by "）也要命中，此时剩余为空串，
            // 递归返回空，结果就是纯译文。
            foreach (LocalizationTable.Fragment f in _table.PrefixFragments)
            {
                if (text.Length < f.Key.Length) continue;
                if (string.CompareOrdinal(text, 0, f.Key, 0, f.Key.Length) != 0) continue;
                // TrimStart：游戏拼串常带多余空格（"Rearmed " + " 100% complete"），
                // 直接拼进译文会留下难看的空隙。
                return f.Value + LocalizePart(text.Substring(f.Key.Length).TrimStart(), scope);
            }

            foreach (LocalizationTable.Fragment f in _table.SuffixFragments)
            {
                if (text.Length < f.Key.Length) continue;
                int at = text.Length - f.Key.Length;
                if (string.CompareOrdinal(text, at, f.Key, 0, f.Key.Length) != 0) continue;
                return LocalizePart(text.Substring(0, at).TrimEnd(), scope) + f.Value;
            }

            foreach (LocalizationTable.Fragment f in _table.InfixFragments)
            {
                if (text.Length < f.Key.Length) continue;
                int at = text.IndexOf(f.Key, StringComparison.Ordinal);
                if (at < 0) continue;
                return LocalizePart(text.Substring(0, at), scope)
                       + f.Value
                       + LocalizePart(text.Substring(at + f.Key.Length), scope);
            }

            return null;
        }

        /// <summary>C 阶段：按分隔符切片，逐段处理，最后原样拼回。</summary>
        private string SliceAndRebuild(string text, string scope, bool isForceScoped)
        {
            string[] parts = TokenPatterns.Delimiter.Split(text);
            bool changed = false;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrEmpty(part)) continue;

                // 单字符分隔符本身
                if (part.Length == 1 && ":/[]()|\n\v".IndexOf(part[0]) >= 0) continue;

                // ---- 富文本标签块
                if (part[0] == '<' && part[part.Length - 1] == '>')
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
                        string innerTranslated = LocalizePart(innerTrim, scope);
                        if (innerTranslated != innerTrim)
                        {
                            parts[i] = part.Replace(innerTrim, innerTranslated);
                            changed = true;
                        }
                    }
                    continue;
                }

                // ---- 纯文本段
                string piece = part.Trim();
                if (piece.Length == 0 || HasChinese(piece)) continue;
                // 术语片段保持英文，且**不记入漏译** —— 它不是漏译，是刻意保留。
                if (_exclusions.IsKeptTerm(piece)) continue;

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

                bool handled;
                string patterned = ApplyTailPatterns(part, out handled, scope);
                if (patterned != part)
                {
                    parts[i] = patterned;
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
        private string ApplyTailPatterns(string text, out bool handled, string scope)
        {
            handled = false;

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
                    string trans = LocalizePart(prefix, scope);
                    // 重新拼合，保留 "+" 与数值的原样
                    return trans + text.Substring(epn.Groups[1].Length);
                }
            }

            return text;
        }

        private string ReplacePrefix(string text, string prefix, string scope)
        {
            if (prefix.Length == 0) return text;
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
        private string LocalizePart(string text, string scope) => Localize(text, scope);

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
