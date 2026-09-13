# АМСУР V2 — MASTER_PLAN (живой рабочий документ)

> Статус: S5 SYNTHESIS заморожен 12.09.2026. V2 = эволюция V1, не переписывание с нуля.
> EPIC-H (13.09.2026): QUALITY OPTIMIZATION COMPLETE — READY FOR UI (Real 1200: 5195→1420, Hard=0, сьют 150/150).
> EPIC-I (13.09.2026): STUDENT COMPACTNESS COMPLETE — 0 окон/старт≤2 (Real 1196: Feasible Hard=0 ~4–6с, xlsx чистый, сьют 163/163).
> EPIC-J (13.09.2026): TEACHER-GAP SPLIT + FLEXIBLE SETTINGS COMPLETE — ordinary/cross-shift разделены (RuleCatalog v3, cross вес 2), профили+персистентность+профиль-селектор в UI, 1–4 классные учителя, targeted-LS; Real 1196: Feasible Hard=0, student 0/0, ordinary 310→200 (−35%), сьют 184/184.
> Источники: `AMSUR_PLAN_FOR_AI_AGENT.md` (спецификация), `AMSUR_PLAN_FOR_USER.md` (продукт),
> аудит `D:\LuMon1x\Pr_Auto` (EduSchedule V1), Critique Report (red team).
> Триаж: задача крупная (архитектура+solver+UI+персистентность) → Compose+.
> Классификация brainstorming: **Architectural**. Approval-gate адаптирован: пользователь дал автономию,
> поэтому ворота = этот документ + DECISIONS.md, без блокировки на каждый шаг (см. §35, §5.2 user-gate только для необратимого).

## S5 DECISION CHECKLIST (обязательный, заполнен письменно)

1. **Отклонённые предложения и почему:**
   - «Новый enum 5 статусов» — ОТКЛОНЕНО: `SolverStatus{Unknown,Optimal,Feasible,Infeasible,ModelInvalid}+WasCancelled` уже покрывает; новый enum ломает маппинг без пользы. Вместо этого — маппинг-хелпер `ToUserStatus()` на уровне Application/UI.
   - «PriorityBand HighPenalty в P0» — ОТКЛОНЕНО для P0, DEFERRED в P1: ломает `HARD_WEIGHT=maxSoftTotal*10+1` и golden-тесты, требует A/B на реальных школах. P0 оставляет Hard/Soft + веса.
   - «Top5View до архива» — ОТКЛОНЕНО: UI на несуществующем `IIncumbentStream`-контракте = mock trap. Порядок инвертирован: сначала spike архива, потом UI.
   - «Abstractions ради абстракций» — ОТКЛОНЕНО в предложенной форме: проблема не отсутствие интерфейсов, а инверсия `Infrastructure→Application`. Заменено инверсией зависимости + тестом на зависимости в CI.
   - «Staged objective сейчас» — DEFERRED в P1: без фиксации весов и плотностно-честных фикстур замер бессмыслен.
   - «Wizard subjects-first как перестановка двух шагов» — ОТКЛОНЕНО как сформулировано; заменено графом зависимостей + версионированным draft (P1).
2. **Противоречия ролей → вердикты (C1–C7 из критики):**
   - C1 streaming vs детерминизм → ПОБЕДИЛ детерминизм: дефолт repro-профиль, streaming opt-in; архив хранит seed/workers/RuleCatalogVersion.
   - C2 staged vs фиксированные веса → сначала фиксация весов (A/B), потом staged.
   - C3 single-active vs кандидаты → РАЗДЕЛЕНИЕ сущностей: `Candidate (volatile)` → `Version (persist, single-active)` только через `AcceptSchedule`.
   - C4 teacher-maxperday vs транзакция → сначала решение L6 (freeze как Hard + ADL-тест), потом транзакция.
   - C5 Repair локально vs глобально → Repair = Partial с MaximumStability+disruption, не новый движок.
   - C6 wizard порядок → граф зависимостей, не хардкод.
   - C7 «эволюция» vs объём → P0-FREEZE: builder/solver/validator только чтение+тесты; фичи по одной за флагами.
3. **Собственные решения (OWN SYNTHESIS):**
   - P0-freeze + 6 маленьких P0-шагов (см. §31) вместо параллельных рефакторов — из критики R3/R4/R7.
   - `Candidate volatile → Version persist` разделение (C3) — синтез architect+critic.
   - Excel split roundtrip в P0 (никто из ролей не предложил, критик поднял как R7) — включаю как P0-блокер.
   - Бенч-стенд с фиксацией плотности 70/85/97% + `workers=1+seed` — синтез architect+critic.
4. **Остаточные риски:** см. §39 + `risks.md` (R1–R10 из критики). Триггеры: падение миграции на грязной БД → data-repair; flood UI из callback → троттлинг 2–4 Гц; дрейф evaluator → паритет-gate.
5. **UI ↔ архитектура:** Top5/график живут только на `IIncumbentStream{Phase,BestScore,Counter}+throttle`; Editor preview — только `IncrementalEvaluator` (без solver); Accept — только транзакция+FullValidator. Компоненты UI ↔ модули: Top5View↔CandidateArchive, Editor↔IncrementalEvaluator+ChangeSet, Generate↔SolverRunner, Diagnostics↔ConflictAnalyzer+Relaxation.
6. **Логика ↔ UX:** каждый UX-сценарий покрыт: generate→two-phase+best-so-far; forbidden move→PlacementValidator причина; penalty→SoftEvaluator breakdown; infeasible→assumption-core+trials; repair→Partial+disruption. `teacher-maxperday` Hard заморожен и показан в UI как «строго».
7. **Трассировка требований:** все P0-требования SPEC §§1–8,14–16,19,23,29,31–34 покрыты задачами §32; P1 (Top-5/diversity/progress) → §31 P1; P2–P4 → §31. Не покрыто в P0 (осознанно): staged objective, PriorityBand, wizard-reorder, виртуализация (только замер), юрсверка SanPin (только бейдж).

---

## 1. Цель проекта

Корректный, проверяемый, реально полезный инструмент составления школьного расписания (Беларусь):
correct > measurable > explainable > improvable > diverse Top-5 > safe manual edit > honest impossibility > evolvable.
Конкурс Технопарка: ввод школы → Generate → first feasible → улучшение → Top-5 → breakdown → ручная правка → экспорт.

## 2. Архитектурные принципы

1. Correctness first: ни одно Accepted-расписание без `FullValidator Hard==0`.
2. Hard ≠ большой штраф: hard = constraints+validator gate; soft = objective terms.
3. Solver не источник истины: solver предлагает, validator распоряжается.
4. Эволюция V1, не big-bang: KEEP ядра, REFACTOR за флагами, P0-freeze solver/validator/builder.
5. Детерминизм по требованию: `workers=1+seed` repro-режим; дефолт может быть многопоточным, но архив хранит seed/workers.
6. Маленькие транзакции: один `SaveChanges`/транзакция на Accept; single-active один source of truth.
7. Три реализации семантики (solver/validator/incremental) — с паритет-gate в CI.
8. No mock trap: UI только на существующем контракте.
9. FACT/ASSUMPTION/HYPOTHESIS/MEASURED/UNVERIFIED разделены (см. §36).
10. YAGNI: нет cloud/multi-user/web/mobile в V1.

## 3. Требования (сводка SPEC)

- Long-running: feasible → best-so-far → Top-K → cancel-safe. Терминология «лучшее найденное» vs «оптимальность доказана».
- Wizard progressive disclosure; дефолт без CP-SAT/seed/weights.
- Shifts, subgroups N, sync shared-start, rooms Universal/Preferred/Required/Forbidden, teacher фиксирован.
- Profiles STANDARD/STUDENT_FRIENDLY/TEACHER_FRIENDLY/NORMATIVE/CUSTOM как данные.
- SQLite single-PC, atomic accept, Excel/PDF (классы/учителя/кабинеты), backup VACUUM INTO.
- DoD 13 пунктов SPEC §38.

## 4. Ограничения и инварианты (авторитетные)

- INV-01: `HardViolations==0` для Accepted (gate во всех путях: generate/move/swap/undo/export/accept).
- INV-02: учитель класса/предмета фиксирован (`TeacherId` не переменная solver).
- INV-03: sync: одинаковый start + дизъюнктный состав + разные teachers.
- INV-04: `teacher-maxperday` = Hard (FROZEN до решения L6; ADL-тест).
- INV-05: смена класса Hard; учителя/кабинеты сквозные.
- INV-06: полнота размещения (placement-count) Hard.
- INV-07: relaxation/what-if никогда не Accepted (отдельный тип).
- INV-08: timeout ≠ infeasible; отмена сохраняет best-so-far.
- INV-09: `HARD_WEIGHT = maxSoftTotal*10+1`, `long/checked`, веса заморожены в P0.
- INV-10: single-active: один source of truth (`ScheduleVersion.IsActive`; решить в P0, см. D-03).

## 5. Анализ старого проекта (сжато; полный — в отчётах S1)

- V1 EduSchedule: .NET10, WPF+CommunityToolkit.Mvvm, EF Core+SQLite (30 DbSet, 6 миграций), OR-Tools 9.15, IntVar `slotPos+Element` (не Bool-матрица), two-phase A/B с hints, `PlacementValidator` авторитетный, `SoftEvaluator` scalar+breakdown, `IncrementalEvaluator` O(k) с паритет-тестом, subgroups first-class (SyncGroupId per hour, GroupParents-фикс), freeze/partial/versions, dry-run импорт, Excel 4 листа+PDF QuestPDF, backup 18мс/restore 11мс, ~160 тестов+3 perf, smoke `--smoke`.
- Сильное: ядро 9/10, тесты, validator, subgroups, incremental, persistence-скелет, диагностика assumption-core sufficient-честная.
- Известное из REPORTS: bottleneck first-feasible на 97% плотности (Medium 350/Large 840 за cap), Excel split roundtrip сломан (UNIQUE), flake `WholeClassOntoSubgroupSlot` ~1/7, SanPin UNVERIFIED, грид не измерен, дистрибуция/подпись/QuestPDF-лицензия не covered, тёмная тема P2.

## 6. Что переносим (KEEP)

- [ ] CP-SAT постановка IntVar+Element+NoOverlap+Cumulative (CpModelBuilder логика)
- [ ] `PlacementValidator` как авторитетный FullValidator
- [ ] `SoftEvaluator`+`RuleCatalog` (scalar+stable breakdown)
- [ ] `IncrementalEvaluator` (DnD <50мс)
- [ ] Subgroup-модель (SyncGroupId, GroupParents, guards)
- [ ] Persistence SQLite+EF+backup VACUUM INTO (механика)
- [ ] Тестовый корпус 160± (MiniYear/TestSeed/SolverScenarios)
- [ ] WPF/MVVM каркас, Theme, DnD-ядро, Swap, ChangeRoom, диагностика presentation, Import dry-run

## 7. Что переписываем (REFACTOR)

- [ ] GenerationService → тонкий use-case + streaming (P1)
- [ ] DiagnosticService выделить из GenerateCore failure-path + починить ungated-cumulative слепое пятно
- [ ] ScheduleEditorService: ChangeSet-запись, score-delta/affected (P1)
- [ ] Freeze/Partial/Versions → выделить `ScheduleCandidateArchive` volatile (P1, spike сначала)
- [ ] Обмен/экспорт: чинить Excel split roundtrip (P0!)
- [ ] WPF: split ScheduleEditorViewModel, человеческие имена компонентов, порядок DataView, ExportDialog виды (P1)
- [ ] CpModelBuilder split только после golden-тестов (P1)
- [ ] Infrastructure→Application инверсия (P1, за флагом, полный сьют)

## 8. Что удаляем (REMOVE)

- [ ] Stale exe 08.09 в корне V1, `TempUiSeed*`-тесты
- [ ] `Versions-as-Top5` ментальная модель (историю оставить, кандидаты отделить)
- [ ] Одноразовый GenerateAsync без прогресса (заменить streaming в P1, не в P0)
- [ ] Frozen-row хак грида (после замера заменить решением)
- [ ] Hardcoded `NumSearchWorkers:8`, захардкоженные `IsHeavy>=7`, `+20 мест/×5`, `PeakDayIndexes` — в каталог/параметры (P1)

## 9. Что исследуем (INVESTIGATE)

- [ ] Flake `WholeClassOntoSubgroupSlot` → seed/карантин (P0)
- [ ] SanPin сверка с актами Минздрава (P1 юрработа; P0 только бейдж)
- [ ] Large-grid замер (P0 замер, P1 решение)
- [ ] QuestPDF лицензия перед дистрибуцией (P0 чеклист)
- [ ] Staged objective vs weighted (P1 бенч)
- [ ] Fingerprint/distance для Top-5 (P1 spike)
- [ ] Excel Splits v2 формат для N-групп (P1)

## 10. Архитектура нового проекта (целевая, эволюция)

```
Amsur.Domain                  // pure entities, guards, enums (порт V1 Domain)
Amsur.Scheduling.Core         // ProblemModel, ProblemBuilder, Validator(Full), SoftEvaluator,
                              // IncrementalEvaluator, Freeze, Disruption, RuleCatalog, ConflictAnalyzer
Amsur.Scheduling.OrTools      // HardBuilder/SoftBuilder/DiagnosticBuilder/SolverRunner (split ПОСЛЕ golden)
Amsur.Application             // тонкие use-cases: Generate, EditMove, PartialRegen, Diagnose, AcceptSchedule, Archive
Amsur.Infrastructure          // EF SQLite, Repositories, UoW+transactions, BackupService (ссылается на Abstractions, НЕ на Application)
Amsur.Wpf                     // MVVM CommunityToolkit, 5 разделов, Top5View (P1), Editor, Generate
Amsur.Tests                   // xUnit, fixtures SPEC §31, parity-gate, quarantine
```
- Зависимости: `Wpf→Application→Scheduling.Core/Domain; OrTools→Core; Infrastructure→Domain/Core(+UoW-abstractions); Application→ISolver/IValidator/IArchive` (без IQueryable в интерфейсах — спецификации).
- P0: scaffold + Domain + Core(validator/evaluator/catalog) + OrTools-runner как есть (порт) + Tests. Без split/inversion/streaming.

## 11. Domain model

- Порт V1 entities (School, AcademicYear, Shift, SchoolDay, TimeSlot, SchoolClass, StudentGroup, Teacher, TeacherSubject, TeacherUnavailability, TeacherDayOff, Room, RoomCapability, Subject, SubjectProfile, CurriculumItem, Lesson, LessonOccurrence, OccurrenceClass/Subgroup, LessonRelation, Schedule, ScheduledPlacement, ScheduleVersion, FreezeRule, ConstraintDefinition, RuleSetVersion, AppSetting, DbVersion, SchoolDayException) + новые: `ScheduleCandidate (volatile, не таблица)`, `CandidateFingerprint`, `PenaltyBreakdown`, `ValidationIssue`, `ReplacementOption`, `QualityProfile`.
- Правила: каждый класс с реальной ответственностью; без enterprise-шелухи.
- Задачи: см. §32 EPIC-A.

## 12. Persistence

- SQLite source of truth, EF Core 10, маппинг только в Infrastructure.
- P0: `AcceptSchedule` выбор single source of truth + data-repair миграция + транзакция + конкурентный тест; Excel split fix; тест зависимостей (запрет Infrastructure→Application).
- PK Guid, FK Restrict + usage-guard, `validate-before-write`, autosave tmp+Move keep-3.
- Backup `VACUUM INTO` + Verify до/после + safety pre-restore.

## 13. Solver architecture

- CP-SAT baseline сохранён (не религия; замена только через эксперимент §5 SPEC).
- Two-phase A (feasibility+tiebreak, 25%/30с) → B (full soft+hints) сохранён.
- P0: порт runner без streaming; P1: `ArchiveCallback : CpSolverSolutionCallback` только фаза B, троттлинг, in-memory.
- `SolverParameters.Build(time,workers,seed)`; repro = workers=1+seed; Diagnostic всегда workers=1 без objective.

## 14. CP-SAT formulation

- Per occurrence: `slotPos (0..P-1)` + Element→(st/en/day/ord) + interval; `roomIdx+useRoom (Sum==1)` + optional intervals; teacher фиксирован; `present[i,d]` 6n Bool.
- Hard: teacher/class/room NoOverlap(+Cumulative cap>1), sync `slotPos eq`, MaxPerDay, hard relations (SameDay/DifferentDay/SameSlot/DifferentSlot/Before/After/Adjacent/NotAdjacent), frozen.
- Soft: weighted sum `wS*sGapS+wT*sGapT+wRoom+wEdge+relations+subject+sanpin+disruption+tie`. Cap tight, checked.
- P0-FREEZE формулировки; P1 golden-тесты трёх Build до split.

## 15. Constraint model

- `RuleCatalog` (каталог+HardCapable+Version=1→bump-политика) + `RuleResolver(catalog→profile→user)` + `EffectiveRuleSet` + `DomainGuards.PhysicalRuleCodes`.
- Веса P0 заморожены: student-gap 25, heavy-edge 20, teacher-gap 10, room 5 и т.д. (см. §4 INV-09).
- UI «Строго/Желательно/Не учитывать» → Hard/Soft/Disabled детерминированно.
- P1: решить teacher-maxperday Hard vs Soft(Enforcement per-teacher), dedup heavy-edge vs sanpin-heavy-edge, пороги в каталог.

## 16. Objective/quality model

- Weighted sum P0 (лексикографии нет до бенча).
- `EvaluateDetailed→PenaltyBreakdown(Total,Components stable,ByClass/ByTeacher/BySubject,AffectedLessonIds)` + `BreakdownDiff`.
- Порядок §6 SPEC как ориентир; инверсию heavy-edge>teacher-gap НЕ чинить в P0 (только A/B в P1).
- Требование «soft не перебивает hard» — структурно (constraints vs terms + validator gate).

## 17. FullValidator (авторитетный)

- `PlacementValidator.Validate(problem,placements)→(HardViolations,Problems,Violations с IDs,SoftPenalties,Warnings)`.
- Проверяет: полноту, принадлежность к candidates, MatchesGridSpan, teacher availability/dayoff/maxperday(Hard FROZEN), occupancy whole/subgroup через GroupParents, room sweep MaxSimultaneousGroups, sync.
- Gate везде. `RelaxationResult` отдельный тип.
- P0: порт + string.Join occupancy → HashSet-ключ (фикс N>2) + ADL-тесты.

## 18. Incremental evaluator

- Срезный `Evaluate(problem,current,move)→(Allowed/Warning/Forbidden,Delta,Reasons)` без solver/CP-SAT, O(k).
- P0: порт + детерминированный property-тест без solver (200+ seed-moves, incremental delta == full recompute) в CI-gate + отдельный swap-паритет. Solver-flake в quarantine.

## 19. Top-K archive (P1, spike сначала)

- `ScheduleCandidateArchive`: snapshot ref, objective, breakdown JSON, timestamp, solver stats, profile version, fingerprint, seed.
- Приём `better-than-worst AND minDist>=thr`, иначе quality-vs-diversity замена ближайшей пары. K=5 конфиг.
- Только фаза B, in-memory ring-buffer, персист только Accept→Version. Троттлинг 2–4 Гц, бенч overhead на Large.
- НЕ в P0.

## 20. Diversity (P1)

- Fingerprint `occId→(day,slot,room)` hash; distance weighted Hamming (time 30/day 100/room 5, переиспользовать Disruption), не Jaccard с нуля.
- Фикстура `Top5Diversity`, документ подхода + бенч. НЕ в P0.

## 21. Diagnostics

- Уровни: input-validation → assumption-core (sufficient, minimized NOT minimal, workers=1, ungated-cumulative дисклеймер) → what-if trials MaxTrials=3 + relaxation-пробы.
- Статусы: маппинг `(SolverStatus,WasCancelled,HasPlacements)→FEASIBLE/INFEASIBLE_CONFIRMED/NO_SOLUTION_WITHIN_LIMIT/CANCELLED_AFTER/CANCELLED_WITHOUT` хелпером (без нового enum).
- P0: порт + честные тексты; P1: выделить из GenerateCore, эвристика cumulative-слепоты.

## 22. UI architecture (эволюция WPF/MVVM)

- Разделы: Home→SchoolData→Generate→Top5(P1)→Editor→Export; Diagnostics панель внутри Generate/Top5; Versions история внутри Editor; Settings/Backup отдельно.
- Progressive disclosure: дефолт человеческие режимы/пресеты/карточки; Advanced веса/seed/workers/solver status.
- P0: scaffold shell + Data CRUD (без Top5/графика); P1: Top5View+график на стабильном контракте, split EditorVM, Export виды, wizard-граф.

## 23. Manual editor

- Preview VALID/WITH_PENALTY/FORBIDDEN до commit (Incremental, без solver) + дельта + affected (первые N) + blocking reasons с entity-ссылками.
- Commit→пересчёт affected→ChangeSet→Undo/Redo групповые; swap атомарный; смена кабинета live; verify полным validator.
- P0: базовый move/preview/undo; P1: swap-диалог, шорткаты в тултипах, легенда/поиск/sticky header.

## 24. Local repair (P1)

- НЕ новый движок: предзаполненный Partial с MaximumStability+disruption из контекста проблемы + превью «затронет N уроков» + old/new score + hard validity + объяснение.
- Узкий scope часто infeasible → честное «не получилось» + расширение scope. Кнопка «Исправить автоматически» = проводка Partial.

## 25. Export

- Excel (Классы/Учителя/Кабинеты/Сводное, по сменам, ClosedXML) + PDF (A4 чанки кириллица QuestPDF) + шаблон/пример + JSON Canonical + atomic persist.
- Gate: FullValidator + warnings (покрытие) + отчёт.
- P0: Excel split roundtrip fix + roundtrip-тест со сплитами (блокер!). P1: виды чекбоксами, русские имена, превью.

## 26. Testing strategy (CORRECTNESS > COVERAGE)

- Фикстуры SPEC §31: TinyFeasible/Infeasible, Teacher/Class/RoomConflict, RoomSpecialization, Student/TeacherWindow, Subgroups2/3, DifferentSubgroupSchemes, Shifts, SubjectDistribution, TeacherAvailability, ReplacementTeacher, Top5Diversity(P1), LongRunning(P1), CancelAfter/BeforeFeasible, ManualMove Valid/Forbidden/SoftPenalty, LocalRepair(P1).
- Детерминизм: seed фиксирован или invariant-assertions; solver-flake в quarantine, не в gate.
- Команды: `dotnet build`, `dotnet test` (~15 мин полный), smoke `--smoke`, `--logcheck` 7/7, NoSilentCatch-gate.
- P0: TinyFeasible/Infeasible/Conflicts/Subgroups2/Shifts/ManualMove + parity-gate + ADL teacher-maxperday.

## 27. Performance strategy

- Не обещать секунды; UX feasible→improve→best→stop.
- Метрики: time-to-first-feasible, time-to-best, objective trajectory, память, conflicts/branches, validator/evaluator мс, diagnostics с.
- P0: замеры validator/evaluator/export/cold start + большой грид рендер-замер (без оптимизации). P1: бенч-стенд 70/85/97% плотности.

## 28. Benchmark strategy

- Стенд P1: фикстуры tiny/small/medium/dense/subgroup-heavy/room-constrained/teacher-constrained × плотности 70/85/97%, workers=1+seed сравнимо, метрики firstFeasibleMs/bestAt60s/gap + PhaseMs.
- Хранить в `BENCHMARKS.md`. Spike-gate «<10с на 300 occ» переформулировать под плотность.
- P0 только фиксирует текущие цифры V1 как baseline (build 156–239мс, validate 5–44мс, evaluator 1.75мс, export ~1.5с/0.5с, diagnostics 4с, cold 3с, 95→419МБ).

## 29. Security/reliability

- NoSilentCatch-gate; audit лог (Started/Stage/Error/BusinessWarning/Cancelled+OperationId); InvariantCulture для native-параметров.
- FK Restrict + usage-guard; concurrent Activate тест; corrupt БД отклоняется; safety pre-restore.
- QuestPDF лицензия + подпись/антивирус в чеклист пилота (P0, без кода).

## 30. Backups/recovery

- `VACUUM INTO` + Verify + ClearAllPools перед restore + safety-копия; UI дата/размер/путь; после restore перезапуск.
- Autosave tmp+Move keep-3 never-break-commit. Замеры 18/11мс сохранить.

## 31. Development phases (P0–P4)

### P0 — Solver correctness (сейчас; freeze solver/validator/builder)
- [ ] EPIC-A Domain+валидация (см. §32)
- [ ] EPIC-B Solver-порт feasibility (см. §32)
- [ ] EPIC-C Persistence-Accept+Excel-split (см. §32)
- [ ] EPIC-D Parity-gate+ADL+замеры (см. §32)
- Ворота P0: все Accepted Hard==0; Tiny/Subgroup/Conflict зелёные; parity-gate зелёный; flake в quarantine; split-roundtrip зелёный; грид замерен.

### P1 — Top-5 и объяснения (за флагами, после зелёного P0)
- [ ] Archive spike (in-memory, фаза B, fingerprint Hamming, троттлинг) + бенч overhead
- [ ] Diversity документ + Top5Diversity фикстура
- [ ] Live progress `IIncumbentStream` + Top5View + график с маркерами фаз
- [ ] Split CpModelBuilder после golden-тестов; инверсия Infrastructure→Application
- [ ] Веса A/B на реальных школах; teacher-maxperday Soft-опция; пороги в каталог
- [ ] Wizard-граф; split EditorVM; Export виды; Repair-проводка Partial

### P2 — Data and usability
- [ ] Wizard полный (§27 порядок из графа) + матрица нагрузки + копирование с прошлого года
- [ ] CRUD + profiles + advanced settings + trace/preview-diff
- [ ] Import-мастер + шаблоны + быстрые фиксы

### P3 — Manual editing
- [ ] Incremental полный + DnD + swap + undo/redo + freeze + local repair scope
- [ ] ChangeSet-запись + score-delta/affected-объяснение

### P4 — Reliability and output
- [ ] Persistence polish + backup UI + Excel/PDF polish + diagnostics polish + installer/подпись + SanPin юрсверка + пилот на реальной школе

## 32. Concrete task backlog (Epic→Phase→Task→Subtask, чекбоксы)

### EPIC-A — Domain + Constraints + Validation (P0) — ПРОГРЕСС 12.09.2026: A1–A6 ядро готово, 9/9 тестов зелёные
- [x] A1 Scaffold `Amsur.slnx` + проекты (Domain, Scheduling.Core, Scheduling.OrTools, Application, Infrastructure, Wpf, Tests) net10.0-windows
  - [x] A1.1 `dotnet new slnx` + `slnx add` — ФАКТ: slnx + Domain/Core/OrTools/Tests созданы и в solution; Application/Infrastructure/Wpf — в EPIC-C/P2 (reconcile: не блокер для solver P0)
  - [x] A1.2 NuGet: ORTools 9.15.6755 подключён и работает (reconcile: EF/ClosedXML/QuestPDF — в EPIC-C; лицензия QuestPDF — чеклист D5)
- [x] A2 Порт Domain entities + Guards + Enums — ФАКТ: EntityBase/Enums/School/SchoolActors/Curriculum/Schedule + PhysicalRuleCodes.EnsureSeverity; DomainGuardTests покрыт через ValidatorTests.PhysicalRule_CannotBeSoft. RECONCILE-отклонение от плана (см. D-12): упрощённая V2-модель без Lesson/SchoolDay/TimeOnly-сетки/SubjectProfile/OccurrenceClass — осознанно для P0, полный порт V1-деталей — P1 при необходимости
  - [x] A2.1 базовые entities (компилируются без EF-атрибутов)
  - [x] A2.2 sync-guards в ProblemBuilder (per hour-instance SyncGroupId + distinct teachers) + Subgroups2 тесты
- [x] A3 RuleCatalog + Profiles — ФАКТ: веса FROZEN (student 25/heavy 20/teacher 10/room 5/maxperday 15/relation 15) + NeedsConfirmation(sanpin-*) + HardWeight; RuleResolver/профили-overrides — P1 (reconcile: для P0 достаточно frozen-констант)
  - [x] A3.1 каталог + Version=1 + SanPin UNVERIFIED-маркировка
  - [ ] A3.2 профили как overrides — ОТЛОЖЕНО в P1 (не нужно для P0-корректности)
- [x] A4 ProblemBuilder — ФАКТ: `ProblemBuilder.Build(ProblemInput, SolverOptions?)` (расширение Hours→Occurrence, сплит A/B с SyncGroupId per-hour, DayOff/Unavailability-предфильтр, sync-пересечение, пустой домен→ModelInvalid-ошибки) + 4 теста
  - [x] A4.1 порт-адаптация + TinyFeasible build (reconcile: адаптирован под упрощённую V2-модель вместо дословного порта V1 TimeOnly-сетки — D-12)
- [x] A5 FullValidator — ФАКТ: `PlacementValidator` (полнота, shift-domain, teacher/group/room, whole-vs-subgroup, maxperday-Hard, sync) + occupancy-ключ без string.Join (сравнение через GroupId??ClassId + whole-блокировка, N-безопасно) + ToUserStatus-маппер (D-10)
  - [x] A5.1 портировано (Teacher/Class/RoomConflict красные, TinyFeasible Hard==0)
  - [ ] A5.2 Subgroups3/N-тест — ОТЛОЖЕНО в P1 (validator N-готов структурно; ингест N — Splits v2 P1)
- [x] A6 SoftEvaluator — ФАКТ: scalar + stable breakdown по всем кодам каталога (student/teacher gaps, subject-maxperday) + Delta; веса frozen
  - [x] A6.1 StudentWindow/TeacherWindow дельты + Total==sum Components

### EPIC-B — Solver feasibility (P0) — ПРОГРЕСС 12.09.2026: B1–B3 ГОТОВО, 23+1 тестов зелёные
- [x] B1 OrTools runner — ФАКТ: `Amsur.Scheduling.OrTools.OrToolsSolver` (IntVar x→Element→t/d, teacher/class/room-Hard, sync-eq, MaxPerDay-реификация, two-phase A hard-only 25%/30с + B proxy-objective hints, IncumbentCallback best-so-far по SoftEvaluator, ct→StopSearch, FullValidator-gate с VALIDATOR REJECTED, PhaseMs, WasCancelled). SolverStatus — единый из Domain (D-10, дубликат удалён)
  - [x] B1.1 CpModelBuilder-адаптация FROZEN-логики без split (TinyFeasible feasible <<5с; Small90 feasible 46с)
  - [x] B1.2 SolverParameters в SolverOptions + InvariantCulture + workers/seed (repro-тест workers=1+seed зелёный)
  - [x] B1.3 GenerationService-тонкий порт ОТЛОЖЕН в EPIC-C (runner + gate уже покрывают P0-требование; сервис-обёртка — с persistence)
- [x] B2 IncrementalEvaluator — ФАКТ: скоуповые Hard-проверки + soft-дельта полным пересчётом (паритет по построению, D-08) + sync room-only разрешён + Forbidden-Room Hard
  - [x] B2.1 ManualMove Valid/Forbidden/SoftPenalty/SyncHalf тесты (4 теста)
- [x] B3 Фикстуры P0 — ФАКТ: ValidatorTests 9 + ProblemBuilderTests 4 + EditorTests 5 (вкл. parity-200) + SolverTests 5 (Tiny/repro/dense-infeasible/cancel/sync) + BenchmarkTests Small90
  - [x] B3.1 MiniYear-аналог покрыт builder-хелперами в тестах (seed фиксированы: 42/7/1/3/11/1234)

### EPIC-C — Persistence + Exchange (P0) — ПРОГРЕСС 12.09.2026: C1–C3 ГОТОВО
- [x] C1 SQLite-приёмка — ФАКТ: `IScheduleStore` в Core (направление Application→Core, Infrastructure→Core) + `AcceptScheduleService` (validator-before-write) + `SqliteScheduleStore` (raw Microsoft.Data.Sqlite, БЕЗ EF — D-13): Schedules/Placements/Versions, partial unique index ux_versions_single_active, BEGIN IMMEDIATE Serializable, gate+recheck внутри транзакции, RepairAsync (дубли active версий/расписаний → max Number + журнал)
  - [x] C1.1 single source of truth `ScheduleVersion.IsActive` + data-repair (тест Repair_DuplicateActive зелёный)
  - [x] C1.2 Accept-транзакция + конкурентный тест (5 parallel → ровно 1 active, Number=5, active validator-clean)
- [x] C2 Excel split roundtrip — ФАКТ: `ExcelLoadExchange` (ClosedXML 0.105.1): сплит = ОДНА строка (Split=A/B + TeacherB), симметричный Export/Import, строгие ошибки (split без TeacherB, TeacherB без флага, часы ≤0); 3 теста зелёные (roundtrip record-equality, 2 reject-теста)
  - [x] C2.1 формат исключает класс V1-бага (дубли CurriculumItem) по построению; привязка имён→Id — P2
- [x] C3 Backup — ФАКТ: `SqliteBackupService` (VACUUM INTO + integrity_check до/после + safety pre-restore + ClearAllPools + corrupt-reject); 2 теста зелёные

### EPIC-D — Gates + Measurements (P0) — ПРОГРЕСС 12.09.2026: D1+D2 готовы, остальное в работе
- [x] D1 Parity-gate — ФАКТ: `Parity_IncrementalDelta_EqualsFullRecompute_200Moves` (seed 42, без solver) зелёный в CI-наборе
- [x] D2 ADL teacher-maxperday Hard — ФАКТ: `TeacherMaxPerDay_FrozenAsHard_D4` зелёный
- [ ] D3 Quarantine flake `WholeClassOntoSubgroupSlot` (seed quarantine, не gate) — НЕ ТРЕБУЕТСЯ в V2 (V1-flake solver-недетерминизма; V2 solver-тесты детерминированы workers=1+seed; при появлении flake — карантин по D-08)
- [x] D4 Замеры P0 — ГОТОВО: Small90 solver 46с + PerfTests 300 occ (validate+soft+100×incremental суммарно <1с; gates validator<5с, soft<5с, incremental<50мс/move) → BENCHMARKS.md. Medium/Dense solver + память + grid-render — P1-стенд (D-09)
- [x] D5 Чеклист пилота (без кода, зафиксирован): QuestPDF НЕ используется в V2 P0 (PDF-экспорт — P4, лицензионный риск отсутствует); NU1903-ворнинг SQLitePCLRaw 2.1.11 (транзитивный от Microsoft.Data.Sqlite 10, только bundled native lib, single-PC офлайн — принять к сведению); подпись/антивирус/установщик — P4; Flows A–K — после WPF (P2); SanPin UNVERIFIED — NeedsConfirmation в коде + бейдж в UI P2

### EPIC-E — Archive/Diversity/Progress (P1, НЕ в P0)
- [x] E1 Archive spike — ФАКТ 12.09.2026 (D-16, KEEP_PROXY): `ScheduleCandidate` (snapshot/soft/breakdown/fingerprint/seed/proxy/stats/timestamp/phase) + `ScheduleCandidateArchive` volatile K=5 (clean-only; лучше худшего → замена; иначе diversity при minDist>=100; веса day100/slot30/room5 baseline) + `SolverIncumbent` (placements/proxy/seq/elapsed/phase/seed) из callback через `ObjectiveValue()` + pipeline-тесты 7/7 + эксперимент 18 occ × seeds {11,22,33}: пул 6–7/≈19с, bestSoft=0 везде (поз. 5/6–7), proxies 79→66 строго, softs немонотонны, B-набор soft-хуже A (mean 37 vs 31 seed22), seeds 11≡33, pairwise min 35–95 dup 0, overhead 1–18мс, C==A. E2-ворота: pool-stress dense/room-constrained до Archive UI
- [x] E2 Pool Stress + Diversity Gate — ФАКТ 12.09.2026 (D-17, вердикт EXPAND_SOLVER_POOL): Dense83 100 occ/0.83 × seeds {11,22} бюджет 60с: pool 10–15, K=5 ✓, tFirst≈11–12с, tK≈14с/52с, bestSoft 230/315, rejSim=0, C==A; room-tight 12 occ: pool=1 (proof за 353мс), K ✗ (tK=-1); near-dup solver: pool=5=K, ветка не связана; синтетика: ветка срабатывает (rejSim=2), но порядок заполнения держит ранние near-dups (C min == A min); seeds dense: best различаются (seed-diversity достаточна на dense); overhead 4–38мс; memDelta ≈34–37МБ. Счётчики archive: acc/rejWorse/rejSim/rejInv. E3-scope: enumeration / multi-seed merge (нужны стабильные ключи occurrence!) / perturbation-рестарты; UI после pool≥K устойчиво
- [x] E3 Pool Expansion — ФАКТ 12.09.2026 (D-18, GO_E4): StableKey + cross-run equality; CandidatePoolMerger (dedup/quality/diversity) + PoolDiagnostics (SUFFICIENT/SMALL/LIMITED); ExcludedPairs/BannedTimes (solver-hard, validator осознанно игнорит); room-tight 3×seed → 9 unique → merged 5 best0; perturbation differs, но хуже чистого seed (mean 74 vs 57) → отклонён; budgets 10/30/60 идентичны (TTFF ~200мс, pool 6) → короткие бюджеты; стратегия: Multi-Seed short-budget + Merge
 - [x] E4 Top-5 UI + Live Progress — ФАКТ 13.09.2026 (D-19, GO_E5): `IIncumbentStream`+DTO в Application (без CP-SAT в UI) + `ThrottledIncumbentStream` 300мс (~3.3 Гц, Received/Emitted) + `GenerationOrchestrator` (multi-seed short-budget, общий архив, честные RU-статусы, cancel after/before feasible) + Top5/Card VM (бейджи, diversity-строки, без proxy/seed, Accept только лучший) + `Amsur.Wpf` Generate-экран (стадии, Stop, diagnostics свернута) + 11 E4-тестов + smoke (tiny 2 seeds → Готово/Soft0/4 варианта ~2.2с) + overhead ≈0 (BENCHMARKS.md E4)
 - [x] E5 Объяснимость качества — ФАКТ 14.09.2026 (D-20, GO_E6): staged-bench по условию НЕ запущен (E3 не показал soft-слепоты пула — DEFER, см. D-20); вместо него слой объяснимости поверх E4: `QualityExplainer` (RU-строки по ненулевым компонентам breakdown с привязкой к месту, Compare «чем хуже/лучше лучшего», QualitySummary; только то, что движок считает; sanpin — только с «требует сверки»; запрет «оптимально/невозможно/нормативно» + CP-SAT-словаря — тестами) + Card.QualityLines/QualitySummary + FromArchive(archive, problem?) (E4-путь без problem — без изменений) + Orchestrator отдаёт последнюю задачу для имён + XAML-блок «Почему такая оценка» + 12 E5-тестов + Explain 20 occ 16мс (BENCHMARKS.md E5)
 - [x] E6 График + фазовые маркеры — ФАКТ 14.09.2026 (малый P1-инкремент поверх E4/E5; отдельного scope в плане не было — бриф пользователя): `TrajectoryHistory` в Application (только полученные события IIncumbentStream, без интерполяции; разрывы при bestSoft==null; маркеры «фаза/смена запуска/финал», номера запусков без seed-чисел) + VM.Trajectory + wiring в оркестраторе (DTO/стрим не менялись) + WPF `TrajectoryChart` без сторонних библиотек (Canvas+Polyline, пустое состояние честное) в GenerateWindow + 10 E6-тестов (накопление/порядок/фазы/раны/пусто/Flush/словарь/интеграция/smoke) + история 10k за 9мс (BENCHMARKS.md E6). D-записи нет (архитектурного решения нет — переиспользование контрактов E4).

### EPIC-F — Accept/Edit/Export (пост-E6 roadmap, определён 14.09.2026: цикл «сгенерировал → принял → поправил → выгрузил»)
- [x] E7 Accept-цикл — ФАКТ 14.09.2026 (D-21): `GenerationOrchestrator.AcceptAsync` (только CanAccept-карточка; год из классов задачи; validator-before-write с честным отказом; fire-and-forget через AcceptRequested + awaitable метод для тестов) + VM AcceptResultText/ActiveVersionText + XAML-строки + `GenerateHost.Create(..., dbPath?)` (null — без персиста) + 6 тестов (persist/single-active/отказы не-лучшего и битого/без хранилища/событие). Solver/архив не тронуты.
- [x] E8 Ручная правка — ФАКТ 14.09.2026 (D-21): `ManualEditService` (Preview через IncrementalEvaluator без solver: Allowed/Warning/Forbidden + RU-вердикт с дельтой; Commit только CanCommit + полный FullValidator-gate → новая версия) + 6 тестов (valid/collision/soft-penalty/commit/refuse/sync-split). Без DnD-UI (экран редактора — backlog).
- [x] E9 Экспорт сетки Excel — ФАКТ 14.09.2026 (D-21): `ScheduleExcelExporter.ExportGrid` (класс-блоки, дни/слоты, «Предмет · Учитель»; INV-01 gate — отказ без файла) + 3 теста (читаемость/отказ/два класса). PDF/QuestPDF — нет (D5). Кнопка экспорта wired в ScheduleWindow (E11).
- [x] E10 Ввод данных — ФАКТ (D-22): `SchoolDataImporter` (LoadRow→сущности, схлопывание имён, дефолты MaxPerDay=2/MaxLessonsPerDay=6/кабинет 30×1, сплит→A/B+SplitTeachers, шаблон header-only) + `SchoolData.ToProblemInput()` + 6 тестов (схлопывание/сплит/кабинет/пусто/roundtrip/настройки).
- [x] E11 Рабочая оболочка — ФАКТ (D-22): `App.xaml` (WinExe, %LocalAppData%/Amsur/amsur.db + year.txt) + `AppSession` (импорт с fail-loud сборкой, BuildProblem, GetActive, ExportActive, EditService) + `MainWindow` (шаблон/импорт/генерация/расписание, дни/слоты) + `ScheduleWindow` (DataGrid активного, проверка/сохранение хода через E8, выгрузка, честное пустое состояние) + threading-фикс GenerateWindow (Dispatcher для прямых UI-касаний) + детерминированные OccurrenceId из StableKey (персист переживает пересборку/перезапуск) + 2 shell-теста (STA+Dispatcher) + E2E `FullLoop` (шаблон→импорт→solver→Top-5→приёмка→правка→xlsx, ~1с) + скриншот MainWindow. exe запускается (`Amsur.Wpf.exe`).
- [x] R-Stress Реальная школа — ФАКТ 14.09.2026 (D-23, закрыт E12): генератор 40 классов (1–9 АБВГ, 10–11 АБ) + 100 учителей + 40 кабинетов + план РБ-аппроксимация (21→34ч) + сплит ин.яз 5–11, сетка 6×7: build 1200 occ за 80мс ✓; старая room-модель давала нативный OOM ~107с → Skip; E12 закрыл (см. EPIC-G). Матрица — в BENCHMARKS.md Large School Solver E12.

### EPIC-G — Large School Solver (E12, бриф Solver Completion)
- [x] G1 Lane room-модель — ФАКТ (D-24): комната cap C = C линий + presence-Bool O(n·L) + NoOverlap (было O(n·R·T) → OOM): Real 6k/58k/147k вместо ~7M; Forbidden/seats в кандидатах; маппинг в RoomId обратно; RoomAudit/Solver/Validator зелёные.
- [x] G2 GreedyPlacer + fast-path Phase A — ФАКТ (D-24): Core-конструктив (sync-юниты, детерминирован seed, честно частичен, validator-clean) + multi-start (3 порядка, кап 2с): Real 1200/1200 за ~0.1с; полный+clean → пропуск CP-SAT A (TTFF Small90 1025→~90мс, total 46→23с).
- [x] G3 LocalSearch-улучшение — ФАКТ (D-24): first-improvement через IncrementalEvaluator (комната фиксирована, time-box, seed→diversity multi-seed): Real 4950→1705 (600с), Large 3815→1445, Medium 1470→890, Small 210→30; бюджет B 50/50 (кап 300с); финал через FullValidator-gate.
- [x] G4 Измерено и отвергнуто — ФАКТ (D-24): presolve-off в A (не помогло), decision-стратегия для B (0 инкумбентов что с ней, что без на ≥388 occ — оставлена), CP-SAT B как движок улучшения на масштабе (мёртв: 0 инкумбентов за 34–180с; жив на малых: Small — 8); декомпозиция по сменам не понадобилась.
- [x] G5 Контракты — ФАКТ: SolverOptions.PresolveInPhaseA (дефолт true); SolverResult.ModelStats (vars/constraints/lanes/hints/peakMb; обратно совместимо); RealityCheck-тест обновлён осознанно (proxy-A/25 → оптимум-B/0: D-15 пересмотрено — качество ведёт настоящий soft); веса FROZEN; E1–E9 зелёные.
- Приёмка §13: OOM нет; Hard=0 везде (gate + ассёрты); rooms/teachers/subgroups/capacity/Forbidden — RoomAudit + validator; timeout/cancel — пути сохранены (сьюта); бюджетные прогоны варьируют в полосе (Real@90с: 2005–3610), gate ≤ greedy стабилен.
- [ ] BACKLOG (не кододоступно сейчас или следующие этапы): мастер Import + шаблоны (P2); CRUD школы + профили (P2); полный Wizard (P2); экран Editor DnD/swap/undo + кнопка экспорта (P3); backup UI + Excel/PDF polish + diagnostics polish (P4); split CpModelBuilder + инверсия (P1, нужен golden-стенд — рискованно при freeze, отложено осознанно).
- [ ] BLOCKED внешним миром (не делать вслепую): веса A/B на реальных школах; teacher-maxperday Soft (нужно решение L6); SanPin юрсверка; QuestPDF-лицензия/подпись/установщик; пилот на реальной школе.

### EPIC-H — Quality Optimization Before UI (13.09.2026) — ГОТОВ, вердикт: QUALITY OPTIMIZATION COMPLETE — READY FOR UI
- [x] H1 Аудит качества — ФАКТ: greedy 5195 = student 625 + teacher 4030 + subj 540 (остальные коды без продьюсеров); LS доводил до 1715 = student 25 + teacher 1270 + subj 420. Остаток — teacher gaps (74%) + subj-doubles.
- [x] H2 Аудит LS — ФАКТ: accept 0.43%, forbidden 81.5%, 1 sweep > 30с, 99.9% времени в Evaluate (~0.66мс/кандидат: 2 полных пересчёта + O(n)-проходы).
- [x] H3 Time-budget — ФАКТ (seed 11): 10с→3930, 30с→2150, 60с→1715, 90с→1705, 180/300с→1705 (ранний выход ~127с — истинная сходимость single-окрестности).
- [x] H4 Seeds — ФАКТ: single best 1725 / median 1880 / worst 2070 / spread 345; VND best 1420 / median 1475 / worst 1630 / spread 210.
- [x] H5.1 SearchIndex — ФАКТ (D-25): O(k)-дельта, паритет доказан (`SearchIndexTests` 5/5); тот же оптимум 1705 за ~1.2с (ускорение eval ~119x). IncrementalEvaluator не тронут (E8-контракт).
- [x] H5.2 Swap-VND — ФАКТ (D-26): pairwise time-swaps + цикл single→swap; 1705→1420 за ~22с (seed 11); плато VND бюджет-независимо (300с→1420, wall 22.6с); greedy-seeds 1420/1235/1340. In-solver multi-start и ILS/perturbation/tabu — ОТКЛОНЕНЫ (оркестратор E4 уже multi-seed; сложность без нужды запрещена).
- [x] H6 CP-SAT B gate — ФАКТ (D-27): пропуск при occ ≥ 300 (Small90 — 8 инкумбентов жив; Medium388+ — 0). Full solve Real@10с: wall 3.9с, Hard=0, soft=1545.
- [x] H7/H8/H10/H11 — ФАКТ: FullValidator-gate везде; контракты (Candidate→Accept→Version, Top-K, QualityExplainer, E7–E9, cancel/timeout, веса FROZEN, API) не тронуты; V1 не тронут; быстрый baseline ~100мс сохранён; сьют 150/150 за 1м35с (было 136/136 за 5м03с).
- Приёмка H9/H12: Real 1200 — 5195 → 1420 (−73% от greedy; −17% от single-плато); остаток 1420 = teacher 990 + subj 405 + student 25; «оптимум» не заявляется. UI — следующий этап (запрет H17 соблюдён).
- Детали замеров — BENCHMARKS.md (EPIC-H), решения — DECISIONS.md (D-25–D-27).

### EPIC-I — Student Compactness + РБ-данные (13.09.2026) — ГОТОВ, вердикт: COMPACTNESS COMPLETE
- [x] I1 Аудит xlsx: 77 class-days late-start (до 3–4 урока) + 18 internal gaps. Root cause: late-start вес 0, student-gap 25, слепота GapOf к дублям сплитов.
- [x] I2 Требования пользователя: окон 0, старт ≤2-го урока, 5-дневка Пн–Пт, типовая СШ Минска, 2 смены (1–4,9–11 → 1-я; 5–8 → 2-я).
- [x] I3 SANPIN_RB.md + SUBJECTS_RB.md (источники; NEEDS-CHECK честно помечены; РБ-названия; суммы 21/23/29/30/32/33/34).
- [x] I4 Движок D-28: validator HARD (gap/late/cap-distinct/1-е классы ≤1 пятидневки) + веса v2 (100/100, каталог v2) + ClassSlots 5×14 + CompactRepair(match) + GradeOneBalance + polish + LNS + skip-A-при-полном-greedy + редактор-HARD/движик-мягче + паритет-superset.
- [x] I5 Баги D-28a–e (dup-GapOf, room double-book, greedy continue, кэп placements→distinct, откат scoring-greedy) — все замером.
- [x] I6 Тесты: 30 падений разобраны по категориям (семантика/фикстуры/веса), MidSlack для parity, LNS-контракт; сьют 163/163.
- [x] I7 RealSchool_Schedule.xlsx регенерирован (90с, soft 7205): python-аудит — 0 окон, 0 стартов позже 2-го, Пн–Пт, смены, РБ-предметы.
- Приёмка: Real 1196 — Feasible Hard=0 за ~4–6с (10с) / soft 7205 (90с). Остаток — teacher gaps + subj-doubles (student 0).
- Детали — BENCHMARKS.md (EPIC-I), решения — DECISIONS.md (D-28).

### EPIC-J — Teacher-Gap Split + Flexible Settings + 1–4 (13.09.2026) — ГОТОВ, вердикт: SPLIT COMPLETE
- [x] J0 Аудит: greedy teacher-units 1389 = ordinary 395 + cross 994 (72%); финал (budget 20) 736 = 325 + 411 (56%); teacher-gap ≈95–96% Soft. Root cause массы — не окна, а естественные перерывы утро+вечер (213/272 gapped teacher-days — cross).
- [x] J1 Split (D-29): GapUtils (DISTINCT везде) + `teacher-cross-shift-gap` (вес 2, 0..50) + `primary-early-start` (0, стаб) + RuleCatalog v3 + EffectiveRuleSet/RuleResolver (STANDARD/STUDENT_FRIENDLY/TEACHER_FRIENDLY/CUSTOM) + HumanScale + ShiftBands из входа + паритет трёх реализаций + Explainer-строки обеих компонент. Student HARD неторгуем во всех профилях (критика S4).
- [x] J2 Динамика (D-30): вес cross=2 сломал сходимость gate (REJECT, 2Г д5) — доказано A/B (cross=10 green). Фикс: targeted-старт LS (ordinary desc + ходы внутри дня+смены) — gate green, soft 4000, LNS failed 2→0. Новых движков нет (ILS/tabu/chain отклонены по ТЗ).
- [x] J3 Матрица cross {0,1,2,5,10} (Real 1196, budget 20, seed 11, новый fixture): w2 — ordinary 200 / cross 477 / subj 28; изоляция эффекта (w10→w2): ordinary 310→200 (−35%), cross 333→477 (+43%), old-scale 6895→7190 (+4%, метрика оставлена осознанно), student 0/0, Hard 0, ~12с везде. w0 отвергнут (метрика слепнет, 772 cross), w5 (ordinary 243 worst), w1 в шуме (176, валиден, оставлен опцией).
- [x] J4 Seed-stability (LS 5с, w2): totals 3075/3184/3249/3127/3160, spread 174 (5.5%); ord 158–170, cross 510–567 — узко.
- [x] J5 Fixture 1–4 (D-31): 16 классных (всё кроме физры/музыки) + 3 физрука + 1 музыкант; учителей 100, occ 1196; тест SingleClassTeacher; gate soft 4000→3549 (−11% от кластеризации). Глобального Hard нет (по ТЗ).
- [x] J6 GUI-архитектура (D-32): QualityHints (8 подсказок) + QualityProfiles/SQLite-store (roundtrip, single-active) + Candidate snapshot (ProfileName/Version/Hash) + solver/orchestrator rules-plumbing + AppSession.QualityRules + профиль-селектор MainWindow + строка профиля в GenerateWindow. Полный 6-табовый диалог — backlog (контракты готовы: RuleResolver/HumanScale/WeightRange/QualityHints).
- [x] J7 Сьют 184/184 за 2м10с (163 + 15 split + 5 profiles + 1 primary). V1 не тронут.
- Приёмка: Real 1196 — Feasible Hard=0, student-gap=0/late=0, ordinary −35% к w10-динамике, cross видим отдельно, завуч выбирает профиль без кода.
- Детали — BENCHMARKS.md (EPIC-J), решения — DECISIONS.md (D-29–D-32).

### EPIC-K — FULL UI (13.09.2026) — ГОТОВ, вердикт: PRODUCT COMPLETE
- [x] K1 Этап A/B: аудит WPF-контрактов + `primer.png` как композиционный референс (без декоративного сайдбара); multi-window сохранён осознанно.
- [x] K2 Этап C: visual system в App.xaml (светлая тема, #4F46E5, карточки, кнопки, бейджи).
- [x] K3 Application: GenerateModes (4 режима) + QualityRating (3 уровня + топ-3) + QualitySettingsEditor (уровни 0..4 ↔ веса, строгие read-only) + 3 теста.
- [x] K4 AppSession: режимы, CUSTOM persist + восстановление при старте, LastQuality/Lines, сводка школы, inline-ошибки импорта.
- [x] K5 Этап D/E: MainWindow-dashboard (карточки школы/профиля, CTA-баннер, 4 мини-карточки, режимы, качество, совет, быстрый старт) + импорт UX.
- [x] K6 Этап F: SettingsWindow (пресеты, Качество-слайдеры с подсказками, Предметы/Классы/Учителя, Эксперт с числами+JSON, «Сохранить „Моя школа“»).
- [x] K7 Этап G/H: GenerateWindow (строка профиль+режим), ScheduleWindow (фильтры Класс/Учитель, строка качества, правка без изменений) + 3 малых окна.
- [x] K8 Этап I: сьют 188/188, STA-smoke всех окон, exe запускается; backend-контракты только аддитивно.
- Лимиты (честно): правки сущностей session-only; day-уровень доступности учителей, doubles/adjacency, 1–4 early-knob — backlog; seed/workers — только под «Подробнее».
- Детали — BENCHMARKS.md (FULL UI), решения — DECISIONS.md (D-33).

## 33. Dependencies

- A2→A3→A4→A5→A6→B1→B2→B3 (solver зависит от validator).
- C1.1→C1.2 (single-truth до транзакции); C2 независим, но блокер для E.
- D1/D2 после A5/A6/B2; D4 после B3.
- E после всего P0 зелёного. Split/inversion после golden-тестов. UI Top5 после E1-контракта.

## 34. Acceptance criteria

- Core solver (§32 SPEC): Accepted Hard==0; нагрузка полная; нет teacher/class/forbidden-room коллизий; sync/shift корректны.
- Optimization: first feasible сохранён; лучший заменяет; отмена сохраняет best; breakdown воспроизводим.
- UX P0: CRUD без кода; forbidden объясняет почему; hard нельзя сохранить молча.
- Diagnostics: timeout ≠ infeasible; UNSAT с дисклеймером sufficient.
- Ворота P0 (§31) все зелёные.

## 35. Definition of Done

- DoD SPEC §38 13 пунктов (для пилота/конкурса) + P0-ворота §31 + parity-gate + ADL + split-roundtrip + замеры в BENCHMARKS.md.
- Красивый UI без зелёного P0 = провал.

## 36. Known uncertainties (FACT vs ASSUMPTION vs HYPOTHESIS vs MEASURED vs UNVERIFIED)

- FACT: V1 ядро/тесты/validator/subgroups/incremental работают (код прочитан).
- MEASURED: bottleneck 97% плотности; validate 5–44мс; evaluator 1.75мс; export 1.5с/0.3с; diagnostics 4с; cold 3с; 95→419МБ.
- ASSUMPTION: обычная школа 60–80% решится за секунды (подтвердить пилотом P4).
- HYPOTHESIS: streaming+archive даст разнообразные Top-5 без просадки first-feasible (проверить spike E1).
- UNVERIFIED: SanPin дефолты (бейдж, не заявлять normative); QuestPDF лицензия; установщик; большой грид.
- INVESTIGATE: flake seed; N-деления ингест; staged objective.

## 37. Decisions log (кратко; полно — DECISIONS.md)

- D-01 V2=эволюция (KEEP ядра) —证据: аудит 9/10 + 160 тестов. Последствие: P0-freeze.
- D-02 CP-SAT IntVar baseline сохранён — замена только через бенч.
- D-03 Single-active `ScheduleVersion.IsActive` (кандидат) — финализировать в C1.1 + data-repair.
- D-04 teacher-maxperday Hard FROZEN — до L6-решения с Enforcement per-teacher (P1).
- D-05 Веса FROZEN в P0 — изменения только A/B P1 + bump RuleCatalog.Version.
- D-06 Candidate volatile → Version persist (C3) — Top5 не раздувает историю.
- D-07 Excel split как один CurriculumItem+LessonSplit — чинить в P0.
- D-08 Паритет без solver в gate; solver-flake в quarantine.
- D-09 Бенч только на плотностно-честных фикстурах + workers=1+seed.

## 38. Benchmark results

### P0.5 Small90 breakdown (MEASURED 12.09.2026, workers=1 seed 11, бюджет 60с)
- builder=16мс | modelBuildA=31мс | **phaseA=1012мс** | modelBuildB=7мс | **phaseB=45033мс** | validator=0мс | soft=0мс
- TTFF≈1025мс | total≈46с | incumbents=12 (10 в B + финал) | validator-rejected=0 | objective=140
- Вывод: ~98% времени — Phase B, жгущая весь остаток бюджета без доказательства оптимальности; model build и валидация пренебрежимы. Первое feasible за ~1с (UX feasible-first уже возможен технически).

### Остальные baseline (см. BENCHMARKS.md)

- Baseline V1 (из REPORTS, перемерить в D4): Small 120 occ first 15с/total 80с/hard=0; Medium 350 cap 150с нет; Large 840 cap 240с нет; build 156–239мс; validate 5–44мс; evaluator 1.75мс; excelExport ~1.5с/parse 0.3с/import 0.5с; schedExcel 267мс/schedPdf 509мс; diagnostics 4с (cap 20с); cold 3.0с/warm 2.8с; 95→419МБ; backup 18мс/restore 11мс.
- V2 MEASURED 12.09.2026 (workers=1, seed фиксированы, стенд D-09):
  - Tiny 3 occ (2д×4сл): feasible <<1с, Hard=0, gate FullValidator пройден.
  - Small90 90 occ (5 классов×6 предм×3ч, 5д×6сл, seed 11, бюджет 60с): **Feasible, Hard=0, elapsed≈46с** (фаза A+B; firstFeasible — см. PhaseMs в SolverResult). Плотность ≈ 90/(5классов×5д×6сл)=60% на класс — в зоне ASSUMPTION «обычная школа 60–80%».
  - Остальное (validator/evaluator мс, Medium/Dense, память) — подэтап D4.
- V2 замеры — в BENCHMARKS.md (D4), затем P1-стенд.

## 39. Regression risks (R1–R10 → митигации, полно — DECISIONS.md/risks)

- R1 миграция unique-index → data-repair + backup + грязная фикстура. R2 streaming flood → троттлинг + in-memory + бенч до UI. R3 split рассинхрон → golden-тесты + запрет мёрджа. R4 смена весов → freeze + A/B + bump. R5 SanPin → бейдж + NeedsConfirmation. R6 паритет с solver → без solver + quarantine. R7 split roundtrip → C2 до архива. R8 narrow repair infeasible → Partial-проводка + превью N. R9 inversion → флаг + полный сьют. R10 график без фаз → маркеры PhaseMs.

## P0.5 Solver Reality Check (12.09.2026, ворота перед P1)

### Таблица claims: требование → поведение (класс без поведения НЕ считается)

| Требование | Реализовано | Тест | Solver | Validator | Статус |
|---|---|---|---|---|---|
| teacher collision | да | ValidatorTests.TeacherConflict | `t[i]!=t[j]` | hard | ✅ |
| class/group collision (whole+A/B) | да | ClassConflict, Subgroups2 | блокировка+whole | hard | ✅ |
| subgroup sync shared-start | да | SyncSplit hard; SolverTests shared-start | равенство t | hard+distinct teachers | ✅ |
| subgroups N>2 | fail-loud (ошибка «Splits v2») | Build_ThreeGroups | — | — | ⚠️ лимит (полная поддержка P1) |
| rooms cap/overflow | да (P0.5) | RoomBottleneck Infeasible; RoomsAssigned | rVar+sum<=cap | hard | ✅ |
| rooms Forbidden | да (P0.5) | ForbiddenRoom hard | исключение из кандидатов/ModelInvalid | hard | ✅ |
| rooms Required/Preferred-различие | нет | — | все не-Forbidden равны | — | ❌ P1 soft |
| room seats>=need | да (P0.5) | bottleneck (30>=25) | предфильтр | — | ✅ (валидатор не дублирует — P1) |
| shifts (multi-shift сетка) | нет | — | uniform grid | — | ❌ лимит P1/P2 (single-shift работает) |
| teacher availability/dayoff | да | Build_TeacherDayOff | домен | — | ✅ |
| teacher max-per-day Hard | да | ADL-тест | реификация sum<=max | hard | ✅ |
| hard relations | нет (нет producer: builder всегда `Relations=[]`) | — | игнор | игнор | ❌ P1 (недостижимо из P0-входов) |
| soft relations/room-pref/heavy/sanpin/disruption | нет | — | proxy only | gaps+maxperday only | ❌ P1 (E4) |
| frozen placements | нет (концепта нет; план — P3) | — | — | — | ❌ P3 по плану |
| cancellation | да | CancelledBeforeFeasible | StopSearch | — | ✅ |
| best-so-far | да (среди VISITED; не глобально — D-15) | Small90 solutionsFound=12 | callback min-soft | — | ⚠️ см. objective gap |
| timeout vs infeasible | да | Dense + StatusMapper | Infeasible/Unknown | — | ✅ |
| validator-rejected incumbents | да (счётчик) | Small90 rejected=0 | — | gate+recheck | ✅ |
| atomic Accept/single-active/repair | да | PersistenceTests 4/4 | — | gate до записи | ✅ |
| excel roundtrip/backup | да | Exchange 3/3, Backup 2/2 | — | — | ✅ |
| parity incremental==full | да | 200 moves | — | — | ✅ |

### Вердикты P0.5
- Bottleneck Small90 — Phase B (45с/46с); TTFF ~1с.
- Semantic gaps rooms — ЗАКРЫТЫ (D-14). Остальные ❌ — честные лимиты (недостижимы из P0-входов либо плановый P1/P3).
- Objective gap — ДОКАЗАН (D-15): proxy≠soft; E1-spike обязан ответить на alignment/diversity пула до Archive UI.
- N>2 — fail-loud вместо молчаливого Take(2).

## 40. Future/deferred (НЕ V1)

- Cloud/multi-user/web/mobile/enterprise identity; сложный replacement workflow; excessive what-if; тёмная тема P2; multi-window; вид «день» как fallback после замера; Splits v2 N-формат (P1); staged/lexicographic (P1 бенч); PriorityBand (P1); wizard-граф (P1).

---
*Следующий этап после заморозки: EPIC-A A1 scaffold → A2 Domain. Обновлять этот документ после каждого крупного этапа.*
