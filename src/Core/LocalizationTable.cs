using System;
using System.Collections.Generic;
using System.IO;

namespace NuclearOptionChineseLocalizationPatch.Core
{
    /// <summary>
    /// 词表容器。负责把四类键（普通 / <c>~</c> 模板 / <c>&gt;&gt;</c> <c>&lt;&lt;</c> <c>==</c> 片段 /
    /// <c>[Scope]</c> 作用域词条）分开索引，并回答"这条原文有没有译文"。
    ///
    /// <para><b>数据布局</b>（分类文件与下面的索引结构一一对应）：</para>
    /// <list type="bullet">
    /// <item><c>translation.json</c> —— 通用词条 → <see cref="_global"/></item>
    /// <item><c>templates.json</c> —— <c>~</c> 模板 → <see cref="_templates"/> + 指纹索引</item>
    /// <item><c>fragments.json</c> —— <c>&gt;&gt;</c> <c>&lt;&lt;</c> <c>==</c> → 三张片段表</item>
    /// <item><c>scopes/*.json</c> —— <c>[Scope]</c> 词条，按语义域分文件 → <see cref="_scoped"/></item>
    /// </list>
    /// <para>载入是**按键前缀分流**的，与键落在哪个文件无关，所以老的单文件布局照样能读。
    /// 布局的权威定义与写出规则在 <c>tools/_table_layout.py</c>。</para>
    ///
    /// <para>索引结构的选择理由：</para>
    /// <list type="bullet">
    /// <item>普通键用 <see cref="StringComparer.OrdinalIgnoreCase"/> 字典 —— 游戏原文大小写不稳定
    ///       （<c>Faction funds</c> / <c>Faction Funds</c> 是同一个键），大小写敏感会漏一半。</item>
    /// <item>作用域词条单独成表：最热的 <see cref="_global"/> 因此从 4600+ 条降到约 2700 条，
    ///       而 <see cref="LookupScoped"/> 的首次探测（每条要渲染的文本都会走）落在小表上。</item>
    /// <item>片段按**键长度降序**排列 —— 匹配时取第一个命中的，长键必须先于短键，
    ///       否则 <c>has been captured by</c> 会被更短的片段抢先切走。</item>
    /// <item>模板记下**最短键长**作为门禁 —— 短于它的文本不可能命中任何模板，
    ///       直接跳过归一化与查表，省掉渲染路径上最贵的一步。</item>
    /// </list>
    /// </summary>
    internal sealed class LocalizationTable
    {
        internal readonly struct Fragment
        {
            internal readonly string Key;
            internal readonly string Value;
            internal Fragment(string key, string value) { Key = key; Value = value; }
        }

        private readonly Dictionary<string, string> _global =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _templates =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 通用表里是否混有 <c>[Scope]原文</c> 形态的键。
        ///
        /// <para>正常载入下恒为 false：<see cref="Ingest"/> 会把所有这类键分流进
        /// <see cref="Scoped"/> 分表，两者用的是同一套前缀判据。于是
        /// <see cref="LookupScoped"/> 末尾那段「主表里的 <c>[Scope]原文</c>」回落
        /// 是**可证死路径**（每次未命中都要白付两次字符串拼接与两次忽略大小写哈希）。</para>
        ///
        /// <para>唯一的例外是作用域名超过 41 字符 —— 那超出了
        /// <c>ExclusionRules.TrySplitScopePrefix</c> 的判据，载入时落进通用表。
        /// 这种情况只在真正发生时置位，回落才重新生效，语义与之前完全一致。</para>
        /// </summary>
        private bool _globalHasBracketKeys;

        /// <summary>
        /// 模板键的**去标签指纹**索引，用于「标签个数与词表键不一致」时的回落匹配。
        ///
        /// <para>值为 <c>null</c> 表示该指纹有**两个以上**模板键共用 ⇒ 无法判定是哪一个，
        /// 整组作废（宁可漏翻也不翻错）。</para>
        /// </summary>
        private readonly Dictionary<string, string> _templateFingerprints =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private int _fingerprintCollisions;

        /// <summary>
        /// 参数化模板（键含 <see cref="ParamMarker"/>，即 <c>{#}</c>），见 <see cref="AddParamTemplate"/>。
        /// 与精确/指纹索引**互斥**：带标记的键不会进那两个索引（真实文本里不会出现 <c>{#}</c>，
        /// 进了也永远命不中，白占内存）。
        /// </summary>
        private readonly List<ParamTemplate> _paramTemplates = new List<ParamTemplate>();

        /// <summary>参数化模板实际命中次数（诊断用；仅在命中时累加）。</summary>
        internal int ParamTemplateHits;

        /// <summary>
        /// 文本通配模板（键含 <see cref="WildMarker"/>，即 <c>{*}</c>）。
        ///
        /// <para>动机：第三方 mod 的台词是**运行时拼装**的，模板里写着
        /// <c>Operation &lt;name&gt;. Phase is now &lt;phase&gt;.</c>，装配后变成
        /// <c>Operation Maris Airport. Phase is now AWAITING FORCES.</c>。
        /// 中间那段是任意文本（不是数字，<c>{#}</c> 吃不下），
        /// 而中文语序与英文不同，靠固定前后缀片段拼接必错 —— 只能整句匹配。</para>
        ///
        /// <para><see cref="WildTemplateIndex"/> 按首段前 4 个字符分桶，
        /// 避免每次都对全表线性扫描（这是 2026-10-29 那次「通用短词片段」事故的教训：
        /// 任何「逐条尝试」的匹配路径都必须先有索引和门禁）。</para>
        /// </summary>
        private readonly Dictionary<string, List<WildTemplate>> _wildByHead =
            new Dictionary<string, List<WildTemplate>>(StringComparer.Ordinal);

        /// <summary>首段为空（键以 <c>{*}</c> 开头）的模板，数量极少，单独放。</summary>
        private readonly List<WildTemplate> _wildHeadless = new List<WildTemplate>();

        /// <summary>
        /// 通配段（<c>{*}</c> 捕获到的那段文本）的译文钩子。
        ///
        /// <para>台词里的占位符替换上来的是**英文实值**（<c>Maris Airport</c> /
        /// <c>AWAITING FORCES</c>），它们本身在词表里有译文。不回译的话，
        /// 整句会变成「行动 Maris Airport。当前阶段：AWAITING FORCES。」这种半中半英。
        /// 由 <see cref="TextLocalizer"/> 注入，只做**精确查表**，绝不递归进模板路径。</para>
        /// </summary>
        internal Func<string, string> CaptureTranslator;

        /// <summary>文本通配模板条数 / 命中次数（诊断用）。</summary>
        internal int WildTemplateCount => _wildTemplateCount;
        internal int WildTemplateHits;
        private int _wildTemplateCount;

        private readonly Dictionary<string, string> _reverse =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly List<Fragment> _prefixFragments = new List<Fragment>();
        private readonly List<Fragment> _suffixFragments = new List<Fragment>();
        private readonly List<Fragment> _infixFragments = new List<Fragment>();

        private readonly Dictionary<string, Dictionary<string, string>> _scoped =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private int _scopedEntries;

        private readonly HashSet<string> _forceScoped =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private int _minTemplateKeyLength = int.MaxValue;

        internal int GlobalCount => _global.Count;
        /// <summary>
        /// 模板条数：精确模板 + 参数化模板（<c>{#}</c>）。
        /// 两者只是索引不同，都是真实存在于词表、也真能命中的键，计数必须一并算上。
        /// </summary>
        internal int TemplateCount => _templates.Count + _paramTemplates.Count + _wildTemplateCount;
        /// <summary>作用域分表里的词条总数（不含分表个数，个数见 <see cref="ScopeCount"/>）。</summary>
        internal int ScopedEntryCount => _scopedEntries;
        /// <summary>
        /// 全表词条总数：通用 + 模板（含参数化）+ 片段 + 作用域词条。用于给用户看的「词表 N 条」。
        ///
        /// <para>这是**索引条目数**：键仅大小写不同的会被合并成一条（四个索引都建在
        /// <see cref="StringComparer.OrdinalIgnoreCase"/> 上），所以会略小于词表文件的原始行数。</para>
        /// </summary>
        internal int TotalEntryCount =>
            _global.Count + _templates.Count + _paramTemplates.Count + _wildTemplateCount
            + FragmentCount + _scopedEntries;
        /// <summary>可用的去标签指纹条数（撞车作废的不计）。</summary>
        internal int TemplateFingerprintCount
        {
            get
            {
                int n = 0;
                foreach (string v in _templateFingerprints.Values) if (v != null) n++;
                return n;
            }
        }
        internal int FragmentCount =>
            _prefixFragments.Count + _suffixFragments.Count + _infixFragments.Count;
        internal int ScopeCount => _scoped.Count;
        internal IReadOnlyList<Fragment> PrefixFragments => _prefixFragments;
        internal IReadOnlyList<Fragment> SuffixFragments => _suffixFragments;
        internal IReadOnlyList<Fragment> InfixFragments => _infixFragments;

        // ------------------------------------------------------------------ 载入

        /// <summary>
        /// 从数据目录载入全部词表资源。单个文件损坏不影响其余部分。
        ///
        /// <para><b>主词表是先解析、后替换。</b>手改词表漏一个逗号时反序列化会抛异常；
        /// 若此时已经清空，游戏立刻全屏英文，而日志里只有一条 Warn（正常运行没人看日志）。
        /// 宁可保留上一份仍然可用的词表，把问题留在日志里。</para>
        ///
        /// <para><b>分类文件</b>（<c>templates.json</c> / <c>fragments.json</c> /
        /// <c>scopes/*.json</c>）是**可选**的：载入按键前缀分流，不看键来自哪个文件，
        /// 所以老的单文件布局、或者用户只搬走了一部分，都不会坏。但「文件在、内容却解析不出来」
        /// 必须**中止**本次载入 —— 否则会静默丢掉 127 条模板与 58 条片段，界面残缺而毫无提示。</para>
        /// </summary>
        /// <returns>主词表是否成功替换。false 表示本次载入已中止、现有词表原封未动。</returns>
        internal bool Load(string dataDir)
        {
            string mainPath = Path.Combine(dataDir, "translation.json");
            Dictionary<string, string> main = JsonFile.Read<Dictionary<string, string>>(mainPath);
            if (main == null || main.Count == 0)
            {
                Diagnostics.Log.Error(
                    "translation.json 解析失败或为空，未替换现有词表（现有普通 " + _global.Count + " 条）。" +
                    "若是按 F11 热重载后出现，请检查该文件语法。");
                return false;
            }

            string broken = null;
            Dictionary<string, string> templates =
                ReadOptional(Path.Combine(dataDir, "templates.json"), ref broken);
            Dictionary<string, string> fragments =
                ReadOptional(Path.Combine(dataDir, "fragments.json"), ref broken);
            if (broken != null)
            {
                Diagnostics.Log.Error(
                    broken + " 解析失败，本次载入已中止（现有词表仍在使用）。请检查该文件的 JSON 语法。");
                return false;
            }

            _global.Clear(); _templates.Clear(); _reverse.Clear();
            _templateFingerprints.Clear(); _fingerprintCollisions = 0;
            _paramTemplates.Clear(); ParamTemplateHits = 0;
            _wildByHead.Clear(); _wildHeadless.Clear();
            _wildTemplateCount = 0; WildTemplateHits = 0;
            _prefixFragments.Clear(); _suffixFragments.Clear(); _infixFragments.Clear();
            _scoped.Clear(); _scopedEntries = 0; _forceScoped.Clear();
            _globalHasBracketKeys = false;
            _minTemplateKeyLength = int.MaxValue;

            Ingest(main);
            Ingest(templates);
            Ingest(fragments);
            LoadScopeFiles(Path.Combine(dataDir, "scopes"));
            LoadForceScopes(Path.Combine(dataDir, "force_scopes.json"));

            _prefixFragments.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            _suffixFragments.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            _infixFragments.Sort((a, b) => b.Key.Length - a.Key.Length);

            foreach (Dictionary<string, string> dict in _scoped.Values) _scopedEntries += dict.Count;
            return true;
        }

        /// <summary>
        /// 读一个**可选**的分类文件。不存在 → 返回 null 并放行（老布局里这些键就在主表里）；
        /// 存在但解析不出来 → 记进 <paramref name="failure"/>，由调用方中止载入。
        /// </summary>
        private static Dictionary<string, string> ReadOptional(string path, ref string failure)
        {
            if (!File.Exists(path)) return null;
            Dictionary<string, string> dict = JsonFile.Read<Dictionary<string, string>>(path);
            if (dict == null && failure == null) failure = Path.GetFileName(path);
            return dict;
        }

        /// <summary>
        /// 吃下一份词表文件。**按键前缀分流，与键来自哪个文件无关** —— 这是「读宽松」的全部：
        /// <c>~</c> → 模板；<c>&gt;&gt;</c> <c>&lt;&lt;</c> <c>==</c> → 片段；
        /// <c>[Scope]原文</c> → 该作用域的分表；其余 → 通用词条。
        ///
        /// <para>前缀判定的**顺序**不能动（<c>~</c> 必须最先），与
        /// <c>tools/_table_layout.py</c> 的 <c>kind_of()</c> 逐条对应。</para>
        /// </summary>
        private void Ingest(Dictionary<string, string> raw)
        {
            if (raw == null) return;
            foreach (KeyValuePair<string, string> kv in raw)
            {
                string key = kv.Key;
                string value = kv.Value;
                if (string.IsNullOrEmpty(key) || value == null) continue;

                if (key[0] == '~' && key.Length > 1)
                {
                    string canonical = TextCanonicalizer.Canonicalize(key.Substring(1));
                    if (canonical.Length == 0) continue;

                    // 参数化模板（{#} = 一段数字）：走独立索引，不进精确/指纹。
                    // 键里的数字位是变化的（如「via Rank ({0})」运行时填等级），
                    // 精确/指纹都对它无能为力。门禁仍用 _minTemplateKeyLength，
                    // 所以这里也要参与最短键长统计（按 {#} 取最短可能 —— 1 位数字）。
                    if (canonical.IndexOf(WildMarker, StringComparison.Ordinal) >= 0)
                    {
                        WildTemplate wt = AddWildTemplate(canonical, value);
                        if (wt != null && wt.MinLength < _minTemplateKeyLength)
                            _minTemplateKeyLength = wt.MinLength;
                        continue;
                    }

                    if (canonical.IndexOf(ParamMarker, StringComparison.Ordinal) >= 0)
                    {
                        AddParamTemplate(canonical, value);
                        int minLength = ParamMinLength(canonical);
                        if (minLength < _minTemplateKeyLength) _minTemplateKeyLength = minLength;
                        continue;
                    }

                    _templates[canonical] = value;
                    if (canonical.Length < _minTemplateKeyLength) _minTemplateKeyLength = canonical.Length;

                    // 顺带建指纹索引。撞车（两个键去掉标签后一模一样）⇒ 整组置 null 作废：
                    // 无法判定该用哪一条译文时，宁可让这条回落失效，也不能翻错。
                    string fingerprint = TextCanonicalizer.Fingerprint(canonical);
                    if (fingerprint.Length >= TextCanonicalizer.MinFingerprintLength)
                    {
                        if (_templateFingerprints.ContainsKey(fingerprint))
                        {
                            if (_templateFingerprints[fingerprint] != null) _fingerprintCollisions++;
                            _templateFingerprints[fingerprint] = null;
                        }
                        else
                        {
                            _templateFingerprints[fingerprint] = value;
                        }
                    }
                }
                else if (key.Length > 2 && key[0] == '>' && key[1] == '>')
                {
                    _prefixFragments.Add(new Fragment(key.Substring(2), value));
                }
                else if (key.Length > 2 && key[0] == '<' && key[1] == '<')
                {
                    _suffixFragments.Add(new Fragment(key.Substring(2), value));
                }
                else if (key.Length > 2 && key[0] == '=' && key[1] == '=')
                {
                    _infixFragments.Add(new Fragment(key.Substring(2), value));
                }
                else
                {
                    string scopeName, text;
                    if (ExclusionRules.TrySplitScopePrefix(key, out scopeName, out text))
                    {
                        AddScoped(scopeName, text, value);
                        continue;
                    }

                    string cleaned = KeyScrubber.Scrub(key).Trim();
                    if (cleaned.Length == 0) continue;
                    _global[cleaned] = value;
                    // 只有超出作用域前缀判据（作用域名 > 41 字符）的键会走到这里还带 '['，
                    // 它就是 LookupScoped 末尾那段回落的唯一服务对象。
                    if (cleaned[0] == '[') _globalHasBracketKeys = true;
                    RegisterReverse(ExclusionRules.StripScopePrefix(cleaned), value);
                }
            }
        }

        /// <summary>
        /// 登记一条作用域词条。作用域名来自键上的 <c>[Scope]</c> 前缀，或分表文件名
        /// （文件名即作用域名是历史约定，见 <see cref="LoadScopeFiles"/>）。
        /// </summary>
        private void AddScoped(string scope, string text, string value)
        {
            if (string.IsNullOrEmpty(scope) || text == null) return;
            string cleaned = KeyScrubber.Scrub(text).Trim();
            if (cleaned.Length == 0) return;

            Dictionary<string, string> dict;
            if (!_scoped.TryGetValue(scope, out dict))
            {
                dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _scoped[scope] = dict;
            }
            dict[cleaned] = value;
            RegisterReverse(cleaned, value);
        }

        /// <summary>
        /// 建反向索引（关闭翻译时把已显示的中文还原回原文）。
        /// <paramref name="plainOriginal"/> 必须是**已经剥掉作用域前缀**的原文。
        ///
        /// <para>★ 作用域词条**也要**登记：它们以前躺在主表里，靠主表那一步顺手建；
        /// 分表之后若不在这里补一次，「关闭翻译」对这些条目会永久失效 ——
        /// 中文一动不动，而且没有任何日志可查。</para>
        /// </summary>
        private void RegisterReverse(string plainOriginal, string value)
        {
            string visible = TokenPatterns.Tag.Replace(value, string.Empty).Trim();
            if (plainOriginal.Length > 0 && visible.Length > 0 && !_reverse.ContainsKey(visible))
            {
                _reverse[visible] = plainOriginal;
            }
        }

        /// <summary>
        /// 载入 <c>scopes/</c> 下的作用域分表。两种写法都认：
        /// <list type="bullet">
        /// <item>键自带 <c>[Scope]</c> 前缀 → 用键里的作用域（现在仓库里用的是这种）；</item>
        /// <item>键不带前缀 → 用**文件名**当作用域名（历史约定，例如 <c>TypeText.json</c>）。</item>
        /// </list>
        /// </summary>
        private void LoadScopeFiles(string scopesDir)
        {
            if (!Directory.Exists(scopesDir)) return;
            string[] files = Directory.GetFiles(scopesDir, "*.json");
            // 排序：反向索引是「先到先得」，顺序必须确定，否则同一份数据在不同机器上
            // 可能给出不同的反向结果（"关闭翻译"时表现为个别条目还原成另一个原文）。
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                Dictionary<string, string> raw = JsonFile.Read<Dictionary<string, string>>(file);
                if (raw == null) continue;

                string stem = Path.GetFileNameWithoutExtension(file);
                foreach (KeyValuePair<string, string> kv in raw)
                {
                    string scopeName, text;
                    if (!ExclusionRules.TrySplitScopePrefix(kv.Key, out scopeName, out text))
                    {
                        scopeName = stem;
                        text = kv.Key;
                    }
                    AddScoped(scopeName, text, kv.Value);
                }
            }
        }

        private void LoadForceScopes(string path)
        {
            List<string> list = JsonFile.Read<List<string>>(path);
            if (list == null) return;
            foreach (string s in list)
            {
                if (!string.IsNullOrEmpty(s)) _forceScoped.Add(s);
            }
        }

        // ------------------------------------------------------------------ 查询

        /// <summary>全局精确查表（清洗 + Trim 后）。</summary>
        internal string LookupGlobal(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string key = KeyScrubber.Scrub(text).Trim();
            if (key.Length == 0) return null;
            string value;
            return _global.TryGetValue(key, out value) ? value : null;
        }

        /// <summary>
        /// 作用域查询：分表（文件名即作用域名）优先，其次主表里的 <c>[Scope]原文</c> 形式。
        /// 返回 null 表示该作用域下没有登记。
        /// </summary>
        /// <summary>
        /// 作用域层级：细分域 → 它的父域。只有一层，没有父域时返回 <c>null</c>。
        ///
        /// <para><c>BS:EvtLog</c> 是 mod HUD 里<b>数据链记录那一个面板</b>（对象
        /// <c>BoscaliEventLog</c> 子树）的细分域，用途只有一个 —— 让该面板里的装备名
        /// 保持英文原样。它的其余文本（<c>INTCP</c> / <c>DEMO</c> / <c>CRASH</c> /
        /// 动词 / 表头 <c>DL EVT LOG</c>）都还登记在父域 <c>BS:HUD</c> 里，
        /// 所以查不到必须回落到父域，否则切域当天这些词会整批变回英文。</para>
        ///
        /// <para>用字面量而不是引 <c>Patching.PatchHelpers</c> 的常量：Core 层不反向
        /// 依赖 Patching 层。两处字符串必须同步改。</para>
        /// </summary>
        private static string ParentScopeOf(string scope)
        {
            return string.Equals(scope, "BS:EvtLog", StringComparison.OrdinalIgnoreCase) ? "BS:HUD" : null;
        }

        internal string LookupScoped(string scope, string text)
        {
            // 「作用域查询」段（性能探针，默认关）。这是本文件被调用最频繁的方法之一，
            // 单次成本很低，所以判读时以探针给出的**次数**为主、毫秒数为辅。
            long probe = Diagnostics.PerfProbe.Begin();
            try
            {
                if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(text)) return null;

                Dictionary<string, string> dict;
                if (_scoped.TryGetValue(scope, out dict))
                {
                    string hit;
                    if (dict.TryGetValue(text, out hit)) return hit;

                    string cleaned = KeyScrubber.Scrub(text).Trim();
                    if (cleaned.Length > 0 && dict.TryGetValue(cleaned, out hit)) return hit;
                }

                // 子域回落父域：mod HUD 里「数据链记录」那一个面板单独成域（BS:EvtLog），
                // 只用来登记「这些名字保持英文」；它的类别词（INTCP/DEMO/CRASH…）、
                // 动词、表头仍然登记在父域 BS:HUD 里。不回落的话，切域当天这些词
                // 就全变回英文（2026-10-30 实机：01:26 CRASH / 01:33 DEMO）。
                string parent = ParentScopeOf(scope);
                if (parent != null && _scoped.TryGetValue(parent, out dict))
                {
                    string hit;
                    if (dict.TryGetValue(text, out hit)) return hit;

                    string cleaned = KeyScrubber.Scrub(text).Trim();
                    if (cleaned.Length > 0 && dict.TryGetValue(cleaned, out hit)) return hit;
                }

                // 主表里的 [Scope]原文 形态。载入期已证明通用表里不会有这类键
                // （见 _globalHasBracketKeys），所以这里默认整段跳过 ——
                // 省掉每次未命中的两次拼接与两次忽略大小写哈希。
                if (!_globalHasBracketKeys) return null;

                string scopedKey = "[" + scope + "]" + text;
                string value;
                if (_global.TryGetValue(scopedKey, out value)) return value;

                string clean = KeyScrubber.Scrub(text).Trim();
                if (clean.Length > 0 && _global.TryGetValue("[" + scope + "]" + clean, out value))
                {
                    return value;
                }
                return null;
            }
            finally
            {
                Diagnostics.PerfProbe.End(Diagnostics.PerfProbe.Seg.Scoped, probe);
            }
        }

        /// <summary>该作用域是否被声明为强制作用域（只走本作用域词条 + 全局精确，不做模糊匹配）。</summary>
        internal bool IsForceScoped(string scope)
        {
            return !string.IsNullOrEmpty(scope) && _forceScoped.Contains(scope);
        }

        /// <summary>
        /// 整段模板查询。<paramref name="text"/> 会先归一化；
        /// 短于最短模板键长的文本直接判否（门禁）。
        ///
        /// <para><b>三步</b>：先逐字符精确匹配；失配则退到<b>去标签指纹</b>匹配 ——
        /// 教程弹窗的 <c>&lt;bind=X&gt;</c> 会被游戏在写入控件前解析成字形 / 文本 / 空，
        /// 标签个数因此与词表键不一致。没有这一步的话，那类卡片会整行停留在英文，
        /// 而且日志干净、只留一条漏译记录（查不出来源）。
        /// 再失配则退到<b>参数化模板</b>（键含 <c>{#}</c>，见 <see cref="AddParamTemplate"/>）。</para>
        /// </summary>
        internal bool TryGetTemplate(string text, out string value)
        {
            bool wildcard;
            return TryGetTemplate(text, out value, out wildcard);
        }

        /// <summary>
        /// 同上，另外告诉调用方命中是不是来自**文本通配模板**。
        ///
        /// <para>区分的原因：通配模板是「按句型的猜测」，而整串精确词条是「确知」。
        /// 一条长句里恰好含 <c>destroyed</c> 时，<c>~{*} destroyed {*}</c> 会抢在
        /// 整串词条之前把句子拆成两半填进去 —— 调用方拿到 <c>wildcard=true</c> 后
        /// 应先用精确词条试一次，成功就以精确词条为准。</para>
        /// </summary>
        internal bool TryGetTemplate(string text, out string value, out bool wildcard)
        {
            // 「模板」段（性能探针，默认关）：整段套计时，多条提前返回路径都由 finally 收口。
            long probe = Diagnostics.PerfProbe.Begin();
            try
            {
                value = null;
                wildcard = false;
                if (string.IsNullOrEmpty(text)) return false;
                if (_templates.Count == 0 && _paramTemplates.Count == 0) return false;
                if (text.Length < _minTemplateKeyLength) return false;

                string canonical = TextCanonicalizer.Canonicalize(text);
                if (canonical.Length < _minTemplateKeyLength) return false;
                if (_templates.TryGetValue(canonical, out value) && value != null) return true;

                // ---- 回落一：去标签指纹
                if (_templateFingerprints.Count > 0)
                {
                    string fingerprint = TextCanonicalizer.Fingerprint(canonical);
                    if (fingerprint.Length >= TextCanonicalizer.MinFingerprintLength
                        && _templateFingerprints.TryGetValue(fingerprint, out value)
                        && value != null)
                    {
                        TemplateFingerprintHits++;
                        return true;
                    }
                }

                // ---- 回落二：文本通配模板（{*} = 任意一段文本）。命中即回填成最终译文。
                if (_wildTemplateCount > 0)
                {
                    string expandedWild = MatchWildTemplates(canonical);
                    if (expandedWild != null)
                    {
                        WildTemplateHits++;
                        value = expandedWild;
                        wildcard = true;
                        return true;
                    }
                }

                // ---- 回落三：参数化模板（{#} = 一段数字）。命中即展开成最终译文。
                if (_paramTemplates.Count > 0)
                {
                    string expanded = MatchParamTemplates(canonical);
                    if (expanded != null)
                    {
                        ParamTemplateHits++;
                        value = expanded;
                        return true;
                    }
                }

                value = null;
                return false;
            }
            finally
            {
                Diagnostics.PerfProbe.End(Diagnostics.PerfProbe.Seg.Template, probe);
            }
        }

        /// <summary>
        /// 参数化模板的占位标记（<c>{#}</c>）：键里表示「这里是一段**变化**的数字」，
        /// 译文里表示「把命中的数字原样填回来」。
        ///
        /// <para>动机：游戏会把运行期数字直接格式化进文本再写控件
        /// （如 <c>Requisition\n via Rank ({0})</c> → <c>…(2)</c>），
        /// 数字逐机不同 ⇒ 精确键与去标签指纹都命不中，而多行文本又被逐行切分，
        /// 换行拆不掉 —— 表现就是按钮文字两行、溢出边框。
        /// 参数化模板在多行切分**之前**的模板路径整串命中，译文写成一行即可合并换行。</para>
        ///
        /// <para><b>约束（加载时校验，违反即丢弃该键）</b>：固定段不得以数字**开头**
        /// —— 否则前一个 <c>{#}</c> 贪婪吃位后会失配；<c>{#}</c> 至少匹配一位数字。</para>
        /// </summary>
        internal const string ParamMarker = "{#}";

        /// <summary>
        /// 文本通配占位符：键里的 <c>{*}</c> 表示「这里是一段**任意文本**」，
        /// 译文里用 <c>{0}</c> <c>{1}</c> … 按出现顺序回填。
        /// 与 <see cref="ParamMarker"/>（只吃数字）互补，见 <see cref="AddWildTemplate"/>。
        /// </summary>
        internal const string WildMarker = "{*}";

        /// <summary>键按 1 位数字展开后的最短可能长度（供模板路径的门禁）。</summary>
        private static int ParamMinLength(string canonical)
        {
            return canonical.Length
                   - (Occurrences(canonical, ParamMarker) * (ParamMarker.Length - 1));
        }

        /// <summary>
        /// 注册一条参数化模板。<paramref name="canonical"/> 是**已归一化**的键（含 <c>{#}</c>），
        /// <paramref name="value"/> 是译文（也可含 <c>{#}</c>，命中后按顺序回填数字；
        /// 若还含标签占位符，会在 <see cref="TextCanonicalizer.ExpandTemplate"/> 里照常回填标签）。
        /// </summary>
        private void AddParamTemplate(string canonical, string value)
        {
            string[] segments = canonical.Split(new[] { ParamMarker }, StringSplitOptions.None);
            if (segments.Length < 2) return;                 // 没有 {#}，不该走到这

            // 固定段不得以数字开头：前一个 {#} 贪婪吃完数字后，剩余数字会被误判进固定段。
            // （首段允许任意，因为 {#} 前没有贪婪问题；尾段同理只看开头。）
            for (int i = 1; i < segments.Length; i++)
            {
                if (segments[i].Length > 0 && IsAsciiDigit(segments[i][0])) return;
            }

            _paramTemplates.Add(new ParamTemplate(segments, value, ParamMinLength(canonical)));
        }

        /// <summary>
        /// 注册一条文本通配模板。键形如 <c>Operation {*}. Phase is now {*}.</c>，
        /// 译文用 <c>{0}</c>/<c>{1}</c> 回填。
        ///
        /// <para><b>加载期校验（不合法直接丢弃，宁可漏翻也不翻错）</b>：</para>
        /// <list type="bullet">
        /// <item>必须至少一个 <c>{*}</c>；</item>
        /// <item>每个 <c>{*}</c> 至少吃 1 个字符，所以 <c>MinLength = 固定段总长 + 通配个数</c>；</item>
        /// <item>译文必须逐个引用 <c>{0}</c>…<c>{n-1}</c> —— 少引用就意味着把运行时值丢了，
        /// 那种「半句中文 + 少了主角」的译文比不翻更糟。</item>
        /// </list>
        /// </summary>
        private WildTemplate AddWildTemplate(string canonical, string value)
        {
            string[] segments = canonical.Split(new[] { WildMarker }, StringSplitOptions.None);
            if (segments.Length < 2) return null;

            int captures = segments.Length - 1;
            for (int i = 0; i < captures; i++)
            {
                if (!value.Contains("{" + i + "}")) return null;   // 漏引用运行时值
            }

            var wt = new WildTemplate(segments, value);
            wt.Owner = this;
            _wildTemplateCount++;

            string head = segments[0];
            if (head.Length == 0)
            {
                _wildHeadless.Add(wt);
            }
            else
            {
                string key = head.Length > WildHeadLength ? head.Substring(0, WildHeadLength) : head;
                List<WildTemplate> list;
                if (!_wildByHead.TryGetValue(key, out list))
                {
                    list = new List<WildTemplate>(2);
                    _wildByHead[key] = list;
                }
                list.Add(wt);
            }
            return wt;
        }

        /// <summary>分桶用的首段前缀长度。4 个字符足够把 6 百多条台词摊薄，也不会漏掉短前缀的键。</summary>
        private const int WildHeadLength = 4;

        /// <summary>
        /// 通配模板能作用的文本最长长度。
        ///
        /// <para><b>为什么需要上限。</b>通配模板服务的是**战报短行**
        /// （<c>甲 destroyed 乙</c>，mod 截断后不超过 55 字符）。而单位描述是
        /// 200 字符上下的整段话，里面可能恰好含 <c>destroyed</c>：
        /// <c>~{*} destroyed {*}</c> 于是把整段按 <c>{0} 摧毁了 {1}</c> 展开，
        /// 得到「…the repair and reconstruction of 摧毁了 hangars, helipads and vehicle depots.」
        /// 这种夹生句 —— 而且它**先于整串精确词条执行**，把本该命中的整段译文也挡掉了
        /// （2026-10-30 实机：M12 折叠刀描述）。</para>
        ///
        /// <para>180 是「战报行上限 55 / 一般 UI 句子 &lt; 120」与「描述段落 &gt; 200」之间的空档。</para>
        /// </summary>
        private const int MaxWildcardSubjectLength = 180;

        private string MatchWildTemplates(string canonical)
        {
            // 长文本不是通配模板的适用对象（见 MaxWildcardSubjectLength）。
            if (canonical.Length > MaxWildcardSubjectLength) return null;

            // ★ 顺序：**先查有固定前缀的**，再查首段为空的兜底。
            //   反过来的话，`~{*}. Phase is now {*}.` 会把
            //   「Operation Maris Airport. Phase is now AWAITING FORCES.」
            //   整句吃成「Maris Airport。当前阶段：…」，丢掉「行动」两个字（实测踩过）。
            int max = canonical.Length < WildHeadLength ? canonical.Length : WildHeadLength;
            for (int len = max; len >= 1; len--)
            {
                List<WildTemplate> list;
                if (!_wildByHead.TryGetValue(canonical.Substring(0, len), out list)) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    string r = list[i].Match(canonical);
                    if (r != null) return r;
                }
            }

            // 首段为空的（键以 {*} 开头）无法分桶，只能逐条试 —— 数量靠 check_data 压到个位数。
            for (int i = 0; i < _wildHeadless.Count; i++)
            {
                string r = _wildHeadless[i].Match(canonical);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>文本通配模板：固定段 + 每段之间一段任意文本。见 <see cref="AddWildTemplate"/>。</summary>
        private sealed class WildTemplate
        {
            internal readonly string[] Segments;
            internal readonly string Value;
            internal readonly int MinLength;

            /// <summary>
            /// 所属表。通配段译文钩子（<see cref="LocalizationTable.CaptureTranslator"/>）
            /// 是 <b>载入之后</b> 由 TextLocalizer 注入的，所以这里只能存表引用、
            /// 匹配时再取 —— 注册那一刻取的话永远是 null（踩过一次）。
            /// </summary>
            internal LocalizationTable Owner;

            internal WildTemplate(string[] segments, string value)
            {
                Segments = segments;
                Value = value;
                int fixedLen = 0;
                for (int i = 0; i < segments.Length; i++) fixedLen += segments[i].Length;
                MinLength = fixedLen + (segments.Length - 1);
            }

            internal string Match(string canonical)
            {
                int n = Segments.Length;
                if (canonical.Length < MinLength) return null;

                string head = Segments[0];
                if (head.Length > 0 && !canonical.StartsWith(head, StringComparison.Ordinal)) return null;

                string tail = Segments[n - 1];
                if (tail.Length > 0)
                {
                    if (canonical.Length - tail.Length < head.Length) return null;
                    if (string.CompareOrdinal(canonical, canonical.Length - tail.Length, tail, 0, tail.Length) != 0)
                        return null;
                }

                int pos = head.Length;
                int end = canonical.Length - tail.Length;
                if (end - pos < n - 1) return null;              // 每个通配至少 1 个字符

                string[] caps = new string[n - 1];
                for (int i = 1; i < n - 1; i++)
                {
                    string seg = Segments[i];
                    int at = seg.Length == 0 ? pos : canonical.IndexOf(seg, pos, end - pos, StringComparison.Ordinal);
                    if (at < 0) return null;
                    if (at <= pos) return null;                  // 通配段不能为空
                    caps[i - 1] = canonical.Substring(pos, at - pos);
                    pos = at + seg.Length;
                }
                if (end <= pos) return null;
                caps[n - 2] = canonical.Substring(pos, end - pos);

                // 通配段里出现换行说明匹配跨了行 —— 台词都是单行，跨行必有误。
                for (int i = 0; i < caps.Length; i++)
                {
                    if (caps[i].IndexOf('\n') >= 0 || caps[i].IndexOf('\r') >= 0) return null;
                }

                // 被动语态守卫（**所有**通配模板通用，不只裸二元）：
                // ① 捕获段以 be 动词收尾 —— "…factories are destroyed…" 被
                //    `{*} destroyed {*}` 吃成 {0}="…are"（victory 文本，2026-10-30 实机）；
                //    片段级通配放开后受害面更大：「X was TAKEN」会被 `~{*} TAKEN`
                //    吃成「X was 已被夺取」。
                // ② 捕获段以 "by " 开头 —— 主动句的施动者被当成了受动者。
                // 命中即放弃：主动语态的值模板套在被动句上必然支离破碎。
                for (int i = 0; i < caps.Length; i++)
                {
                    if (EndsWithBeVerb(caps[i]) || StartsWithBy(caps[i])) return null;
                }

                Func<string, string> tr = Owner != null ? Owner.CaptureTranslator : null;
                string result = Value;
                for (int i = 0; i < caps.Length; i++)
                {
                    string cap = caps[i];
                    if (tr != null) cap = TranslateCaptureKeepingTags(cap, tr);
                    result = result.Replace("{" + i + "}", cap);
                }
                return result;
            }

            /// <summary>
            /// 捕获段带富文本占位符时，只翻占位符之间的正文。
            ///
            /// <para><b>为什么必须做这一步。</b><see cref="TextCanonicalizer.Canonicalize"/>
            /// 把标签换成了 <see cref="TextCanonicalizer.TagPlaceholder"/>，捕获段因此长成
            /// 「占位符 + AFV8 Mobile Air Defense + 占位符」。
            /// 直接交给 <see cref="LocalizationTable.CaptureTranslator"/> 会整段 miss ——
            /// 词表里没有带占位符的键 —— 于是整句模板虽然命中，<b>被着色的那一段却原样保留英文</b>。</para>
            ///
            /// <para><b>实机症状（2026-10-30）。</b>原版战报
            /// <c>&lt;color=#F05&gt;AFV8 Mobile Air Defense&lt;/color&gt; intercepted &lt;color=#5CF&gt;AGM-48&lt;/color&gt;</c>
            /// 显示成「AFV8 Mobile Air Defense 拦截了 AGM-48」：动词是模板固定段、翻了；
            /// 两个单位名都是捕获段、没翻。带标签的整行之所以还能翻名字，是因为它走的是切片那条路
            /// （标签块分支），而模板这条路抢在切片**之前**，把整句吃掉了。</para>
            ///
            /// <para><b>占位符处理。</b>只认「前导 + 尾随」两组：颜色标签包住整段正文就是这种形状，
            /// 个数与顺序原样保留，<see cref="TextCanonicalizer.ExpandTemplate"/> 回填时才能对上原文标签。
            /// 正文**中间**还夹着占位符的（正文被标签切断）结构无法复原，原样返回 —— 宁可少翻一次，
            /// 也不要把标签顺序搞乱、把颜色糊到别的词上。</para>
            /// </summary>
            private static string TranslateCaptureKeepingTags(string cap, Func<string, string> tr)
            {
                const char Tag = TextCanonicalizer.TagPlaceholder;

                int lead = 0;
                while (lead < cap.Length && cap[lead] == Tag) lead++;
                int trail = 0;
                while (trail < cap.Length - lead && cap[cap.Length - 1 - trail] == Tag) trail++;

                string core = cap.Substring(lead, cap.Length - lead - trail);
                if (core.Length == 0) return cap;                       // 整段都是标签
                if (core.IndexOf(Tag) >= 0) return cap;                  // 正文中间还夹着标签

                string translated = tr(core);
                if (translated == core || translated.Length == 0) return cap;

                var sb = new System.Text.StringBuilder(translated.Length + lead + trail);
                sb.Append(Tag, lead);
                sb.Append(translated);
                sb.Append(Tag, trail);
                return sb.ToString();
            }

            /// <summary>{0} 是否以 be 动词收尾（被动语态的前半句）。
            /// 词表自带前导空格 —— 「factories are」的尾 4 字符就是「 are」，
            /// 前一个字符是主语的词尾字母，**不能**再要求空格。</summary>
            private static bool EndsWithBeVerb(string s)
            {
                string[] beVerbs = { " is", " are", " was", " were", " be", " been", " am",
                                     " isn't", " aren't", " wasn't", " weren't" };
                for (int i = 0; i < beVerbs.Length; i++)
                {
                    string v = beVerbs[i];
                    if (s.Length < v.Length) continue;
                    if (string.Compare(s, s.Length - v.Length, v, 0, v.Length,
                                       StringComparison.OrdinalIgnoreCase) == 0) return true;
                }
                return false;
            }

            private static bool StartsWithBy(string s)
            {
                if (s.Length < 3) return false;
                return (s[0] == 'b' || s[0] == 'B')
                    && (s[1] == 'y' || s[1] == 'Y')
                    && s[2] == ' ';
            }
        }

        private string MatchParamTemplates(string canonical)
        {
            foreach (ParamTemplate pt in _paramTemplates)
            {
                if (canonical.Length < pt.MinLength) continue;
                string expanded = pt.Match(canonical);
                if (expanded != null) return expanded;
            }
            return null;
        }

        private static bool IsAsciiDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        private static int Occurrences(string s, string marker)
        {
            int n = 0, at = 0;
            while ((at = s.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
            {
                n++;
                at += marker.Length;
            }
            return n;
        }

        /// <summary>参数化模板：固定段 + 数字位。见 <see cref="AddParamTemplate"/>。</summary>
        private sealed class ParamTemplate
        {
            /// <summary><c>{#}</c> 之间的固定段。段数 = 数字位数 + 1；首尾段可能为空串。</summary>
            internal readonly string[] Segments;
            internal readonly string Value;
            internal readonly int MinLength;

            internal ParamTemplate(string[] segments, string value, int minLength)
            {
                Segments = segments;
                Value = value;
                MinLength = minLength;
            }

            /// <summary>
            /// 整串匹配：<c>{#}</c> 至少吃一位数字（贪婪），固定段逐字符对齐，
            /// 末尾要求**整串消耗完**（防前缀撞车）。命中返回把数字回填后的译文。
            /// </summary>
            internal string Match(string canonical)
            {
                int pos = 0;
                List<string> numbers = new List<string>(Segments.Length);
                for (int s = 0; s < Segments.Length; s++)
                {
                    if (s > 0)
                    {
                        int start = pos;
                        while (pos < canonical.Length && IsAsciiDigit(canonical[pos])) pos++;
                        if (pos == start) return null;       // {#} 至少一位数字
                        numbers.Add(canonical.Substring(start, pos - start));
                    }

                    string seg = Segments[s];
                    if (seg.Length == 0) continue;
                    if (pos + seg.Length > canonical.Length
                        || string.CompareOrdinal(canonical, pos, seg, 0, seg.Length) != 0)
                    {
                        return null;
                    }
                    pos += seg.Length;
                }

                if (pos != canonical.Length) return null;    // 只匹配了前缀 ⇒ 不算

                // 回填：译文里的 {#} 按顺序替换成捕获的数字。
                string value = Value;
                if (value.IndexOf(ParamMarker, StringComparison.Ordinal) >= 0)
                {
                    var sb = new System.Text.StringBuilder(value.Length + 8);
                    int from = 0, idx = 0;
                    int at;
                    while ((at = value.IndexOf(ParamMarker, from, StringComparison.Ordinal)) >= 0)
                    {
                        sb.Append(value, from, at - from);
                        sb.Append(idx < numbers.Count ? numbers[idx] : string.Empty);
                        idx++;
                        from = at + ParamMarker.Length;
                    }
                    sb.Append(value, from, value.Length - from);
                    value = sb.ToString();
                }
                return value;
            }
        }

        /// <summary>指纹回落实际命中次数（诊断用；仅在回落时累加）。</summary>
        internal int TemplateFingerprintHits;

        /// <summary>禁用翻译时用：把已显示的中文反查回原文。</summary>
        internal bool TryReverse(string plainChinese, out string original)
        {
            return _reverse.TryGetValue(plainChinese, out original);
        }

        private static class JsonFile
        {
            /// <summary>
            /// 读取并反序列化一个 JSON 文件。文件缺失 / 内容为空 / 解析失败时返回 null（并告警），
            /// 调用方据此判定「这份数据没载入」。
            /// </summary>
            internal static T Read<T>(string path) where T : class
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    string json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json)) return null;
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(json);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Warn("词表文件读取失败 " + Path.GetFileName(path) + "：" + ex.Message);
                    return null;
                }
            }
        }
    }
}
