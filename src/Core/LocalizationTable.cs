using System;
using System.Collections.Generic;
using System.IO;

namespace NuclearOptionChineseLocalizationPatch.Core
{
    /// <summary>
    /// 词表容器。负责把四类键（普通 / <c>~</c> 模板 / <c>&gt;&gt;</c> <c>&lt;&lt;</c> <c>==</c> 片段）
    /// 分开索引，并回答"这条原文有没有译文"。
    ///
    /// <para>索引结构的选择理由：</para>
    /// <list type="bullet">
    /// <item>普通键用 <see cref="StringComparer.OrdinalIgnoreCase"/> 字典 —— 游戏原文大小写不稳定
    ///       （<c>Faction funds</c> / <c>Faction Funds</c> 是同一个键），大小写敏感会漏一半。</item>
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

        private readonly HashSet<string> _forceScoped =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private int _minTemplateKeyLength = int.MaxValue;

        internal int GlobalCount => _global.Count;
        internal int TemplateCount => _templates.Count;
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

            _global.Clear(); _templates.Clear(); _reverse.Clear();
            _templateFingerprints.Clear(); _fingerprintCollisions = 0;
            _prefixFragments.Clear(); _suffixFragments.Clear(); _infixFragments.Clear();
            _scoped.Clear(); _forceScoped.Clear();
            _minTemplateKeyLength = int.MaxValue;

            IngestMain(main);
            LoadScopeFiles(Path.Combine(dataDir, "scopes"));
            LoadForceScopes(Path.Combine(dataDir, "force_scopes.json"));

            _prefixFragments.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            _suffixFragments.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            _infixFragments.Sort((a, b) => b.Key.Length - a.Key.Length);
            return true;
        }

        private void IngestMain(Dictionary<string, string> raw)
        {
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
                    string cleaned = KeyScrubber.Scrub(key).Trim();
                    if (cleaned.Length == 0) continue;
                    _global[cleaned] = value;

                    string plain = ExclusionRules.StripScopePrefix(cleaned);
                    string visible = TokenPatterns.Tag.Replace(value, string.Empty).Trim();
                    if (plain.Length > 0 && visible.Length > 0 && !_reverse.ContainsKey(visible))
                    {
                        _reverse[visible] = plain;
                    }
                }
            }
        }

        private void LoadScopeFiles(string scopesDir)
        {
            if (!Directory.Exists(scopesDir)) return;
            foreach (string file in Directory.GetFiles(scopesDir, "*.json"))
            {
                Dictionary<string, string> raw = JsonFile.ReadObject(file);
                if (raw == null) continue;

                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> kv in raw)
                {
                    string cleaned = KeyScrubber.Scrub(kv.Key).Trim();
                    if (cleaned.Length > 0) dict[cleaned] = kv.Value;
                }
                string scopeName = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrEmpty(scopeName)) _scoped[scopeName] = dict;
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
