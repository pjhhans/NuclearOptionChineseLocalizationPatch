using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;

namespace NuclearOptionChineseLocalizationPatch.UiMods
{
    /// <summary>
    /// 挂架武器下拉框（<c>WeaponSelector</c>）的 UI 修整。
    ///
    /// <para><b>背景（IL 实证，见 tools/_il_ws*_20260927.py）：</b>
    /// <c>WeaponSelector.dropdown</c> 是标准的 <see cref="TMP_Dropdown"/>，
    /// <c>PopulateOptions</c> 只负责 <c>options.Add(new OptionData(名))</c>。
    /// 中文武器名（如「AGM-84H 斯拉姆-ER」）在弹出列表里被 TMP 溢出裁剪截断。</para>
    ///
    /// <para><b>关键实测（诊断日志）：</b>模板根 296px / 模板标签 262px，但弹出列表实际
    /// 只有 137px、条目标签实际 103px —— <b>模板几何并不传导到弹出列表</b>（游戏预制体的
    /// Viewport/Content 固定宽，且 Show 只改 sizeDelta 的 y 分量）。所以<strong>在模板上加宽
    /// 是无效的</strong>，必须像 <see cref="PylonDropdownPopupPatches"/> 那样在 Show 之后对
    /// 实际弹出的列表动手。本类只负责：登记实例、修模板滚动条方向（会被克隆进弹出列表）、
    /// 输出量测侧诊断。</para>
    ///
    /// <para><b>滚动条反向（游戏预制体 bug）：</b>这一版 TMP 的 Show 完全不触碰滚动条，
    /// 条目由 Show 统一按「0 号在最上」重排，所以正确的滚动条方向恒为
    /// <see cref="Scrollbar.Direction.BottomToTop"/>（1 = 在顶端 = 看着列表顶）。
    /// 游戏模板若是 TopToBottom，拖动与位置就整组反向。这里直接把模板上的竖向滚动条
    /// 规范化为 BottomToTop —— 本来就是的话零影响。实机已验证有效。</para>
    ///
    /// <para><b>本类只含补丁方法</b>；度量与几何 helper 见 <see cref="DropdownGeom"/>。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class PylonDropdownPatches
    {
        /// <summary>配置开关（启动时由 ModSettings 接线）。</summary>
        internal static bool Enabled = true;

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
                if (dd == null || dd.template == null || dd.itemText == null) return;

                DropdownGeom.FixScrollbarDirection(dd.template);

                _tracked.Add(dd);
                Diagnostics.Log.Debug(string.Format(
                    "[挂架下拉·量测] 模板 {0:F0}px，标签 {1:F0}px，最长译文 {2:F0}px（按自适应上限字号），层级 {3}",
                    dd.template.rect.width, dd.itemText.rectTransform.rect.width,
                    DropdownGeom.MeasureMaxOptionWidth(dd),
                    DropdownGeom.PathOf(dd.itemText.transform, dd.template)));
            }
            catch (System.Exception ex)
            {
                // UI 修整失败不该连累翻译本身；Warn 默认可见，便于实机定位。
                Diagnostics.Log.Warn("挂架下拉框修整失败：" + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
