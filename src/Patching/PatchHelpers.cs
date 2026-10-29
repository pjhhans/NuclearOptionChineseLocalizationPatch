using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Host = NuclearOptionChineseLocalizationPatch.Hosting.PluginHost;

namespace NuclearOptionChineseLocalizationPatch.Patching
{
    /// <summary>
    /// 补丁层的公共入口。所有钩子都经过这里，保证「翻译—写回」的行为完全一致。
    ///
    /// <para><b>只写回含中文的结果</b>是收敛性的关键。若把一种英文写成另一种英文，
    /// 就会形成 A→B→A 的来回改写，文本每帧重建，表现为抖动。</para>
    /// </summary>
    internal static class PatchHelpers
    {
        /// <summary>
        /// 直接读写组件的底层文本字段，绕开 <c>text</c> 属性。
        ///
        /// <para>用 Harmony 的 FieldRef 委托而不是反射 FieldInfo：这些钩子挂在每帧都会执行的
        /// 路径上，HUD 上几十个文本组件意味着每秒上万次访问，反射调用会成为可观开销。</para>
        /// </summary>
        private static AccessTools.FieldRef<TMP_Text, string> _tmpField;
        private static AccessTools.FieldRef<Text, string> _legacyField;

        /// <summary>
        /// 「我们写下去的译文 → 它对应的原文」。
        ///
        /// <para>读回还原靠它，靠词表的反向索引不够 —— 模板与片段拼接的结果
        /// 是运行期拼出来的，词表里并没有"译文→原文"这一项。</para>
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> WrittenOriginal =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);

        private const int WrittenOriginalLimit = 8192;

        private static void Remember(string translation, string original)
        {
            if (WrittenOriginal.Count >= WrittenOriginalLimit) WrittenOriginal.Clear();
            WrittenOriginal[translation] = original;
        }

        static PatchHelpers()
        {
            try
            {
                _tmpField = AccessTools.FieldRefAccess<TMP_Text, string>("m_text");
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn("无法访问 TMP_Text.m_text，原地翻译与防回写将不可用：" + ex.Message);
            }
            try
            {
                _legacyField = AccessTools.FieldRefAccess<Text, string>("m_Text");
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn("无法访问 UI.Text.m_Text：" + ex.Message);
            }
        }

        /// <summary>
        /// 从组件推断作用域。用 GameObject 名是有意的：游戏给 HUD 上每个文本对象都起了
        /// 有语义的名字（<c>countermeasureName</c> / <c>weaponName</c> / <c>InfoText</c> …），
        /// 而词表正是按这些名字分组登记的。
        ///
        /// <para><b>必须剥掉 Unity 自动加的重名后缀。</b>同一预制体被实例化多次时，Unity 会把
        /// 后续实例命名为 <c>average (1)</c> / <c>throttleLabel (2)</c>。这些名字永远命中不了
        /// 按 <c>average</c> / <c>throttleLabel</c> 登记的作用域词条，于是静默退化到全局匹配 ——
        /// 部分文本还能翻、部分翻不出，是最难排查的那种故障。</para>
        /// </summary>
        internal static string ScopeOf(Component comp)
        {
            if (comp == null) return "Unknown";
            string name = comp.gameObject.name;
            if (string.IsNullOrEmpty(name)) return "Unknown";

            // 先做一次字符速判，绝大多数名字不以 ')' 结尾，省掉正则。
            if (name[name.Length - 1] == ')') name = UnityDuplicateSuffix.Replace(name, string.Empty);
            if (name.Length == 0) return "Unknown";

            // ★ 第三方 mod 的 UI 对象名极其通用（Title / Value / Label / Text / Caption …），
            //   与游戏本体的作用域名**逐字重合**：本体有 [Label] 112 条、[Title] 63 条、
            //   [Name] 45 条、[Text] 35 条。不隔离的话，mod 的 "Label" 会直接查本体的
            //   [Label] 分表，命中即串味（同一串英文在两个界面含义不同，译文张冠李戴）。
            //   判据取「祖先链上存在 Boscali 开头的节点」：mod 的根是 BoscaliSummer.Core，
            //   自带画布叫 "Boscali / Interaction"、"Boscali / Chatter"，名字空间不会与本体相撞。
            //   mod 文本因此得到 BS: 前缀，与本体彻底分表。
            if (ModScopeEnabled && IsModUi(comp.transform))
            {
                int room = 40 - ModScopePrefix.Length;   // 作用域名上限 40（见 ScopePrefixMax）
                if (name.Length > room) name = name.Substring(0, room);
                return ModScopePrefix + name;
            }
            return name;
        }

        /// <summary>第三方 mod 的 UI 作用域前缀。空串表示不隔离（回退到旧行为）。</summary>
        private const string ModScopePrefix = "BS:";

        /// <summary>mod 是否在用独立作用域。默认开；装了 mod 才有意义，没装时 IsModUi 恒 false。</summary>
        internal static bool ModScopeEnabled { get; set; } = true;

        /// <summary>
        /// 祖先链上是否有 mod 自己的节点。结果按 Transform 缓存 ——
        /// 本方法在每次潜在翻译时都会被调用（HUD 上每秒上万次），
        /// 向上遍历虽有缓存，仍要避免重复走父链。
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Transform, object> ModUiCache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Transform, object>();
        private static readonly object Yes = new object();
        private static readonly object No = new object();

        private static bool IsModUi(Transform t)
        {
            if (t == null) return false;
            // 「不是 mod」也要缓存：本体 HUD 的文本每秒被查上万次，
            // 每次都向上走 12 层父链是不可接受的。缓存后每个 Transform 一生只走一次。
            object cached;
            if (ModUiCache.TryGetValue(t, out cached)) return ReferenceEquals(cached, Yes);

            bool mod = false;
            Transform cur = t;
            for (int depth = 0; depth < 12 && cur != null; depth++)
            {
                string n = cur.name;
                // 只看名字前缀：BoscaliSummer.Core / Boscali / xxx / BoscaliXxx
                if (n != null && n.Length >= 7 && n.StartsWith("Boscali", StringComparison.Ordinal))
                {
                    mod = true;
                    break;
                }
                cur = cur.parent;
            }

            // 目标框架是 .NET Framework（Mono），ConditionalWeakTable 没有 AddOrUpdate，
            // 用 Remove + Add 代替；都在主线程，无并发问题。
            ModUiCache.Remove(t);
            ModUiCache.Add(t, mod ? Yes : No);
            return mod;
        }

        /// <summary>Unity 的重名后缀，形如 <c>" (3)"</c>。</summary>
        private static readonly System.Text.RegularExpressions.Regex UnityDuplicateSuffix =
            new System.Text.RegularExpressions.Regex(
                @"\s*\(\d+\)$", System.Text.RegularExpressions.RegexOptions.Compiled);

        // ---------------------------------------------------------------- 控制台作用域

        /// <summary>
        /// mod 自建航电控制台（NOAvionics 的 <c>AvConsole</c>）的**备选作用域**，如
        /// <c>"BS:AvConsole WMC"</c>；不在控制台里则返回 null。
        ///
        /// <para><b>为什么需要这一层。</b>控制台整棵子树用的都是 mod 的通用节点名
        /// （<c>Title</c>/<c>Label</c>/<c>Value</c>…），逐字撞上本体的作用域名。
        /// 实锤：WMC 中队控制台标题是 <c>AvText.Make(root, "Title", …, "WING")</c>
        /// （<c>WmcPanel.cs:366</c> 的 <c>AvConsole.Build(content, "WMC", "WING", …)</c>），
        /// 于是命中挂载界面挂点组那条 <c>[Title]WING = 翼下</c>，控制台标题永远显示「翼下」；
        /// 五个选项卡同理，<c>Control WING</c> 里的 <c>Label</c> 命中 <c>[label]WING = 机翼</c>。
        /// 而控制台根节点叫 <c>"AvConsole WMC"</c>、不在 <c>Boscali*</c> 命名空间里，
        /// 上面那套 <c>BS:</c> 隔离对它不生效。</para>
        ///
        /// <para><b>为什么不在 <see cref="ScopeOf"/> 里直接换返回值。</b>那会把整棵子树的叶子
        /// 作用域一次全换掉，靠叶子名命中的既有译文会一起失效，回归面太大。这里只提供
        /// 「备选作用域」，由 <see cref="TryLocalize"/> **先精确查它、查不到再走原路** ——
        /// 只有显式登记过的键才会改变行为，其余逐字不变。</para>
        ///
        /// <para><b>成本。</b>按 Transform 缓存（<c>ConditionalWeakTable</c>），每个文本节点
        /// 一生只向上走一次父链；之后每条文本多一次字典探测（约 30 ns），
        /// 且只发生在 mod 航电控制台内部（几十个节点），不在 HUD 主路径上。</para>
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Transform, object> ConsoleScopeCache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Transform, object>();

        private static string ConsoleScopeOf(Component comp)
        {
            if (comp == null || !ModScopeEnabled) return null;
            Transform t = comp.transform;
            if (t == null) return null;

            object cached;
            if (ConsoleScopeCache.TryGetValue(t, out cached)) return cached as string;

            string scope = null;
            Transform cur = t;
            // 深度 5：Title 直接挂在根下（1 层）；选项卡标签是 根 > Tabs > Control X > Label（3 层）。
            for (int depth = 0; depth < 5 && cur != null; depth++)
            {
                string n = cur.name;
                if (n != null && n.Length > 9 && n.StartsWith("AvConsole ", StringComparison.Ordinal))
                {
                    int room = 40 - ModScopePrefix.Length;
                    scope = ModScopePrefix + (n.Length > room ? n.Substring(0, room) : n);
                    break;
                }
                cur = cur.parent;
            }

            ConsoleScopeCache.Remove(t);
            ConsoleScopeCache.Add(t, scope ?? (object)No);
            return scope;
        }

        /// <summary>
        /// mod HUD（Boscali Summer 的 <c>Hud</c> 模块）的**备选作用域**。
        ///
        /// <para><b>为什么要单独成域。</b>这个模块不是用 TMP 组件摆文字，而是
        /// <c>ScopeText</c> 自己开一个 <b>固定 12 px 网格</b> 的标签池，逐词
        /// <c>SetCharArray</c> 画上去（坐标、字号、列宽全部按 <c>DesignAdvance = 0.65 em</c>
        /// 的等宽拉丁字面算死）。所以它的词是**独立语义**，与本体同名词条冲突：
        /// HUD 的 <c>CLEAR</c> 是「无威胁」，本体词表里 <c>CLEAR = 清除</c>；
        /// HUD 的 <c>GEAR</c> 是起落架状态灯，<c>SAFE</c> 是武器保险，都落在本体
        /// 同名键上，一旦串味就是错的。</para>
        ///
        /// <para><b>判据。</b>这个模块的标签对象名固定是 <c>"Word"</c>
        /// （<c>ScopeText.NewLabel</c> 里 <c>new GameObject("Word", …)</c>），
        /// 且挂在 <c>Boscali*</c> 根下（<c>StatusPanel</c> 的根叫
        /// <c>"BoscaliStatusPanel"</c>），所以「名字 = Word 且在 mod 子树内」即可，
        /// 与本体任何节点都不会撞。</para>
        ///
        /// <para><b>成本。</b>按 Transform 缓存（<c>ConditionalWeakTable</c>），
        /// 每个标签对象一生只走一次父链；之后每条文本多一次表探测（约 30 ns）。
        /// HUD 上标签池是按帧复用的（几十个），且只在文字变化时才调
        /// <c>SetCharArray</c>，稳定画面下等于零。</para>
        /// </summary>
        internal const string HudScopeName = "BS:HUD";

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Transform, object> HudWordCache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Transform, object>();

        /// <summary>这个组件是不是 mod HUD 的一个词（<c>ScopeText</c> 的标签池成员）。</summary>
        internal static bool IsHudWord(Component comp)
        {
            if (comp == null || !ModScopeEnabled) return false;
            Transform t = comp.transform;
            if (t == null) return false;

            object cached;
            if (HudWordCache.TryGetValue(t, out cached)) return ReferenceEquals(cached, Yes);

            // 先比名字（一次比较，绝大多数节点当场退出），再判 mod 子树。
            bool hit = string.Equals(t.name, "Word", StringComparison.Ordinal) && IsModUi(t);

            HudWordCache.Remove(t);
            HudWordCache.Add(t, hit ? (object)Yes : No);
            return hit;
        }

        /// <summary>已经报过「HUD 放不下」的原文（同一串只报一次）。</summary>
        private static readonly System.Collections.Generic.HashSet<string> HudTooWideSeen =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private static int _hudTooWideBudget = 80;

        /// <summary>
        /// 记一条「译文比字格子宽，所以保持英文」。
        ///
        /// <para>HUD 的列宽是按等宽拉丁算死的（<c>ScopeText.DesignAdvance = 0.65 em</c>），
        /// 中文一字 1 em，超出就被 <c>RectMask2D</c> 裁掉半个字。这种情况下保持英文比
        /// 画一半更好，但必须留下线索 —— 否则「明明有译文却不显示」又会变成要靠猜的问题。
        /// 同一串只报一次，预算 80 条。</para>
        /// </summary>
        internal static void ReportHudTooWide(string original, string translated)
        {
            if (_hudTooWideBudget <= 0) return;
            if (!HudTooWideSeen.Add(original)) return;
            _hudTooWideBudget--;
            Diagnostics.Log.Warn("[HUD] 放不进格子，保持英文：「" + original + "」→「" + translated + "」");
        }

        /// <summary>
        /// 尝试翻译。返回 true 表示应当写回（结果与原文不同且确实含中文）。
        /// </summary>
        internal static bool TryLocalize(string original, Component comp, out string translated)
        {
            translated = original;
            if (string.IsNullOrEmpty(original)) return false;

            // 看门狗：逐帧宿主会在场景加载时被 Unity 一并销毁。这条路径是翻译命中的
            // 必经之处，也就是最后一道重建机会。宿主已存在时它只做一次静态字段比较。
            Host.Ensure();

            var localizer = LocalizationPlugin.Localizer;
            if (localizer == null) return false;

            // 「作用域推断」段（性能探针，默认关）：每次潜在翻译一次，含原生 name 编组。
            long probeScope = Diagnostics.PerfProbe.Begin();
            string scope = ScopeOf(comp);
            // mod 自绘 HUD 的标签池：作用域直接换成 HUD 专用域。
            // 它每个标签的节点名都叫 "Word"（ScopeOf 会给出 "BS:Word"），
            // 拿这个当作用域既没意义、又让 HUD 的 CLEAR/GEAR/SAFE 落在本体同名词条上。
            if (IsHudWord(comp)) scope = HudScopeName;
            Diagnostics.PerfProbe.End(Diagnostics.PerfProbe.Seg.ScopeOf, probeScope);

            // 备选作用域：先试「更细的那一层」，只认精确登记，未登记则原路返回、行为不变。
            //   · mod 航电控制台（AvConsole）—— 叶子名是 Title/Label，会被本体同名词条串味；
            //   · mod 自绘 HUD（ScopeText 的 "Word" 标签池）—— CLEAR/GEAR/SAFE 与本体
            //     同名不同义，见 HudScopeName 的注释。
            string altScope = ConsoleScopeOf(comp);
            if (altScope == null && IsHudWord(comp)) altScope = HudScopeName;

            if (altScope != null)
            {
                string altHit = LocalizationPlugin.Table.LookupScoped(altScope, original);
                if (altHit != null && altHit != original && Core.TextLocalizer.HasChinese(altHit))
                {
                    translated = altHit;
                    Remember(altHit, original);
                    RewriteGuard.Track(comp, original, altHit);
                    Host.NotifyTranslationActivity();
                    LocalizationPlugin.NotifyLocalized(comp as TMP_Text);
                    return true;
                }
            }

            string result = localizer.Localize(original, scope);
            if (result == original || !Core.TextLocalizer.HasChinese(result)) return false;

            translated = result;
            Remember(result, original);
            RewriteGuard.Track(comp, original, result);
            // 新内容真的翻出来了 —— 告诉兜底扫描「界面在变」，把空闲退避清零，
            // 并让下一轮扫描尽快跟上（有些文本绕过 setter，只有扫描才够得着）。
            Host.NotifyTranslationActivity();
            // 翻译刚写进去就把对象登记下来：TMP 可能因为「行高不够 + Ellipsis」把整条中文清零，
            // 下一帧的自愈通道会立刻把它救回来（详见 LocalizationPlugin.RepairStuck）。
            LocalizationPlugin.NotifyLocalized(comp as TMP_Text);
            return true;
        }

        /// <summary>
        /// 原地翻译组件当前持有的文本。用于 <c>OnEnable</c> 这类"预制体自带英文默认值、
        /// 不经过任何 setter"的场景。
        /// </summary>
        /// <returns>是否真的改写了文本。兜底扫描靠它统计「本轮翻到东西没有」，
        /// 作为空闲退避的信号 —— 什么都不改的扫描才允许拉长间隔。</returns>
        internal static bool LocalizeInPlace(Component comp)
        {
            if (!IsSupported(comp)) return false;
            try
            {
                string current = Read(comp);
                if (string.IsNullOrEmpty(current)) return false;

                string translated;
                if (!TryLocalize(current, comp, out translated)) return false;

                // 直接写字段，避开 setter —— 否则会触发我们自己的 set_text 钩子，
                // 虽然幂等短路能兜住，但白白多走一遍完整流水线。
                Write(comp, translated);
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug("原地翻译失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>读取组件当前文本（优先走底层字段）。供防回写使用。</summary>
        internal static string Read(Component comp)
        {
            var tmp = comp as TMP_Text;
            if (tmp != null) return _tmpField == null ? null : _tmpField(tmp);

            var legacy = comp as Text;
            if (legacy != null) return _legacyField == null ? null : _legacyField(legacy);

            return null;
        }

        /// <summary>写回组件文本（优先走底层字段）。供防回写使用。</summary>
        internal static void Write(Component comp, string value)
        {
            var tmp = comp as TMP_Text;
            if (tmp != null) { if (_tmpField != null) _tmpField(tmp) = value; return; }

            var legacy = comp as Text;
            if (legacy != null) { if (_legacyField != null) _legacyField(legacy) = value; }
        }

        /// <summary>某个组件的文本能否被我们读写（字段反射成功且类型受支持）。</summary>
        internal static bool IsSupported(Component comp)
        {
            if (comp is TMP_Text) return _tmpField != null;
            if (comp is Text) return _legacyField != null;
            return false;
        }

        /// <summary>
        /// 把组件上的中文还原回英文。供「关闭翻译」使用。
        ///
        /// <para>这里<b>刻意不</b>套用 <see cref="TryLocalize"/> 的「只写含中文的结果」收敛性限制 ——
        /// 该限制的目的是防止英文↔英文来回改写，而这里的目标恰恰是写回英文。</para>
        /// </summary>
        internal static void RevertInPlace(Component comp)
        {
            if (!IsSupported(comp)) return;
            try
            {
                string current = Read(comp);
                string original;
                if (!TryLookupOriginal(current, out original)) return;
                Write(comp, original);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug("还原失败：" + ex.Message);
            }
        }

        /// <summary>当前文本是中文时，反查出它对应的英文原文。</summary>
        private static bool TryLookupOriginal(string current, out string original)
        {
            original = null;
            if (string.IsNullOrEmpty(current)) return false;
            if (!Core.TextLocalizer.HasChinese(current)) return false;

            // 优先查「我们写下去的那一份」：模板与片段拼接的结果是运行期拼出来的，
            // 词表的反向索引里根本没有这一项。
            if (WrittenOriginal.TryGetValue(current, out original) && !string.IsNullOrEmpty(original))
            {
                return true;
            }

            var localizer = LocalizationPlugin.Localizer;
            return localizer != null && localizer.TryReverseLookup(current, out original)
                   && !string.IsNullOrEmpty(original);
        }

        /// <summary>
        /// 读回还原：把中文译文反查回英文原文。
        ///
        /// <para><b>为什么必须做。</b>游戏里大量「读回比较后再写回」的用法，例如
        /// <c>if (weaponName.text != info.shortName) weaponName.text = info.shortName;</c>。
        /// 只要读出来是中文，比较就恒为真，游戏每帧把英文重写一遍 ——
        /// 这就是右上角文本「一闪中文又变回英文」的根源。还原成原文后比较重新成立，回写自然停止。</para>
        ///
        /// <para><b>为什么只对 TMP 做、不动旧版 UI.Text。</b>TMP 的渲染走 <c>m_text</c> 字段，
        /// 不经过 <c>get_text</c>，所以改读回值不影响屏上显示；而旧版 UI.Text 的文本网格
        /// 就是直接读 <c>text</c> 生成的，改读回值会把显示也变成英文。</para>
        /// </summary>
        internal static void RestoreOriginalOnRead(ref string value)
        {
            string original;
            if (TryLookupOriginal(value, out original)) value = original;
        }
    }
}
