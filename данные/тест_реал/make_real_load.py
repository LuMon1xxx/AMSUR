# -*- coding: utf-8 -*-
"""Реальный Load 5–11: часы+кабинеты из фото (build_load DATA),
учителя — настоящие ФИО из кросс-чека (who_out majority).
Вакансии там, где учителя нет. Сплиты: EN*/IN*/LA/DM."""
import importlib.util
import openpyxl
from collections import Counter

spec = importlib.util.spec_from_file_location(
    "bl", "../build_load_5-11.py")
bl = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bl)
DATA = bl.DATA

FIO = {1: "Абрамова Т.П.", 2: "Сидорович С.Б.", 3: "Потапов В.Н.",
4: "Берёзко Т.А.", 5: "Авдеёнок О.В.", 6: "Бурко А.С.", 7: "Воронова А.Ф.",
8: "Жилко Л.В.", 9: "Толстик С.М.", 10: "Лащ А.Н.", 11: "Лабикова Н.К.",
12: "Хинич К.Н.", 13: "Гордионок С.Г.", 14: "Мисевич Ю.Е.", 15: "Хильчук В.В.",
16: "Ильюшина Г.М.", 17: "Бадеева Е.В.", 18: "Наконечная Е.В.",
19: "Замена (вакансия)", 20: "Денискин Е.В.", 21: "Александрович В.А.",
22: "Воробьёва Ж.А.", 23: "Авхименко Е.С.", 24: "Крупович Ю.А.",
25: "Павлович С.А.", 26: "Шаболтиев С.В.", 27: "Коротченя С.И.",
28: "Гигель Н.К.", 29: "Свищёва В.А.", 30: "Пекарский И.В.",
31: "Апанович К.С.", 32: "Разводовская М.О.", 33: "Шустова В.Д.",
34: "Плужнова М.Д.", 35: "Касперович А.А.", 36: "Богданова К.Ю.",
37: "Новикова И.А.", 38: "Хорошко Е.Н.", 39: "Островская В.В.",
40: "Мазынская М.М.", 41: "Маляревич П.С.", 42: "Демидчик И.Е.",
43: "Липницкая М.И.", 44: "Дамашевич Е.С.", 45: "Чепикова У.В.",
46: "Шаройкина А.В.", 47: "Королёнок У.С.", 48: "Асафова Т.И.",
49: "Голубец М.С."}

# ASCII-tag -> короткий тег сетки
A2S = {"RU": "РусЯ", "RUL": "РусЛит", "BY": "БелЯ", "BYL": "БелЛит",
"EN": "Англ", "EN*": "АнглS", "ENP": "АнглП", "ENB": "АнглБ",
"MA": "Матем", "PH": "Физика", "CH": "Химия", "CHP": "ХимП", "CHB": "ХимБ",
"BI": "Биология", "BIP": "БиоП", "BIB": "БиоБ",
"HI": "ИстРБ", "HIB": "ИстБелБ", "HIP": "ИстБелП", "HIK": "ИстРК",
"OB": "Обществ", "OBP": "ОбществП", "OBB": "ОбществБ", "WH": "ВсИст",
"GE": "География", "IN": "Информатика", "IN*": "ИнфS", "PE": "Физра",
"LA": "ТрудS", "DM": "ДМП_S", "AR": "Иск", "AR2": "Черчение",
"CM": "ЧиМ", "OBZ": "ОБЖ", "CC": "КлЧас", "AS": "Астрон",
"РусЛит[?]": "РусЛит"}
# короткий тег -> официальное имя (ключи DATA build_load)
S2O = {"РусЯ": "Русский язык", "РусЛит": "Русская литература",
"БелЯ": "Белорусский язык", "БелЛит": "Белорусская литература",
"Англ": "Английский язык", "АнглS": "Английский язык",
"АнглП": "Английский язык (проф)", "АнглБ": "Английский язык (база)",
"Матем": "Математика", "Физика": "Физика", "Химия": "Химия",
"ХимП": "Химия (проф)", "ХимБ": "Химия (база)",
"Биология": "Биология", "БиоП": "Биология (проф)", "БиоБ": "Биология (база)",
"ИстРБ": "История Беларуси", "ИстБелБ": "История Беларуси (база)",
"ИстБелП": "История Беларуси (проф)", "ИстРК": "История РБ в контексте мировой истории",
"Обществ": "Обществоведение", "ОбществП": "Обществоведение (проф)",
"ОбществБ": "Обществоведение (база)", "ВсИст": "Всемирная история",
"География": "География", "Информатика": "Информатика", "ИнфS": "Информатика",
"Физра": "Физическая культура и здоровье", "ТрудS": "Трудовое обучение",
"ДМП_S": "ДМП", "Иск": "Искусство", "Черчение": "Черчение",
"ЧиМ": "Человек и мир", "ОБЖ": "Основы безопасности жизнедеятельности",
"КлЧас": "Классный час", "Астрон": "Астрономия"}

# who_out majority: (класс, ASCII) -> [(T, n)]
import re
maj = {}
for ln in open('who_out.txt', encoding='utf-8'):
    m = re.match(r'(\S+) (\S+): (.*) \(cells=\d+\)', ln.strip())
    if not m:
        continue
    cls, tag, rest = m.groups()
    pairs = re.findall(r'T(\d+)=(\d+)', rest)
    maj[(cls, tag)] = [(int(t), int(n)) for t, n in pairs]

SPLIT_SUBJ = {"Английский язык", "Информатика", "Трудовое обучение", "ДМП",
              "Английский язык (проф)", "Английский язык (база)"}

# Производные перекрытия: 21.09 ночь — к/ч 5б в Чт ведёт Авдеёнок (к/ч:5б
# в проверенном файле), к/ч 5в в Чт — Королёнок (исправление пользователя).
OVERRIDE = {("5В", "Классный час"): [(47, 1)], ("5Б", "Классный час"): [(5, 1)]}

# Проверка покрытия: каждый предмет DATA обязан найтись в S2O
_all_official = set(S2O.values())
for _cls, _subs in DATA.items():
    for _s in _subs:
        assert _s in _all_official, f"DATA subject w/o mapping: {_s}"

rows = []
notes = []
vac = Counter()
for cls, subs in DATA.items():
    for official, (hours, room, old_split) in subs.items():
        # найти ASCII-тег по official
        cand = [a for a, s in A2S.items() if S2O.get(s) == official]
        key = (cls, cand[0]) if cand else None
        found = maj.get(key, []) if key else []
        if (cls, official) in OVERRIDE:
            ov = OVERRIDE[(cls, official)]
            notes.append(f"{cls} {official}: override {[FIO[t] for t, _ in ov]} (документировано)")
            found = ov
        if found:
            top = sorted(found, key=lambda x: -x[1])
            teacher = FIO[top[0][0]]
            teacherB = FIO[top[1][0]] if len(top) > 1 else None
        else:
            teacher = "ВАКАНСИЯ"
            teacherB = None
            vac[(cls, official)] += 1
        is_split = official in SPLIT_SUBJ
        if is_split and teacherB is None:
            # пара из данных кросс-чека: второй по числу клеток того же сплита
            teacherB = "ВАКАНСИЯ (пара)"
            notes.append(f"{cls} {official}: split, TeacherB вакансия")
        rows.append((cls, official, hours, teacher, "A/B" if is_split else "",
                     teacherB if is_split else "", room or ""))
        if not found:
            notes.append(f"{cls} {official} ({hours}ч): учителя нет — ВАКАНСИЯ")

wb = openpyxl.Workbook()
ws = wb.active
ws.title = "Load"
ws.append(["Class", "Subject", "HoursPerWeek", "Teacher", "Split",
           "TeacherB", "Room", "UnavailDays", "UnavailSlots"])
for r in rows:
    ws.append(list(r))
out = "РеальнаяНагрузка_5-11.xlsx"
wb.save(out)
print(f"rows={len(rows)} vacancies={sum(vac.values())} -> {out}")
open('real_notes.txt', 'w', encoding='utf-8').write(chr(10).join(notes))
print('notes:', len(notes))
