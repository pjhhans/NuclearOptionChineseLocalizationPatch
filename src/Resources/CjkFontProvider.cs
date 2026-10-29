using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace NuclearOptionChineseLocalizationPatch.Resources
{
    /// <summary>
    /// 中文字体：从 <c>font.ttf</c> 在运行期构造 TMP 字体资产，并登记进 TMP 的回退链。
    ///
    /// <para><b>为什么不能用 TMP 的默认字体。</b>游戏自带字体不含汉字，缺字会显示成方块。
    /// 而 TMP 的字体回退是"主字体缺哪个字就查回退链"，所以只要把中文字体加进
    /// <c>TMP_Settings.fallbackFontAssets</c>，所有 TMP 组件就都能显示中文，
    /// 不需要逐个替换字体资产。</para>
    ///
    /// <para><b>图集尺寸为什么是 2048×2048 且开多图集。</b>中文常用字数千，TMP 默认的
    /// 1024×1024 图集只能容纳约一百个 90px 字形；图集放满后 TMP 会重建并重新上传整张纹理，
    /// 表现为周期性的卡顿与掉帧。开到 2048×2048 并允许多图集后，常用字基本能全部装下。</para>
    /// </summary>
    internal static class CjkFontProvider
    {
        private const int AtlasWidth = 4096;
        private const int AtlasHeight = 4096;
        private const int SamplingPointSize = 90;
        private const int AtlasPadding = 9;

        internal static TMP_FontAsset Font { get; private set; }

        internal static void Load()
        {
            if (Font != null) return;

            string path = PluginPaths.FontFile;
            if (!System.IO.File.Exists(path))
            {
                Diagnostics.Log.Warn("找不到中文字体，界面中文可能显示为方块：" + path);
                return;
            }

            try
            {
                var unityFont = new Font(path);
                if (unityFont == null)
                {
                    Diagnostics.Log.Error("字体文件无法加载：" + path);
                    return;
                }

                Font = TMP_FontAsset.CreateFontAsset(
                    unityFont,
                    SamplingPointSize,
                    AtlasPadding,
                    GlyphRenderMode.SDFAA,
                    AtlasWidth,
                    AtlasHeight,
                    AtlasPopulationMode.Dynamic,
                    true);

                if (Font == null)
                {
                    // 不同 TMP 版本的重载签名有差异，退回最简重载。
                    Diagnostics.Log.Warn("完整重载失败，退回默认图集尺寸。");
                    Font = TMP_FontAsset.CreateFontAsset(unityFont);
                }

                if (Font == null)
                {
                    Diagnostics.Log.Error("无法从字体文件构造 TMP 字体资产。");
                    return;
                }

                Font.name = "NuclearOptionChinese_Dynamic";
                Diagnostics.Log.Info($"中文字体就绪：{unityFont.name}，图集 {AtlasWidth}x{AtlasHeight}，动态多图集。");
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error("字体加载异常：" + ex.Message);
            }
        }

        /// <summary>已挂过回退的字体资产（instanceID）。挂一次就够，别每帧重复插。</summary>
        private static readonly HashSet<int> Attached = new HashSet<int>();

        /// <summary>上次扫描到的字体资产总数。数量没变就不重扫，省掉上万对象的遍历。</summary>
        private static int _lastFontCount = -1;

        /// <summary>上次扫描的时刻（unscaledTime）。兜底扫描调用很频，扫描本身要节流。</summary>
        private static float _lastAttachScan = -100f;

        private static bool _coverageDumped;

        /// <summary>
        /// 一次性的字形覆盖诊断。
        ///
        /// <para>实机上出现过「同一段中文，一半控件显示、一半控件整块空白」：
        /// 词表与字符串都没问题，差别只可能落在字形是否真在图集里。
        /// 这里对一小撮代表性汉字做三态判定 —— √ 已在查找表、
        /// ＋ 现场动态加进了图集、× 加不进去（字体缺字或图集写入失败）——
        /// 再打印图集页数与已加载字数，用来区分「字体缺字」与「图集容量不足」。</para>
        /// </summary>
        private static void DumpCoverage()
        {
            try
            {
                const string probe = "座舱视角外部干扰弹关目标武器";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < probe.Length; i++)
                {
                    char ch = probe[i];
                    // 第一个调用只查查找表；第二个带 tryAddCharacter，能把字现场加进动态图集。
                    bool has = Font.HasCharacter(ch, false, false);
                    string mark = has ? "√" : (Font.HasCharacter(ch, false, true) ? "＋" : "×");
                    sb.Append(ch).Append(mark).Append(' ');
                }

                int pages = Font.atlasTextures == null ? 0 : Font.atlasTextures.Length;
                int chars = Font.characterLookupTable == null ? 0 : Font.characterLookupTable.Count;
                Diagnostics.Log.Info($"字形覆盖：{sb}｜图集 {pages} 页 / 已加载 {chars} 字 / {AtlasWidth}×{AtlasHeight}");
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug("字形覆盖诊断失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 把中文字体登记进 TMP 回退链。每次场景加载后都要调用一次 ——
        /// TMP_Settings 在场景切换时可能被重新加载，回退链会被重置。
        /// </summary>
        internal static void Register()
        {
            if (Font == null) return;
            try
            {
                List<TMP_FontAsset> fallbacks = TMP_Settings.fallbackFontAssets;
                if (fallbacks != null && !fallbacks.Contains(Font))
                {
                    fallbacks.Add(Font);
                    Diagnostics.Log.Debug("中文字体已加入 TMP 回退链。");
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug("登记 TMP 回退字体失败：" + ex.Message);
            }

            AttachToLoadedFonts();
        }

        /// <summary>
        /// 给**每个**已加载的字体资产挂上中文回退。
        ///
        /// <para><b>为什么只挂 TMP_Settings 不够。</b>这版 TMP 的字符查找
        /// （<c>TMP_Text.GetTextElement</c>）顺序是：主字体图集 → 主字体自己的
        /// <c>fallbackFontAssetTable</c>（递归）→ <c>TMP_Settings.fallbackFontAssets</c>。
        /// 第三方 mod 往往自己串一条私有回退链（BoscaliSummer 的
        /// <c>AvType.LinkFallbacks</c> 只串了 Cond/CondStrong→Mono→本体字体，
        /// 而它实际渲染用的 **MonoStrong 根本不在链上**），于是同一条中文串
        /// 在不同控件上时灵时不灵。挂到每个字体上是把这条路一次性堵死。</para>
        ///
        /// <para><b>代价。</b>只在字体资产数量变化时扫一次
        /// （<c>FindObjectsOfTypeAll</c>），挂表是 O(1) 的 List.Insert；
        /// 挂完把这些控件的 <c>havePropertiesChanged</c> 置脏让它们重排一次，
        /// 否则已经生成好的网格不会因为回退表变化而重建。</para>
        /// </summary>
        private static void AttachToLoadedFonts()
        {
            try
            {
                // 兜底扫描每隔一两秒就会调一次 Register；FindObjectsOfTypeAll 是 O(全部对象)，
                // 不节流的话光这一项就够把每帧预算吃掉一块。字体资产只在场景/包加载时新增，
                // 两秒扫一次完全够用。
                float now = Time.unscaledTime;
                if (now - _lastAttachScan < 2f) return;
                _lastAttachScan = now;

                TMP_FontAsset[] all = UnityEngine.Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                if (all == null || all.Length == 0) return;
                if (all.Length == _lastFontCount && Attached.Count > 0) return;
                _lastFontCount = all.Length;

                int attached = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    TMP_FontAsset fa = all[i];
                    if (fa == null || fa == Font) continue;
                    if (!Attached.Add(fa.GetInstanceID())) continue;

                    List<TMP_FontAsset> table = fa.fallbackFontAssetTable;
                    if (table == null)
                    {
                        table = new List<TMP_FontAsset>(1);
                        fa.fallbackFontAssetTable = table;
                    }
                    if (!table.Contains(Font)) table.Insert(0, Font);
                    attached++;
                }

                if (attached == 0) return;
                Diagnostics.Log.Info($"已给 {attached} 个字体资产挂上中文回退（共扫描 {all.Length} 个）。");

                if (!_coverageDumped)
                {
                    _coverageDumped = true;
                    DumpCoverage();
                }

                // 回退表变了，已生成的文本网格不会自动重建 —— 置脏让它们下一帧重排。
                TMP_Text[] texts = UnityEngine.Resources.FindObjectsOfTypeAll<TMP_Text>();
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i] != null) texts[i].havePropertiesChanged = true;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug("挂载字体回退失败：" + ex.Message);
            }
        }
    }
}
