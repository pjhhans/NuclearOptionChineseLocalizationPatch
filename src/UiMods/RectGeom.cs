using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearOptionChineseLocalizationPatch.UiMods
{
    /// <summary>
    /// 界面修整共用的矩形几何工具：世界坐标换算、矩形快照/回放、脱离布局引擎、文本宽度下界。
    ///
    /// <para><b>为什么要独立成类（而不是留在补丁类里）。</b>Harmony 的 Roslyn 分析器
    /// 会把 <c>[HarmonyPatch]</c> 类中的<b>所有方法</b>当作候选补丁方法做检查，于是
    /// <see cref="IsAncestorOf"/> 里 <c>cursor = cursor.parent</c> 这种对自身参数的赋值
    /// 被误报成 <c>Harmony003</c>（"patch parameter modified, no effect"）。
    /// helper 与 <c>[HarmonyPatch]</c> 类分家后，这类误报从根上消失 —— 这也是本类存在的理由，
    /// 别再把几何 helper 挪回补丁类。</para>
    ///
    /// <para>全部为无状态纯函数，不持有任何实例状态，可被任意层调用。</para>
    /// </summary>
    internal static class RectGeom
    {
        /// <summary>把反射取到的字段值（RectTransform / GameObject / Component）统一取成 RectTransform。</summary>
        internal static RectTransform ToRect(object value)
        {
            if (value is RectTransform rt)
                return rt;
            if (value is GameObject go)
                return go.GetComponent<RectTransform>();
            if (value is Component comp)
                return comp.GetComponent<RectTransform>();
            return null;
        }

        /// <summary>让控件脱离父级布局引擎（父级 LayoutGroup 从此跳过它）。</summary>
        internal static void DetachFromLayout(RectTransform rt)
        {
            LayoutElement le = rt.GetComponent<LayoutElement>();
            if (le == null)
                le = rt.gameObject.AddComponent<LayoutElement>();
            if (!le.ignoreLayout)
                le.ignoreLayout = true;
        }

        /// <summary>把控件归还父级布局引擎（看门狗/重置用）。</summary>
        internal static void UndetachFromLayout(RectTransform rt)
        {
            if (rt != null && rt.GetComponent<LayoutElement>() is LayoutElement le)
                le.ignoreLayout = false;
        }

        /// <summary>「标签:」里冒号的下标（半角或全角），无则 -1。</summary>
        internal static int IndexOfColon(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] == ':' || s[i] == '：')
                    return i;
            return -1;
        }

        /// <summary>
        /// 单元格文本宽度下界：<c>GetPreferredValues</c> 实测与逐字符估计取较大者。
        ///
        /// <para>实测依赖当前字体度量 —— 新克隆的菜单首刷时 CJK 可能仍走回退字体
        /// （每字 ≈0.5em），此时估出的列位会把中文标签压住。汉字/全角字符在真实字体里
        /// 恰为 1em，故以 fontSize 逐字符估计兜底。</para>
        /// </summary>
        internal static float CellWidth(TMP_Text tmp, string s, float fontSize)
        {
            float est = 0f;
            for (int i = 0; i < s.Length; i++)
                est += s[i] >= 0x2E80 ? fontSize * 1.1f : fontSize * 0.62f; // CJK 按 1.1em 估（部分字体 advance > 1em）
            float measured = 0f;
            try { measured = tmp.GetPreferredValues(s).x; } catch { /* 字体未就绪时用估计值 */ }
            return Mathf.Max(measured, est);
        }

        /// <summary>矩形左缘的世界 x（<c>position</c> 是 pivot 的世界坐标，需回退 pivot 占比）。</summary>
        internal static float LeftWorldX(RectTransform rt)
            => rt.position.x - rt.pivot.x * rt.rect.width * rt.lossyScale.x;

        /// <summary>矩形的世界空间包围盒（轴对齐，含缩放）。</summary>
        internal static Rect WorldRect(RectTransform rt)
        {
            Vector2 size = new Vector2(rt.rect.width * rt.lossyScale.x, rt.rect.height * rt.lossyScale.y);
            return new Rect((Vector2)rt.position - rt.pivot * size, size);
        }

        /// <summary><paramref name="maybeAncestor"/> 是否为 <paramref name="cursor"/> 的祖先。</summary>
        internal static bool IsAncestorOf(Transform maybeAncestor, Transform cursor)
        {
            while (cursor != null)
            {
                if (cursor.parent == maybeAncestor)
                    return true;
                cursor = cursor.parent;
            }
            return false;
        }

        /// <summary>矩形当前几何快照（父级局部像素）；未激活返回 null。</summary>
        internal static RectPin? Snapshot(RectTransform rt)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy)
                return null;
            return new RectPin(rt.anchoredPosition.x, rt.anchoredPosition.y, rt.rect.width, rt.rect.height);
        }

        /// <summary>按钉死几何回放矩形（位置 + 尺寸；尺寸差 &gt;0.5px 才写）。</summary>
        internal static void ApplyRect(RectTransform rt, RectPin pin, bool setHeight = true)
        {
            const float eps = 0.5f;
            if (Mathf.Abs(rt.rect.width - pin.W) > eps)
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, pin.W);
            if (setHeight && Mathf.Abs(rt.rect.height - pin.H) > eps)
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, pin.H);
            ApplyPos(rt, pin);
        }

        /// <summary>按钉死几何回放位置（位移 &gt;0.5px 才写，避免每帧无谓写入触发重建）。</summary>
        internal static void ApplyPos(RectTransform rt, RectPin pin)
        {
            const float eps = 0.5f;
            float dx = rt.anchoredPosition.x - pin.X;
            float dy = rt.anchoredPosition.y - pin.Y;
            if (dx * dx + dy * dy > eps * eps)
                rt.anchoredPosition = new Vector2(pin.X, pin.Y);
        }

        /// <summary>屏幕 px → 世界 px 的换算系数（Overlay 画布 scaleFactor 即每单位像素数）。</summary>
        internal static float CanvasScaleOf(RectTransform rt)
        {
            Canvas cv = rt != null ? rt.GetComponentInParent<Canvas>() : null;
            return cv != null && cv.scaleFactor > 0f ? cv.scaleFactor : 1f;
        }
    }

    /// <summary>
    /// 矩形的钉死快照（父级局部坐标 + 尺寸）。等价于原先的 <c>float[] { x, y, w, h }</c>，
    /// 但字段有名字、不再依赖 <c>[0]…[3]</c> 的魔法下标；又因为是值类型，顺带免掉了逐帧的小数组分配。
    /// </summary>
    internal readonly struct RectPin
    {
        internal readonly float X;
        internal readonly float Y;
        internal readonly float W;
        internal readonly float H;

        internal RectPin(float x, float y, float w, float h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        /// <summary>只改 X 的副本（参数块整体平移用）。</summary>
        internal RectPin WithX(float x) => new RectPin(x, Y, W, H);
    }
}
