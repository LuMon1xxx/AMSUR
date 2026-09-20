# DECISION Wave-2: чистка + аудит + багфиксы + гибкость (15.09.2026)

> Синтез — оркестратор (без nemotron-агентов по требованию пользователя).
> Вход: S1 Ground-Truth (cp-analyst), build 0 errors, сьют 258/259 (1 KNOWN-FAIL).
> Предыдущая волна: .opencode/compose-plus/decision-r1r9.md (R1–R9, Flex P1–P5, A/B).

## S5 DECISION CHECKLIST

1. **Отклонённые предложения и почему:**
   - «Чинить solver ради MidSchoolTight_StandardRun_Feasible сейчас» — ОТКЛОНЕНО.
     Тест новый (не входит в 253/253 автора), time-boxed (~4с/seed при бюджете 12с),
     greedy 986/994 — выход ранний, причина не в бюджете. Слепая правка solver
     нарушает «не переписывай работающий код». Вердикт: KNOWN-FAIL + карантин,
     корень — отдельной задачей с профилированием фаз.
   - «git rm --cached bin/obj (2190 tracked) сейчас» — DEFERRED до подтверждения:
     дифф на 2190 файлов необратим в смысле истории, хотя безопасен для дерева.
   - «Весь Этап 4 целиком» — ОТКЛОНЕНО как скоуп: Splits v2 / 6-й день / WeekType /
     DnD+Undo — это отдельные EPIC. В волну берутся только тонкие пробросы
     (F2 DayOff в импорт — домен уже готов).
2. **Противоречия ролей:** неприменимо — ролевые агенты не запускались
   (запрет nemotron). Синтез из фактов S1 + WORK_LOG + прямое чтение кода.
3. **Собственные решения (OWN SYNTHESIS):**
   - Карантин perf-теста вместо Skip-молчания: тест остаётся, помечается
     категорию TIGHT, идёт в KNOWN-FAIL с записью в WORK_LOG (честный красный gate).
   - _archive/<что>-20260915: раздельные датовые папки samples / builds.
   - B3/B1/B2 — verify-only (уже сделаны в A1/P3: Task.Run, StaticResource, SQLite-персист).
4. **Остаточные риски:** см. risks-wave2.md (R-W2-1..R-W2-4).
5. **UI ↔ архитектура:** HTML-экспорт — новый метод рядом с ExportGrid, без смены
   слоёв (Application, ClosedXML уже в прод-пути; HTML — чистый string-builder).
   DayOff — только входные колонки → существующие AllowedDays/Unavailability.
6. **Бизнес-логика ↔ UX:** fuzzy-матчинг — только импорт (предложение замен,
   fail-loud сохраняется); DayOff — hard-ограничение, валидатор уже умеет
   (ProblemBuilder IsForbidden). Инварианты INV-01..INV-09 не меняются.
7. **Трассировка требований:** Этап 1 → P-CLEAN (готово); Этап 2 → P-AUDIT (доки);
   B1 verify, B2 verify, B3 verify, B4 → P-B4, B5 → P-B5, B6 verify, B7 → P-B47;
   F1 backlog, F2 → P-F2, F3 backlog (ONLY уже есть), F4 backlog, F5 backlog,
   F6 backlog (аппр. в SUBJECTS_RB), F7 backlog (Preview/Commit уже есть).

## Заморозка

- P-CLEAN: только перемещения в _archive, git mv для tracked. Без удалений.
- P-AUDIT: только правки цифр/статусов + новые D-записи. Историю не переписывать.
- Код-пакеты: каждый = тест + замер + WORK_LOG. Красный gate (R-W2-1) остаётся
  красным до отдельной задачи по solver; новые пакеты не должны добавлять падений.
