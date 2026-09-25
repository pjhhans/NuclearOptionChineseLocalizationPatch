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
        private const int AtlasWidth = 2048;
        private const int AtlasHeight = 2048;
        private const int SamplingPointSize = 90;
        private const int AtlasPadding = 9;

        internal static TMP_FontAsset Font { get; private set; }

        internal static bool IsLoaded => Font != null;

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
                if (fallbacks == null) return;
                if (!fallbacks.Contains(Font))
                {
                    fallbacks.Add(Font);
                    Diagnostics.Log.Debug("中文字体已加入 TMP 回退链。");
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug("登记 TMP 回退字体失败：" + ex.Message);
            }
        }
    }
}
