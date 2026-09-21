# -*- coding: utf-8 -*-
"""Кросс-чек 'классы <-> учителя' по клеткам (день, урок).
Правила:
- целая клетка класса -> ровно 1 учитель с этим классом там же (предмет совместим);
- сплит (S) -> 2 учителя; пара (A,B) -> 2 учителя (предметы свои);
- КлЧас -> учитель с меткой к/ч (любой);
- коды учителя ф/пл/инд/над/аст/цех/3кл-над -> сверки с классом НЕТ (вне сетки),
  учитываются отдельно;
- классы 3-*, 4-* вне scope 5-11 -> пропуск.
Совместимость предмета: primary-пулы от ученика; '?' = гибкие (фиксируем вывод).
"""
import sys
sys.path.insert(0, '.')
from teachers_grid import TEACH
from classes_grid import CLS

POOL = {}  # учитель -> множество возможных предметов-категорий
rus = {"РусЯ", "РусЛит"}
bel = {"БелЯ", "БелЛит"}
eng = {"Англ", "АнглS", "АнглП", "АнглБ"}
mat = {"Матем"}
phy = {"Физика"}
che = {"Химия", "ХимП", "ХимБ"}
bio = {"Био", "БиоП", "БиоБ"}
his = {"ИстРБ", "ИстБелБ", "ИстБелП", "ИстРК", "Обществ", "ОбществП", "ОбществБ", "ВсИст"}
geo = {"Гео"}
inf = {"Инф", "ИнфS"}
pe = {"Физра"}
lab = {"ТрудS"}
dmp = {"ДМП_S"}
art = {"Иск", "Черчение"}
chim = {"ЧиМ"}
ALL = rus | bel | eng | mat | phy | che | bio | his | geo | inf | pe | lab | dmp | art | chim | {"ОБЖ", "Астрон"}

NAMES = ["Абрамова", "Сидорович", "Потапов", "Берёзко", "Авдеёнок", "Бурко",
"Воронова", "Жилко", "Толстик", "Лащ", "Лабикова", "Хинич", "Гордионок",
"Мисевич", "Хильчук", "Ильюшина", "Бадеева", "Наконечная", "Бесфамильно",
"Денискин", "Александрович", "Воробьёва", "Авхименко", "Крупович", "Павлович",
"Шаболтиев", "Коротченя", "Гигель", "Свищёва", "Пекарский", "Апанович",
"Разводовская", "Шустова", "Плужнова", "Касперович", "Богданова", "Новикова",
"Хорошко", "Островская", "Мазынская", "Маляревич", "Демидчик", "Липницкая",
"Дамашевич", "Чепикова", "Шаройкина", "Королёнок", "Асафова", "Голубец"]

for i, n in enumerate(NAMES, 1):
    if 1 <= i <= 3: POOL[n] = rus
    elif i == 4: POOL[n] = geo
    elif 5 <= i <= 9: POOL[n] = bel
    elif 10 <= i <= 12: POOL[n] = lab
    elif i == 14 or i == 15: POOL[n] = mat
    elif i == 17 or i == 18: POOL[n] = inf
    elif i == 20: POOL[n] = phy
    elif 22 <= i <= 25: POOL[n] = his
    elif i == 26: POOL[n] = dmp
    elif i == 27: POOL[n] = art
    elif 28 <= i <= 31: POOL[n] = pe
    elif 32 <= i <= 41: POOL[n] = eng
    else: POOL[n] = set(ALL)  # 13,16,19,21,42-46 гибкие (вывод фиксируем)

CODES = ("-ф", "-пл", "-инд", "аст", "цех", "над", "3кл-над")


def split_mark(m):
    if m.startswith("к/ч:"):
        return ("kc", m[4:])
    for c in ("-ф", "-пл", "-инд"):
        if m.endswith(c):
            return ("extra", m[:-len(c)])
    if m in ("аст", "цех", "над", "3кл-над") or m.endswith("/7а") or "/" in m and any(
            x in m for x in ("ф.", "пл.", "инд.")):
        return ("extra", m)
    if m == "7б/7а":
        return ("multi", ["7б", "7а"])
    return ("cls", m)


orphans, extras, mism, ok_split, ok_single, flexible = [], [], [], 0, 0, {}
for cls, days in CLS.items():
    for day, slots in days.items():
        for slot, cell in slots.items():
            subs = list(cell) if isinstance(cell, tuple) else [cell]
            expect_n = 2 if (len(subs) == 2 or subs == ["АнглS"] or cell in ("АнглS",) or str(cell).endswith("S") and cell not in ("КлЧас",)) else 1
            if subs == ["КлЧас"]:
                expect_n = 1
            found = []
            for t, td in TEACH.items():
                m = td.get(day, {}).get(slot)
                if not m:
                    continue
                kind, val = split_mark(m)
                if kind == "cls" and val.casefold() == cls.casefold():
                    found.append(t)
                elif kind == "multi" and cls.casefold() in [x.casefold() for x in val]:
                    found.append(t)
                elif kind == "kc" and val.casefold() == cls.casefold() and subs == ["КлЧас"]:
                    found.append(t)
            if not found:
                orphans.append((cls, day, slot, subs))
                continue
            # проверка совместимости
            good = True
            for t in found:
                pool = POOL[t]
                if pool == set(ALL):
                    for s in subs:
                        flexible.setdefault(t, {}).setdefault(s, []).append((cls, day, slot))
                elif not any(s in pool or s == "КлЧас" for s in subs):
                    mism.append((cls, day, slot, subs, t, sorted(pool)[:3]))
                    good = False
            if good:
                if len(found) >= 2 or expect_n == 2:
                    ok_split += 1
                else:
                    ok_single += 1

print("== ИТОГ ==")
print(f"ok_single={ok_single} ok_split/pair={ok_split} orphans={len(orphans)} mism={len(mism)}")
print("\n== БЕЗ УЧИТЕЛЯ (мои misread-кандидаты) ==")
for o in orphans:
    print(o)
print("\n== НЕСОВПАДЕНИЕ ПРЕДМЕТА ==")
for m in mism:
    print(m)
print("\n== ГИБКИЕ УЧИТЕЛЯ -> выведенные предметы ==")
for t, d in flexible.items():
    print(t, {s: len(v) for s, v in d.items()})
