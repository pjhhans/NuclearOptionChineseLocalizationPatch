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
        /// <summary>
        /// 富文本标签。必须是「真标签」形状：<c>&lt;/?[字母#]…&gt;</c>。
        ///
        /// <para><b>为什么不能用 <c>&lt;[^&gt;]*&gt;</c>。</b>通讯面板的台词引号
        /// <c>&lt;&lt; 台词 &gt;&gt;</c>（ChatterStrip 敌方网 / vanilla 聊天）会被它整段吃掉：
        /// 从第二个 <c>&lt;</c> 起，<c>[^&gt;]*</c> 一路吃到 <c>&gt;</c> —— 整句台词被当成一个
        /// "标签" 剥空，于是 <c>&lt;color=…&gt;&lt;&lt; 台词 &gt;&gt;&lt;/color&gt;</c> 的
        /// 内芯剥完是空串 → 永不翻译、也不进漏译（2026-10-30 实机：敌方红色通讯整行英文）。
        /// 真 TMP 标签的首字符只会是字母、<c>#</c>（颜色简写）或 <c>/</c>；
        /// <c>&lt;&lt;</c>、<c>&lt; </c>、<c>&lt;1</c> 都不是标签。</para>
        /// </summary>
        internal static readonly Regex Tag = new Regex(@"</?[A-Za-z#][^>]*>", RegexOptions.Compiled);

        /// <summary>
        /// 行分隔符：换行（<c>\n</c>）与垂直制表（<c>\v</c>）。
        /// 多行文本按它逐行翻译、再原样重组（分隔符本身保留）。
        /// </summary>
        internal static readonly Regex LineBreak = new Regex(@"[\n\v]", RegexOptions.Compiled);

        /// <summary>
        /// 分隔符。除了常规标点，还刻意包含：
        ///   <c>T/A-30</c> —— 机型代号里的斜杠不能当分隔符（否则会被切成 T 和 A-30）
        ///   <c>&lt;[字母#]…&gt;.*?&lt;/[字母]…&gt;</c> —— 成对标签整体作为一个"不可切"单元
        ///   <c>--</c> 与 <c>\s+-\s+</c> —— 破折号
        ///   <c>\t</c> —— 制表符。游戏里它是 UI 列表的**列分隔符**（例如任务编辑器的
        ///               编队标签 <c>BDF Combined Arms Company\t$250m</c>：名称 + 费用）。
        ///               不切开的话整条永远查不中词表，而名称本身是有词条的。
        ///   <c>·</c>（U+00B7）—— 第三方 mod 的**栏间分隔**（<c>COST 39 · POOL 994,520</c>）。
        ///               这类行天然是「若干『关键字 + 数值』小段」拼起来的，整段永远查不中；
        ///               切碎之后每段各走 <see cref="WordPlusNumber"/>，才能只翻关键字、留住数值。
        ///   <c>▪ ▸</c> —— mod 的次级分隔符（<c>1 VEH G1 ▪ 14.4 KM</c>）。
        ///   <c>+++</c> —— mod 新闻滚动条的**条目分隔符**
        ///               （<c>&lt;color=#00F0FF&gt;+++&lt;/color&gt;</c>，多条拼接成一整条串）。
        ///               不切开的话，首条正文的尾段会变成
        ///               <c>ECONOMY: … SHOPS  &lt;color=#00F0FF&gt;+++</c>——中间夹着标签，
        ///               查表必失败（2026-10-30 实机：只有第一条英文）。
        ///
        /// <para>标签分量与 <see cref="Tag"/> 同形状（<c>&lt;/?[A-Za-z#]…&gt;</c>）：
        /// 旧写法 <c>&lt;[^&gt;]+&gt;</c> 会把台词引号 <c>&lt;&lt; 台词 &gt;&gt;</c> 当成
        /// 一个"标签分隔符"整段吞掉 —— 被吞的文本不翻也不进漏译。</para>
        /// </summary>
        internal static readonly Regex Delimiter = new Regex(
            @"(T/A-30|\+\+\+|<[A-Za-z#][^>]*>.*?</[A-Za-z][^>]*>|</?[A-Za-z#][^>]*>|--|\s+-\s+|[:/\[\]()|\n\v\t\u00B7\u25AA\u25B8\u25B2\u2715])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 只由符号构成、切分后应原样跳过的分隔符。
        /// 必须与 <see cref="Delimiter"/> 里「本身没有语义」的那部分逐字一致 ——
        /// 漏一个就会把该符号当成待译片段，记成一条永远补不掉的漏译。
        /// （<c>+++</c> 是唯一的多字符条目，长度判据在 <c>SliceAndRebuild</c> 里单独处理。）
        /// </summary>
        internal const string LoneDelimiters = ":/[]()|\n\v\t\u00B7\u25AA\u25B8\u25B2\u2715";

        /// <summary>多字符的纯符号分隔符，切分后同样原样跳过。</summary>
        internal const string LoneDelimiterStrings = "+++";

        // ---------------------------------------------------------------- 噪声
        /// <summary>
        /// 纯读数与序号 —— 命中即**不进漏译清单**。
        ///
        /// <para>实机跑两小时，<c>missing.json</c> 里 463 条有 85 条是这类东西：
        /// <c>M 0.08  G 0.2</c>（马赫/过载双读数，一屏就能刷 43 条）、
        /// <c>×680</c>、<c>SN A814</c>（序列号）、<c>▲ 4</c>。
        /// 它们永远没有译文可补，却把真缺口淹没在噪声里 ——
        /// 判断放在 <see cref="Diagnostics.MissLog"/> 的写入侧，只影响记录、不影响翻译。</para>
        /// </summary>
        internal static readonly Regex ReadoutOnly = new Regex(
            @"^(?:(?:M|G)\s*[+\-]?\d+(?:\.\d+)?)(?:\s+(?:M|G)\s*[+\-]?\d+(?:\.\d+)?)*$" +
            @"|^×\s*\d[\d,]*$" +
            @"|^SN(?:\s+[0-9A-Fa-f]{2,8})?$" +
            @"|^[\d.,]+\s*[A-Z°]{0,4}$" +
            @"|^[\u25B2\u2715]\s*[\d.,]+$",
            RegexOptions.Compiled);

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

        /// <summary>纯符号行（含短横、斜杠、括号）。逗号也在内 —— 带千位分隔的坐标三元组
        /// （<c>37845.3, 283.1, 44958.8</c>）是调试读数，不该进漏译清单。</summary>
        internal static readonly Regex NoisePureSymbols = new Regex(
            @"^[+\-0-9\s.,/()\[\]%#@&*|<>—]+$", RegexOptions.Compiled);

        /// <summary>爆炸当量：<c>150K TNT</c>。</summary>
        internal static readonly Regex NoiseExplosive = new Regex(
            @"^\d*[\d.]+\s*(kg|kt)\s*tnt$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>距离指示：<c>250 m &lt;</c>。</summary>
        internal static readonly Regex NoiseDistanceIndicator = new Regex(
            @"^\d*[\d.]+\s*(m|km)\s*<$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>编号代码：<c>R12 A3</c>。</summary>
        internal static readonly Regex NoiseTechnicalCode = new Regex(
            @"^[RAVHM]\d+(\s+[RAVHM]\d+)*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 「读数」的形状：<c>3</c> / <c>673 km/h</c> / <c>+1.9</c> / <c>12kJ</c>。
        /// 单位最长 4 个字符 —— 再长就说明后面跟的是词句，不能当成读数。
        ///
        /// <para>排除名单的两处判据共用它：<c>IsExcluded</c> 用它确认「名单条目 + 空格」之后
        /// 确实是读数（否则 <c>A 155mm slug …</c> 这类整句会被误排除）；<c>IsKeptTerm</c> 用它
        /// 确认术语之后是读数而非词句（否则 <c>VT-7 Vagrant +1.9</c> 会被整条判成保持英文）。
        /// 两处都属于「判据放宽一点就会静默吞掉整句」的高危点，故形状收窄且共用同一定义。</para>
        /// </summary>
        internal static readonly Regex ReadoutTail = new Regex(
            @"^[+\-±]?\s*[\d.,]+\s*[a-zA-Z/°%]{0,4}$", RegexOptions.Compiled);

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

        /// <summary>
        /// <c>Rank 3</c> / <c>COST 39</c> / <c>POOL 994,520</c> / <c>CHANGE −$750</c> ——
        /// 词 + 数字，两者都要保留可读性。
        ///
        /// <para>数值尾部除了千分位逗号，还放行货币符号与前缀正负号（含 U+2212 数学减号）：
        /// 第三方 mod 的财政卡把 <c>"CHANGE " + FormatResource(delta)</c> 直接拼出来，
        /// 负值形如 <c>CHANGE −$750</c>，旧的正则只认裸数字，于是同一张卡上
        /// 正数那一格是中文、负数那一格是英文。</para>
        /// </summary>
        internal static readonly Regex WordPlusNumber = new Regex(
            @"^([a-zA-Z\s]+)\s+([+\-\u2212]?\$?\d[\d,]*(?:\.\d+)?[kKmM]?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// <c>47 CARDS</c> / <c>350 FUNDS</c> / <c>0 SORTIES</c> —— 数字在前、词在后。
        ///
        /// <para>与 <see cref="WordPlusNumber"/> 正好互为镜像。第三方 mod 的抬头与小结
        /// 大量用这个语序（<c>count + " CARDS"</c>、<c>credits + " FUNDS"</c>），
        /// 词表里明明有 <c>CARDS</c>／<c>SORTIES</c> 的译文，整段却一直是英文 ——
        /// 因为切片不切空格，整段作为一块去查表，永远差一个数字。</para>
        /// </summary>
        internal static readonly Regex NumberWord = new Regex(
            @"^(\d[\d,]*)\s+([A-Za-z][A-Za-z ]{0,24})$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// <c>01  OPEN a front for its map, programmes and perks.</c> ——
        /// 铭牌段落里带两位序号的行（<c>AvBayPlate.Compose</c> 的拼法是
        /// <c>(i+1).ToString("00") + "  " + orders[i]</c>，两条空格）。
        ///
        /// <para><b>为什么必须有这一条。</b>序号直接拼进正文，整行于是永远查不中词表；
        /// 而切分只看标点不看序号，这一行会作为一个完整片段落到漏译分支 ——
        /// 表现是「段落正文词条明明在同表里，铭牌上却整段英文」，mod 的 18 处
        /// 面板脚注全是这个形状。剥掉序号再查，序号原样拼回。</para>
        /// </summary>
        internal static readonly Regex NumberedLine = new Regex(
            @"^(\d{1,2})[ ]{2,}(?=[A-Za-z])(\S.*)$", RegexOptions.Compiled);

        /// <summary>
        /// <c>3X SPEARHEAD MBT</c> / <c>44% OWN</c> / <c>+35% SUPPORT COST</c> / <c>#1 COVER THE SUPPLY RUN</c>
        /// —— 数量或百分比**前缀**直接拼进正文。
        ///
        /// <para>这类串整条去查表永远差一个数字，而正文本身（<c>SPEARHEAD MBT</c>、
        /// <c>SUPPORT COST</c>）往往就在词表里。判据刻意收得很窄：前缀只能是
        /// 「<c>#n</c>」「<c>nX</c>」「<c>[+-]n%</c>」三种形状之一，必须后跟空格，
        /// 正文首字符不能是数字 —— 免得把 <c>17°C EST</c>、<c>M 0.08 G 0.2</c>
        /// 这类纯读数也认领进来。</para>
        /// </summary>
        internal static readonly Regex QuantityPrefix = new Regex(
            @"^((?:#\d{1,2}|\d{1,3}[Xx]|[+\-\u2212]?\d{1,3}(?:\.\d+)?%))[ ]+([A-Za-z0-9].*)$",
            RegexOptions.Compiled);

        /// <summary>
        /// <c>GUN ×680</c> / <c>AGR-24 ×8</c> / <c>POWER ×1.00</c> —— 乘号（U+00D7）+ 数字的数量后缀。
        ///
        /// <para>与已有的 <see cref="QuantitySuffix"/>（<c>Heater x4</c>，拉丁小写 x）是**两种字符**：
        /// mod 的装弹行用真乘号，并且允许小数（<c>×1.00</c>）。混用一条正则会把
        /// <c>×1.00</c> 判成不匹配（<c>x\d+</c> 不含小数点），于是同一排读数一半中文一半英文。</para>
        /// </summary>
        internal static readonly Regex TimesSuffix = new Regex(
            @"^(.{1,40}?)\s*×\s*(\d[\d,]*(?:\.\d+)?)$", RegexOptions.Compiled);

        /// <summary>
        /// <c>2S OLD</c> —— 秒数紧贴 <c>S</c>。词表里已有 <c> S OLD</c> 后缀片段，
        /// 但它要求数字与 S 之间有一个空格；mod 的倒计时是 <c>Clock(n) + "S OLD"</c> 拼的，
        /// 没有空格，于是 <c>2S OLD</c> 整条漏掉。
        /// </summary>
        internal static readonly Regex SecondsOld = new Regex(
            @"^(\d{1,4})S OLD$", RegexOptions.Compiled);

        /// <summary>
        /// 方位地标：<c>ASSAULT SW OF MARIS AIRPORT</c> / <c>N OF DUSTBOWL HIGHWAY STRIP</c>。
        ///
        /// <para><b>为什么要单独一条。</b>这类串是「（可选的行动类型）+ 八方位 + OF + 地名」，
        /// 由 <c>PlaceNames.Describe</c>（<c>Compass(...) + " OF " + 地名</c>）与
        /// <c>TheaterWords</c> 的行动类型拼出来。中文语序必须倒置（地名在前、方位在后），
        /// 而词表是「键 → 值」的逐段替换，**顺序改不了**，所以只能整条认领、按模板重排。</para>
        ///
        /// <para><b>性能</b>：单个锚定正则，量词全部有上界（<c>{0,23}</c> / <c>{0,47}</c>），
        /// 不允许 <c>.+</c> —— 无界量词遇上「像但结尾不合法」的长串会退化成 O(n²) 回溯。
        /// 实测（net6.0，40 万次）：命中 27 字符 295 ns、首字符不是字母的 280 字符滚动条 32 ns；
        /// 只挂在漏译路径上，约 1–6 µs/帧（0.03% 单帧，单核）。</para>
        ///
        /// <para>刻意**不加 IgnoreCase**：<c>OF</c> 必须大写才算方位串，
        /// 否则普通散文里的 "s of t" 也会被当成方位重排。</para>
        /// </summary>
        internal static readonly Regex CompassOfPlace = new Regex(
            @"^([A-Za-z][A-Za-z ]{0,23} )?(N|NE|E|SE|S|SW|W|NW) OF ([A-Za-z0-9][A-Za-z0-9 '\.\-]{0,47})$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// <c>RAM-45 5</c> / <c>AGM-68 x2</c> 之类「型号 + 数量」。
        ///
        /// <para>为什么要单独一条：型号里带连字符与数字（<c>RAM-45</c>、<c>T/A-30</c>），
        /// 而 <see cref="WordPlusNumber"/> 的词部只允许字母与空白，于是整条落不进任何模式，
        /// 最后被记成一条永远补不掉的漏译（补 `RAM-45 5` 会把数量锁死在键里，
        /// 而数量随挂载变化）。剥出型号后交给 <c>ReplacePrefix</c>：
        /// 型号若是 identity（`RAM-45` → `RAM-45`），译文与原文相同 ⇒ 不算漏译。</para>
        /// </summary>
        internal static readonly Regex ModelPlusCount = new Regex(
            @"^([a-zA-Z][a-zA-Z0-9\-/\.\s]*?)\s+(\d+)$",
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

        /// <summary>
        /// 补给战报的费用归因尾缀：<c>$45.3k by Munitions Bunker</c>。
        ///
        /// <para><b>为什么必须有这一条。</b>游戏里补给战报的真实拼法是
        /// <c>"Rearmed " + " {0:F0}% complete" + " - cost: " + 金额 + " by " + 补给单位名</c>
        /// （<c>Unit::UserCode_RpcRearm</c> 的 IL 逐字如此），所以 <c> - cost: </c> 之后剩下的
        /// 那一截<b>恰好是「金额 + by + 单位名」</b>——金额是动态读数、整串永远匹配不上词表，
        /// 只有把 <c>by</c> 后面的单位名单独翻出来才可能出中文。</para>
        ///
        /// <para><b>为什么不用中段片段 <c>== by </c>。</b>英文里 " by " 到处都是（散文、装备描述、
        /// <c>picked up by ships</c>…）。做成通用中段会<b>劫持任意含 " by " 的句子</b>，
        /// 产出「半中半英」的畸形结果；更糟的是结果含中文会命中幂等短路，
        /// 于是<b>永久固化、永不重试，还不会进漏译清单</b>。
        /// 这里用<b>金额锚定</b>把它收窄到唯一一种真实形状：<c>$</c> 开头的读数 + <c>by</c> + 名称。</para>
        /// </summary>
        internal static readonly Regex CostByUnitSuffix = new Regex(
            @"^(\$[\d.,]+\s*[a-zA-Z]?)\s+by\s+([A-Za-z][A-Za-z0-9\s\-'.]*)$",
            RegexOptions.Compiled);

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
