# -*- coding: utf-8 -*-
"""Чистый вывод учитель->предметы ТОЛЬКО из двух папок (без пулов из чата).
Для каждой клетки учителя (день, урок, класс) берём предмет из классной сетки.
Коды ф/пл/инд/над/аст/цех и к/ч — отдельно."""
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


def cls_subjs(cls, day, slot):
    for k, v in CLS.items():
        if k.casefold() == cls.casefold():
            c = v.get(day, {}).get(slot)
            if c is None:
                return []
            subs = list(c) if isinstance(c, tuple) else [c]
            return [TAG.get(s, s) for s in subs]
    return []


out = []
for t, td in TEACH.items():
    from collections import Counter
    c = Counter()
    codes = Counter()
    for day, slots in td.items():
        for slot, m in slots.items():
            if m.startswith("к/ч:") or (m.endswith("*") and len(m) > 2):
                c["CC"] += 1
                continue
            if m.endswith("-ф") or m.endswith("-пл") or m.endswith("-инд") or m in ("аст", "цех", "над", "3кл-над") or ("/" in m and any(x in m for x in ("ф.", "пл.", "инд."))):
                codes[m] += 1
                continue
            targets = [m] if "/" not in m else [x for x in m.split("/") if x]
            if m == "7б/7а":
                targets = ["7б", "7а"]
            for tg in targets:
                for s in cls_subjs(tg, day, slot):
                    c[s] += 1
    total = sum(v for k, v in c.items() if k != "CC")
    out.append(f"{t}: n={total} " + " ".join(f"{k}={v}" for k, v in c.most_common()) +
               (" | codes: " + " ".join(codes) if codes else ""))
open('pure_out.txt', 'w', encoding='utf-8').write(chr(10).join(out))
print('ok', len(out))
