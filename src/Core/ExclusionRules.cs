using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NuclearOptionChineseLocalizationPatch.Core
{
    /// <summary>
    /// 不翻译名单。三个互相独立的维度：
    ///
    /// <list type="bullet">
    /// <item><b>Scopes</b> —— 整个作用域不翻译。用于每帧回写型 HUD 组件，
    ///       它们的文本由游戏持续覆盖，翻译只会造成拉锯。</item>
    /// <item><b>Texts</b> —— 精确排除的整条文本（以及以它 + 空格开头的读数）。</item>
    /// <item><b>Terms</b> —— 保持英文的术语（RCS / NEZ / SARH 这类缩写）。
    ///       它们写在固定宽度的槽位里，中文全称宽度是原文数倍，塞进去必然溢出；
    ///       而且保留英文缩写本身就是业界惯例。</item>
    /// </list>
    ///
    /// <para>三个集合的判定都必须**严格**，否则会把该翻译的东西一起放过。
    /// 判据细则见各方法注释。</para>
    /// </summary>
    internal sealed class ExclusionRules
    {
        private static readonly Regex TagPattern = new Regex(@"<[^>]*>", RegexOptions.Compiled);

        /// <summary>术语尾部可能带的标点。去掉后仍是术语即整体放过。</summary>
        private const string TrailingPunctuation = " :.= -/";

        private readonly HashSet<string> _scopes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _texts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _terms =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal int ScopeCount => _scopes.Count;
        internal int TextCount => _texts.Count;
        internal int TermCount => _terms.Count;

        internal static ExclusionRules FromJson(string scopesJson)
        {
            var rules = new ExclusionRules();
            if (string.IsNullOrWhiteSpace(scopesJson)) return rules;
            rules.Load(scopesJson);
            return rules;
        }

        /// <summary>从文件重新载入。可重复调用（热重载用），会先清空现有名单。</summary>
        internal void LoadFile(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    Diagnostics.Log.Warn("找不到排除名单，按空名单继续：" + path);
                    return;
                }
                Load(System.IO.File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn("排除名单载入失败：" + ex.Message);
            }
        }

        internal void Load(string json)
        {
            _scopes.Clear();
            _texts.Clear();
            _terms.Clear();
            try
            {
                var dto = Newtonsoft.Json.JsonConvert.DeserializeObject<ExclusionDto>(json);
                if (dto == null) return;
                AddAll(_scopes, dto.scopes);
                AddAll(_texts, dto.texts, scrub: true);
                AddAll(_terms, dto.terms);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn("exclusions.json 解析失败，按空名单继续：" + ex.Message);
            }
        }

        /// <summary>
        /// 加入集合。<paramref name="scrub"/> 为 true 时先过一遍
        /// <see cref="KeyScrubber"/> —— texts 名单必须洗掉零宽空格 / BOM / LRM / RLM，
        /// 否则名单里的 <c>008FFF\u200B</c> 与运行时来的 <c>008FFF</c> 是两个不同的字符串，
        /// 表现为「名单里明明登记了，却因为一个看不见的字符而匹配不上」。
        /// </summary>
        private static void AddAll(HashSet<string> set, List<string> items, bool scrub = false)
        {
            if (items == null) return;
            foreach (string s in items)
            {
                if (string.IsNullOrEmpty(s)) continue;
                string item = scrub ? KeyScrubber.Scrub(s) : s;
                if (item.Length > 0) set.Add(item);
            }
        }

        /// <summary>该作用域是否整体不翻译。</summary>
        internal bool IsScopeExcluded(string scope)
        {
            return !string.IsNullOrEmpty(scope) && _scopes.Contains(scope);
        }

        /// <summary>
        /// 整条文本是否需要排除。<paramref name="scope"/> 命中名单时直接返回 true。
        /// </summary>
        internal bool IsExcluded(string text, string scope)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (IsScopeExcluded(scope)) return true;

            if (_texts.Count == 0) return false;

            // 与名单同一套清洗（见 AddAll 的说明）：游戏会给输入框里的名字追加
            // \u200B（`airbase 1\u200B`、`玩家名\u200B`），不洗掉的话名单永远匹配不上。
            text = KeyScrubber.Scrub(text);
            string trimmed = text.Trim();
            if (_texts.Contains(text)) return true;
            if (trimmed != text && _texts.Contains(trimmed)) return true;

            // 「某项 + 空格开头」的读数一并排除：登记了 SPD 673，
            // 那么 SPD 673 km/h 也应当跟着排除，否则同一读数会一半中文一半英文。
            foreach (string candidate in _texts)
            {
                if (trimmed.Length > candidate.Length &&
                    trimmed[candidate.Length] == ' ' &&
                    string.Compare(trimmed, 0, candidate, 0, candidate.Length,
                                   StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 是否属于「保持英文」的术语。判据是**收窄**的，只有三种形态才算：
        ///
        /// <list type="number">
        /// <item>文本本身就是术语；</item>
        /// <item>术语 + 尾随标点；</item>
        /// <item>术语 + 分隔符（<c> </c> <c>:</c> <c>=</c>）+ 其余部分**含数字或纯符号**
        ///       —— 即后面跟的是读数而不是词。所以登记 <c>FS-3</c> 不会屏蔽
        ///       <c>FS-3 Ternion</c>（后者照常翻译）。</item>
        /// </list>
        /// </summary>
        internal bool IsKeptTerm(string text)
        {
            if (string.IsNullOrEmpty(text) || _terms.Count == 0) return false;

            string t = StripScopePrefix(text);
            t = TagPattern.Replace(t, string.Empty).Trim();
            if (t.Length == 0) return false;

            if (_terms.Contains(t)) return true;

            // 形态 2：去掉尾随标点后仍是术语
            int end = t.Length;
            while (end > 0 && TrailingPunctuation.IndexOf(t[end - 1]) >= 0) end--;
            if (end != t.Length && end > 0)
            {
                string head = t.Substring(0, end).TrimEnd();
                if (head.Length > 0 && _terms.Contains(head)) return true;
            }

            // 形态 3：术语 + 分隔符 + 读数
            //
            // ★ 判据必须收窄。旧写法是「rest 里**任意位置**出现数字就算读数」，
            //   于是术语后面跟一整句话时也会被整条判成"保持英文"。真实受害例子
            //   （都已写进 check_report_pipeline.py 的用例）：
            //     `VT-7 Vagrant +1.9`  —— 术语 VT-7 + 空格 + 后面带数字的一段话（击杀得分行）
            //     `… Airport / Ab12`   —— 术语 VT-7 + 空格 + 后面的格子坐标（部署播报）
            //   两条都是**整条不翻译**：结果里没有中文，也不进漏译清单，从界面到日志都看不出异常。
            //   现在只认真正的读数形状：纯数字/纯符号，或「数字 + 紧跟其后的短单位」
            //   （5.2、+1.9、40 km、12kJ）。
            foreach (string term in _terms)
            {
                int len = term.Length;
                if (t.Length <= len) continue;
                if (string.Compare(t, 0, term, 0, len,
                                   StringComparison.OrdinalIgnoreCase) != 0) continue;

                char sep = t[len];
                if (sep != ' ' && sep != ':' && sep != '=') continue;

                string rest = t.Substring(len).Trim(' ', ':', '=');
                if (rest.Length == 0) return true;

                bool hasDigit = false, hasLetter = false;
                foreach (char c in rest)
                {
                    if (c >= '0' && c <= '9') hasDigit = true;
                    else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) hasLetter = true;
                }
                if (!hasLetter) return true;                             // 纯数字 / 纯符号
                if (hasDigit && TermReadout.IsMatch(rest)) return true;   // 数字 + 短单位
                // 其余是「术语后面跟词句」，属正文，继续走后续翻译流程
            }
            return false;
        }

        /// <summary>
        /// 术语后面那种"读数"的形状：<c>5.2</c> / <c>+1.9</c> / <c>40 km</c> / <c>12kJ</c>。
        /// 单位最长 4 个字符 —— 再长就说明后面跟的是词而不是单位。
        /// </summary>
        private static readonly Regex TermReadout =
            new Regex(@"^[+\-±]?\s*[\d.,]+\s*[a-zA-Z/°%]{0,4}$", RegexOptions.Compiled);

        /// <summary>剥掉形如 <c>[Scope]</c> 的前缀。作用域名长度设为 1–40，避免把正文里的方括号误当作用域。</summary>
        internal static string StripScopePrefix(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '[') return text;
            int close = text.IndexOf(']');
            if (close <= 0 || close > 41) return text;
            return text.Substring(close + 1);
        }

        private sealed class ExclusionDto
        {
            public List<string> scopes { get; set; }
            public List<string> texts { get; set; }
            public List<string> terms { get; set; }
        }
    }
}
