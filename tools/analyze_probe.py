#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""分析汉化插件自带性能探针（PerfProbe → ProbeDump）落盘的 CSV。

与 analyze.py 的分工：
  analyze.py    读「独立记录插件」的逐帧数据，回答「整帧跑多快」；
  analyze_probe  读「汉化插件自己」的每秒窗口，回答「本地化占了整帧多少毫秒」。

后者之所以必要：实测同一遍录制内、同高度巡航的 20 s 窗 P50 极差就有 1.0~2.0 ms，
而待检出的汉化开销约 0.3 ms —— 整帧口径下信号被工况漂移完全遮住。探针只量自己那段
代码的墙钟，外部负载怎么漂都进不了分子。

用法：
    python tools/analyze_probe.py <probe-*.csv 或所在目录> [...]
    python tools/analyze_probe.py <csv> --warmup 5 --markdown
"""
import argparse
import csv
import glob
import os
import sys

SEGMENTS = [
    ("scopeof", "作用域推断"),
    ("namelist", "名单判定"),
    ("template", "模板查询"),
    ("scoped", "分表查询"),
    ("core", "主体流水线"),
]


def pct(values, p):
    if not values:
        return 0.0
    v = sorted(values)
    if len(v) == 1:
        return v[0]
    k = (len(v) - 1) * p
    lo = int(k)
    hi = min(lo + 1, len(v) - 1)
    return v[lo] + (v[hi] - v[lo]) * (k - lo)


def collect(paths):
    if not paths:
        paths = ["."]
    files = []
    for p in paths:
        if os.path.isdir(p):
            files.extend(sorted(glob.glob(os.path.join(p, "probe-*.csv"))))
        elif os.path.isfile(p):
            files.append(p)
    rows = []
    for f in files:
        with open(f, newline="", encoding="utf-8-sig") as fh:
            for r in csv.DictReader(fh):
                try:
                    rows.append({
                        "file": os.path.basename(f),
                        "wall": r["wall"],
                        "win_s": float(r["win_s"]),
                        "frames": int(float(r["frames"])),
                        "fps": float(r["fps"]),
                        "calls": int(float(r["calls"])),
                        "ms_per_s": float(r["ms_per_s"]),
                        "us_per_call": float(r["us_per_call"]),
                        "ms_per_frame": float(r["ms_per_frame"]),
                        "gc0": int(float(r["gc0"])),
                        "kb_per_s": float(r["kb_per_s"]),
                        "b_per_frame": float(r["b_per_frame"]),
                        "segs": {k: float(r["ms_" + k]) for k, _ in SEGMENTS},
                        "seg_calls": {k: int(float(r["cd_" + k])) for k, _ in SEGMENTS},
                    })
                except (KeyError, ValueError):
                    continue
    return files, rows


def main():
    ap = argparse.ArgumentParser(description="分析汉化插件性能探针 CSV")
    ap.add_argument("paths", nargs="*", help="CSV 文件或目录（默认当前目录）")
    ap.add_argument("--warmup", type=float, default=3.0, help="丢弃开头 N 秒（默认 3）")
    ap.add_argument("--baseline", type=float, default=0.0,
                    help="去掉本地化后的基线帧时长 ms（默认 0=按实测帧时长推算）")
    ap.add_argument("--markdown", action="store_true", help="只打印，不写文件")
    args = ap.parse_args()

    files, rows = collect(args.paths)
    if not rows:
        print("没有读到任何 probe-*.csv。请先在游戏里 F11 勾选「性能探针」并飞一段。", file=sys.stderr)
        return 1

    # 预热：按窗口累计时长丢弃开头
    acc, keep = 0.0, []
    for r in rows:
        acc += r["win_s"]
        if acc >= args.warmup:
            keep.append(r)
    rows = keep or rows

    total_s = sum(r["win_s"] for r in rows)
    frames = sum(r["frames"] for r in rows)
    calls = sum(r["calls"] for r in rows)
    ms = [r["ms_per_frame"] for r in rows]
    per_call = [r["us_per_call"] for r in rows if r["us_per_call"] > 0]
    cps = [r["calls"] / r["win_s"] for r in rows if r["win_s"] > 0]
    fps = [r["fps"] for r in rows if r["fps"] > 0]

    out = []
    add = out.append
    add("# 汉化性能探针报告\n")
    add(f"- 数据文件：{', '.join(os.path.basename(f) for f in files)}")
    add(f"- 有效窗口：{len(rows)} 个，覆盖 {total_s:.1f} s / {frames} 帧")
    add(f"- 平均帧率：{frames / total_s:.1f} fps" if total_s else "- 平均帧率：n/a")
    add("")

    add("## 每帧本地化耗时（核心指标）\n")
    add("| 指标 | 值 |")
    add("|---|---|")
    add(f"| 均值 | {sum(ms) / len(ms):.4f} ms/帧 |")
    add(f"| P50 | {pct(ms, 0.5):.4f} ms/帧 |")
    add(f"| P90 | {pct(ms, 0.9):.4f} ms/帧 |")
    add(f"| P99 | {pct(ms, 0.99):.4f} ms/帧 |")
    add(f"| 最大 | {max(ms):.4f} ms/帧 |")
    if fps:
        base = args.baseline if args.baseline > 0 else 1000.0 / (frames / total_s)
        med = pct(ms, 0.5)
        if base > med:
            free = 1000.0 / (base - med)
            add(f"| 等效 FPS 损失 | 在 {base:.2f} ms 基线上：{1000.0 / base:.1f} → {free:.1f} fps（+{free - 1000.0 / base:.2f}） |")
    add("")
    add("> 该读数是**上界**：探针每次赋值多两次 `GetTimestamp`（约 20~30 ns），")
    add("> 且会打乱指令缓存局部性。真实开销只会更小。")
    add("")

    add("## 赋值频次\n")
    add("| 指标 | 值 |")
    add("|---|---|")
    add(f"| 总赋值次数 | {calls} |")
    add(f"| 每秒次数 P50 | {pct(cps, 0.5):.0f} |")
    add(f"| 每秒次数 P90 | {pct(cps, 0.9):.0f} |")
    add(f"| 每秒次数 最大 | {max(cps):.0f} |" if cps else "| 最大 | n/a |")
    add(f"| 单次耗时 P50 | {pct(per_call, 0.5):.2f} µs |" if per_call else "| 单次耗时 | n/a |")
    add(f"| 单次耗时 P90 | {pct(per_call, 0.9):.2f} µs |" if per_call else "")
    add("")

    add("## 成本归因（各段占本地化总耗时的比例）\n")
    tot = sum(sum(r["segs"].values()) for r in rows) or 1.0
    add("| 分段 | ms/秒 累计 | 占比 | 次/秒 中位 |")
    add("|---|---|---|---|")
    for key, label in SEGMENTS:
        s = sum(r["segs"][key] for r in rows)
        c = [r["seg_calls"][key] / r["win_s"] for r in rows if r["win_s"] > 0]
        add(f"| {label} | {s:.1f} | {s / tot * 100:.1f}% | {pct(c, 0.5):.0f} |")
    add("")
    add("> 各段是「子系统累计」，含递归调用内部的时间，因此各段之和**不等于** Total；")
    add("> Total 的墙钟才对应上表的 ms/帧。分段只用于看钱花在哪。")
    add("")

    add("## 每帧耗时随时间（每 5 秒聚合，单位 ms/帧）\n")
    buckets = {}
    t = 0.0
    for r in rows:
        b = int(t // 5) * 5
        buckets.setdefault(b, []).append(r)
        t += r["win_s"]
    add("```")
    for b in sorted(buckets):
        vals = [r["ms_per_frame"] for r in buckets[b]]
        m = sum(vals) / len(vals)
        add(f"{b:5d}s {m:7.3f} | {'#' * int(m / 0.01)}")
    add("```")
    add("")

    gc = sum(r["gc0"] for r in rows)
    add(f"GC0：{gc} 次 / {total_s:.0f} s（{gc / total_s * 60:.2f} 次/分钟）" if total_s else "")

    text = "\n".join(out)
    print(text)

    if not args.markdown:
        base = os.path.dirname(files[0]) if files else "."
        dst = os.path.join(base, "probe-report.md")
        with open(dst, "w", encoding="utf-8") as fh:
            fh.write(text)
        print(f"\n已写入 {dst}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
