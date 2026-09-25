using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearOptionChineseLocalizationPatch.Patching
{
    /// <summary>
    /// 补丁层的公共入口。所有钩子都经过这里，保证「翻译—写回」的行为完全一致。
    ///
    /// <para><b>只写回含中文的结果</b>是收敛性的关键。若把一种英文写成另一种英文，
    /// 就会形成 A→B→A 的来回改写，文本每帧重建，表现为抖动。</para>
    /// </summary>
    internal static class PatchHelpers
    {
        /// <summary>
        /// 直接读写组件的底层文本字段，绕开 <c>text</c> 属性。
        ///
        /// <para>用 Harmony 的 FieldRef 委托而不是反射 FieldInfo：这些钩子挂在每帧都会执行的
        /// 路径上，HUD 上几十个文本组件意味着每秒上万次访问，反射调用会成为可观开销。</para>
        /// </summary>
        private static AccessTools.FieldRef<TMP_Text, string> _tmpField;
        private static AccessTools.FieldRef<Text, string> _legacyField;

        /// <summary>
        /// 「我们写下去的译文 → 它对应的原文」。
        ///
        /// <para>读回还原靠它，靠词表的反向索引不够 —— 模板与片段拼接的结果
        /// 是运行期拼出来的，词表里并没有"译文→原文"这一项。</para>
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> WrittenOriginal =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);

        private const int WrittenOriginalLimit = 8192;

        private static void Remember(string translation, string original)
        {
            if (WrittenOriginal.Count >= WrittenOriginalLimit) WrittenOriginal.Clear();
            WrittenOriginal[translation] = original;
        }

        static PatchHelpers()
        {
            try
            {
                _tmpField = AccessTools.FieldRefAccess<TMP_Text, string>("m_text");
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn("无法访问 TMP_Text.m_text，原地翻译与防回写将不可用：" + ex.Message);
            }
            try
            {
                _legacyField = AccessTools.FieldRefAccess<Text, string>("m_Text");
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn("无法访问 UI.Text.m_Text：" + ex.Message);
            }
        }

        /// <summary>
        /// 从组件推断作用域。用 GameObject 名是有意的：游戏给 HUD 上每个文本对象都起了
        /// 有语义的名字（<c>countermeasureName</c> / <c>weaponName</c> / <c>InfoText</c> …），
        /// 而词表正是按这些名字分组登记的。
        /// </summary>
        internal static string ScopeOf(Component comp)
        {
            return comp == null ? "Unknown" : comp.gameObject.name;
        }

        /// <summary>
        /// 尝试翻译。返回 true 表示应当写回（结果与原文不同且确实含中文）。
        /// </summary>
        internal static bool TryLocalize(string original, Component comp, out string translated)
        {
            translated = original;
            if (string.IsNullOrEmpty(original)) return false;

            var localizer = LocalizationPlugin.Instance?.Localizer;
            if (localizer == null) return false;

            string scope = ScopeOf(comp);
            string result = localizer.Localize(original, scope);
            if (result == original || !Core.TextLocalizer.HasChinese(result)) return false;

            translated = result;
            Remember(result, original);
            RewriteGuard.Track(comp, original, result);
            return true;
        }

        /// <summary>
        /// 原地翻译组件当前持有的文本。用于 <c>OnEnable</c> 这类"预制体自带英文默认值、
        /// 不经过任何 setter"的场景。
        /// </summary>
        internal static void LocalizeInPlace(TMP_Text text)
        {
            if (text == null || _tmpField == null) return;
            try
            {
                string current = _tmpField(text);
                if (string.IsNullOrEmpty(current)) return;

                string translated;
                if (!TryLocalize(current, text, out translated)) return;

                // 直接写字段，避开 setter —— 否则会触发我们自己的 set_text 钩子，
                // 虽然幂等短路能兜住，但白白多走一遍完整流水线。
                _tmpField(text) = translated;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug("原地翻译失败：" + ex.Message);
            }
        }

        internal static void LocalizeInPlace(Text text)
        {
            if (text == null || _legacyField == null) return;
            try
            {
                string current = _legacyField(text);
                if (string.IsNullOrEmpty(current)) return;
                string translated;
                if (!TryLocalize(current, text, out translated)) return;
                _legacyField(text) = translated;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug("原地翻译失败：" + ex.Message);
            }
        }

        /// <summary>读取组件当前文本（优先走底层字段）。供防回写使用。</summary>
        internal static string Read(Component comp)
        {
            var tmp = comp as TMP_Text;
            if (tmp != null) return _tmpField == null ? null : _tmpField(tmp);

            var legacy = comp as Text;
            if (legacy != null) return _legacyField == null ? null : _legacyField(legacy);

            return null;
        }

        /// <summary>写回组件文本（优先走底层字段）。供防回写使用。</summary>
        internal static void Write(Component comp, string value)
        {
            var tmp = comp as TMP_Text;
            if (tmp != null) { if (_tmpField != null) _tmpField(tmp) = value; return; }

            var legacy = comp as Text;
            if (legacy != null) { if (_legacyField != null) _legacyField(legacy) = value; }
        }

        /// <summary>某个组件的文本能否被我们读写（字段反射成功且类型受支持）。</summary>
        internal static bool IsSupported(Component comp)
        {
            if (comp is TMP_Text) return _tmpField != null;
            if (comp is Text) return _legacyField != null;
            return false;
        }

        /// <summary>
        /// 读回还原：把中文译文反查回英文原文。
        ///
        /// <para><b>为什么必须做。</b>游戏里大量「读回比较后再写回」的用法，例如
        /// <c>if (weaponName.text != info.shortName) weaponName.text = info.shortName;</c>。
        /// 只要读出来是中文，比较就恒为真，游戏每帧把英文重写一遍 ——
        /// 这就是右上角文本「一闪中文又变回英文」的根源。还原成原文后比较重新成立，回写自然停止。</para>
        ///
        /// <para><b>为什么只对 TMP 做、不动旧版 UI.Text。</b>TMP 的渲染走 <c>m_text</c> 字段，
        /// 不经过 <c>get_text</c>，所以改读回值不影响屏上显示；而旧版 UI.Text 的文本网格
        /// 就是直接读 <c>text</c> 生成的，改读回值会把显示也变成英文。</para>
        /// </summary>
        internal static void RestoreOriginalOnRead(ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (!Core.TextLocalizer.HasChinese(value)) return;

            string original;
            if (WrittenOriginal.TryGetValue(value, out original))
            {
                value = original;
                return;
            }

            // 退一步：用词表的反向索引（覆盖普通词条）。
            var localizer = LocalizationPlugin.Instance?.Localizer;
            if (localizer != null && localizer.TryReverseLookup(value, out original))
            {
                value = original;
            }
        }
    }
}
