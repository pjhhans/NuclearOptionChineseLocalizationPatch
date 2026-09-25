using System.Runtime.CompilerServices;
using HarmonyLib;

namespace NuclearOptionChineseLocalizationPatch.Diagnostics
{
    /// <summary>
    /// Harmony 存活自检用的靶子。
    ///
    /// <para>把补丁打在自己的方法上再调用它，看返回值有没有被改写。这条路完全不牵扯
    /// 游戏类型，所以结论没有歧义：<c>false</c> 表示 Harmony 在本环境根本没生效
    /// （那么所有基于补丁的功能都是死的，不必再去怀疑补丁目标）；<c>true</c> 表示
    /// Harmony 正常，问题只可能出在「目标方法不是运行时真正调用的那个」。</para>
    /// </summary>
    internal static class HarmonySelfTest
    {
        internal const int Expected = 4242;

        /// <summary>
        /// <c>NoInlining</c> 不可省：<c>Awake</c> 在打补丁<b>之前</b>就被 JIT 编译，
        /// 若本方法被内联进调用点，补丁改写的是另一个函数体，永远观察不到。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Ping() => 1;
    }

    /// <summary>把 <see cref="HarmonySelfTest.Ping"/> 的返回值改写成 <see cref="HarmonySelfTest.Expected"/>。</summary>
    [HarmonyPatch(typeof(HarmonySelfTest), nameof(HarmonySelfTest.Ping))]
    internal static class HarmonySelfTestPatch
    {
        [HarmonyPostfix]
        internal static void Postfix(ref int __result) => __result = HarmonySelfTest.Expected;
    }
}
