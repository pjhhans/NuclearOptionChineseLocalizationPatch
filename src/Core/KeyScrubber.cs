using System.Text;

namespace NuclearOptionChineseLocalizationPatch.Core
{
    /// <summary>
    /// 词表键清洗。
    ///
    /// 游戏文本里混着大量不可见字符：零宽空格、BOM、左右标记符（LRM/RLM），
    /// 以及 <c>\r</c> —— 它们肉眼不可见，却会让字典键完全不等，
    /// 表现为"词表里明明有这一条，游戏里就是不翻"。
    ///
    /// 规则：删除零宽类字符与 <c>\r</c>；<c>\v</c> 与 <c>\t</c> 折叠成空格。
    /// 作用对象是**词表键载入**与**普通查表**两侧，必须严格一致。
    /// </summary>
    internal static class KeyScrubber
    {
        /// <summary>需要改写的字符全集。用于快速探测，避免每次分配 StringBuilder。</summary>
        private static bool NeedsWork(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                switch (s[i])
                {
                    case '\u200B':   // zero width space
                    case '\uFEFF':   // BOM
                    case '\u200E':   // LRM
                    case '\u200F':   // RLM
                    case '\r':
                    case '\u000B':
                    case '\t':
                        return true;
                }
            }
            return false;
        }

        internal static string Scrub(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (!NeedsWork(s)) return s;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\u200B':
                    case '\uFEFF':
                    case '\u200E':
                    case '\u200F':
                    case '\r':
                        continue;               // 直接丢弃
                    case '\u000B':
                    case '\t':
                        sb.Append(' ');         // 折叠成空格
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
