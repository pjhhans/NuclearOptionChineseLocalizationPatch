using System.Text.RegularExpressions;

namespace NuclearOptionChineseLocalizationPatch.Core
{
    /// <summary>
    /// 把原文规范化成**模板键**的可比形式。
    ///
    /// 顺序不能变：
    ///   1. 富文本标签 → <c>\u0001</c>（占位，保留"这里有个标签"的信息）
    ///   2. <c>\r\n</c> 与裸 <c>\r</c> → <c>\n</c>
    ///   3. <c>\v</c> → <c>\n</c>
    ///   4. 连续空格 / 制表符折叠成一个空格
    ///   5. Trim
    ///
    /// <para><b>第 2 步的 CR 处理不能省略。</b>词表键在载入时会经过
    /// <see cref="KeyScrubber.Scrub"/>，而它会把 <c>\r</c> <b>删掉</b>；
    /// 如果这里保留 <c>\r</c>，含 CRLF 的原文（例如教程正文
    /// <c>away.\r\nGrey = ...</c>）就会键分叉，模板静默失效。
    /// 统一成 <c>\n</c> 之后两侧都不含 <c>\r</c>，差异消失。</para>
    ///
    /// <para>生成词表的脚本必须与这里逐字符一致，否则模板永远命不中。</para>
    /// </summary>
    internal static class TextCanonicalizer
    {
        private static readonly Regex TagPattern = new Regex(@"<[^>]*>", RegexOptions.Compiled);
        private static readonly Regex HorizontalSpace = new Regex(@"[ \t]+", RegexOptions.Compiled);

        /// <summary>标签在归一化结果里的占位符。译文中的该字符按序回填原文标签。</summary>
        internal const char TagPlaceholder = '\u0001';

        internal static string Canonicalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            string s = TagPattern.Replace(text, "\u0001");
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            s = s.Replace('\u000B', '\n');
            s = HorizontalSpace.Replace(s, " ");
            return s.Trim();
        }

        /// <summary>指纹匹配允许的最短长度。太短的指纹会与普通文本撞车。</summary>
        internal const int MinFingerprintLength = 12;

        /// <summary>
        /// 去标签**指纹**：在 <see cref="Canonicalize"/> 的结果上把标签占位符整个删掉、再折叠空白。
        ///
        /// <para><b>为什么需要它</b>：教程弹窗正文里的 <c>&lt;bind=X&gt;</c> 由游戏在写入控件<em>之前</em>
        /// 就解析成「字形 / 文本 / 空」三态（例如 <c>&lt;sprite name="G"&gt;</c>，或者什么都不留）。
        /// 于是进到插件手里的字符串其**标签个数与词表键不一致**，逐字符比对必然失配 ——
        /// 表现是「词条明明在表里、那一行却整行英文、日志干净、还进漏译清单」。
        /// 指纹把两侧的标签差异抹平后仍能命中，译文里的占位符再按
        /// <see cref="ExpandTemplate"/> 的规则回填（多余的自动丢弃）。</para>
        /// </summary>
        internal static string Fingerprint(string canonical)
        {
            if (canonical == null) return string.Empty;
            if (canonical.IndexOf(TagPlaceholder) < 0) return canonical;
            return HorizontalSpace.Replace(canonical.Replace(TagPlaceholder.ToString(), string.Empty), " ").Trim();
        }

        /// <summary>
        /// 展开模板：把译文里的 <see cref="TagPlaceholder"/> 按出现顺序替换成原文中的第 N 个标签。
        /// 译文占位符少于原文标签时，多余的标签直接丢弃 —— 绝不把英文标签糊到中文末尾。
        /// </summary>
        internal static string ExpandTemplate(string original, string translation)
        {
            if (translation.IndexOf(TagPlaceholder) < 0) return translation;

            MatchCollection tags = TagPattern.Matches(original);
            var sb = new System.Text.StringBuilder(translation.Length + 32);
            int next = 0;
            foreach (char c in translation)
            {
                if (c != TagPlaceholder)
                {
                    sb.Append(c);
                    continue;
                }
                if (next < tags.Count) sb.Append(tags[next].Value);
                next++;
            }
            return sb.ToString();
        }
    }
}
