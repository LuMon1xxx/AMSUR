# -*- coding: utf-8 -*-
"""Детектор column-drift: для каждого (учитель, день) считает совпадения
предметов при сдвигах -1/0/+1. Отчёт только где лучший != 0."""
from teachers_grid import TEACH
from classes_grid import CLS

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
TAG = {"РусЯ": "RU", "РусЛит": "RUL", "БелЯ": "BY", "БелЛит": "BYL",
"Англ": "EN", "АнглS": "EN*", "АнглП": "ENP", "АнглБ": "ENB",
"Матем": "MA", "Физика": "PH", "Химия": "CH", "ХимП": "CHP", "ХимБ": "CHB",
"Био": "BI", "БиоП": "BIP", "БиоБ": "BIB",
"ИстРБ": "HI", "ИстБелБ": "HIB", "ИстБелП": "HIP", "ИстРК": "HIK",
"Обществ": "OB", "ОбществП": "OBP", "ОбществБ": "OBB", "ВсИст": "WH",
"Гео": "GE", "Инф": "IN", "ИнфS": "IN*", "Физра": "PE", "ТрудS": "LA",
"ДМП_S": "DM", "Иск": "AR", "Черчение": "AR2", "ЧиМ": "CM", "ОБЖ": "OBZ",
"КлЧас": "CC", "Астрон": "AS"}
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
    else: POOL[n] = None

SKIP3 = {"3а", "3б", "3в", "3г", "4а", "4б", "4в", "4г"}


def cls_at(cls, day, slot):
    for k, v in CLS.items():
        if k.casefold() == cls.casefold():
            cell = v.get(day, {}).get(slot)
            if cell is None:
                return []
            subs = list(cell) if isinstance(cell, tuple) else [cell]
            return [TAG.get(s, s) for s in subs]
    return []


out = []
for t, pool in POOL.items():
    if pool is None:
        continue
    td = TEACH.get(t, {})
    for day in ["Пн", "Вт", "Ср", "Чт", "Пт"]:
        marks = [(s, m) for s, m in td.get(day, {}).items()
                 if isinstance(m, str) and not m.startswith("к/ч")
                 and "-" not in m and m not in ("аст", "цех", "над", "3кл-над")
                 and "/" not in m and m.casefold() not in SKIP3]
        if len(marks) < 3:
            continue
        scores = {}
        for off in (-1, 0, 1):
            hit = tot = 0
            for s, m in marks:
                subs = cls_at(m, day, s + off)
                if not subs:
                    continue
                tot += 1
                if any(x in pool or x == "CC" for x in subs):
                    hit += 1
            scores[off] = (hit, tot)
        best = max(scores, key=lambda o: (scores[o][0], -abs(o)))
        if best != 0 and scores[best][0] > scores[0][0]:
            out.append(f"{t} {day}: " +
                       " ".join(f"{o}:{scores[o][0]}/{scores[o][1]}" for o in (-1, 0, 1)) +
                       f" => BEST {best:+d}")
open('drift_out.txt', 'w', encoding='utf-8').write(chr(10).join(out))
print('drift candidates:', len(out))
