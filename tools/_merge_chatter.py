# -*- coding: utf-8 -*-
"""把 _tr/<包名>.py 里的台词译文合并进词表。

用法： python tools/_merge_chatter.py stations
约定：
  * 模块里 T = [(英文原文含 <占位符>, 中文含 {0}{1}...), ...]
  * 原文里的 <x> 在词表键里换成 {*}，译文里的 {n} 按出现顺序回填（由引擎在运行时替换）
  * 无占位符的走 translation.json；有占位符的走 templates.json（键加 ~ 前缀）
"""
import io, json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.environ.get('NO_DATA', r'E:/SteamLibrary/steamapps/common/Nuclear Option\Git\NuclearOptionChineseLocalizationPatch\data')
PACKS = os.path.join(ROOT, '_bs_gh', 'modules', 'Chatter', 'Assets', 'Packs')

LINE = re.compile(r'^\[([^\]]*)\]\s+(.+)$')
PH = re.compile(r'<[a-zA-Z]+>')


def load_json(p):
    if not os.path.exists(p):
        return {}
    with io.open(p, encoding='utf-8') as f:
        return json.load(f)


def save_json(p, d):
    with io.open(p, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(d, f, ensure_ascii=False, indent=2, sort_keys=True)
        f.write('\n')


def main():
    name = sys.argv[1]
    sys.path.insert(0, os.path.join(ROOT, '_tr'))
    mod = __import__(name)
    table = {}
    for en, cn in mod.T:
        if en in table:
            raise SystemExit('模块内重复条目: ' + en)
        table[en] = cn

    pack = os.path.join(PACKS, name + '.chatter')
    lines = []
    with io.open(pack, encoding='utf-8') as f:
        for raw in f:
            m = LINE.match(raw.strip())
            if m:
                lines.append(m.group(2))

    missing = [l for l in lines if l not in table]
    extra = [k for k in table if k not in lines]
    if missing:
        print('!! 包内还有 %d 条没在 T 里（前 5 条）:' % len(missing))
        for l in missing[:5]:
            print('   ', l)
    if extra:
        print('!! T 里有 %d 条与包内原文对不上（前 5 条）:' % len(extra))
        for l in extra[:5]:
            print('   ', l)

    plain, tmpl, bad = {}, {}, []
    for en in lines:
        cn = table.get(en)
        if cn is None:
            continue
        n = len(PH.findall(en))
        key = PH.sub('{*}', en)

        # 译文里的 {n} 必须恰好是 0..n-1
        used = set(int(x) for x in re.findall(r'\{(\d+)\}', cn))
        if used != set(range(n)):
            bad.append((en, cn, n, sorted(used)))
            continue

        if n == 0:
            if '{' in key or '}' in key:
                bad.append((en, cn, -1, 'raw-brace'))
                continue
            plain[key] = cn
        else:
            # 固定段不得以数字开头（与 {#} 模板同一条约束：通配贪婪吃位后会对不齐）
            segs = key.split('{*}')
            for s in segs[1:]:
                if s.strip()[:1].isdigit():
                    bad.append((en, cn, -2, s[:12]))
                    break
            else:
                tmpl['~' + key] = cn

    if bad:
        print('!! %d 条译文占位符对不上：' % len(bad))
        for b in bad[:8]:
            print('   ', b)

    tp = os.path.join(DATA, 'translation.json')
    mp = os.path.join(DATA, 'templates.json')
    t = load_json(tp)
    m = load_json(mp)
    before = (len(t), len(m))
    t.update(plain)
    m.update(tmpl)
    save_json(tp, t)
    save_json(mp, m)
    print('普通词条 %d → %d（+%d）' % (before[0], len(t), len(t) - before[0]))
    print('模板词条 %d → %d（+%d）' % (before[1], len(m), len(m) - before[1]))
    print('本包：纯文本 %d / 占位符模板 %d / 不合格 %d / 未翻 %d'
          % (len(plain), len(tmpl), len(bad), len(missing)))


if __name__ == '__main__':
    main()
