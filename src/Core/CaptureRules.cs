using System;
using System.Text;

namespace NuclearOptionChineseLocalizationPatch.Core
{
    /// <summary>
    /// 把「通配模板捕获到的运行时实值」按固定格式转成中文。
    ///
    /// <para><b>为什么需要它。</b>第三方 mod 的台词模板是数据资产，运行时把占位符替换成实值：
    /// <c>&lt;bra&gt;</c> 由 <c>Core/Math/Bra.cs</c> 拼成
    /// <c>BRA 090, 40 kilometres, 1500 metres, hot</c>，<c>&lt;clock&gt;</c> 是
    /// <c>three o'clock</c>，<c>&lt;runway&gt;</c> 是 <c>Runway 21</c>。
    /// 这些串每次数字都不同，永远不会有精确词条；不做规则，整句就只能是
    /// 「敌机，BRA 090, 40 kilometres, 1500 metres, hot。」这种半中半英。</para>
    ///
    /// <para><b>边界。</b>只认**明确的结构**（前缀 <c>BRA </c>、结尾 <c>o'clock</c>、
    /// 整串就是 <c>Runway N</c>），匹配不上就返回 <c>null</c> 交给精确查表 ——
    /// 宁可不改，也不能把呼号、玩家名这类自由文本改坏。</para>
    ///
    /// <para><b>成本。</b>只在通配段回译时调用，且先看首/尾几个字符再决定是否深入解析；
    /// 一次调用是常数级字符串操作，没有正则、没有分配（除结果串本身）。</para>
    /// </summary>
    internal static class CaptureRules
    {
        /// <summary>返回转换结果；没有匹配的格式返回 <c>null</c>。</summary>
        internal static string Convert(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;

            string bra = Bra(s);
            if (bra != null) return bra;

            string clock = Clock(s);
            if (clock != null) return clock;

            string runway = Runway(s);
            if (runway != null) return runway;

            return null;
        }

        /// <summary>
        /// <c>BRA 090, 40 kilometres, 1500 metres, hot</c> →
        /// <c>方位 090，40 公里，1500 米，迎头</c>。
        ///
        /// <para>格式来自 <c>Core/Math/Bra.cs</c>：方位三位数；距离公制带 <c>kilometres</c>、
        /// 英制只写数字（海里）；高度公制带 <c>metres</c>、英制是 <c>NN thousand</c>（千英尺）；
        /// 最后一段是目标航向（<c>hot/flanking/beaming/cold</c>），目标太慢时没有这一段。</para>
        /// </summary>
        private static string Bra(string s)
        {
            if (s.Length < 5 || s[0] != 'B' || s[1] != 'R' || s[2] != 'A' || s[3] != ' ')
                return null;

            string[] parts = s.Substring(4).Split(new[] { ", " }, StringSplitOptions.None);
            if (parts.Length < 2) return null;

            var sb = new StringBuilder(s.Length + 8);
            sb.Append("方位 ").Append(parts[0].Trim());

            if (parts.Length > 1)
            {
                sb.Append('，').Append(Measure(parts[1], "公里"));
            }
            if (parts.Length > 2)
            {
                string alt = parts[2];
                if (EndsWithWord(alt, "thousand"))
                    sb.Append('，').Append(TrimWord(alt, "thousand")).Append(" 千英尺");
                else
                    sb.Append('，').Append(Measure(alt, "米"));
            }
            if (parts.Length > 3)
            {
                string aspect = Aspect(parts[3].Trim());
                if (aspect != null) sb.Append('，').Append(aspect);
            }
            return sb.ToString();
        }

        /// <summary>「N kilometres」→「N 公里」；纯数字（英制海里）原样保留；其它一律放弃。</summary>
        private static string Measure(string text, string unit)
        {
            if (EndsWithWord(text, "kilometres") || EndsWithWord(text, "kilometers"))
                return TrimWord(text, text.EndsWith("kilometres", StringComparison.Ordinal) ? "kilometres" : "kilometers") + " " + unit;
            if (EndsWithWord(text, "metres") || EndsWithWord(text, "meters"))
                return TrimWord(text, text.EndsWith("metres", StringComparison.Ordinal) ? "metres" : "meters") + " " + unit;

            string t = text.Trim();
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] < '0' || t[i] > '9') return t;   // 不是纯数字：原样吐回去
            }
            return t;
        }

        private static bool EndsWithWord(string text, string word)
        {
            if (text.Length <= word.Length) return false;
            if (string.CompareOrdinal(text, text.Length - word.Length, word, 0, word.Length) != 0) return false;
            return text[text.Length - word.Length - 1] == ' ';
        }

        private static string TrimWord(string text, string word)
        {
            return text.Substring(0, text.Length - word.Length).Trim();
        }

        private static string Aspect(string word)
        {
            switch (word)
            {
                case "hot": return "迎头";
                case "flanking": return "侧向";
                case "beaming": return "横切";
                case "cold": return "尾追";
                default: return null;
            }
        }

        /// <summary><c>three o'clock</c> / <c>12 o'clock</c> → <c>3 点方向</c>。</summary>
        private static string Clock(string s)
        {
            const string suffix = " o'clock";
            if (s.Length <= suffix.Length) return null;
            if (string.CompareOrdinal(s, s.Length - suffix.Length, suffix, 0, suffix.Length) != 0) return null;

            string word = s.Substring(0, s.Length - suffix.Length).Trim();
            string number = ClockNumber(word);
            return number == null ? null : number + " 点方向";
        }

        private static string ClockNumber(string word)
        {
            switch (word)
            {
                case "twelve": return "12";
                case "one": return "1";
                case "two": return "2";
                case "three": return "3";
                case "four": return "4";
                case "five": return "5";
                case "six": return "6";
                case "seven": return "7";
                case "eight": return "8";
                case "nine": return "9";
                case "ten": return "10";
                case "eleven": return "11";
            }
            // 也接受直接写数字的版本（其它 mod / 后续版本可能改成 "3 o'clock"）
            if (word.Length == 0 || word.Length > 2) return null;
            for (int i = 0; i < word.Length; i++)
            {
                if (word[i] < '0' || word[i] > '9') return null;
            }
            return word;
        }

        /// <summary><c>Runway 21</c> / <c>Runway 09L</c> → <c>21 号跑道</c>。</summary>
        private static string Runway(string s)
        {
            const string head = "Runway ";
            if (s.Length <= head.Length) return null;
            if (string.CompareOrdinal(s, 0, head, 0, head.Length) != 0) return null;

            string rest = s.Substring(head.Length).Trim();
            if (rest.Length == 0 || rest.Length > 3) return null;
            for (int i = 0; i < rest.Length; i++)
            {
                char c = rest[i];
                bool ok = (c >= '0' && c <= '9') || (i == rest.Length - 1 && (c == 'L' || c == 'R' || c == 'C'));
                if (!ok) return null;
            }
            return rest + " 号跑道";
        }
    }
}
