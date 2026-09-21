# -*- coding: utf-8 -*-
"""Кросс-чек v2: ASCII-вывод, extras, строгие пары/сплиты."""
import sys
sys.path.insert(0, '.')
from teachers_grid import TEACH
from classes_grid import CLS

TAG = {"РусЯ": "RU", "РусЛит": "RUL", "БелЯ": "BY", "БелЛит": "BYL",
"Англ": "EN", "АнглS": "EN*", "АнглП": "ENP", "АнглБ": "ENB",
"Матем": "MA", "Физика": "PH", "Химия": "CH", "ХимП": "CHP", "ХимБ": "CHB",
"Био": "BI", "БиоП": "BIP", "БиоБ": "BIB",
"ИстРБ": "HI", "ИстБелБ": "HIB", "ИстБелП": "HIP", "ИстРК": "HIK",
"Обществ": "OB", "ОбществП": "OBP", "ОбществБ": "OBB", "ВсИст": "WH",
"Гео": "GE", "Инф": "IN", "ИнфS": "IN*", "Физра": "PE", "ТрудS": "LA",
"ДМП_S": "DM", "Иск": "AR", "Черчение": "AR2", "ЧиМ": "CM", "ОБЖ": "OBZ",
"КлЧас": "CC", "Астрон": "AS"}

def tag(s):
    return TAG.get(s, "?" + s)


def norm_cls(c):
    return c.encode('utf-8').decode('utf-8').casefold()

POOL = {}
order = ["Абрамова", "Сидорович", "Потапов", "Берёзко", "Авдеёнок", "Бурко",
"Воронова", "Жилко", "Толстик", "Лащ", "Лабикова", "Хинич", "Гордионок",
"Мисевич", "Хильчук", "Ильюшина", "Бадеева", "Наконечная", "Бесфамильно",
"Денискин", "Александрович", "Воробьёва", "Авхименко", "Крупович", "Павлович",
"Шаболтиев", "Коротченя", "Гигель", "Свищёва", "Пекарский", "Апанович",
"Разводовская", "Шустова", "Плужнова", "Касперович", "Богданова", "Новикова",
"Хорошко", "Островская", "Мазынская", "Маляревич", "Демидчик", "Липницкая",
"Дамашевич", "Чепикова", "Шаройкина", "Королёнок", "Асафова", "Голубец"]
RU = {"RU", "RUL"}
BE = {"BY", "BYL"}
EN = {"EN", "EN*", "ENP", "ENB"}
for i, n in enumerate(order, 1):
    if 1 <= i <= 3: POOL[n] = RU
    elif i == 4: POOL[n] = {"GE"}
    elif 5 <= i <= 9: POOL[n] = BE
    elif 10 <= i <= 12: POOL[n] = {"LA"}
    elif i in (14, 15): POOL[n] = {"MA"}
    elif i in (17, 18): POOL[n] = {"IN"}
    elif i == 20: POOL[n] = {"PH"}
    elif 22 <= i <= 25: POOL[n] = {"HI", "HIB", "HIP", "HIK", "OB", "OBP", "OBB", "WH"}
    elif i == 26: POOL[n] = {"DM"}
    elif i == 27: POOL[n] = {"AR", "AR2"}
    elif 28 <= i <= 31: POOL[n] = {"PE"}
    elif 32 <= i <= 41: POOL[n] = EN
    else: POOL[n] = None  # flexible: 13,16,19,21,42-46

FLEX = {n for n, p in POOL.items() if p is None}

out = []


def w(s):
    out.append(s)


def parse_mark(m):
    if m.startswith("к/ч:"):
        return ("kc", m[4:])
    if m.endswith("*") and len(m) > 2:
        return ("kc", m[:-1])  # суффиксная форма к/ч из teachers_grid
    if m.endswith("-ф") or m.endswith("-пл") or m.endswith("-инд"):
        return ("extra", m)
    if m in ("аст", "цех", "над", "3кл-над"):
        return ("extra", m)
    if "/" in m and any(x in m for x in ("ф.", "пл.", "инд.")):
        return ("extra", m)
    if m == "7б/7а":
        return ("multi", ["7б", "7а"])
    return ("cls", m)


ok1 = ok2 = 0
orphans, mism, extras = [], [], []
flexmap = {}
for cls, days in CLS.items():
    for day, slots in days.items():
        for slot, cell in slots.items():
            subs = list(cell) if isinstance(cell, tuple) else [cell]
            tags = [tag(s) for s in subs]
            is_split = len(subs) == 2 or (len(subs) == 1 and subs[0].endswith("S") and subs[0] not in ("КлЧас",))
            found = []
            for t, td in TEACH.items():
                m = td.get(day, {}).get(slot)
                if not m:
                    continue
                kind, val = parse_mark(m)
                if kind == "cls" and norm_cls(val) == norm_cls(cls):
                    found.append(t)
                elif kind == "multi" and norm_cls(cls) in [norm_cls(x) for x in val]:
                    found.append(t)
                elif kind == "kc" and norm_cls(val) == norm_cls(cls) and subs == ["КлЧас"]:
                    found.append(t)
            if not found:
                orphans.append(f"{cls} {day} {slot} {tags}")
                continue
            bad = False
            for t in found:
                pool = POOL[t]
                if pool is None:
                    for s in tags:
                        flexmap.setdefault(t, {}).setdefault(s, 0)
                        flexmap[t][s] += 1
                elif not any(s in pool or s == "CC" for s in tags):
                    mism.append(f"{cls} {day} {slot} {tags} teacher={t}")
                    bad = True
            if not bad:
                if is_split or len(found) >= 2:
                    ok2 += 1
                else:
                    ok1 += 1

# extras: учительские клетки без пары в классной сетке
for t, td in TEACH.items():
    for day, slots in td.items():
        for slot, m in slots.items():
            kind, val = parse_mark(m)
            if kind == "extra":
                continue
            targets = [val] if kind in ("cls", "kc") else val
            for tg in targets:
                if tg.casefold() in ("3а", "3б", "3в", "3г", "4а", "4б", "4в", "4г"):
                    continue
                c = CLS.get(tg, CLS.get(tg.capitalize(), CLS.get(tg.upper())))
                if c is None:
                    # ищем без учёта регистра
                    c = next((v for k, v in CLS.items() if norm_cls(k) == norm_cls(tg)), None)
                if c is None or slot not in c.get(day, {}):
                    extras.append(f"{t} {day} {slot} -> {tg} (class empty)")

w(f"ok_single={ok1} ok_split={ok2} orphans={len(orphans)} mism={len(mism)} extras={len(extras)}")
w("--- ORPHANS ---")
w(chr(10).join(orphans))
w("--- MISM ---")
w(chr(10).join(mism))
w("--- EXTRAS(teacher->empty, no code) ---")
w(chr(10).join(extras))
w("--- FLEXMAP ---")
for t, d in flexmap.items():
    w(f"{t}: {d}")
w("--- SPLIT-PAIRS (class day slot subj -> teachers) ---")
pairs = {}
for cls, days in CLS.items():
    for day, slots in days.items():
        for slot, cell in slots.items():
            subs = list(cell) if isinstance(cell, tuple) else [cell]
            if len(subs) == 2 or (len(subs) == 1 and subs[0].endswith("S") and subs[0] not in ("КлЧас",)):
                found = []
                for t, td in TEACH.items():
                    m = td.get(day, {}).get(slot)
                    if not m:
                        continue
                    kind, val = parse_mark(m)
                    if kind == "cls" and norm_cls(val) == norm_cls(cls):
                        found.append(t)
                    elif kind == "multi" and norm_cls(cls) in [norm_cls(x) for x in val]:
                        found.append(t)
                tags = [tag(s) for s in subs]
                pairs.setdefault(tags[0], []).append(f"{cls} {day}{slot}={'+'.join(found) if found else 'NONE'}")
for k, v in pairs.items():
    w(f"{k}: " + "; ".join(v[:14]))
open('crosscheck_out.txt', 'w', encoding='utf-8').write(chr(10).join(out))
print('wrote crosscheck_out.txt')
