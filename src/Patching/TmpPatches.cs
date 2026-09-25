using System.Text;
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

        /// <summary>
        /// <c>SetText(StringBuilder)</c> —— <b>一条完全独立的通路，必须单独打点。</b>
        ///
        /// <para>它既不经过 <c>text</c> setter，也不经过 <c>SetText(string, bool)</c>：
        /// 实现只是 <c>PopulateTextBackingArray(sourceText, 0, sourceText.Length)</c>
        /// 然后 <c>m_inputSource = TextInputSources.SetText</c>，<b>全程不写 <c>m_text</c></b>。
        /// 所以 <see cref="ParseInputTextPrefix"/> 那个兜底也接不住它（它只处理 <c>m_text</c>）——
        /// 文本会彻底绕开整条流水线：既不命中、也不进漏译清单，日志干干净净。</para>
        ///
        /// <para><b>左上角战报（击杀信息）正是走这条。</b>
        /// <c>MessageManager.UserCode_RpcKillMessage</c> 把
        /// <c>&lt;color=#…&gt;杀伤者&lt;/color&gt; shot down &lt;color=#…&gt;受害者&lt;/color&gt;</c>
        /// 交给 <c>GameplayUI.KillFeed</c> → <c>MessageUI.KillFeed</c>（按 <c>\n</c> 拆行后
        /// <c>MessageFeed.Enqueue</c>）→ <c>MessageFeed.RefreshUI</c> 把队列拼进一个 StringBuilder
        /// 再 <c>_display.SetText(_sb)</c>。</para>
        ///
        /// <para>实测边界：整个 Assembly-CSharp 只引用了 <b>两个</b> <c>TMP_Text.SetText</c> 重载
        /// （<c>(string, bool)</c> 与 <c>(StringBuilder)</c>），所以补上本方法即已覆盖游戏的全部
        /// <c>SetText</c> 用法，不是打地鼠。</para>
        ///
        /// <para><b>用换引用而不是就地改写。</b>这个 builder 属于调用方（<c>MessageFeed._sb</c> 会被复用），
        /// 就地 <c>Clear/Append</c> 会把中文留在调用方的对象里。换成新实例后调用方自己的 builder 不受影响，
        /// 而 TMP 拿到的是译文。1 参重载是「先取 <c>sourceText.Length</c>、再委托给 3 参重载」，
        /// 换引用后长度自然按译文算，不会截断。</para>
        /// </summary>
        [HarmonyPatch(typeof(TMP_Text), "SetText", new[] { typeof(StringBuilder) })]
        [HarmonyPrefix]
        internal static void SetTextStringBuilderPrefix(TMP_Text __instance, ref StringBuilder sourceText)
        {
            // 1 参重载自己会算 length 并转调 3 参重载，所以这里只需要把 builder 换成译文版。
            if (sourceText == null || sourceText.Length == 0) return;

            string translated;
            if (!PatchHelpers.TryLocalize(sourceText.ToString(), __instance, out translated)) return;
            sourceText = new StringBuilder(translated);
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
