using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearOptionChineseLocalizationPatch.Patching
{
    /// <summary>
    /// 挂架武器下拉框（<c>WeaponSelector</c>）的 UI 修整。
    ///
    /// <para><b>背景（IL 实证，见 tools/_il_ws*_20260927.py）：</b>
    /// <c>WeaponSelector.dropdown</c> 是标准的 <see cref="TMP_Dropdown"/>，
    /// <c>PopulateOptions</c> 只负责 <c>options.Add(new OptionData(名))</c>，
    /// 弹出列表的宽度完全由预制体里的模板写死，代码层从不调整。
    /// 中文武器名（如「1200kg 副油箱」「AGM-84H 斯拉姆-ER」）比英文原串占宽更省
    /// 却仍超模板宽时，TMP 溢出裁剪直接把字切掉。</para>
    ///
    /// <para><b>修法 = 改模板几何，不动 TMP 内部：</b>本机 Unity.TextMeshPro.dll 的
    /// <c>TMP_Dropdown.Show()</c> 逐指令确认了两件事：
    /// <list type="number">
    /// <item>弹出列表是模板的克隆，列表宽度 = 模板宽度；内容高度由 Show 自己算，
    ///       宽度从不被改写。所以只要把模板加宽，每次展开都是宽的。</item>
    /// <item>Show 里有 <c>GetWorldCorners</c> + 画布越界检测 +
    ///       <c>FlipLayoutOnAxis</c>，列表超出屏幕右缘时 TMP 会自己翻转展开方向，
    ///       <b>不需要</b>我们再做屏幕钳制。</item>
    /// </list></para>
    ///
    /// <para><b>滚动条反向（游戏预制体 bug）：</b>这一版 TMP 的 Show 完全不触碰滚动条
    /// （没有旧版的 m_ListScrollbar 逻辑），条目由 Show 统一按「0 号在最上」重排，
    /// 所以正确的滚动条方向恒为 <see cref="Scrollbar.Direction.BottomToTop"/>
    /// （1 = 在顶端 = 看着列表顶）。游戏模板若是 TopToBottom，拖动与位置就整组反向。
    /// 这里直接把模板上的竖向滚动条规范化为 BottomToTop——本来就是的话零影响。</para>
    ///
    /// <para><b>幂等：</b>加宽量 = 当前标签宽与实测需求宽之差，模板只会被加宽到刚好够；
    /// 重复 PopulateOptions 时第二次差值 ≈ 0，不会滚雪球。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class PylonDropdownPatches
    {
        /// <summary>配置开关（启动时由 ModSettings 接线）。</summary>
        internal static bool Enabled = true;

        private const float MinDelta = 1f;    // 差值小于 1px 不动，避免浮点噪声反复加宽
        private const float MaxTemplateWidth = 800f; // 保险上限，防病态长串把列表撑满全屏
        private const float ExtraPadding = 8f;       // 文字与边缘之间的呼吸空间

        private static FieldInfo _dropdownField;
        internal static readonly HashSet<TMP_Dropdown> _tracked = new HashSet<TMP_Dropdown>();

        /// <summary>
        /// 目标 = WeaponSelector.PopulateOptions 的全部重载。
        /// 用 TargetMethods 而不是按名打点，因为有两个重载且未来可能增减；
        /// 找不到类型/方法时返回空集，补丁退化为无操作（与逐类安装的容错哲学一致）。
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("WeaponSelector");
            if (type == null) yield break;

            if (_dropdownField == null)
                _dropdownField = AccessTools.Field(type, "dropdown");

            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                if (m.Name == "PopulateOptions") yield return m;
        }

        [HarmonyPostfix]
        internal static void Postfix(object __instance)
        {
            if (!Enabled || _dropdownField == null) return;

            try
            {
                var dd = _dropdownField.GetValue(__instance) as TMP_Dropdown;
                if (dd == null) return;
                RectTransform template = dd.template;
                if (template == null) return;
                TMP_Text itemText = dd.itemText;
                if (itemText == null) return;

                FixScrollbarDirection(template);

                float templateW = template.rect.width;
                float labelW = itemText.rectTransform.rect.width;
                float maxPref = MeasureMaxOptionWidth(itemText, dd.options);

                _tracked.Add(dd);
                Diagnostics.Log.Info(string.Format(
                    "[挂架下拉·量测] 模板 {0:F0}px，标签 {1:F0}px，最长译文 {2:F0}px；标签字号 {3:F1}，自适应 {4}（{5:F0}~{6:F0}），字体 {7}，溢出 {8}，层级 {9}",
                    templateW, labelW, maxPref,
                    itemText.fontSize, itemText.enableAutoSizing, itemText.fontSizeMin, itemText.fontSizeMax,
                    itemText.font != null ? itemText.font.name : "null",
                    itemText.overflowMode, PathOf(itemText.transform, template)));

                float delta = maxPref + ExtraPadding - labelW;
                if (delta >= MinDelta && templateW + delta <= MaxTemplateWidth)
                {
                    ApplyWidth(template, itemText, delta);
                    Diagnostics.Log.Info(string.Format(
                        "[挂架下拉] 已加宽 +{0:F0}px：模板 {1:F0}->{2:F0}px，标签 {3:F0}px，最长译文 {4:F0}px",
                        delta, templateW, templateW + delta, labelW, maxPref));
                }
                else
                {
                    Diagnostics.Log.Info(string.Format(
                        "[挂架下拉] 未加宽：模板 {0:F0}px，标签 {1:F0}px，最长译文 {2:F0}px，需差 {3:F0}px（负=装得下）",
                        templateW, labelW, maxPref, maxPref + ExtraPadding - labelW));
                }
            }
            catch (System.Exception ex)
            {
                // UI 修整失败不该连累翻译本身；Warn 默认可见，便于实机定位。
                Diagnostics.Log.Warn("挂架下拉框修整失败：" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>量出所有选项译文里最宽的那条首选宽（用模板 item 标签的字体设置）。</summary>
        private static float MeasureMaxOptionWidth(TMP_Text itemText, IReadOnlyList<TMP_Dropdown.OptionData> options)
        {
            if (options == null || options.Count == 0) return 0f;

            // 这一版 TMP 里 GetPreferredWidth 两个重载都是 protected，公共入口是
            // GetPreferredValues(string)（返回宽高向量，纯布局演算，不写 m_text）。
            // 选项原始串是英文；实际显示的是译文，量之前先过一遍自家翻译管线。
            float maxPref = 0f;
            for (int i = 0; i < options.Count; i++)
            {
                string raw = options[i] != null ? options[i].text : null;
                if (string.IsNullOrEmpty(raw)) continue;

                string shown = PatchHelpers.TryLocalize(raw, itemText, out string translated)
                    ? translated : raw;

                float w = itemText.GetPreferredValues(shown).x;
                if (w > maxPref) maxPref = w;
            }
            return maxPref;
        }

        /// <summary>模板加宽 delta，并沿 itemText→模板的祖先链补宽所有非拉伸锚节点。</summary>
        private static void ApplyWidth(RectTransform template, TMP_Text itemText, float delta)
        {
            template.sizeDelta += new Vector2(delta, 0f);

            // 祖先链兜底：不假设模板子节点叫什么名字（游戏预制体未必是 TMP 标准层级）。
            // 拉伸锚的节点随父变宽，天然跳过；固定宽的中间层（视口/内容/行）逐个补上增量。
            Transform t = itemText.transform;
            while (t != null && !(t is RectTransform rt && rt == template))
            {
                StretchChild(t as RectTransform, delta);
                t = t.parent;
            }

            // 选中高亮背景是 item 标签的兄弟节点，不在祖先链上，按名字补一遍。
            StretchChild(FindByPath(template, "Viewport/Content/Item/Item Background"), delta);
        }

        /// <summary>模板上的竖向滚动条规范化为 BottomToTop（见类注释第 3 点）。</summary>
        private static void FixScrollbarDirection(RectTransform template)
        {
            Scrollbar bar = template.GetComponentInChildren<Scrollbar>(true);
            if (bar == null) return;
            if (bar.direction == Scrollbar.Direction.BottomToTop) return;
            if (bar.direction != Scrollbar.Direction.TopToBottom) return; // 只修竖向的反向
            bar.direction = Scrollbar.Direction.BottomToTop; // 属性 setter 会重映射当前取值
        }

        /// <summary>横向拉伸锚的节点随父变宽，跳过；固定宽的节点补上增量。</summary>
        private static void StretchChild(RectTransform rt, float delta)
        {
            if (rt == null) return;
            if (rt.anchorMax.x - rt.anchorMin.x > 0.5f) return; // 拉伸锚，继承父宽
            rt.sizeDelta += new Vector2(delta, 0f);
        }

        private static RectTransform FindByPath(RectTransform root, string path)
        {
            Transform t = root.Find(path);
            return t as RectTransform;
        }

        /// <summary>诊断用：从 root 到 node 的层级路径。</summary>
        private static string PathOf(Transform startNode, Transform root)
        {
            var sb = new System.Text.StringBuilder();
            Transform cur = startNode;
            while (cur != null && cur != root)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, cur.name);
                cur = cur.parent;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 诊断用（独立补丁类，v4）：挂架下拉 <c>TMP_Dropdown.Show</c> 展开后取渲染真相。
    ///
    /// <para><b>为什么必须是独立的类：</b>Harmony 对含 <c>TargetMethods</c> 的补丁类，
    /// 会把类内<b>所有</b>补丁方法套到每个目标上。v3 曾把声明 <c>TMP_Dropdown __instance</c>
    /// 参数的 Show postfix 和 <c>WeaponSelector.PopulateOptions</c> 的 TargetMethods
    /// 放在同一类里 —— __instance 类型对不上，安装时直接抛 Patching exception，
    /// <b>整个类（含滚动条修复与加宽）都没装上</b>，实机表现为「滚动条又反了 + 不加宽」。
    /// 参数类型兼容的补丁方法才允许与 TargetMethods 同类。</para>
    /// </summary>
    [HarmonyPatch(typeof(TMP_Dropdown), "Show")]
    internal static class PylonDropdownShowDiagnostics
    {
        private static FieldInfo _popupField;

        [HarmonyPostfix]
        internal static void Postfix(TMP_Dropdown __instance)
        {
            if (!PylonDropdownPatches.Enabled || !PylonDropdownPatches._tracked.Contains(__instance)) return;

            try
            {
                if (_popupField == null)
                    _popupField = AccessTools.Field(typeof(TMP_Dropdown), "m_Dropdown");
                object popupObj = _popupField?.GetValue(__instance);
                GameObject popup = popupObj as GameObject ?? (popupObj as Component)?.gameObject;
                if (popup == null) return;

                RectTransform rt = popup.transform as RectTransform;
                float popupW = rt != null ? rt.rect.width : -1f;

                TMP_Text[] texts = popup.GetComponentsInChildren<TMP_Text>(false);
                var sb = new System.Text.StringBuilder();
                sb.AppendFormat("[挂架下拉·Show] 列表宽 {0:F0}px，条目文本组件 {1} 个", popupW, texts.Length);
                int shown = 0;
                for (int i = 0; i < texts.Length && shown < 3; i++)
                {
                    TMP_Text t = texts[i];
                    string s = t.text;
                    if (string.IsNullOrEmpty(s)) continue;
                    float pref = t.GetPreferredValues(s).x;
                    sb.AppendFormat(" | #{0} \"{1}\" 字号{2:F1} 自适应{3} 宽{4:F0} 量测{5:F0} 字体{6}",
                        shown, s, t.fontSize, t.enableAutoSizing, t.rectTransform.rect.width, pref,
                        t.font != null ? t.font.name : "null");
                    shown++;
                }
                Diagnostics.Log.Info(sb.ToString());
            }
            catch (System.Exception ex)
            {
                Diagnostics.Log.Warn("挂架下拉 Show 诊断失败：" + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
