using HarmonyLib;
using UnityEngine.UI;

namespace NuclearOptionChineseLocalizationPatch.Patching
{
    /// <summary>
    /// 旧版 UI.Text 补丁集。游戏主要的文本组件是 TextMeshPro，但少量遗留界面
    /// （部分老式 UI 与第三方组件）仍使用 <see cref="Text"/>，需要单独覆盖。
    ///
    /// <para><b>这里刻意不打 <c>text</c> getter。</b>旧版 UI.Text 的文本网格是直接读
    /// <c>text</c> 生成的，若把读回值还原成英文，屏上显示也会跟着变回英文 ——
    /// 与 TMP 不同，TMP 的渲染走 <c>m_text</c> 字段，不经过 getter。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class LegacyUiPatches
    {
        [HarmonyPatch(typeof(Text), "text", MethodType.Setter)]
        [HarmonyPrefix]
        internal static void TextSetterPrefix(Text __instance, ref string value)
        {
            string translated;
            if (PatchHelpers.TryLocalize(value, __instance, out translated)) value = translated;
        }

        [HarmonyPatch(typeof(Text), "OnEnable")]
        [HarmonyPostfix]
        internal static void OnEnablePostfix(Text __instance)
        {
            PatchHelpers.LocalizeInPlace(__instance);
        }
    }
}
