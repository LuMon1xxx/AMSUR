# PLAN — пакеты итерации «лучше человека» (23.09.2026)

## Пакет 1 — doubles-adjacency терм (главный рычаг)
Файлы: RuleCatalog.cs (код + Version 6→7 + DefaultWeight + WeightRange),
SoftUnits.cs (DoublesScattered), SoftEvaluator.cs (полный пересчёт),
SearchIndex.cs (_dblSlots + Insert/Remove + SoftDelta + swap),
QualityExplainer.cs (HumanName + CountUnits + Explain + Compare),
FlexP2Tests / ActiveDayTests-образец → новый DoublesAdjacencyTests.cs
(юниты + SoftEvaluator + паритет 200 ходов + swap-паритет + Explainer).
ТЗ: формула §1 decision; дефолт 10, диапазон 0..100; детерминизм; Hard не трогать.
Приёмка: новый тест зелёный, паритет сошёлся, сьют зелёный, замер таблицей (ожидание −15..−25 от 265).

## Пакет 2 — active-day связка (без смены дефолта)
Файлы: TeacherDayLns.cs / GreedyPlacer.cs — без смены логики, только замеры:
topK/restarts сетка + compact-старт + LNS на фиксированных сидах, gapsFirst=off.
Приёмка: 204 → ≤181 учителе-дня при тех же уроках; failedDays=0, pupilHard=0; таблица.

## Пакет 3 — отбор по дырам + multi-seed + полный прогон
Файлы: оркестратор Top-5 (min-gaps), ScratchGen (перезапись ФИНАЛа — только по команде),
перегенерация АМСУР_движок_по_дням/. Best-of-3 seeds.
Приёмка: −10..−20 бесплатно; ФИНАЛ hard=0, pupilHard=0; сеточная метрика + парное сравнение + СанПиН-аудит.

## Пакет 4 (запасной, триггер >140) — full-day ruin
Рушить целые дни топ-рваных учителей (не топ-16 дней). Отдельное решение + замер.

Зависимости: 1 → 2 → 3 → (4). Параллелить нельзя (один движок, один ФИНАЛ).
Каждый пакет: тесты + замер таблицей (конфиг | дыры | failedDays | pupilHard | soft).
