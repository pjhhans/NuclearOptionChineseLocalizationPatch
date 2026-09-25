using HarmonyLib;
using TMPro;

namespace NuclearOptionChineseLocalizationPatch.Patching
{
    /// <summary>
    /// TMP 补丁集。游戏里绝大多数界面文本都经由 TextMeshPro，这里是主战场。
    ///
    /// <para>打点选择的依据是"这个游戏用哪种方式写文本"：
    /// <list type="bullet">
    /// <item><c>text</c> setter —— 最常见的赋值方式，主入口。</item>
    /// <item><c>SetText(string, bool)</c> —— 部分代码绕过属性直接调方法（它内部直接写字段）。</item>
    /// <item><c>ParseInputText</c> —— TMP 解析文本的必经之处，兜住前两者都没覆盖的路径。</item>
    /// <item><c>OnEnable</c> —— 预制体自带的英文默认值不会经过任何 setter，只能在这里补一刀。</item>
    /// <item><c>text</c> getter —— 读回还原，见 <see cref="PatchHelpers.RestoreOriginalOnRead"/>。</item>
    /// </list></para>
    ///
    /// <para>TMP_Text 本身没有声明 <c>OnEnable</c>，需要分别在 TextMeshProUGUI 与 TextMeshPro
    /// 两个子类上补。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class TmpPatches
    {
        [HarmonyPatch(typeof(TMP_Text), "text", MethodType.Getter)]
        [HarmonyPostfix]
        internal static void TextGetterPostfix(ref string __result)
        {
            PatchHelpers.RestoreOriginalOnRead(ref __result);
        }

        [HarmonyPatch(typeof(TMP_Text), "text", MethodType.Setter)]
        [HarmonyPrefix]
        internal static void TextSetterPrefix(TMP_Text __instance, ref string value)
        {
            string translated;
            if (PatchHelpers.TryLocalize(value, __instance, out translated)) value = translated;
        }

        [HarmonyPatch(typeof(TMP_Text), "SetText", typeof(string), typeof(bool))]
        [HarmonyPrefix]
        internal static void SetTextPrefix(TMP_Text __instance, ref string sourceText)
        {
            string translated;
            if (PatchHelpers.TryLocalize(sourceText, __instance, out translated)) sourceText = translated;
        }

        [HarmonyPatch(typeof(TMP_Text), "ParseInputText")]
        [HarmonyPrefix]
        internal static void ParseInputTextPrefix(TMP_Text __instance)
        {
            PatchHelpers.LocalizeInPlace(__instance);
        }

        [HarmonyPatch(typeof(TextMeshProUGUI), "OnEnable")]
        [HarmonyPostfix]
        internal static void UguiOnEnablePostfix(TextMeshProUGUI __instance)
        {
            PatchHelpers.LocalizeInPlace(__instance);
        }

        [HarmonyPatch(typeof(TextMeshPro), "OnEnable")]
        [HarmonyPostfix]
        internal static void MeshOnEnablePostfix(TextMeshPro __instance)
        {
            PatchHelpers.LocalizeInPlace(__instance);
        }
    }
}
