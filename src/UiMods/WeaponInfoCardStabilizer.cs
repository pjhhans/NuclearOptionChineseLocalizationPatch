using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearOptionChineseLocalizationPatch.UiMods
{
    /// <summary>
    /// 几何稳定化的<b>每帧入口</b>：钩 <c>AircraftSelectionMenu.Update</c>，把执行交给
    /// <see cref="WeaponInfoCardLayout"/>。
    ///
    /// <para><b>本类只含补丁方法</b>（helper 若留在 <c>[HarmonyPatch]</c> 类内，会被
    /// Harmony 分析器误判为补丁参数改写 → <c>Harmony003</c>）。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class WeaponInfoCardStabilizer
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("AircraftSelectionMenu");
            if (type == null)
                yield break;
            WeaponInfoCardPatches.ResolveFields(type);
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                if (m.Name == "Update")
                    yield return m;
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!WeaponInfoCardPatches.Enabled)
                return;

            try
            {
                WeaponInfoCardLayout.Stabilize(__instance);
            }
            catch (System.Exception ex)
            {
                Diagnostics.Log.Debug("[信息卡] stabilize 异常（忽略）: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 几何稳定执行器：每帧读上一帧布局的最终 rect，按实例「等待布局稳定 → 快照 → detach + 钉死」。
    ///
    /// <para><b>失败过的做法（别加回来）：</b>早期版本用一张全局几何表对「新建实例」盲目回放 ——
    /// 新实例首帧 rect 还是预制体默认值就被 detach + 钉死，钉在错误状态，表现为第二次打开图片与参数消失。</para>
    ///
    /// <para><b>现行策略（按实例钉死）：</b>每个新实例先完全不碰几何（保持原生布局），
    /// 直到其自身满足有效性判据（三者激活 + 图片宽≥1 + 参数/描述位于图片右侧），同帧读快照 →
    /// detach + 钉死。与既有全局几何对照，不一致则告警并自适应采用新值。
    /// 看门狗：已钉实例激活却持续退化（图片/描述宽&lt;1）超过 60 帧 → 解除钉死重记。</para>
    /// </summary>
    internal static class WeaponInfoCardLayout
    {
        /// <summary>无表格几何时的右列起点回落值（局部 px，相对行左缘）。</summary>
        private const float FallbackColC = 128f;

        private sealed class StatCell
        {
            public RectTransform Rt;
            public Vector2 Orig;     // 原生 anchoredPosition
            public int Col;          // 0=左格（参数+数值），1=右格
            public float LeftEdge;   // 原生布局下矩形左缘的局部 x
            public float RowLeft;    // 所在行左缘的局部 x
        }

        private sealed class InstState
        {
            public readonly List<StatCell> Cells = new List<StatCell>();

            /// <summary>本实例的钉死几何（键 → {x, y, w, h}，父级局部坐标）。
            /// <b>按实例隔离</b> —— 旧版全局表被多个菜单克隆互相踩踏，重钉/重算
            /// 从别实例改过的值起步累加，描述左缘棘轮式右爬（1309→1400 实证）。</summary>
            public readonly Dictionary<string, float[]> Pins = new Dictionary<string, float[]>();

            /// <summary>首次钉死时描述矩形的原生世界左缘（描述左缘绝对目标的下界基准）。</summary>
            public float NativeDescLeftWorld = float.PositiveInfinity;

            public float ParamShift; // 参数块整体平移量（世界 px，负=向左），供 FitDescription 修正
            public bool NoWeapon;    // 无武器模式：只钉描述（居中全宽）
            public float FittedRightContent = -1f; // 上次描述让位所依据的右格内容宽（变更才重算）

            // —— 描述滚动视图：钉死矩形的作用对象从 desc 移交 Viewport ——
            public RectTransform Viewport;   // 视口（钉死矩形 + RectMask2D 裁剪）
            public RectTransform Scrollbar;  // 滚动条背景（视口右缘内侧）
            public RectTransform Handle;     // 滑块
            public float ScrollOffset;       // 当前滚动像素（内容顶端偏移）
            public Transform OrigDescParent; // desc 原生父级/锚定（Reset 归还用）
            public Vector2 OrigAnchorMin, OrigAnchorMax, OrigPivot, OrigPos, OrigSize;
        }

        private static readonly HashSet<int> _pinnedInstances = new HashSet<int>();
        private static readonly Dictionary<int, InstState> _instStates = new Dictionary<int, InstState>();
        private static readonly HashSet<int> _widthDrift = new HashSet<int>(); // 宽度漂移告警（每实例一次）
        private static int _degenerateFrames;
        private static int _screenW, _screenH;

        internal static void Stabilize(object instance)
        {
            int id = RuntimeHelpers.GetHashCode(instance);

            // 分辨率变化 → 全部重来（锚定坐标是父级局部像素值）
            if (Screen.width != _screenW || Screen.height != _screenH)
            {
                if (_screenW != 0)
                    Diagnostics.Log.Info("[信息卡] 分辨率变化，解除钉死重记");
                _screenW = Screen.width;
                _screenH = Screen.height;
                ResetAll(instance);
                return;
            }

            RectTransform imageArea = RectGeom.ToRect(
                WeaponInfoCardPatches._imageAreaField?.GetValue(instance));
            RectTransform infoArea = RectGeom.ToRect(
                WeaponInfoCardPatches._infoAreaField?.GetValue(instance));
            RectTransform desc = WeaponInfoCardPatches._infoField?.GetValue(instance) is TMP_Text t
                ? t.rectTransform
                : null;

            // —— 同帧先读快照（此刻三者都还是布局引擎给出的完好几何） ——
            float[] img = RectGeom.Snapshot(imageArea);
            float[] inf = RectGeom.Snapshot(infoArea);
            float[] des = RectGeom.Snapshot(desc);
            bool imgValid = img != null && img[2] >= 1f;
            bool infValid = inf != null && imgValid && inf[0] >= img[0] + img[2] - 5f; // 参数须在图片右侧
            bool desBase = des != null && des[2] >= 1f;
            bool desValid = desBase && imgValid && des[0] >= img[0] + img[2] - 5f;

            if (!_pinnedInstances.Contains(id))
            {
                // —— 无武器分支（本体 SetActive(false) 掉图片/参数区）：
                //     描述独占卡片全宽并居中，右缘仍钳到右侧面板（长描述不再越界）——
                if (!(imgValid && infValid))
                {
                    if (!desBase)
                        return;
                    var st0 = new InstState { NoWeapon = true };
                    st0.NativeDescLeftWorld = RectGeom.LeftWorldX(desc);
                    st0.Pins["description"] = des;
                    RectGeom.DetachFromLayout(desc);
                    _pinnedInstances.Add(id);
                    _instStates[id] = st0;
                    _degenerateFrames = 0;
                    PinDescriptionCentered(st0, desc);
                    InstallDescScroll(id, desc);
                    Diagnostics.Log.Info("[信息卡·钉死] 实例 " + id + "（无武器，描述居中全宽）");
                    return;
                }

                // 新实例：布局稳定前完全不碰几何（原生布局，绝不产生「钉错」）
                if (!desValid)
                    return;

                var st = new InstState();
                st.NativeDescLeftWorld = RectGeom.LeftWorldX(desc);
                st.Pins["imageArea"] = img;
                // 参数区相对图片下移 8px（垂直居中对齐，用户裁决）
                st.Pins["infoArea"] = new[] { inf[0], inf[1] - 8f, 0f, 0f };
                st.Pins["description"] = des;

                RectGeom.DetachFromLayout(imageArea);
                RectGeom.DetachFromLayout(infoArea);
                RectGeom.DetachFromLayout(desc);

                _pinnedInstances.Add(id);
                if (_pinnedInstances.Count > 32)
                {
                    _pinnedInstances.RemoveWhere(x => x != id);
                    _instStates.Remove(id);
                    // 清理已消亡实例的表格几何（菜单 Clone 销毁后键残留）
                    var stale = new List<int>();
                    foreach (int k in WeaponInfoCardTable.Geom.Keys)
                        if (k != id && !_pinnedInstances.Contains(k))
                            stale.Add(k);
                    foreach (int k in stale)
                        WeaponInfoCardTable.Geom.Remove(k);
                }
                _instStates[id] = st;
                _degenerateFrames = 0;

                ArrangeStatCells(st, instance, id);
                AlignParamBlock(id, imageArea, infoArea);
                RectGeom.ApplyPos(infoArea, st.Pins["infoArea"]); // 立即施加平移——FitDescription 现场量取的内容右缘必须含平移
                FitDescription(id, desc);
                InstallDescScroll(id, desc);

                float[] dp = st.Pins["description"];
                Diagnostics.Log.Info(string.Format(
                    "[信息卡·钉死] 实例 {0}: image {1:F0}x{2:F0}@({3:F0},{4:F0}) info @({5:F0},{6:F0}) desc {7:F0}x{8:F0}@({9:F0},{10:F0})",
                    id, img[2], img[3], img[0], img[1], inf[0], inf[1], dp[2], dp[3], dp[0], dp[1]));
                return;
            }

            // —— 已钉实例：模式切换检测 + 每帧回放 ——
            bool nowNoWeapon = !imgValid || !infValid;
            _instStates.TryGetValue(id, out InstState cur);
            if (cur != null && cur.NoWeapon != nowNoWeapon)
            {
                ResetAll(instance); // 无武器↔有武器切换：归还布局，按新模式重新钉死
                return;
            }
            if (cur != null && cur.NoWeapon)
            {
                RectTransform pinRt0 = cur.Viewport != null ? cur.Viewport : desc;
                if (pinRt0 != null)
                {
                    RectGeom.DetachFromLayout(pinRt0);
                    RectGeom.ApplyRect(pinRt0, cur.Pins["description"]);
                    if (cur.Viewport != null)
                        UpdateDescScroll(cur, desc);
                    else
                        ForceDescWrap(desc);
                }
                return;
            }
            if (cur == null)
                return; // 已钉但状态缺失（不应发生），本帧跳过
            if (imageArea != null)
            {
                if (WeaponInfoCardPatches._weaponImageField?.GetValue(instance) is Image wimg && !wimg.preserveAspect)
                    wimg.preserveAspect = true;
                RectGeom.DetachFromLayout(imageArea);
                RectGeom.ApplyRect(imageArea, cur.Pins["imageArea"]);
            }
            if (infoArea != null)
            {
                RectGeom.DetachFromLayout(infoArea);
                RectGeom.ApplyPos(infoArea, cur.Pins["infoArea"]); // 只钳位置（0 宽是本体设计）
            }
            {
                InstState st = cur;
                ApplyCellLayout(id, st);

                // 钉死矩形的作用对象：滚动视图安装后是视口（desc 是其中的滚动内容）
                RectTransform pinRt = st.Viewport != null ? st.Viewport : desc;
                if (pinRt != null)
                {
                    RectGeom.DetachFromLayout(pinRt);
                    RectGeom.ApplyRect(pinRt, st.Pins["description"]);
                    if (st.Viewport != null)
                        UpdateDescScroll(st, desc);
                    else
                        ForceDescWrap(desc);
                }

                // 右格内容宽变化（极端数值如 15000000kg）→ 描述让位重算；正常数值不动
                if (desc != null
                    && WeaponInfoCardTable.Geom.TryGetValue(id, out float[] tgx)
                    && tgx.Length > 3 && tgx[3] != st.FittedRightContent)
                {
                    st.FittedRightContent = tgx[3];
                    st.ScrollOffset = 0f; // 换武器回到顶部
                    FitDescription(id, desc);
                }
                // 宽度漂移巡检：钉值与实际矩形不一致 = 渲染期有其它机制在改宽度（留证）
                if (pinRt != null && st.Pins.TryGetValue("description", out float[] dpw)
                    && Mathf.Abs(pinRt.rect.width - dpw[2]) > 2f && _widthDrift.Add(id))
                    Diagnostics.Log.Info(string.Format(
                        "[信息卡·表格] 描述宽度漂移：实际 {0:F0} ≠ 钉值 {1:F0}（渲染期被改写）",
                        pinRt.rect.width, dpw[2]));
            }

            // —— 看门狗：激活却持续退化（图片/描述宽<1）→ 解除钉死重记 ——
            bool degenerate =
                (imageArea != null && imageArea.gameObject.activeInHierarchy && imageArea.rect.width < 1f) ||
                (desc != null && desc.gameObject.activeInHierarchy && desc.rect.width < 1f);
            _degenerateFrames = degenerate ? _degenerateFrames + 1 : 0;
            if (_degenerateFrames > 60)
            {
                Diagnostics.Log.Info("[信息卡] 几何持续退化，解除钉死重记");
                ResetAll(instance);
            }
        }

        /// <summary>
        /// 表格化排布：六个值文本按父行分组，行内按 x 排序；记录每格的原生位置、
        /// 左缘与行左缘。右格实际位置由 <see cref="ApplyCellLayout"/> 按表格几何
        /// （行左缘 + colC）回放，与 &lt;pos&gt; 列位共同构成四列表格。
        /// 原位按实例记录，回放/重置均可逆。
        /// </summary>
        private static void ArrangeStatCells(InstState state, object instance, int id)
        {
            FieldInfo[] fields = WeaponInfoCardPatches._statFields;
            if (fields == null)
                return;

            var byParent = new Dictionary<Transform, List<RectTransform>>();
            foreach (FieldInfo f in fields)
            {
                if (!(f?.GetValue(instance) is TMP_Text tmp) || tmp.rectTransform == null || tmp.rectTransform.parent == null)
                    continue;
                Transform parent = tmp.rectTransform.parent;
                if (!byParent.TryGetValue(parent, out List<RectTransform> list))
                    byParent[parent] = list = new List<RectTransform>();
                list.Add(tmp.rectTransform);
            }

            state.Cells.Clear();
            foreach (KeyValuePair<Transform, List<RectTransform>> kv in byParent)
            {
                List<RectTransform> cells = kv.Value;
                cells.Sort((a, b) => a.localPosition.x.CompareTo(b.localPosition.x));
                float rowLeft = float.MaxValue;
                foreach (RectTransform rt in cells)
                    rowLeft = Mathf.Min(rowLeft, rt.localPosition.x - rt.pivot.x * rt.rect.width);
                for (int i = 0; i < cells.Count; i++)
                {
                    RectTransform rt = cells[i];
                    state.Cells.Add(new StatCell
                    {
                        Rt = rt,
                        Orig = rt.anchoredPosition,
                        Col = i > 0 ? 1 : 0,
                        LeftEdge = rt.localPosition.x - rt.pivot.x * rt.rect.width,
                        RowLeft = rowLeft,
                    });
                }
            }
            ApplyCellLayout(id, state);

            string geom = WeaponInfoCardTable.Geom.TryGetValue(id, out float[] tg)
                ? string.Format("posL={0:F0} posR={1:F0} colC={2:F0}", tg[0], tg[1], tg[2])
                : "无表格几何（回落）";
            Diagnostics.Log.Info("[信息卡·表格] 值单元格 " + state.Cells.Count + " 个，" + geom);
        }

        /// <summary>按表格几何回放单元格位置：右格左缘 = 行左缘 + colC，左格归原位。</summary>
        private static void ApplyCellLayout(int id, InstState st)
        {
            float colC = WeaponInfoCardTable.Geom.TryGetValue(id, out float[] tg)
                ? tg[2]
                : FallbackColC;
            foreach (StatCell c in st.Cells)
            {
                Vector2 target = c.Col == 1
                    ? c.Orig + new Vector2((c.RowLeft + colC) - c.LeftEdge, 0f)
                    : c.Orig;
                if (c.Rt.anchoredPosition != target)
                    c.Rt.anchoredPosition = target;
            }
        }

        /// <summary>参数列与图片右缘的目标间距（infoArea 局部 px）。</summary>
        private const float ParamGap = 18f;

        /// <summary>
        /// 参数块整体平移贴向图片（用户裁决「参数左边空得有点大」）：
        /// infoArea 原生 x 距图片右缘仅 ~10px，但其行/单元格内部还有居中偏移
        /// （实机实测左列标签距图片右缘 ~158px）。按「左列左缘 = 图片右缘 +
        /// <see cref="ParamGap"/>」求世界位移，加到 infoArea 钉死 x 上（所有行/格
        /// 均为 infoArea 子孙，整体平移，下一帧回放生效）。平移量记入
        /// <see cref="InstState.ParamShift"/>，供 <see cref="FitDescription"/> 修正。
        /// </summary>
        private static void AlignParamBlock(int id, RectTransform imageArea, RectTransform infoArea)
        {
            if (imageArea == null || infoArea == null
                || !_instStates.TryGetValue(id, out InstState st)
                || !st.Pins.TryGetValue("infoArea", out float[] pin))
                return;

            float minLeft = float.MaxValue;
            foreach (StatCell c in st.Cells)
                if (c.Col == 0)
                    minLeft = Mathf.Min(minLeft, RectGeom.LeftWorldX(c.Rt));
            if (minLeft == float.MaxValue)
                return;

            float imgRight = imageArea.position.x
                + (1f - imageArea.pivot.x) * imageArea.rect.width * imageArea.lossyScale.x;
            float deltaWorld = imgRight + ParamGap * infoArea.lossyScale.x - minLeft;
            deltaWorld = Mathf.Clamp(deltaWorld, -300f, 60f);
            if (Mathf.Abs(deltaWorld) < 2f)
                return;

            st.ParamShift = deltaWorld; // 记录平移量（诊断/追溯用；FitDescription 现场量取格位，不再引用）
            pin[0] += deltaWorld / infoArea.lossyScale.x;
            Diagnostics.Log.Info(string.Format("[信息卡·表格] 参数块平移 {0:F0}px 贴近图片（目标间距 {1:F0}px）",
                deltaWorld / infoArea.lossyScale.x, ParamGap));
        }

        /// <summary>
        /// 描述右缘外部上限 = min(父容器右缘 − 边距, 右侧相邻面板左缘 − 边距)。
        /// 实证：Darkener 同级<b>没有任何其它子项</b>（候选列表为空）——飞机统计面板
        /// 在更高层级，故从父级起<b>逐级向上扫最多 3 级</b>（跳过包含本段的子树）。
        /// 候选须在描述左缘右侧 120px 之外、与卡片垂直重叠、且高度 ≥ 卡片一半
        /// （排除上方的武器槽位短条），落选候选全量进日志。
        /// 注意不再包含原生右缘：宽度直接延伸到外部边界（卡片右段死空间交给描述）。
        /// </summary>
        private static float DescriptionRightLimitWorld(
            RectTransform prt, float scale, float minLeftWorld, out string boundaryLog)
        {
            float rightWorld = float.PositiveInfinity;
            boundaryLog = null;
            if (!(prt.parent is RectTransform pr))
                return rightWorld;

            Rect prW = RectGeom.WorldRect(pr);
            rightWorld = prW.xMax - 18f * scale;

            string bestName = null;
            float bestLeft = float.PositiveInfinity;
            var candidates = new List<string>();

            Transform level = pr.parent;
            for (int depth = 0; depth < 3 && level != null; depth++)
            {
                foreach (Transform sib in level)
                {
                    if (!(sib is RectTransform sr) || !sr.gameObject.activeInHierarchy)
                        continue;
                    if (sr == pr || RectGeom.IsAncestorOf(sr, pr))
                        continue; // 跳过包含武器卡段的子树
                    Rect sw = RectGeom.WorldRect(sr);
                    candidates.Add(string.Format("d{0}:{1}[{2:F0}-{3:F0},y{4:F0}-{5:F0}]",
                        depth, sr.name, sw.xMin, sw.xMax, sw.yMin, sw.yMax));
                    bool rightOf = sw.xMin > minLeftWorld + 120f * scale;
                    bool vOverlap = sw.yMin < prW.yMax - 2f && sw.yMax > prW.yMin + 2f;
                    bool tallEnough = sw.height >= prW.height * 0.5f;
                    if (rightOf && vOverlap && tallEnough && sw.xMin < bestLeft)
                    {
                        bestLeft = sw.xMin;
                        bestName = sr.name;
                    }
                }
                if (bestName != null)
                    break;
                level = level.parent;
            }
            if (bestName != null)
            {
                rightWorld = Mathf.Min(rightWorld, bestLeft - 26f * scale);
                boundaryLog = string.Format("右侧面板 {0} 左缘 {1:F0}", bestName, bestLeft);
            }
            else
            {
                boundaryLog = "无右侧面板（候选: " + string.Join(" | ", candidates) + "）";
            }
            return rightWorld;
        }

        /// <summary>
        /// 每帧巡检：描述换行若被运行时关闭（切武器 DisplayInfo 重跑的嫌疑）立即恢复。
        /// 实证：钉死时刻换行是开的（无「强制」日志），实机仍间歇溢出 → 状态事后被改。
        /// </summary>
        private static void ForceDescWrap(RectTransform desc)
        {
            if (desc != null && desc.GetComponent<TMP_Text>() is TMP_Text t && !t.enableWordWrapping)
            {
                t.enableWordWrapping = true;
                Diagnostics.Log.Info("[信息卡·表格] 描述换行被运行时关闭，已恢复（每帧巡检）");
            }
        }

        /// <summary>
        /// 安装描述滚动视图（用户需求「描述太长加个滚动条」）：
        /// 视口（RectMask2D 裁剪）接管钉死矩形，desc 移入视口作滚动内容
        /// （水平撑满、顶端对齐），右缘内侧挂 4~6px 滚动条。描述矩形高度不再
        /// 随文本扩张 —— 上下溢出从结构上消除。滚轮滚动不依赖 EventSystem
        /// （Update 里直接读指针位置，规避射线/输入系统差异）。
        /// </summary>
        private static void InstallDescScroll(int id, RectTransform desc)
        {
            if (desc == null || !_instStates.TryGetValue(id, out InstState st) || st.Viewport != null)
                return;
            if (!(desc.parent is RectTransform parentRT))
                return;

            // 记录原生布局（Reset 归还用）
            st.OrigDescParent = desc.parent;
            st.OrigAnchorMin = desc.anchorMin;
            st.OrigAnchorMax = desc.anchorMax;
            st.OrigPivot = desc.pivot;
            st.OrigPos = desc.anchoredPosition;
            st.OrigSize = desc.sizeDelta;

            int layer = desc.gameObject.layer;

            // 视口：复制 desc 当前锚定方案（随后每帧按钉死值回放）
            var vpGo = new GameObject("DescViewport", typeof(RectTransform), typeof(RectMask2D));
            vpGo.layer = layer;
            RectTransform vp = vpGo.GetComponent<RectTransform>();
            vp.SetParent(parentRT, false);
            vp.anchorMin = desc.anchorMin;
            vp.anchorMax = desc.anchorMax;
            vp.pivot = desc.pivot;
            vp.anchoredPosition = desc.anchoredPosition;
            vp.sizeDelta = desc.sizeDelta;
            RectGeom.DetachFromLayout(vp);
            RectGeom.ApplyRect(vp, st.Pins["description"]); // 立即对齐钉死矩形，防首帧闪烁

            // 滚动条：视口右缘内侧（背景 + 滑块，手动驱动）
            var barGo = new GameObject("DescScrollbar", typeof(RectTransform), typeof(Image));
            barGo.layer = layer;
            RectTransform bar = barGo.GetComponent<RectTransform>();
            bar.SetParent(vp, false);
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(1f, 0.5f);
            bar.anchoredPosition = new Vector2(-2f, 0f);
            bar.sizeDelta = new Vector2(6f, -8f);
            Image barImg = bar.GetComponent<Image>();
            barImg.color = new Color(0f, 0f, 0f, 0.35f);
            barImg.raycastTarget = false;

            var hGo = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            hGo.layer = layer;
            RectTransform handle = hGo.GetComponent<RectTransform>();
            handle.SetParent(bar, false);
            handle.anchorMin = new Vector2(0f, 1f);
            handle.anchorMax = new Vector2(1f, 1f);
            handle.pivot = new Vector2(0.5f, 1f);
            handle.anchoredPosition = Vector2.zero;
            handle.sizeDelta = new Vector2(0f, 40f);
            Image hImg = handle.GetComponent<Image>();
            hImg.color = new Color(0.62f, 0.78f, 0.62f, 0.9f);
            hImg.raycastTarget = false;

            // desc 移入视口：水平撑满（右缘内缩给滚动条让位）、顶端对齐滚动
            desc.SetParent(vp, false);
            desc.anchorMin = new Vector2(0f, 1f);
            desc.anchorMax = new Vector2(1f, 1f);
            desc.pivot = new Vector2(0.5f, 1f);
            desc.anchoredPosition = Vector2.zero;
            desc.sizeDelta = new Vector2(-20f, vp.rect.height);
            if (desc.GetComponent<TMP_Text>() is TMP_Text dt)
                dt.alignment = TextAlignmentOptions.Top; // 顶端对齐（水平保持居中观感）

            st.Viewport = vp;
            st.Scrollbar = bar;
            st.Handle = handle;
            st.ScrollOffset = 0f;
            Diagnostics.Log.Info("[信息卡·表格] 描述滚动视图已安装（滚轮 + 滚动条）");
        }

        /// <summary>每帧滚动更新：内容高度 = max(视口高, 渲染文本高)，滚轮驱动偏移，滑块随动。</summary>
        private static void UpdateDescScroll(InstState st, RectTransform desc)
        {
            if (st?.Viewport == null || desc == null)
                return;
            float vpH = st.Viewport.rect.height;
            if (vpH < 10f)
                return;

            float textH = 0f;
            if (desc.GetComponent<TMP_Text>() is TMP_Text t)
                textH = t.textBounds.size.y; // 渲染文本真实高度（中文）；preferred 走英文原文会低估
            if (textH <= 0f)
                return; // 尚未首渲染

            float contentH = Mathf.Max(vpH, textH + 8f);
            Vector2 sd = desc.sizeDelta;
            if (Mathf.Abs(sd.y - contentH) > 1f)
                desc.sizeDelta = new Vector2(sd.x, contentH);

            float maxScroll = Mathf.Max(0f, contentH - vpH);
            if (maxScroll > 0f)
            {
                // 指针在视口内时按滚轮符号走 45px/档（轮询实现见 WidgetWheel，
                // 与下拉列表的滚轮共用同一套降级逻辑）
                int wheel = WidgetWheel.Poll(st.Viewport);
                if (wheel > 0)
                    st.ScrollOffset -= 45f;
                else if (wheel < 0)
                    st.ScrollOffset += 45f;
            }
            st.ScrollOffset = Mathf.Clamp(st.ScrollOffset, 0f, maxScroll);

            Vector2 ap = desc.anchoredPosition;
            if (Mathf.Abs(ap.y - st.ScrollOffset) > 0.25f)
                desc.anchoredPosition = new Vector2(ap.x, st.ScrollOffset);

            // 滑块随动（内容装得下时隐藏滚动条）
            if (st.Scrollbar == null || st.Handle == null)
                return;
            bool show = maxScroll > 0f;
            if (st.Scrollbar.gameObject.activeSelf != show)
                st.Scrollbar.gameObject.SetActive(show);
            if (!show)
                return;
            float barH = st.Scrollbar.rect.height;
            float handleH = Mathf.Max(24f, barH * Mathf.Clamp01(vpH / contentH));
            st.Handle.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, handleH);
            float travel = Mathf.Max(1f, barH - handleH);
            Vector2 hp = st.Handle.anchoredPosition;
            Vector2 ht = new Vector2(hp.x, -(st.ScrollOffset / maxScroll) * travel);
            if ((hp - ht).sqrMagnitude > 0.25f)
                st.Handle.anchoredPosition = ht;
        }

        /// <summary>拆除滚动视图：desc 归还原生父级与锚定，销毁视口/滚动条（Reset 用）。</summary>
        private static void TearDownScroll(InstState st, RectTransform desc)
        {
            if (st == null)
                return;
            if (st.Viewport == null)
            {
                st.Viewport = null; st.Scrollbar = null; st.Handle = null;
                return;
            }
            if (desc != null)
            {
                desc.SetParent(st.OrigDescParent, false);
                desc.anchorMin = st.OrigAnchorMin;
                desc.anchorMax = st.OrigAnchorMax;
                desc.pivot = st.OrigPivot;
                desc.anchoredPosition = st.OrigPos;
                desc.sizeDelta = st.OrigSize;
            }
            UnityEngine.Object.Destroy(st.Viewport.gameObject); // 滚动条是视口子级，一并销毁
            st.Viewport = null;
            st.Scrollbar = null;
            st.Handle = null;
        }

        /// <summary>
        /// 描述区适配：① 强制自动换行（描述若 Overflow/禁换行，长行会在矩形右缘
        /// 被裁字）；② 左缘 = <b>绝对目标</b>「参数内容右缘 + <see cref="DescGap"/>」
        /// （不随历史累积 —— 旧版 max(上次值, …) 在多实例共享全局 Pins 时棘轮式右爬，
        /// 1309→1400 实测）；③ 右缘钳到 <see cref="DescriptionRightLimitWorld"/>。世界坐标运算。
        /// </summary>
        private static void FitDescription(int id, RectTransform desc)
        {
            if (desc == null
                || !_instStates.TryGetValue(id, out InstState st)
                || !st.Pins.TryGetValue("description", out float[] dpin))
                return;

            // 滚动视图安装后，钉死矩形的作用对象是视口（desc 只是其中的滚动内容）
            RectTransform prt = st.Viewport != null ? st.Viewport : desc;
            float scale = prt.lossyScale.x;

            // ① 禁自身 ContentSizeFitter —— 实测证据：钉死位置生效（文字起点对）
            //    但钉死宽度失效（文本折行点在 ~2 倍钉宽处）：布局引擎在渲染期按文本
            //    把矩形重新撑大，我们的宽度每帧被覆盖。fitter 不禁，钉宽全是空话。
            // ② 强制自动换行 —— 长行必须在矩形右缘折行，否则溢出被裁。
            ContentSizeFitter fitter = desc.GetComponent<ContentSizeFitter>();
            if (fitter != null && (fitter.horizontalFit != ContentSizeFitter.FitMode.Unconstrained
                || fitter.verticalFit != ContentSizeFitter.FitMode.Unconstrained))
            {
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
                Diagnostics.Log.Info("[信息卡·表格] 描述自身 ContentSizeFitter 已禁用（矩形曾被文本反向撑大，钉宽失效）");
            }

            TMP_Text dtmp = desc.GetComponent<TMP_Text>();
            if (dtmp != null && (!dtmp.enableWordWrapping || dtmp.overflowMode != TextOverflowModes.Overflow))
            {
                dtmp.enableWordWrapping = true;
                dtmp.overflowMode = TextOverflowModes.Overflow;
                Diagnostics.Log.Info("[信息卡·表格] 描述强制自动换行（修右缘溢出/裁字）");
            }

            // 参数内容右缘（世界）＝ 最右右格左缘 + 右格内容宽 × <b>格子自身缩放</b>。
            // 旧版把格子局部量乘 desc 缩放（1.48）再加固定余量 —— 凭空多出 ~100px
            // 且随武器宽窄浮动（「中间空隙有时多有时少」的主体）。
            float contentRight = float.MinValue;
            if (WeaponInfoCardTable.Geom.TryGetValue(id, out float[] tg) && tg.Length > 3)
            {
                float maxLeft = float.MinValue;
                float cellScale = scale;
                foreach (StatCell c in st.Cells)
                {
                    if (c.Col != 1)
                        continue;
                    float l = RectGeom.LeftWorldX(c.Rt);
                    if (l > maxLeft)
                    {
                        maxLeft = l;
                        cellScale = c.Rt.lossyScale.x;
                    }
                }
                if (maxLeft > float.MinValue)
                    contentRight = maxLeft + tg[3] * cellScale;
            }

            // 左缘 = 内容右缘 + 固定间距（常量，武器间不浮动）+ 右移偏移；
            // 无几何时保持现状。上限 = 原生左缘 + 300：极端数值（1500000kg）让位但防吃满描述区。
            float nativeLeftWorld = RectGeom.LeftWorldX(prt);
            float targetLeft = contentRight > float.MinValue
                ? Mathf.Min(contentRight + DescGap, st.NativeDescLeftWorld + 300f)
                : nativeLeftWorld;
            // 描述整体右移（用户裁决「向右偏 2~4%」，取屏幕宽 3%）：
            // 只加在左缘目标上，右缘仍钳外部边界 —— 描述只收窄不右越，
            // 内容右缘与滚动条的 10px 内缩关系不变，文本永不进入滚动条底下。
            float shiftWorld = 0f;
            if (contentRight > float.MinValue)
            {
                shiftWorld = Screen.width * DescShiftRatio / RectGeom.CanvasScaleOf(prt);
                targetLeft += shiftWorld;
            }
            float deltaLocal = (targetLeft - nativeLeftWorld) / scale;

            // 右缘直接采纳外部边界（父容器/右侧面板），不再保留原生右缘 ——
            // 把卡片右段死空间交给描述（日志实证：原生右缘 1824 vs 面板左缘 2154）。
            // 描述矩形更宽 → 行数更少；残余超高由滚动视图消化。
            float rightLimit = DescriptionRightLimitWorld(prt, scale, targetLeft, out string boundaryLog);
            float newWidth = Mathf.Max(160f, (rightLimit - targetLeft) / scale);
            st.Pins["description"] = new[] { dpin[0] + deltaLocal, dpin[1], newWidth, dpin[3] };

            Diagnostics.Log.Info(string.Format(
                "[信息卡·表格] 描述适配：{0}；内容右缘 {1:F0}（tg3 {2:F0}×cellScale {3:F2}），左缘 {4:F0}→{5:F0}（间距 {6:F0}，右移 {7:F0}），宽 {8:F0}→{9:F0}",
                boundaryLog ?? "无右侧面板", contentRight,
                tg != null && tg.Length > 3 ? tg[3] : -1f,
                st.Cells.Count > 0 ? st.Cells[st.Cells.Count - 1].Rt.lossyScale.x : scale,
                nativeLeftWorld, targetLeft, targetLeft - shiftWorld - contentRight, shiftWorld,
                dpin[2], newWidth));
        }

        /// <summary>参数内容右缘与描述左缘的固定间距（世界 px，不随武器/缩放浮动）。</summary>
        private const float DescGap = 26f;

        /// <summary>描述右移比例：屏幕宽 × 此比例（用户裁决 2~4%，取中 3%）。</summary>
        private const float DescShiftRatio = 0.03f;

        /// <summary>
        /// 无武器分支：描述独占卡片全宽（左缘 = 卡片左缘 + 边距），右缘仍钳到
        /// 右侧相邻面板；描述文本本身居中对齐 ⇒ 视觉居中，长描述有最大宽度可用。
        /// </summary>
        private static void PinDescriptionCentered(InstState st, RectTransform desc)
        {
            if (desc == null
                || !st.Pins.TryGetValue("description", out float[] dpin))
                return;

            float scale = desc.lossyScale.x;
            ContentSizeFitter fitter0 = desc.GetComponent<ContentSizeFitter>();
            if (fitter0 != null && fitter0.horizontalFit != ContentSizeFitter.FitMode.Unconstrained)
            {
                fitter0.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                Diagnostics.Log.Info("[信息卡·表格] 无武器：描述 ContentSizeFitter 已禁用");
            }
            float nativeLeftWorld = RectGeom.LeftWorldX(desc);
            float leftWorld = desc.parent is RectTransform pr
                ? RectGeom.WorldRect(pr).xMin + 12f * scale
                : nativeLeftWorld;
            float rightLimit = DescriptionRightLimitWorld(desc, scale, leftWorld, out string boundaryLog);
            float newWidth = Mathf.Max(160f, (rightLimit - leftWorld) / scale);
            float deltaLocal = (leftWorld - nativeLeftWorld) / scale;
            st.Pins["description"] = new[] { dpin[0] + deltaLocal, dpin[1], newWidth, dpin[3] };

            Diagnostics.Log.Info(string.Format(
                "[信息卡·表格] 无武器：描述居中全宽，左缘 {0:F0}→{1:F0}，宽 {2:F0}→{3:F0} {4}",
                nativeLeftWorld, leftWorld, dpin[2], newWidth, boundaryLog ?? ""));
        }

        private static void ResetAll(object instance)
        {
            int id = RuntimeHelpers.GetHashCode(instance);
            RectTransform descRt = WeaponInfoCardPatches._infoField?.GetValue(instance) is TMP_Text tt
                ? tt.rectTransform
                : null;
            if (_instStates.TryGetValue(id, out InstState st))
            {
                foreach (StatCell c in st.Cells)
                    c.Rt.anchoredPosition = c.Orig; // 归还原位
                TearDownScroll(st, descRt); // 描述归还原生父级/锚定，销毁视口与滚动条
                _instStates.Remove(id);
            }
            // 表格几何保留（按实例冻结，跨 无武器↔有武器 模式抖动重钉不丢；实例销毁由
            // 钉死路径的 stale 清理回收）—— 清掉会让右列起点随武器重算而抖动。
            // 钉死几何（Pins）随 InstState 一并移除 —— 按实例隔离，无全局状态。
            _pinnedInstances.Clear();
            _degenerateFrames = 0;
            // 本实例控件归还布局引擎（其它实例随销毁消亡）
            RectGeom.UndetachFromLayout(RectGeom.ToRect(
                WeaponInfoCardPatches._imageAreaField?.GetValue(instance)));
            RectGeom.UndetachFromLayout(RectGeom.ToRect(
                WeaponInfoCardPatches._infoAreaField?.GetValue(instance)));
            if (descRt != null)
                RectGeom.UndetachFromLayout(descRt);
        }
    }
}
