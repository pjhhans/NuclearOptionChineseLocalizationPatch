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
        internal int FragmentCount =>
            _prefixFragments.Count + _suffixFragments.Count + _infixFragments.Count;
        internal int ScopeCount => _scoped.Count;
        internal IReadOnlyList<Fragment> PrefixFragments => _prefixFragments;
        internal IReadOnlyList<Fragment> SuffixFragments => _suffixFragments;
        internal IReadOnlyList<Fragment> InfixFragments => _infixFragments;

        // ------------------------------------------------------------------ 载入

        /// <summary>从数据目录载入全部词表资源。单个文件损坏不影响其余部分。</summary>
        internal void Load(string dataDir)
        {
            _global.Clear(); _templates.Clear(); _reverse.Clear();
            _prefixFragments.Clear(); _suffixFragments.Clear(); _infixFragments.Clear();
            _scoped.Clear(); _forceScoped.Clear();
            _minTemplateKeyLength = int.MaxValue;

            LoadMainTable(Path.Combine(dataDir, "translation.json"));
            LoadScopeFiles(Path.Combine(dataDir, "scopes"));
            LoadForceScopes(Path.Combine(dataDir, "force_scopes.json"));

            _prefixFragments.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            _suffixFragments.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            _infixFragments.Sort((a, b) => b.Key.Length - a.Key.Length);
        }

        private void LoadMainTable(string path)
        {
            Dictionary<string, string> raw = JsonFile.ReadObject(path);
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
        /// </summary>
        internal bool TryGetTemplate(string text, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(text)) return false;
            if (_templates.Count == 0 || text.Length < _minTemplateKeyLength) return false;

            string canonical = TextCanonicalizer.Canonicalize(text);
            if (canonical.Length < _minTemplateKeyLength) return false;
            return _templates.TryGetValue(canonical, out value) && value != null;
        }

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
