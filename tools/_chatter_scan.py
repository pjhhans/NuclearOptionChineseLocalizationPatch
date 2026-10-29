# -*- coding: utf-8 -*-
"""扫描 mod 的 .chatter 资源包，统计台词在我们词表里的覆盖情况。

为什么单独做这个：这些台词**不是 C# 字面量**，而是嵌在 DLL 里的资源文件
（BoscaliSummer.Chatter.*.chatter），按 [角色] 文本 的格式列出。
之前几轮提取都是从反编译的 .cs / GitHub 的 .cs 里捞，整类资产文本从来没进过视野。
"""
import io, json, os, re, sys, collections

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PACKS = os.path.join(ROOT, '_bs_gh', 'modules', 'Chatter', 'Assets', 'Packs')
DATA = os.path.join(os.environ.get('NO_DATA', r'E:/SteamLibrary/steamapps/common/Nuclear Option\Git\NuclearOptionChineseLocalizationPatch\data'))

LINE = re.compile(r'^\[([^\]]*)\]\s+(.+)$')
PH = re.compile(r'<[a-zA-Z]+>')

def load(name):
    p = os.path.join(DATA, name)
    if not os.path.exists(p):
        return {}
    with io.open(p, encoding='utf-8') as f:
        return json.load(f)

def main():
    global_t = load('translation.json')
    frag = load('fragments.json')
    tmpl = load('templates.json')
    scoped = {}
    sd = os.path.join(DATA, 'scopes')
    if os.path.isdir(sd):
        for fn in os.listdir(sd):
            if fn.endswith('.json'):
                for k, v in load(os.path.join('scopes', fn)).items():
                    scoped[k] = v

    rows = []
    for fn in sorted(os.listdir(PACKS)):
        if not fn.endswith('.chatter'):
            continue
        with io.open(os.path.join(PACKS, fn), encoding='utf-8') as f:
            for raw in f:
                m = LINE.match(raw.strip())
                if m:
                    rows.append((fn, m.group(1), m.group(2)))

    hit, miss, ph_rows = 0, [], 0
    for fn, tag, text in rows:
        if PH.search(text):
            ph_rows += 1
            miss.append((fn, tag, text, 'PH'))
            continue
        if text in global_t:
            hit += 1
        else:
            miss.append((fn, tag, text, ''))

    print(f'台词总条数 {len(rows)}；已有词条 {hit}；缺口 {len(miss)}（其中含占位符 {ph_rows}）')
    per = collections.Counter(r[0] for r in miss)
    for k, v in sorted(per.items()):
        print(f'  {k:24} 缺口 {v}')
    out = os.path.join(ROOT, '_chatter_missing.json')
    with io.open(out, 'w', encoding='utf-8') as f:
        json.dump(miss, f, ensure_ascii=False, indent=1)
    print('明细写入', out)

if __name__ == '__main__':
    main()
