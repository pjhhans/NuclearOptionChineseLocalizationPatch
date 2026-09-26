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

                WidenToFit(template, itemText, dd.options);
            }
            catch (System.Exception ex)
            {
                // UI 修整失败不该连累翻译本身，吞掉但留一句日志。
                Diagnostics.Log.Debug("挂架下拉框修整失败：" + ex.Message);
            }
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

        /// <summary>按选项译文的最长首选宽把模板加宽到刚好够。</summary>
        private static void WidenToFit(RectTransform template, TMP_Text itemText, IReadOnlyList<TMP_Dropdown.OptionData> options)
        {
            if (options == null || options.Count == 0) return;

            // 用模板自带的 item 标签测量：字体、字号、字距、边距全部与真实渲染一致。
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
            if (maxPref <= 0f) return;
            maxPref += ExtraPadding;

            float labelW = itemText.rectTransform.rect.width;
            float delta = maxPref - labelW;
            if (delta < MinDelta) return; // 装得下，不动

            float newWidth = template.rect.width + delta;
            if (newWidth > MaxTemplateWidth)
                delta -= newWidth - MaxTemplateWidth; // 钳到上限
            if (delta < MinDelta) return;

            // 模板自身必然加宽；子节点若不是横向拉伸锚（不随父变宽）就一并加。
            // 层级按 TMP 标准模板路径找：Template/Viewport/Content/Item/{Item Background, Item Label}。
            // 找不到的（层级被改过 / 本来就是拉伸锚）跳过即可，拉伸锚会随父变宽。
            template.sizeDelta += new Vector2(delta, 0f);

            StretchChild(itemText.rectTransform, delta);
            StretchChild(FindByPath(template, "Viewport"), delta);
            StretchChild(FindByPath(template, "Viewport/Content"), delta);
            StretchChild(FindByPath(template, "Viewport/Content/Item"), delta);
            StretchChild(FindByPath(template, "Viewport/Content/Item/Item Background"), delta);
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
    }
}
