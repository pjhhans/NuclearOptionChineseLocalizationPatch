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
    ///
    /// <para><b>本类只含补丁方法。</b>所有公共逻辑走 <see cref="PatchHelpers"/>，不要在本类内新增
    /// helper —— Harmony 分析器会把补丁类中的<b>所有</b>方法当候选补丁检查，helper 里对自身参数的
    /// 赋值会误报 <c>Harmony003</c>。</para>
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

        /// <summary>
        /// <c>SetCharArray(char[], int, int)</c> —— mod 自绘 HUD 走的唯一通路。
        ///
        /// <para><b>为什么必须单独打点。</b>Boscali Summer 的 <c>Hud</c> 模块不用
        /// TMP 组件的 <c>text</c>：它自己维护一个标签池（<c>ScopeText</c>），每个词
        /// 走 <c>TextMeshProUGUI.SetCharArray(chars, 0, len)</c> 画上去。
        /// 这条路径既不经过 <c>text</c> setter，也不经过任何 <c>SetText</c> 重载，
        /// 于是整套 HUD（STORES MGMT / PWR BUS / EXPENDABLES / GEAR / SYSTEMS /
        /// THREAT / 告警条 …）在词表里明明有译文却**一个都不出中文**，
        /// 而且既不进命中、也不进漏译清单（日志干干净净）—— 2026-10-29 实机截图就是这样。</para>
        ///
        /// <para><b>只作用于 mod HUD。</b><c>SetCharArray</c> 是 TMP 的通用底层接口，
        /// 卡组、输入框、第三方 mod 都可能用；这里用 <see cref="PatchHelpers.IsHudWord"/>
        /// （节点名 <c>Word</c> 且在 <c>Boscali*</c> 子树内）把范围钉死，
        /// 其余调用一律原样放过 —— 与「只有显式登记的键才改变行为」的既有原则一致。</para>
        ///
        /// <para><b>宽度守卫。</b>HUD 的列宽是按等宽拉丁字面算死的
        /// （<c>ScopeText.DesignAdvance = 0.65 em</c>），外层还套了 <c>RectMask2D</c>：
        /// 中文一字 1 em，放不下就会被裁掉半个字，比不翻更难看。所以超出格子就
        /// **保持英文**，并把这条记进日志（去重、限次），由数据侧改成更短的译名。</para>
        /// </summary>
        [HarmonyPatch(typeof(TMP_Text), "SetCharArray", new[] { typeof(char[]), typeof(int), typeof(int) })]
        [HarmonyPrefix]
        internal static void SetCharArrayPrefix(TMP_Text __instance, ref char[] sourceText, ref int start, ref int length)
        {
            if (sourceText == null || length <= 0 || length > MaxHudWord) return;
            if (start < 0 || start + length > sourceText.Length) return;
            if (!PatchHelpers.IsHudWord(__instance)) return;

            string original = new string(sourceText, start, length);

            // ★ 被 FitText 截断过的串要先还原再查表。
            //   `ScopeCanvas.FitText` 在宽度不够时是**先截断原文、再加省略号**，然后把
            //   `"DUSTBOWL HIGHWAY…"` 整条交给 Text/Draw —— 词表里当然没有这种带省略号的键，
            //   于是长名字（基地、机场、单位）永远保持英文。剥掉省略号再查，译文一般更短，
            //   正好放回原来那个格子。
            //   宽度仍按**带省略号的原文长度**算（那才是 Draw 给的框），否则会误判为放得下。
            string lookup = original;
            if (lookup.Length > 1 && lookup[lookup.Length - 1] == '\u2026')
            {
                lookup = lookup.Substring(0, lookup.Length - 1).TrimEnd();
                if (lookup.Length == 0) return;
            }

            string translated;
            if (!PatchHelpers.TryLocalize(lookup, __instance, out translated)) return;

            if (!FitsHudGrid(original, translated))
            {
                PatchHelpers.ReportHudTooWide(lookup, translated);
                return;
            }

            char[] buffer = translated.ToCharArray();
            sourceText = buffer;
            start = 0;
            length = buffer.Length;
        }

        /// <summary>HUD 的一个词最长也就几十个字符（整句不会走这条），超出的直接放过。</summary>
        private const int MaxHudWord = 96;

        /// <summary>
        /// 译文放得进 <c>ScopeText.Draw</c> 算出来的那个字格子吗。
        ///
        /// <para>格子宽（像素）= <c>原文长度 × 0.65 × size × 1.1 + 6</c>，
        /// 换算成 em 就是 <c>原文长度 × 0.715 + 6/size</c>。这里按 size = 12 取
        /// （HUD 词的字号绝大多数在这一档），常数项折成 0.5 em。
        /// 译文的视觉宽度按「汉字 1 em、其余 0.65 em」估 —— 这就是实机上会被裁的量。</para>
        /// </summary>
        private static bool FitsHudGrid(string original, string translated)
        {
            float grid = original.Length * 0.715f + 0.5f;
            float need = 0f;
            for (int i = 0; i < translated.Length; i++)
            {
                char c = translated[i];
                need += IsWideChar(c) ? 1f : 0.65f;
            }
            return need <= grid;
        }

        /// <summary>全角字符（汉字、全角标点、假名）。判据与 <see cref="TextLocalizer.HasChinese"/> 同源。</summary>
        private static bool IsWideChar(char c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF)     // CJK 部首 / 假名 / 统一汉字
                || (c >= 0xF900 && c <= 0xFAFF)     // 兼容汉字
                || (c >= 0xFF00 && c <= 0xFFEF);    // 全角形式
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
