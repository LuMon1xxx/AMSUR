# DECISIONS.md — журнал архитектурных решений АМСУР V2

Формат: Decision / Context / Alternatives / Chosen / Why / Evidence / Consequences.

## D-01 V2 = эволюция V1, не переписывание
- Context: V1 EduSchedule зрелый (160 тестов, validator, subgroups, incremental).
- Alternatives: (a) big-bang rewrite, (b) эволюция.
- Chosen: (b) эволюция, P0-freeze solver/validator/builder.
- Why: ядро 9/10, переписывание = регресс корректности (C7).
- Evidence: аудиты S1, Critique R3/R4.
- Consequences: порт с тестами; split/inversion только P1 за флагами.

## D-02 CP-SAT IntVar baseline сохранён
- Context: SPEC §14-15 допускает замену через эксперимент.
- Alternatives: Bool-матрица, interval-пер-слот, другой solver.
- Chosen: IntVar slotPos+Element+NoOverlap как в V1.
- Why: ~10k IntVar вместо 1.5M BoolVar, teacher не переменная, sync shared-start.
- Evidence: CpModelBuilder чтение; bottleneck — свойство поиска, не баг.
- Consequences: замена только через бенч §5 SPEC (стенд 70/85/97%).

## D-03 Single-active source of truth
- Context: два флага IsActive (Schedule + Version), код признаёт "DB inconsistency: N active".
- Alternatives: Schedule.IsActive / Version.IsActive / оба.
- Chosen (кандидат): `ScheduleVersion.IsActive` — финализировать в C1.1.
- Why: версии уже несут Number/Parent/metadata + rollback-новой-версией.
- Evidence: VersionService.cs:116, Critique A3 CRITICAL.
- Consequences: data-repair миграция → filtered unique index → транзакционный Activate + конкурентный тест. Без п.2 миграция сломает пилотные БД (R1).

## D-04 teacher-maxperday = Hard (FROZEN)
- Context: план относит перегрузку к soft; код делает Hard (ProblemBuilder TeacherCap + Diagnostic INFEASIBLE-генератор + validator hard).
- Alternatives: Hard / Soft(weight) / per-teacher Enforcement.
- Chosen P0: Hard frozen + ADL-тест; P1: per-teacher Enforcement{Hard,Soft}.
- Why: смена молча ломает диагностику и публикует перегрузки (Critique L6 CRITICAL).
- Evidence: ProblemBuilder.cs:239, DiagnosticTests, TeacherDialog без флага.
- Consequences: до L6-решения solver/accept не трогают семантику.

## D-05 Веса FROZEN в P0
- Context: heavy-edge 20 > teacher-gap 10; двойной учёт heavy-edge + sanpin-heavy-edge-limit.
- Alternatives: flip сейчас / PriorityBand сейчас / freeze + A/B позже.
- Chosen: freeze; изменения только P1 через A/B на реальных школах + bump RuleCatalog.Version.
- Why: flip ломает ValidatorTests/Strict/golden (Critique L2 HIGH), PriorityBand ломает HARD_WEIGHT (L1).
- Evidence: RuleCatalog.cs:90-102, HARD_WEIGHT формула.
- Consequences: бенч staged только поверх зафиксированных весов (C2).

## D-06 Candidate volatile → Version persist
- Context: версии (история, single-active) vs кандидаты Top-5 (volatile, 5 шт).
- Alternatives: хранить кандидатов как версии / отделить.
- Chosen: отделить; Top5View работает с volatile, Versions с persist; промоут только через AcceptSchedule.
- Why: иначе раздувание истории + путаница "что активно" (C3).
- Evidence: VersionService, Critique A4/C3.
- Consequences: Archive spike in-memory, персист только Accept (R2 митигация).

## D-07 Excel split как один CurriculumItem + LessonSplit
- Context: экспорт N строк → импорт дубли → UNIQUE violation; ингест только A/B.
- Alternatives: оставить / один item + split-сущность.
- Chosen: один CurriculumItem с LessonSplit + симметричный экспорт/импорт + roundtrip-тест.
- Why: блокер архива/версионности (R7), никем не предложен кроме критика.
- Evidence: ExcelExchangeService.cs:485-486.
- Consequences: P0-блокер C2; Splits v2 N-формат — P1.

## D-08 Паритет incremental==full без solver в gate
- Context: паритет заявлен, но solver-flake даёт второй flake в gate.
- Alternatives: паритет с solver / без solver / без gate.
- Chosen: детерминированный property-тест (фиксированная problem + 200+ seed-moves) + swap-паритет в gate; solver-тесты в quarantine.
- Why: иначе эрозия CI (R6).
- Evidence: IncrementalEvaluator коммент, WholeClassOntoSubgroupSlot ~1/7.
- Consequences: D1/D3 в P0.

## D-09 Бенч-стенд плотностно-честный
- Context: Medium/Large 97% плотности за cap; обычная школа 60-80% за секунды.
- Alternatives: wall-clock на текущих фикстурах / стенд с фиксацией плотности+seed.
- Chosen: стенд 70/85/97% × tiny/small/medium/dense/..., workers=1+seed, firstFeasibleMs/bestAt60s/gap + PhaseMs.
- Why: иначе staged/веса измеряют шум (A5, R10).
- Evidence: PERFORMANCE_REPORT, bench/P2Bench.
- Consequences: P0 только baseline D4; решения P1 только со стенда.

## D-10 № UI на несуществующем контракте (no mock trap)
- Context: Top5View/график требуют IIncumbentStream которого нет; график A→B с разрывом objective вводит в заблуждение.
- Alternatives: мокать архив / ждать контракт.
- Chosen: ждать E1-контракта (троттлинг 2-4 Гц + Phase в событии); P0 оставить фазы-словами+elapsed.
- Why: Critique U5 HIGH, R2/R10.
- Evidence: GenerateViewModel, OrToolsSolver A/B.
- Consequences: порядок E1→E3, не наоборот.

## D-11 SanPin — бейдж, не верификация
- Context: каталог честно NeedsConfirmation=true, формулировки по вторичным данным, два правила Disabled-стабы.
- Alternatives: каталогизировать пороги / сверить с НПА / бейдж.
- Chosen P0: бейдж "требует сверки" + фиксация RuleSetVersion; юрсверка P4.
- Why: каталогизация без сверки прячет UNVERIFIED (L5).
- Evidence: RuleCatalog.cs:117-158, SanPinTests.
- Consequences: не заявлять normative до сверки; не включать SanPin-Heavy в Strict-дефолт.

## D-12 Упрощённая V2-модель + proxy-objective фазы B (reconcile 12.09.2026)
- Context: план §32 требовал дословного порта V1 (Lesson/SchoolDay/TimeOnly-сетка/SubjectProfile/OccurrenceClass). Фактический код V2 — упрощённая модель ((DaysCount×SlotsPerDay) + AllowedDays/AllowedSlots).
- Alternatives: (a) дословный порт V1-деталей сейчас, (b) упрощённая P0-модель + добор деталей в P1 при необходимости.
- Chosen: (b) — улучшение, не ошибка: инварианты (teacher-фикс, sync per-hour, whole-блокировка, maxperday-Hard, полнота, shift-domain) перенесены поведением, а не классами.
- Why: P0-цель — feasibility + validator-gate + parity; TimeOnly-сетка/смены-звонки/SubjectProfile не влияют на P0-семантику и добавят риск без выгоды. P0-freeze (C7) запрещает раздувание.
- Evidence: 24/24 тестов зелёные, Small90 feasible+validator-clean.
- Consequences: Phase B objective — proxy (сумма t) + best-so-far по SoftEvaluator через IncumbentCallback; точная линеаризация gaps — P1 staged-бенч (E4). Смены-звонки (TimeSlot.Start/End), SubjectProfile, Splits-v2, room-capabilities в solver — P1/P2 по мере нужды. Это решение НЕ меняет hard-семантику.

## D-13 Persistence без EF: raw Microsoft.Data.Sqlite (конфликт с планом C1 «EF Core 10»)
- Context: план требовал EF SQLite-порт (30 DbSet). Факт: P0-scope persistence = 3 таблицы пути приёмки; Domain-сущности с init-only Id и без nav-props; риск повторения V1-инверсии Infrastructure→Application через DbContext/DI.
- Alternatives: (a) EF Core + Configurations + миграции, (b) raw Sqlite + явные транзакции.
- Chosen: (b) для P0. Анализ конфликта: замена безопасна — код persistence новый, ломать нечего; поведение (atomic Accept, single-active, VACUUM-бэкап) покрыто 6 тестами.
- Why: явный BEGIN IMMEDIATE + partial unique index + VACUUM INTO без ORM-магии; интерфейс IScheduleStore в Core сохраняет возможность заменить реализацию.
- Evidence: PersistenceTests 4/4 (accept/reject/concurrent/repair) + BackupTests 2/2.
- Consequences: school-data CRUD P2 может пересмотреть (EF) через тот же IScheduleStore-подход; зависимости Application→Core, Infrastructure→Core (проверено csproj — Infrastructure НЕ ссылается на Application).

## D-14 Rooms в solver P0.5 (semantic gap закрыт, был FIX_BEFORE_P1)
- Context: P0.5-аудит доказал RED-тестами: solver игнорировал кабинеты (RoomId=null), validator не проверял Forbidden → solver-feasible + validator-valid при SPEC-infeasible (1 кабинет cap=1 на 2 вынужденно-одновременных урока).
- Alternatives: (a) оставить known limitation, (b) смоделировать rooms в solver.
- Chosen: (b). Кандидаты = исключение Forbidden + Seats>=need (need=StudentCount whole / половина subgroup); rVar через Element-домен (без table constraint); вместимость sum<=MaxSimultaneousGroups на (room,globalTime) с реификацией; пустые кандидаты → ModelInvalid; hints включают кабинет; validator: добавлен Forbidden-hard.
- Why: forbidden-room/overflow — SPEC-hard, влияют на feasibility; архив поверх room-blind solver хранил бы room-невалидные кандидаты.
- Evidence: RoomAuditTests 3/3 (bottleneck Infeasible; 2 rooms → assigned+validator-clean; forbidden → hard).
- Consequences: Required/Preferred-различие НЕ моделируется (все не-Forbidden равны; предпочтение — P1 soft); O(n·R·T) BoolVar — room-фикстуры P0 маленькие, скейлинг — P1-оговорка; проблемы без Rooms работают как раньше (RoomId=null).

## D-16 E1: KEEP_PROXY + E2-ворота (измерено, не предположено)
- Context: P0.5 доказал структурный proxy≠soft риск (A soft=25 выбран, B soft=0 не посещён). E1 измерил пул на реалистичной фикстуре 18 occ × seeds {11,22,33}, бюджет 25с.
- Measurements: пул 6–7 incumbents/≈19с; bestSoft=0 во всех seeds (позиция 5/6–7); proxies строго убывают 79→66, softs немонотонны ([75,105,10,35,0,35,0]) — «later≠better soft» подтверждён мягко; B-proxy набор soft-хуже A (seed22 mean 37 vs 31, worst 105 vs 75); seeds 11≡33 (seed-diversity слабая); pairwise min 35–95, dupRate 0; overhead архива 1–18мс (≈0); C==A (порог diversity ни разу не сработал — пул мал и разнообразен).
- Alternatives: KEEP_PROXY / ADD_OBJECTIVE_ALIGNMENT / CHANGE_TO_MULTI_STAGE.
- Chosen: KEEP_PROXY как временный P1-компромисс (измерения на тестовом классе: пул поставляет soft-0 + разнообразные наборы; proxy-смещение мягкое).
- Why: менять objective без измеренной боли запрещено (§7 E1); боли на реалистичной фикстуре нет.
- Consequences (E2-ворота, обязательно): pool-stress на dense/room-constrained (пул <K? best-soft отсутствует? near-dups давят diversity?) → если да, то ADD alignment (минимум student-gap 25) ДО Archive UI; seed как diversity-источник не работает — разнообразие только из пула/рестартов с разной структурой.

## D-17 E2: EXPAND_SOLVER_POOL (вердикт; alignment и policy не виноваты)
- Context: E2 подверг archive давлению: Dense83 (100 occ, плотность 0.83, seeds 11/22, бюджет 60с), room-tight (12 occ, 2 каб cap1 + Forbidden), near-dup синтетика, seed-trajectories на Dense30.
- Measurements: Dense: pool 10–15, K=5 достигнут, tFirst≈11–12с, tK≈14с/52с, bestSoft 230/315, rejSim=0 везде, C==A (деградации нет, uplift нет); room-tight: pool=1 (оптимум доказан за 353мс), K НЕ достигнут, tK=-1; near-dup solver: pool=5=K, ветка не связана; синтетика: ветка diversity срабатывает (rejSim=2), но порядок заполнения удерживает ранние near-dups (C min == A min); seeds dense: best различаются (seed-diversity достаточна на dense; на мелкой фикстуре E1 seeds 11≡33); overhead 4–38мс; memDelta ≈34–37МБ/запуск (GC, не лимит).
- Gate §7: триггер «archiveCount<K» срабатывает на small-tight (доказанный оптимум → короткий поток); «same pool» — только мелкая фикстура; near-dup-доминирования, деградации C vs A — нет.
- Alternatives: GO_E3_UI / FIX_ARCHIVE / ADD_OBJECTIVE_ALIGNMENT / EXPAND_SOLVER_POOL.
- Chosen: EXPAND_SOLVER_POOL. Размер пула — binding constraint (pool=1 при быстром proof), а не направление objective (bias мягкий, C==A) и не политика (ветка рабочая, C не хуже A).
- Why: UI «Top-5» с пулом 1 — честно, но бесполезно; alignment не создаёт кандидатов; FIX_ARCHIVE (order-dependence при равенстве soft) — минорный кандидат в backlog, не блокер.
- Consequences (E3-scope, без UI): эксперименты расширения пула — (a) enumeration K разнообразных (не только improving), (b) multi-seed merge (нужны СТАБИЛЬНЫЕ ключи occurrence — Guid-фингерпринты кросс-запусков несравнимы, finding!), (c) perturbation-рестарты; метрики pool/K/diversity; UI только после pool≥K устойчиво.

## D-18 E3: стратегия Multi-Seed short-budget + Merge (вердикт GO_E4)
- Context: E3 проверял single vs multi-seed vs perturbation vs budgets на 18-occ и room-tight.
- Measurements: StableKey `Class|Subject|Teacher|Group#hour` — cross-run equality доказан тестами (fingerprint/dist=0 между сборками); room-tight: 3×seed → total 9 unique 9/9 → merged 5/5 best 0, вердикт POOL_SUFFICIENT; perturbation (ban половины best, seed33): differs ✓, но pool 8 / mean 74.4 / worst 140 ХУЖЕ чистого seed22 (pool 8 / mean 56.9 / worst 95) — пользы нет; budgets 10/30/60: пулы идентичны (6, best 0, archN 5), TTFF ~200мс — бюджет сверх ~10с на этом классе ничего не даёт; PoolDiagnostics (POOL_SUFFICIENT/GENUINELY_SMALL/SEARCH_LIMITED) покрыт юнитами; BannedTimes — только search-конструкт (validator/evaluator его игнорируют осознанно: баны не доменные правила).
- Alternatives: GO_E4 / KEEP_SINGLE_RUN / ADD_PERTURBATION / REDESIGN_SEARCH.
- Chosen: GO_E4 со стратегией Multi-Seed (короткие бюджеты ~10–15с, фиксированный набор seeds) + Merge через Archive-политику на StableKey. Perturbation — отклонить (сложность без выгоды). KEEP_SINGLE_RUN — отклонить (single даёт pool<K на tight-классе). REDESIGN — не нужен (solver поставляет).
- Why: измерения по всем 6 критериям (best/mean/worst, K, diversity через merge, wall, memory ~35МБ, детерминизм seed-набора).
- Consequences: UX-цикл `first feasible → improve → accumulate across seeds → stop`; E4-UI строится на merged-архиве; стабильные ключи — обязательное условие любого кросс-запускового сравнения.

## D-19 E4: Top-5 UI + Live Progress (вердикт GO_E5)
- Context: E3 доказал Multi-Seed short-budget + Merge (D-18). E4 превращает pipeline в UI: live progress, Top-5 карточки, stop/cancel, diagnostics.
- Chosen:
  - `IIncumbentStream` + `GenerationProgressDto` живут в Application (не Core): UI-контракт отделён от CP-SAT; адаптер `IncumbentProgressAdapter` — чистая функция (DTO-рефлексия без Google.OrTools — в тестах).
  - Throttling default 300мс (~3.3 Гц, в окне 2–4 Гц), thread-safe; solver пушит на полной частоте; счётчики Received/Emitted; `Flush()` на финале/остановке.
  - `GenerationOrchestrator`: seeds последовательно, общий архив через инкрементальный TryAdd (≡ merge, порядок детерминирован); Application НЕ ссылается на OrTools — запуск инжектится делегатом `StreamingRun`, продакшн-wire только в `Amsur.Wpf.GenerateHost` (направление зависимостей сохранено).
  - Статусы RU честные: «Ищем подходящее расписание…» / «Генерация остановлена» (best+архив живы) / «Рабочее расписание не найдено» (timeout ≠ infeasible — «невозможно» запрещено тестом).
  - ViewModel (INPC, без WPF-ссылок) — в Application, тестируется в net10.0; тонкий XAML-view — в новом `Amsur.Wpf` (net10.0-windows): стадии с выделением текущей, status-area без ложного %, Top-5 без пустых карточек, Stop-кнопка, diagnostics-Expander свернут по умолчанию.
  - Карточка скрывает proxy/seed/workers/objective/seq/fingerprint (проверено рефлексией); бейджи «Лучший»/«Отличается на N%» (нормировка на DayWeight); diversity-строки по дням/времени/кабинетам; Accept только у лучшего; Open → Editor через событие.
- Measurements: overhead ≈0 (A 6338мс / B 6199мс+2мс / C 6170мс+1мс; 18 occ, budget 8с); stream 4→2; smoke: 2 seeds tiny → «Готово», Soft 0, 4 варианта за ~2.2с.
- Alternatives: VM в Wpf (отклонено — нетестируемо без windows-таргета); персист архива (отклонено — §12 «не делать»); fake progress 0→100% (запрещён ТЗ).
- Evidence: 11 E4-тестов + smoke + overhead зелёные; полный сьют — см. BENCHMARKS.md.
- Consequences: E5 (staged objective) — только при доказанной soft-слепоте пула; замечено попутно: AllowedSlots 1-based (1..SP) при 0-based днях — поведение Core, не решение E4 (тесты зафиксировали фактически); PlacementValidator бросает KeyNotFound на чужих OccurrenceId вместо issue — кандидат в риски (вне E4-scope, solver/оркестратор чужие Id не подают).

## D-20 E5: staged DEFER + слой объяснимости (вердикт GO_E6)
- Context: план требовал staged-bench «только если E3 покажет soft-слепоту пула»; бриф E5 — объяснимость качества поверх E4.
- Triage staged (по измерениям, не предположениям): E1 (D-16) — пул даёт soft-0 + разнообразие, bias мягкий; E2 (D-17) — C==A везде, binding constraint = размер пула, не направление objective; E3 (D-18) — merge даёт K=5 best 0, perturbation отклонён, бюджеты идентичны. Soft-слепота пула НЕ показана → условие E5 не сработало → staged-bench DEFERRED (не отменён: триггер — будущее измерение «пул стабильно без soft-best при K≥…»).
- Chosen (объяснимость): `QualityExplainer` в Application (без OR-Tools): HumanName всех кодов каталога; Explain — строки только по ненулевым компонентам реального breakdown (student/teacher окна с классом/учителем/днём/диапазоном уроков; повторы с нормой; прочие коды — общей строкой без выдуманных причин); Compare — дельты по кодам («хуже/лучше», +N к оценке); Summary — «Без мягких нарушений» | «Основное: …».
- Кросс-запусковость: привязка через StableKey к эталонной задаче (D-18), чужие Guid не роняют (Resolve пропускает нераспознанные; покрыто тестом CrossRun).
- Честность: запрещённый словарь (cp-sat/incumbent/proxy/fingerprint/solver/seed/оптимал*/идеальн*/невозможно/нормативн*) + hard/soft разделение (в строках качества нет «жёстк») — тестами; sanpin>0 — только с «требует сверки» (D-11); «лучший из найденных», никогда «оптимальный».
- Alternatives: (a) staged сейчас без триггера — ОТКЛОНЕНО (нарушает условие плана + P0-freeze дух, solver не трогаем); (b) имена парсингом StableKey — ОТКЛОНЕНО (дублирование формата builder; вместо этого Resolve через OccKeys); (c) Compare вместо Explain на всех карточках — гибрид: лучший Explain, остальные Compare.
- Measurements: Explain 20 occ — 16мс (gate <1с); полный сьют 87/87.
- Consequences: веса FROZEN не тронуты; E4-контракты расширены обратно совместимо (опциональный problem); персист архива/редактор — не E5.

## D-21 E7–E9: замыкание цикла Generate→Accept→Edit→Export (вердикт: конец кододоступной части)
- Context: после E6 остался разомкнутый цикл (AcceptRequested без подписчика, правки и выгрузка только в Core). E7–E9 замыкают его use-case'ами без новых экранов.
- Chosen:
  - E7: приёмка — метод оркестратора (событие окна → fire-and-forget, тесты → awaitable); учебный год выводится из классов задачи (отдельного контекста года в V2-модели нет); сервис перепроверяет внутри (двойной gate: оркестратор для честного текста + сервис); solverSettings — диагностическая строка (seed/proxy/phase/fp — НЕ в пользовательский UI).
  - E8: превью дословно из IncrementalEvaluator (его RU-причины уже человеческие — не перефразируем, чтобы не разойтись); hypo-перестроение дублирует 5 строк evaluator (осознанно: evaluator не возвращает hypo, менять его контракт ради E8 — лишний риск); коммит = новая версия через тот же Accept (история вместо перезаписи).
  - E9: сетка «класс-блоки × дни × слоты», ячейка «Предмет · Учитель», subgroups в одной клетке через « / »; gate до создания файла (файл не пишется при отказе); Excel only (PDF — D5).
- Alternatives: контекст года параметром оркестратора (отклонено — плодит состояние; вывод из задачи детерминирован); undo/redo в E8 (отклонено — нужен ChangeSet-журнал, это P3); кнопка экспорта в GenerateWindow без экрана редактора (отклонено — некуда класть диалог сохранения; backlog).
- Measurements: E7 6/6, E8 6/6, E9 3/3; полный сьют 112/112; операции локальные (мс; доминанта Excel — старт ClosedXML ~1с, см. C2).
- Consequences: цикл замкнут на уровне use-case; UI-редактор/визард/CRUD — backlog; внешне блокированное — в плане EPIC-F.

## D-22 Рабочая версия: ввод данных + оболочка + детерминированные Id
- Context: use-case'ы E7–E9 висели без входа (нет данных) и выхода (нет exe). Для запускаемого приложения нужны ввод + оболочка + стабильность Id между пересборками.
- Chosen:
  - Ввод — через существующий Load-формат C2 (шаблон header-only + импорт + `SchoolDataImporter`): имена схлопываются (OrdinalIgnoreCase), сущности создаются с дефолтами; сплит — одна A/B-пара на класс (DMP — P1); вход fail-loud собирается ДО принятия данных.
  - OccurrenceId детерминированы MD5(StableKey) в ProblemBuilder: пересборки одного входа дают те же Id → актив из SQLite совпадает с новой сборкой после перезапуска (имена стабильны). Дубли StableKey (два класса с одним именем) — громкая ошибка сборки вместо падения словарей. SyncGroupId остался случайным (per-build конструкт).
  - Оболочка — мультиоконная (Main + Generate + Schedule), чтобы не рефакторить E4-файлы; GenerateHost получил dbPath (null — без персиста); год персистится в year.txt (данные — нет, повторный импорт тех же имён даёт те же Id).
  - Threading: прямые UI-касания GenerateWindow уходят в Dispatcher (биндинги WPF маршалит сам).
- Alternatives: StableKey-колонка в Placements + миграция (отклонено — тяжелее, трогает интерфейс store); GUID Id + хранение OccKeys (отклонено — то же); CRUD-редактор вместо импорта (отклонено — на порядок больше; импорт закрывает ввод).
- Measurements: E10 6/6, shell 2/2, E2E ~1с; полный сьют 123/123; скриншот MainWindow — рендерится корректно, кнопка генерации блокирована без данных.
- Consequences: приложение запускается и проходит цикл; Tests переведены на net10.0-windows (нужны для WPF-тестов); CRUD/визард/редактор-DnD — backlog.

## D-23 RealSchool: предел масштаба измерен (room-модель — блокер целой школы)
- Context: стресс 40 классов/100 учителей/40 кабинетов/1200 occ (план РБ-аппроксимация 21→34ч, сетка 6×7).
- Measurements: build 80мс (данные OK); без кабинетов Phase A 8.5с, Unknown, без краха; С кабинетами — нативный SEHException/OOM через ~107с (O(n·R·T) BoolVar).
- Chosen: целую школу одним прогоном НЕ обещать; рабочий диапазон — до ~100 occ (параллель/смена); room-модель v2 (табличные/ленивые вместимости, специализация кабинетов) и декомпозиция по сменам — обязательные ворота перед «целой школой»; краш-тест оставлен как Skip с репродукцией.
- Alternatives: молча уронить лимит кабинетов (отклонено — подлог входа); чинить room-модель сейчас (отклонено — большой рерайт P0.5-зоны без golden-стенда; backlog P1).
- Consequences: ответ «можно ли нормально использовать» — по частям да (см. отчёт), целиком школу — нет; требуется E-этап room-v2/смены.

## D-24 Large School Solver: lanes + greedy/LS (вердикт SOLVER COMPLETE)
- Context: 1200×40×42 ронял натив OOM (D-23). Лог показал второе дно: presolve-probing
  съедал бюджет Phase A до первой ветки (branches: 0), а hint-subsolver с workers=1
  стартовал последним — полные hints не помогали.
- Измерено и выбрано (каждая гипотеза — замером):
  - Lane-модель (комната cap C = C линий + NoOverlap + presence-Bool O(n·L)):
    6k/58k/147k vars/constraints вместо ~7M; OOM ушёл; семантика та же (Forbidden/seats
    в кандидатах, cap — числом линий; маппинг в RoomId обратно). Small/Medium/RoomAudit — зелёные.
  - GreedyPlacer (Core, детерминирован, sync-юниты, честно частичен): 1200/1200 за ~0.1с,
    validator-clean → feasible-first за ~0.1с вместо 22с поиска. Multi-start (3 seed-порядка)
    закрыл покрытие Large (849/850 → 850/850).
  - Greedy fast-path в Phase A (полный+clean → пропуск CP-SAT A): Small90 TTFF 1025→~90мс,
    total 46→23с, soft 140→30. Частичный greedy → CP-SAT A как раньше.
  - LocalSearch (first-improvement через IncrementalEvaluator, time-box, seed):
    Real 4915→1705 (600с), Large 3815→1445, Medium 1470→890, Small 210→30. Бюджет B: 50/50 (кап 300с).
  - ОТКЛОНЕНО замером: presolve-off в A (не помогло), decision-стратегия для B
    (0 инкумбентов что с ней, что без — оставлена как безвредная для малых),
    CP-SAT B как движок улучшения на ≥388 occ (0 инкумбентов за 34–180с — мёртв на масштабе,
    жив на малых: Small90 — 8 инкумбентов).
  - D-15 пересмотрено: proxy-смещение CP-SAT осталось, но качество single-run теперь ведёт
    настоящий soft (greedy+LS): фикстура-доказательство инвертирована осознанно (A/25 → B/0).
- Честные оговорки: бюджетные прогоны варьируют в полосе (Real@90с: 2005–3610) —
  wall-clock time-box; gate ≤ greedy стабилен; bit-repro только для сошедшихся (мелких).
- Alternatives: декомпозиция по сменам (не понадобилась — глобальная модель влезла);
  staged/lexicographic objective (не понадобился); удаление B (нет — жив на малых).
- Consequences: SolverOptions.PresloveInPhaseA (дефолт true); SolverResult.ModelStats;
  RealityCheck-тест обновлён осознанно; веса FROZEN; E1–E9 зелёные (1 тест обновлён).

## D-25 EPIC-H: SearchIndex — O(k)-дельта вместо O(n)-пересчёта (поведение сохранено)
- Context: H2 измерил — 99.9% времени LS внутри IncrementalEvaluator (2 полных
  SoftEvaluator + 3 O(n)-прохода на кандидата, ~0.66мс); 1 sweep > 30с на RealSchool.
- Alternatives: (a) оставить (плато 1705 за 60–90с), (b) персистентный индекс с O(k)-дельтой,
  (c) кешировать before-пересчёт (только ~2x).
- Chosen: (b). `SearchIndex` (Core): lift-проверки + скоуповые hard + дельта только
  затронутых (класс-день/учитель-день/предмет-день); зеркало арифметики SoftEvaluator
  вплоть до дубликатов слотов. IncrementalEvaluator НЕ тронут (контракт E8-preview, H8).
- Measurements: тот же single-optimum 1705 за ~1.2с (sweeps [205,23,1,0]); throughput eval ~119x.
- Evidence: `SearchIndexTests` 5/5 (паритет 200+500+200 ходов incl. сплиты/sync/room-only; накопление==полному).
- Consequences: LS в OrToolsSolver переключён на индекс (тот же порядок/окрестность);
  D-08 расширен (паритет-gate покрывает индекс); веса FROZEN не тронуты.

## D-26 EPIC-H: swap-VND + отказ от in-solver multi-start
- Context: H3/H5 доказали single-плато 1705 (ранний выход, бюджет-независимо).
  Остаток — teacher gaps 74% + subj-doubles 24.5%.
- Alternatives: (a) оставить single, (b) pairwise time-swaps VND, (c) in-solver multi-start
  (best из N greedy×LS), (d) ILS/perturbation/tabu/chain.
- Chosen: (b). `TrySwap/CommitSwap` в индексе (атомарно, комнаты свои, sync через скоупы)
  + VND-цикл single→swap→single до полного цикла без улучшений.
- Measurements: 1705→1420 за ~22с (seed 11); плато VND бюджет-независимо (300с→1420, wall 22.6с);
  seeds spread 345→210; greedy-seeds 1420/1235/1340.
- Rejected: (c) — оркестратор E4 уже делает multi-seed short-budget + merge (дублирование
  в solver = сложность без выгоды); (d) — плато зафиксировано честно, сложность запрещена H5.
- Evidence: SwapParity-тест (дельта==полному, коммит validator-clean); H1/H4/H5-харнес.
- Consequences: AcceptedMoves включает swaps (тотал; сплит — в Audit); детерминизм по seed сохранён;
  E1–E9 зелёные без изменений (150/150).

## D-27 EPIC-H: пропуск CP-SAT Phase B при occ ≥ 300 (H6)
- Context: D-24 измерил смерть B на масштабе (0 инкумбентов за 34–180с на ≥388 occ);
  H6 подтвердил: B жив только на малых (Small90 — 8 инкумбентов).
- Alternatives: (a) оставить B везде (до 50% бюджета впустую), (b) пропуск по порогу occ,
  (c) adaptive probe (сложно, непредсказуемо).
- Chosen: (b) `LargeSchoolPhaseBThreshold=300` (public const; между измеренными 90 и 388) +
  честная diagnostics-строка «Phase B skipped». LS-бюджет и Accept-gate без изменений.
- Measurements: full SolveAsync Real@10с — wall 3.9с, Feasible, Hard=0, soft=1545, phaseB=0.
- Evidence: H6-gate тест `PhaseBSkipped_OnRealSchool` в сьюте; Small90/RealityCheck без изменений (<300).
- Consequences: время large-решения определяется сходимостью VND (~23с), а не бюджетом;
  возврат B — только новым замером пользы (H6 запрещает «потому что должен»).

## D-28 Ученическая компактность — HARD + 5-дневка + 2 смены + РБ-данные (бриф 13.09.2026)
- Context: xlsx-аудит показал 77 class-days с поздним стартом (вплоть до 3–4 урока)
  и 18 с внутренними окнами. Root cause: SoftEvaluator считал только внутренние
  окна (вес 25), поздний старт стоил 0 → solver считал пустоту первого урока бесплатной.
- Requirements (ответы пользователя): окон нет вообще; старт максимум ко 2-му уроку;
  5-дневка Пн–Пт; прототип «типовая СШ Минска»; две смены (1–4,9–11 — первая; 5–8 — вторая).
- Chosen:
  - Validator HARD: `student-gap` (внутренние), `student-late-start` (первый позже anchor+1),
    `class-maxperday` (СанПиН-кэп по DISTINCT-слотам: сплит-час — 1 слот) + «1-е классы:
    5-урочных дней ≤1». Якорь = min AllowedSlots класса (смена 1→1, 2→8).
  - Веса v2 (разморозка D-05): StudentGap 25→100 + новый StudentLateStart 100;
    RuleCatalog.Version 1→2. TeacherGap 10 без изменений.
  - Сетка 5×14 (бэнды 1–7 / 8–14) через ProblemInput.ClassSlots; SchoolClass.MaxLessonsPerDay
    (1→5, 2–4→5, 5–6→6, 7–11→7); SchoolClass.ShiftId задействован в данных.
  - Движок: CompactRepair (дефрагментация к якорю; матчинг групп→слоты с бэктрекингом) +
    GradeOneBalance (междневная доводка 1-х классов) + polish-циклы repair→LS +
    RuinRecreate LNS (ruin проваленных дней + contention, Replant поверх frozen) +
    LS-доборы. CP-SAT A пропускается при полном greedy (компактность он не моделирует;
    на 5×14 за cap не находит — измерено). IncrementalEvaluator (редактор) — HARD-preview;
    SearchIndex (движок) — осознанно мягче (single-ходы чинят {3,4,5}-паттерны только
    через промежуточные окна); паритет-gate переформулирован (superset + дельты где оба считают).
  - Данные: SANPIN_RB.md + SUBJECTS_RB.md; планы под нормы 21/23/29/30/32/33/34
    (3,4: 25→23; 7: 29→32 — аппроксимация, зафикстирована честно); РБ-названия;
    белорусские фамилии учителей; экспорт «Класс · смена», дни Пн–Пт, строки 1–7 смены.
- Measurements: RealSchool 1196 occ: greedy 1196/1196 ~150мс → repair → LS → polish →
  Feasible Hard=0 за ~4–6с (10с-бюджет); soft ~8600–9500 (доминанта teacher-gap).
- Bugs found & fixed (каждый — замером, не предположением):
  - D-28a: GapOf считал дубликаты сплит-пар (слоты {1,2,2,4} давали gap −1 вместо 1) —
    слепота валидатора/soft/ремонта одновременно. Фикс: DISTINCT везде.
  - D-28b: CompactRepair матчинг писал комнаты отложенно → sync-пары получали один
    кабинет дважды (room overflow). Фикс: инкрементальный учёт planRoomUse.
  - D-28c: GreedyPlacer `return false` вместо `continue` при занятых кабинетах клетки —
    юнит бросался целиком (MidSlack 11 unplaced). Фикс однострочный.
  - D-28d: кэп класса считал placements, а не distinct-слоты → сплит-часы съедали
    ёмкость (5А: 30 < 32). Фикс: distinct-слоты везде (validator/greedy/index/editor).
  - D-28e: scoring-greedy (разброс учителей) резал покрытие 1196→1192 — откачен к first-fit;
    покрытие важнее, разброс — работа LS/LNS.
- Consequences: веса v1 несовместимы (тесты обновлены); CP-SAT B по-прежнему skipped ≥300;
  LNS в 10с-бюджете обычно не задействуется (страховка); E-компоненты (Top5/Explainer/
  Accept/Edit/Export) расширены обратно совместимо (late-start код, Пн–Пт, смены).
- Open: веса v2 не проходили A/B на реальных школах (триггер P1); юрсверка СанПиН —
  по-прежнему NEEDS-CHECK (SANPIN_RB.md §8); сдвоенные/пиковые/физра-разнос — коды-заглушки.
## D-15 Proxy-objective вердикт P0.5 (без переписывания)
- Context: Phase B минимизирует сумму t; SoftEvaluator отбирает best среди VISITED. Фикстура-доказательство: A=(1,3) proxy 4/soft 25 vs B=(2,3) proxy 5/soft 0 — solver вернул A, B никогда не посещён.
- Alternatives: (a) линеаризовать gaps сейчас, (b) оставить proxy + ворота на E1-spike.
- Chosen: (b). Callback-митигация НЕ гарантирует soft-best (B не посещён → не отобран); пул proxy-улучшений proxy-смещён → Archive нельзя строить на предположении «поток solver = soft-ранжированные разнообразные кандидаты».
- Why: задача P0.5 запретила автоматический рерайт objective; single-best продукт (feasibility-first) с proxy приемлем временно.
- Evidence: ProxyPrefersA_SoftPrefersB (solver slots=[1,3] soft=25, alt B soft=0 валиден).
- Consequences: P1-входные ворота E1-spike обязан ответить: soft-качество/diversity пула, нужен ли alignment (хотя бы student-gap 25) ДО Archive UI; иначе архив унаследует proxy-смещение.

## D-29 Teacher-gap split: ordinary vs cross-shift + RuleCatalog v3 + EffectiveRuleSet (EPIC-J)
- Context: teacher-gap ≈95% Soft (финал 7360/7645); аудит показал 56–72% массы — перерывы утро+вечер (crossDays 142/199), а не окна. Старая метрика ценила естественный разрыв двухсменки как окно.
- Alternatives: (a) оставить как есть, (b) бинарный cross-штраф за день, (c) units-сплит total−ordinary с малым весом.
- Chosen: (c). GapUtils (DISTINCT везде — закрыто расхождение SoftEvaluator/SearchIndex); `teacher-cross-shift-gap` вес 2 (0..50); `primary-early-start` 0 (стаб под будущее); RuleCatalog v3; EffectiveRuleSet+RuleResolver (профили — данные); HumanScale; ShiftBands из входа (дефолт SP==14 → [(1,7),(8,14)]); паритет трёх реализаций; Explainer-строки обеих компонент. Student HARD един для всех профилей (S4-критика uiux).
- Measurements: матрица w{0,1,2,5,10} (budget 20, seed 11): w2 ordinary 200/cross 477/subj 28; изоляция w10→w2: ordinary 310→200 (−35%), cross 333→477 (+43%), old-scale 6895→7190 (+4% — цена честной метрики), student 0/0, Hard 0, ~12с.
- Rejected: w0 (772 cross — метрика слепнет), w5 (ordinary 243 worst), w1 (176 — в шуме ±15%, оставлен опцией), бинарный штраф (не различает 6→9 и 1→8).
- Consequences: breakdown несёт оба кода; Candidate snapshot — ProfileName/Version/WeightsHash; старые БД читаются (RuleSetVersion уже хранился), CUSTOM со старой версии — плашка (R5).

## D-30 Targeted-старт LS вместо нового движка (EPIC-J)
- Context: смена веса cross 10→2 сломала сходимость H6-gate (VALIDATOR REJECTED, 2Г д5) — доказано A/B тем же сидом. Причина: LS блуждает в неремонтопригодные состояния (LNS failed 1→1).
- Alternatives: (a) вернуть cross=10, (b) новый движок (ILS/tabu — запрещено ТЗ), (c) targeted-порядок существующего VND.
- Chosen: (c). Первый single-проход: порядок teacher-ordinary desc (+seed-shuffle внутри равных, детерминировано) + ходы только внутри дня+смены; дальше обычный VND. Rules-параметр проброшен в LocalSearch/RuinRecreate/OrToolsSolver (опционально, дефолт STANDARD).
- Measurements: gate green (Feasible, soft 4000, LNS failed 2→0); seed-stability LS 5с: spread 174 (5.5%).
- Consequences: детерминизм-тест зелёный; H4-полоса сузится измерением позже.

## D-31 Fixture 1–4: классные учителя (только данные, EPIC-J)
- Context: fixture раздавал началку по предметным пулам (нереалистично для РБ).
- Chosen: 16 классных (всё кроме физры/музыки) + 3 физрука + 1 музыкант; учителей ровно 100 (перебаланс пулов); occ 1196; тест SingleClassTeacher (≥6 предметов у классного, остальные — только физра/музыка, слоты 1..7). Глобального Hard нет (ТЗ): старт держит существующий late-start HARD.
- Measurements: gate soft 4000→3549 (−11% от кластеризации нагрузки).
- Consequences: StableKey учителей началки изменились (fixture-внутреннее, персист продакшена не задет).

## D-32 Профили/персистентность/WPF-минимум (EPIC-J)
- Context: веса должны настраиваться через GUI без кода; обычный завуч — ноль настроек.
- Chosen: QualityHints (8 подсказок) + SqliteQualityProfileStore (QualityProfiles, single-active, валидация через резолвер до записи) + AppSession.QualityRules + GenerateHost/orchestrator rules-plumbing + профиль-селектор MainWindow (3 профиля, CUSTOM скрыт до диалога) + строка профиля в GenerateWindow. Полный 6-табовый диалог — backlog (контракты готовы: RuleResolver/HumanScale/WeightRange/QualityHints).
- Alternatives: полный диалог сейчас — отклонён (R4 scope-риск после P1–P3).
- Consequences: завуч выбирает профиль без кода; CUSTOM end-to-end (движок+store+тесты), GUI-кнопка сохранения — backlog.

## D-33 FULL UI: dashboard + режимы + настройки + рейтинг (промт FULL UI/UX)
- Context: backend E1–E9/EPIC-H/I/J готов и покрыт; UI был минимальным (4 кнопки, MessageBox-ошибки, чисел-весов не было вовсе). Референс `primer.png` — композиция dashboard.
- Chosen:
  - Multi-window сохранён (dashboard + Generate + Schedule + Settings-диалог + 3 малых окна); сайдбар-навигация primer не переносилась (декорация без пользы — честное отклонение, композиция сохранена).
  - Visual system в App.xaml (светлая тема, акцент #4F46E5, карточки R12, BtnPrimary/Secondary, Badge).
  - Режимы — данные: QUICK (3с/1 seed) / STANDARD (12с/3) / MAXIMUM (30с/5) / EXPERT (настройки + 12с/3); `GenerateModes` в Application.
  - QualityRating: 3 честных уровня (Отличное/Хорошее/Требует внимания) + топ-3 юнитов + окно «Почему так»; голый Soft с карточек Top-5 не убирался (контракт E5), рейтинг — слой сверху.
  - SettingsWindow: пресеты + Качество (слайдеры 0..4 + бейджи Строгое/Пожелание + подсказки) + Предметы/Классы/Учителя (MaxPerDay/MaxLessonsPerDay, session-only) + Эксперт (числа, JSON export/import, «Сохранить „Моя школа“» в SQLite, восстановление при старте).
  - Логика настроек — в Application (`QualitySettingsEditor`, тестируется); XAML тонкий. STA-конструкты всех окон — в сьюте.
  - ScheduleWindow: фильтры Класс/Учитель + строка качества; правка и gate без изменений.
  - Импорт: inline-панель ошибок «что исправить» вместо одинокого MessageBox.
- Alternatives: single-shell rewrite (отклонён — ломает STA/E2E-контракты, риск без выгоды); рейтинг 5 звёзд (отклонён — выдуманная точность).
- Measurements: 188/188, STA 3/3, exe жив.
- Consequences (честные лимиты): правки сущностей — session-only (персист школьных данных — backlog); day-level доступность учителей и doubles/adjacency-предпочтения — backlog (продьюсеров нет, мёртвых ручек не даём); диагностика seed/workers осталась под «Подробнее» (допустимо).

## D-34 Flexibility-first: шаблон для всех школ, всё перенастраивается (ответы пользователя 14.09.2026)
- Context: 9 фич R1–R9 (спец-кабинеты, часы по параллели/классу, общий урок, классрук, многоместные, сложность, закрепление учителя, приоритет 11>9, экспорт учителей) + overflow MainWindow. Школы разные: у автора один учитель на класс весь год, у других — иначе; классный час (чт слот1 5–11) нужен не всем.
- Chosen (ГЛАВНОЕ ПРАВИЛО ПРОЕКТА): дефолты — типовая школа (работает из коробки без настроек); КАЖДОЕ правило — гибко настраивается: вкл/выкл + параметры + уровень (Hard/Soft/Disabled где применимо). Никаких хардкодов школьных привычек.
- Ответы пользователя (зафиксированы):
  - R1 спец-кабинеты: Hard-запрет + ручные (solver сам не ставит; ONLY-subject чужой = hard; ручное всегда можно).
  - R2 часы: приоритет Класс > Параллель > Предмет-дефолт (9Б 7ч бьёт 5ч параллели).
  - R3 общий урок: по умолчанию ВЫКЛЮЧЕН; включается одной галочкой (день/слот/параллели/свои кабинеты/классрук — всё настраивается).
  - R4 классрук: поле на классе, источник учителя для R3 + отображение.
  - R5 многоместный: макс групп Hard (напр. 4) + желательно Soft (напр. 2); подгруппа = 1 единица вместимости; превышение желаемого = soft-штраф.
  - R6 сложность: слайдер 1..10 + IsHeavy-порог настраиваемый (дефолт >=7).
  - R7 закрепление: по умолчанию Hard на класс (один учитель на (класс,предмет) весь год — как у автора), но переключается: Hard класс / Soft / параллель / выкл. Где настраивается — см. ниже.
  - R8 приоритет 11>9: всё гибко — вкл/выкл + веса (дефолт вкл: 11-е x3, 9-е x2, остальные x1, слайдер).
  - R9 экспорт учителей: один лист Teacher в том же Excel (Учитель|День|Урок|Класс|Кабинет) + табы Класс/Учитель/Кабинет.
  - Порядок: сначала overflow-фикс, потом R1–R9 пакетами.
- Где настраивается (карта вкладок — фиксировано, детали в plan.md):
  - SchoolDataWindow (8 табов слева): Нагрузка | Часы (матрица предмет×параллель + overrides на класс) | Классы+Классрук | Учителя+Закрепление | Предметы+Сложность | Кабинеты (режим/вместимость) | Подгруппы | Общие уроки (выкл по умолчанию).
  - SettingsWindow → карточка «Приоритет выпускных» (тумблер + слайдер весов) + Эксперт (точные числа).
  - ExportWindow → Radio Класс/Учитель/Кабинет + чек «Лист Teacher».
  - ScheduleWindow → таб-фильтр Класс/Учитель/Кабинет + бейджи SPEC/ONLY/общий урок (только индикация).
- Alternatives: хардкод под школу автора (отклонено — нарушает главное правило); всё-soft без hard (отклонено — R1/R7 требуют hard по ответам).
- Consequences: каждая фича — Domain-поле + SQLite-миграция + ProblemBuilder/Validator + UI-таб + тест; severity-переключатели через EffectiveRuleSet/RuleResolver (D-29); regression gate по V1 baseline обязателен.
