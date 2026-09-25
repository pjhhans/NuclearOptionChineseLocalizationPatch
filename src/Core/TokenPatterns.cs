using System.Text.RegularExpressions;

namespace NuclearOptionChineseLocalizationPatch.Core
{
    /// <summary>
    /// 全部正则模式的唯一出处。
    ///
    /// 分两组：
    ///   <b>噪声模式</b> —— 识别"本来就不该翻译"的片段（纯数值、坐标、爆炸当量…），
    ///                     避免把它们误记成漏译。
    ///   <b>尾缀模式</b> —— 把 HUD 读数拆成「可翻译的词」+「必须原样保留的数值」。
    ///
    /// 全部使用 <see cref="RegexOptions.Compiled"/>：这些模式在渲染路径上被高频调用，
    /// 解释执行的开销会直接体现为帧时间。
    /// </summary>
    internal static class TokenPatterns
    {
        // ---------------------------------------------------------------- 切分与标签
        /// <summary>富文本标签。注意是 <c>*</c> 而非 <c>+</c>，要能匹配 <c>&lt;&gt;</c> 空标签。</summary>
        internal static readonly Regex Tag = new Regex(@"<[^>]*>", RegexOptions.Compiled);

        /// <summary>
        /// 分隔符。除了常规标点，还刻意包含：
        ///   <c>T/A-30</c> —— 机型代号里的斜杠不能当分隔符（否则会被切成 T 和 A-30）
        ///   <c>&lt;[^&gt;]+&gt;.*?&lt;/[^&gt;]+&gt;</c> —— 成对标签整体作为一个"不可切"单元
        ///   <c>--</c> 与 <c>\s+-\s+</c> —— 破折号
        /// </summary>
        internal static readonly Regex Delimiter = new Regex(
            @"(T/A-30|<[^>]+>.*?</[^>]+>|<[^>]+>|--|\s+-\s+|[:/\[\]()|\n\v])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ---------------------------------------------------------------- 噪声
        /// <summary>纯数值 + 短单位：<c>673 km/h</c>、<c>$1.96m</c>。</summary>
        internal static readonly Regex NoiseValueUnit = new Regex(
            @"^[+-]?\s*\$?\d*[\d.]+\s*[a-zA-Z/°%]{0,6}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>带前缀的读数：<c>x4</c>、<c>CAPACITOR 12 kJ</c>、<c>V +1.2</c>、<c>mag x4</c>。</summary>
        internal static readonly Regex NoisePrefixedReadout = new Regex(
            @"^(x\s*\d+|capacitor\s*[\d.]+\s*[a-z]*|[vhm]\s*[+-]?\s*[\d.]+|" +
            @"[\$¥€]\s*[\d.]+[kKmM]?|[\d.]+[kKmM]\s*[\$¥€]|mag\s*x\s*\d*[\d.]+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>网格坐标：<c>Ab12</c>。</summary>
        internal static readonly Regex NoiseCoordinate = new Regex(
            @"^[A-Z][a-z](\d{1,4})?$", RegexOptions.Compiled);

        /// <summary>纯符号行（含短横、斜杠、括号）。</summary>
        internal static readonly Regex NoisePureSymbols = new Regex(
            @"^[+\-0-9\s./()\[\]%#@&*|<>—]+$", RegexOptions.Compiled);

        /// <summary>爆炸当量：<c>150K TNT</c>。</summary>
        internal static readonly Regex NoiseExplosive = new Regex(
            @"^\d*[\d.]+\s*(kg|kt)\s*tnt$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>距离指示：<c>250 m &lt;</c>。</summary>
        internal static readonly Regex NoiseDistanceIndicator = new Regex(
            @"^\d*[\d.]+\s*(m|km)\s*<$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>编号代码：<c>R12 A3</c>。</summary>
        internal static readonly Regex NoiseTechnicalCode = new Regex(
            @"^[RAVHM]\d+(\s+[RAVHM]\d+)*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ---------------------------------------------------------------- 尾缀数值
        /// <summary><c>Version 1.2.3</c> —— 保留版本号，翻译前缀。</summary>
        internal static readonly Regex VersionSuffix = new Regex(
            @"^(.*?version.*?)\s*(\d+\.\d+\.\d+.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary><c>Runway 09</c> —— 保留跑道号。</summary>
        internal static readonly Regex RunwaySuffix = new Regex(
            @"^(.*?\brunway)\s+\d{1,2}.*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary><c>SPD 673 km/h</c> —— 保留数值与单位。</summary>
        internal static readonly Regex ValueUnitSuffix = new Regex(
            @"^(.*?)\s+[+-]?\d*[\d.]+\s*([a-zA-Z°%]{1,3})$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary><c>Heater x4</c> —— 保留数量词。</summary>
        internal static readonly Regex QuantitySuffix = new Regex(
            @"^(.*?)\s+x\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary><c>Rank 3</c> —— 词 + 数字，两者都要保留可读性。</summary>
        internal static readonly Regex WordPlusNumber = new Regex(
            @"^([a-zA-Z\s]+)\s+([+-]?\d+(?:\.\d+)?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary><c>12 kJ</c> —— 只有数值 + 单位，翻单位。</summary>
        internal static readonly Regex NumberUnitOnly = new Regex(
            @"^(\d*[\d.]+)\s*([a-zA-Z/°%]+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary><c>Score 42</c>。</summary>
        internal static readonly Regex ScoreSuffix = new Regex(
            @"^(score\s*)[\d.]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary><c>Rearmed +100%</c>。</summary>
        internal static readonly Regex PlusNumberSuffix = new Regex(
            @"^(.+?)\s*\+\s*(\d+(?:\.\d{1,5})?)$", RegexOptions.Compiled);

        // ---------------------------------------------------------------- 句式
        /// <summary><c>Booting ...</c> —— 启动提示。</summary>
        internal static readonly Regex BootingSentence = new Regex(
            @"^(Booting)\s+(.+?)(\.{0,4})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary><c>Buy ...</c> —— 采购提示。</summary>
        internal static readonly Regex BuySentence = new Regex(
            @"^(Buy)\s+(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary><c>... set to ...</c> —— 设置提示，主干是 "set to"。</summary>
        internal static readonly Regex SetToSentence = new Regex(
            @"^((?:\S+\s+){0,2}\S+)\s+(set to)\s+((?:\S+\s+){0,1}\S+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary><c>Cleared to taxi to runway 09</c> —— 滑行许可。</summary>
        internal static readonly Regex TaxiToSentence = new Regex(
            @"^(Cleared to taxi to|Taxi to)\s+(runway\s+\d{1,2}|(?:\S+\s+){1,2}\S+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary><c>... Turret under pilot control</c> —— 炮塔归属说明。</summary>
        internal static readonly Regex TurretControl = new Regex(
            @"^(.*?)\s+(Turret|Turrent)\s+(under\s+pilot\s+control)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}
