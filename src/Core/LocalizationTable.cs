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
        /// 模板键的**去标签指纹**索引，用于「标签个数与词表键不一致」时的回落匹配。
        ///
        /// <para>值为 <c>null</c> 表示该指纹有**两个以上**模板键共用 ⇒ 无法判定是哪一个，
        /// 整组作废（宁可漏翻也不翻错）。</para>
        /// </summary>
        private readonly Dictionary<string, string> _templateFingerprints =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private int _fingerprintCollisions;

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
        internal int TemplateCount => _templates.Count;
        /// <summary>作用域分表里的词条总数（不含分表个数，个数见 <see cref="ScopeCount"/>）。</summary>
        internal int ScopedEntryCount => _scopedEntries;
        /// <summary>全表词条总数：通用 + 模板 + 片段 + 作用域词条。用于给用户看的「词表 N 条」。</summary>
        internal int TotalEntryCount => _global.Count + _templates.Count + FragmentCount + _scopedEntries;
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
            Dictionary<string, string> main = JsonFile.ReadObject(mainPath);
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
            _prefixFragments.Clear(); _suffixFragments.Clear(); _infixFragments.Clear();
            _scoped.Clear(); _scopedEntries = 0; _forceScoped.Clear();
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
            Dictionary<string, string> dict = JsonFile.ReadObject(path);
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
                Dictionary<string, string> raw = JsonFile.ReadObject(file);
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
            List<string> list = JsonFile.ReadStringArray(path);
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
        internal string LookupScoped(string scope, string text)
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

        /// <summary>该作用域是否被声明为强制作用域（只走本作用域词条 + 全局精确，不做模糊匹配）。</summary>
        internal bool IsForceScoped(string scope)
        {
            return !string.IsNullOrEmpty(scope) && _forceScoped.Contains(scope);
        }

        /// <summary>
        /// 整段模板查询。<paramref name="text"/> 会先归一化；
        /// 短于最短模板键长的文本直接判否（门禁）。
        ///
        /// <para><b>两步</b>：先逐字符精确匹配；失配则退到<b>去标签指纹</b>匹配 ——
        /// 教程弹窗的 <c>&lt;bind=X&gt;</c> 会被游戏在写入控件前解析成字形 / 文本 / 空，
        /// 标签个数因此与词表键不一致。没有这一步的话，那类卡片会整行停留在英文，
        /// 而且日志干净、只留一条漏译记录（查不出来源）。</para>
        /// </summary>
        internal bool TryGetTemplate(string text, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(text)) return false;
            if (_templates.Count == 0 || text.Length < _minTemplateKeyLength) return false;

            string canonical = TextCanonicalizer.Canonicalize(text);
            if (canonical.Length < _minTemplateKeyLength) return false;
            if (_templates.TryGetValue(canonical, out value) && value != null) return true;

            // ---- 回落：去标签指纹
            if (_templateFingerprints.Count == 0) return false;
            string fingerprint = TextCanonicalizer.Fingerprint(canonical);
            if (fingerprint.Length >= TextCanonicalizer.MinFingerprintLength
                && _templateFingerprints.TryGetValue(fingerprint, out value)
                && value != null)
            {
                TemplateFingerprintHits++;
                return true;
            }

            value = null;
            return false;
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
            internal static Dictionary<string, string> ReadObject(string path)
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    string json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json)) return null;
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Warn("词表文件读取失败 " + Path.GetFileName(path) + "：" + ex.Message);
                    return null;
                }
            }

            internal static List<string> ReadStringArray(string path)
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    string json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json)) return null;
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(json);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Warn("读取失败 " + Path.GetFileName(path) + "：" + ex.Message);
                    return null;
                }
            }
        }
    }
}
