# BENCHMARKS.md — замеры АМСУР V2

## Baseline V1 (из REPORTS Pr_Auto, перемерить в D4 на V2-стенде)
- Small 120 occ: first feasible 15с / total 80с / hard=0
- Medium 350 occ: cap 150с — feasible нет (плотность 97%)
- Large 840 occ: cap 240с — feasible нет (плотность 97%)
- Problem build 156–239мс; FullValidator 5–44мс; IncrementalEvaluator 1.75мс/eval (drag-gate <50мс)
- Excel export ~1.5с / parse ~0.3с / import ≤0.5с; schedExcel 267мс / schedPdf 509мс
- Diagnostics 4с (cap 20с); cold start 3.0с / warm 2.8с; память 95→419МБ Large
- Backup 18мс / restore 11мс

## V2 P0 (12.09.2026) — MEASURED, workers=1, seed фиксированы
- Build: 0 warnings / 0 errors (net10.0)
- Tests: 75/75 passed (12.09–13.09.2026: P0 34 + P0.5 6 + E1 8 + E2 5 + E3 9 + E4 11 + overhead/smoke 2)
- Tiny 3 occ (2д×4сл, seed 1): Feasible, Hard=0, <<1с, gate FullValidator пройден
- Small90 90 occ (5кл×6пр×3ч, 5д×6сл, seed 11, бюджет 60с): **Feasible, Hard=0, elapsed≈46с**, validator-clean
- Repro: workers=1+seed=7 бит-идентичен (мультимножество времён)
- Cancel: WasCancelled + best-so-far сохранён (CANCELLED_AFTER_FEASIBLE) либо честно пуст
- Dense overconstrained (1д×1сл, 2ч): Infeasible/Unknown без placements (timeout≠infeasible соблюдён)
- Validator/evaluator мс: PerfTests 300 occ (10кл×10пр×3ч, 5д×7сл, seed 9, БЕЗ solver) — build+validate+soft+100×incremental суммарно <1с (весь тест ~0.1с); gates validator<5с, soft<5с, incremental<50мс/move — все с запасом; Total==sum Components
- Persistence: accept валидного ~мс (SQLite файл, WAL); 5 конкурентных Accept сериализованы BEGIN IMMEDIATE (busy_timeout 30с) — ровно 1 active
- Excel roundtrip (2 строки incl. сплит): ~1с (доминирует старт ClosedXML); семантическое равенство записей
## V2 P0.5 Reality Check (12.09.2026) — MEASURED, workers=1
- Small90 breakdown (seed 11, бюджет 60с): builder=16мс, modelBuildA=31мс, phaseA=1012мс, modelBuildB=7мс, **phaseB=45033мс**, validator=0мс, soft=0мс, TTFF≈1025мс, total≈46с, incumbents=12, validator-rejected=0, objective=140
- Proxy-аудит: фикстура A=(1,3) proxy4/soft25 vs B=(2,3) proxy5/soft0 → solver вернул A (185мс); B валиден, но не посещён
- Room-фикстуры (2 occ, 1–2 rooms): bottleneck Infeasible ~0.2с; assigned+clean ~0.2с
- Medium/Dense solver, память, grid-render, cold start: НЕ МЕРЯЛИ (P1-стенд D-09)
- Предупреждения: NU1903 SQLitePCLRaw.lib.e_sqlite3 2.1.11 (транзитивный от Microsoft.Data.Sqlite 10, bundled native; single-PC офлайн P0 — принято к сведению)

## V2 E1 Archive Spike (12.09.2026) — MEASURED, фикстура 18 occ (3кл×3пр×2ч, 3д×4сл, 2 каб cap2, budget 25с, workers=1)
- seed=11: incumbents=7, proxies 79→66, softs [75,105,10,35,0,35,0], bestSoft=0 поз.5/7, total≈19.1с
- seed=22: incumbents=6, proxies 79→66 (71 вместо 70), softs [75,105,10,70,0,0], bestSoft=0 поз.5/6, total≈18.9с
- seed=33: идентичен seed=11 (seed-diversity слабая)
- Наборы (seed22): A soft best0/mean31/worst75; B soft best0/mean37/worst105; C==A (порог 100 ни разу не сработал)
- Diversity: pairwise mean ≈909–1014, min 35–95, dupRate 0, nearDup(<30) 0, diffFromBest 4/4
- Overhead архива (validate+soft+fingerprint/incumbent): 1–18мс (≈0 от solve)
- Вывод: KEEP_PROXY (D-16); E2-ворота: pool-stress dense/room-constrained

## V2 E2 Pool Stress (12.09.2026) — MEASURED, workers=1, K=5, threshold=100
- Dense83 100 occ (4кл×5пр×5ч, 5д×6сл, плотность 0.83, 3 каб cap2, бюджет 60с):
  seed=11: pool=10, K ✓, tFirst≈11.7с, tK≈14.1с/52.3с, bestSoft=230, rejSim=0, C==A (best230/mean377/worst520, pairMin 7985)
  seed=22: pool=15, K ✓, tFirst≈11.4с, tK≈13.9с/52.0с, bestSoft=315, rejSim=0, C==A (B mean 353 vs A 342 — proxy слегка хуже)
- Room-tight 12 occ (3кл×2пр×2ч, 2д×3сл, плотность 0.67, 2 каб cap1 + Forbidden): pool=1 (proof 353мс), K ✗, tK=-1, bestSoft=50
- Near-dup solver 18 occ (2 каб identical): pool=5=K, ветка не связана; синтетика 8 кандидатов: rejSim=2, ветка рабочая, но порядок заполнения держит ранние near-dups (C min == A min)
- SeedDiv Dense30: pools 10/15/14, best 230/315/200, best fingerprints различаются (seed-diversity достаточна на dense)
- Overhead: 4–38мс; memDelta ≈34–37МБ/запуск; inter-incumbent: секунды (dense), мс (small)
- Плотность := occ / (classes × days × slots)

## V2 E3 Pool Expansion (12.09.2026) — MEASURED, workers=1
- StableKey cross-run: fingerprint/dist=0 между сборками (тесты); Guid-identity для кросс-сравнений запрещена
- Room-tight 4 occ (2кл×2ч, 2д×2сл, 2 каб cap1, 15с): 3×seed → total 9 unique 9/9 → merged 5/5 best 0 (POOL_SUFFICIENT)
- Perturbation 18-occ (ban половины best, seed33, 25с): pool 8 unique 8 best 0 mean 74.4 worst 140 vs clean seed22 pool 8 mean 56.9 worst 95 → perturbation отклонён
- Budgets 18-occ (seed11): 10с/30с/60с → пулы идентичны (6, best 0, archN 5), TTFF 261/160/165мс → sweet spot ~10с на классе
- Merger/diagnostics/perturbation-ban юниты зелёные; BannedTimes — search-only (validator/evaluator игнорят осознанно)

## V2 E4 UI Overhead (13.09.2026) — MEASURED, фикстура 18 occ (3кл×3пр×2ч, 3д×4сл, 2 каб cap2, budget 8с, workers=1, seed 11)
- A solver голый: wall=6338мс
- B solver+archive: wall=6199мс, callback-overhead=2мс, archN=4
- C solver+archive+throttled progress: wall=6170мс, callback-overhead=1мс, archN=4, stream received=4 → emitted=2
- Вывод: overhead UI-слоя ≈0 (разброс wall — шум solver; колбэки 1–2мс суммарно); throttling режет UI-события (4→2) при полной частоте solver. Ворота §14 пройдены.

## V2 E5 Explainability (14.09.2026) — MEASURED, фикстура 20 occ (5кл×4пр×1ч, 5д×7сл, workers=1, без solver)
- QualityExplainer.Explain 20 occ: 16мс (gate <1000мс, с запасом; O(n)-группировки)
- Вывод: слой объяснимости бесплатен относительно solve (мс против секунд/минут).
- Tests: 136/136 passed — 14.09.2026, длительность 5м03с (E12: +Greedy 4 + LS 3 + Matrix 2, Skip снят, RealityCheck обновлён)

## V2 Large School Solver E12 (14.09.2026) — MEASURED, матрица §8 (workers=1, seed 11)
| Size | occ | build | modelBuild | first | total | soft | hard | peakMb | vars I/B/C | lanes | seeds |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Small | 90 | 25мс | 38мс | 89мс | 23.8с (60с) | 30 (было 140) | 0 | 123 | 270/450/2506 | — | 1 |
| Medium | 388 | 66мс | 263мс | 101мс | 44.2с (60с) | 890 | 0 | 263 | 1940/18696/47878 | 43 | 1 |
| Large | 850 | ~60мс | ~700мс | 108мс | 91.8с (120с) | 1445 | 0 | 479 | 4250/40932/103353 | 43 | 1 |
| Real | 1200 | 80мс | ~800мс | ~100мс | ~70с (90с) | ~2000–2100 (полоса) | 0 | 645 | 6000/57744/146528 | 43 | 1 |
| Real | 1200 | 80мс | ~800мс | ~100мс | 420с (600с) | 1705 | 0 | 645 | те же | 43 | 1 |
- Траектория Real: greedy 4950 (0.1с) → LS 2005–2115 (34с) → LS 1705 (120с); CP-SAT B:
  0 инкумбентов за 34–180с на ≥388 occ (мёртв на масштабе, жив на малых: Small — 8).
- Было (до E12): OOM/SEHException через ~107с; стало: OOM нет, Hard=0 везде, first ~0.1с.
- Tests: итог полного сьюта E12 — 136/136 (см. строку Tests выше).
- История (D-23, закрыта E12): старая O(n·R·T) room-модель давала нативный SEHException/OOM
  через ~107с Phase A на 1200×40×42; зонд без кабинетов (30с) — Unknown за 8.5с без краха.
  E12 заменил модель (lanes) + добавил greedy/LS: OOM нет, first ~0.1с. Skip с краш-теста снят.
- Рабочая версия: E2E-цикл (шаблон→импорт→solver→Top-5→приёмка→правка→xlsx, крошечная школа) ~1с; exe `Amsur.Wpf.exe` запускается, MainWindow рендерится (скриншот).
- E7–E9: локальные операции (SQLite accept, preview/commit, сетка Excel) — миллисекунды; доминанта Excel-выгрузки — старт ClosedXML ~1с (см. C2, не регрессия).

## V2 E6 Trajectory (14.09.2026) — MEASURED, без solver
- TrajectoryHistory 10k append+snapshot: 9мс (in-memory список, gate <1000мс)
- Smoke (реальный solver, tiny 2 seeds, продакшн-throttle 300мс): events=2, markers=1 («Готово»), hasLine=False — разреженная история короткого прогона, график показывает ось+маркер без выдуманной линии
- Вывод: история бесплатна относительно solve; при медленном throttle честно мало точек.

## V2 EPIC-H Quality Optimization (13.09.2026) — MEASURED, RealSchool 1200 occ (40 кл / 100 уч / 40 каб, 6×7)
Методология: Greedy baseline + LocalSearch напрямую (без CP-SAT — на ≥388 occ CP-SAT B даёт
0 инкумбентов, D-24; LS и есть движок качества). Харнес: `QualityAuditTests` (флаг `AMSUR_EPICH=1`,
вне CI-гейта) + `SearchIndexTests` (паритет-gate, в сьюте). Workers=1 (LS однопоточен).

### H1 — breakdown Soft (greedy seed 11 → LS seed 11, 60с, ДО swaps)
- Greedy 5195 = student 625 (25u) + teacher 4030 (403u!) + subj-maxperday 540 (36u); остальные коды = 0 (продьюсеров нет — подтверждено).
- Greedy+LS 1715 = student 25 (1u) + teacher 1270 (127u) + subj 420 (28u).
- Вывод: student-окна LS решает почти полностью (16/184 → 1/234 gapped class-days);
  остаток — teacher gaps (74%) + subject-doubles (24.5%).

### H2 — механика LS (ДО индекса, 30с): accept 0.43%, forbidden 81.5%, 1 sweep > 30с, eval 99.9% времени
- attempts=45561, forbidden=37151, nonImproving=8216, accepted=194; trajectory 1/5/10/30с: 5035/4485/3890/2195.
- Диагноз: IncrementalEvaluator = 2 полных SoftEvaluator-пересчёта + 3 O(n)-прохода на кандидата (~0.66мс).

### H3 — time-budget curve ДО (seed 11, greedy 5195): 10с→3930, 30с→2150, 60с→1715, 90с→1705, 180с→1705 (wall 127с, ранний выход), 300с→1705 (wall 129с)
- Плато single-moves ~60–90с: дальше полный проход без улучшений (истинная сходимость, не обрезка бюджетом).

### H4 — seed stability ДО (60с): best=1725, median=1880, worst=2070, spread=345

### H5.1 — SearchIndex (O(k)-дельта, паритет с IncrementalEvaluator доказан тестами 200+500+200 ходов)
- Тот же single-optimum 1705 за **~1.2с вместо ~60–90с** (sweeps [205,23,1,0], ~197k попыток, ~5.6мкс/eval — ускорение ~100x throughput).
- Паритет-гейт в сьюте: `SearchIndexTests` 5/5 (tiny/mid-split-rooms/room-only/накопление/swap-дельта).

### H5.2 — swap-VND (single до сходимости → pairwise time-swaps → повтор; комнаты свои, sync через индекс)
- 1705 → **1420 за ~22с** (seed 11; singles 230 + swaps 21; sweeps singles [205,23,1,0,1,0,0], swaps [19,2,0,0,0]).
- Breakdown 1420 = student 25 (1u) + teacher 990 (99u) + subj 405 (27u).
- Плато VND: 10с→1430, 30с→1420, 60с→1420, 300с→1420 (wall 22.6с, ранний выход — бюджет-независимо).
- Seeds VND (60с): best=1420, median=1475, worst=1630, spread=210 (уже, чем 345).
- Greedy-seeds VND: 1420 / 1235 (g22) / 1340 (g33) — best-of находка 1235; in-solver multi-start ОТКЛОНЁН (оркестратор E4 уже multi-seed + merge).
- Отвергнуто без реализации: ILS/perturbation/tabu/chain (плато VND честно зафиксировано; сложность без доказанной нужды запрещена H5).

### H6 — CP-SAT B gate: пропуск при occ ≥ 300 (измерено: Small90 — 8 инкумбентов; Medium388+ — 0)
- Full SolveAsync RealSchool бюджет 10с: wall **3.9с** (было ~10с+ впустую), Feasible, Hard=0, soft=1545 (частичный VND), firstFeasible 108мс, phaseB=0 + «Phase B skipped» в diagnostics.
- Малые (<300) — без изменений (B жив).

### Итог EPIC-H (Real 1200, seed 11, сопоставимый бюджет)
| | Before | After |
|---|---|---|
| Soft | 1705 (60–90с) | **1420** (~22с, сходимость) |
| Hard | 0 | 0 |
| first feasible | ~100мс | ~100мс (без изменений) |
| CP-SAT B на large | ~34–50% бюджета впустую | пропущен (gate) |
| Full suite | 136/136, 5м03с | **150/150, 1м35с** |
| Память | peak ~78МБ (H3) | класс тот же (memDelta 1–3МБ; отдельных замеров peak после не было) |
- Улучшение 5195 → 1420 (−73% от greedy; −17% от single-плато 1705) при честном VND-плато.
- Остаток 1420: teacher gaps 70% + subj-doubles 28.5% + student 1.5%; «оптимум» НЕ заявляется.

## V2 EPIC-I Student Compactness (13.09.2026) — MEASURED, RealSchool 1196 occ (40 кл / 100 уч / 40 каб, 5×14, 2 смены)
Методология: xlsx-аудит старого файла (77 late-start + 18 internal gaps) → root cause
(late-start вес 0, student-gap 25, слепота GapOf к дублям сплитов) → D-28 (HARD + веса v2 +
CompactRepair/Matching + GradeOneBalance + polish + LNS + honest DISTINCT).
- Greedy best-of-4: 1196/1196 за ~150мс (day-балансировка + distinct-кэпы; room-full → continue).
- CompactRepair(greedy): ~17–21 дней чинено сразу (матчинг групп→слоты с бэктрекингом).
- LS (3.75с) + polish (1.5с) + GradeOneBalance: failed-дни → 0.
- Итог 10с-бюджет: **Feasible, Hard=0, wall ~4–6с**, soft ~8600–9500 (доминанта teacher-gap).
- Итог 90с-бюджет: **Feasible, Hard=0, soft=7205** (файл RealSchool_Schedule.xlsx).
- xlsx-верификация (независимый python-аудит): **0 внутренних окон** (было 18),
  **0 стартов позже 2-го урока** (было до 3–4-го), 53 дня со стартом со 2-го (допустимо),
  5 дней Пн–Пт, 7 строк/смена, смены в заголовках, РБ-названия предметов.
- LNS RuinRecreate: в 10с-прогонах обычно не задействуется (страховка цепочек;
  контракт-тест зелёный). CP-SAT A пропускается при полном greedy (D-28b).
- Full suite: **163/163 за ~2м08с** (было 150/150).
- Найденные и исправленные баги: dup-слоты в GapOf (D-28a), room double-book в матчинге (D-28b),
  greedy return-false вместо continue (D-28c), кэп по placements вместо distinct-слотов (D-28d).

## V2 EPIC-J Teacher-Gap Split (13.09.2026) — MEASURED, RealSchool 1196 occ (новый fixture: 16 классных 1–4)
Методология: greedy-аудит split → V1-baseline (`.opencode/compose-plus/baseline-v1.md`) →
S4-критика → S5-синтез → P1 split → P3 targeted (починка сходимости, доказана A/B) →
P2 fixture → P4 профили → матрица весов + stability → regression gate.
- Baseline greedy (seed 0, частичен 1193/1196): teacher-units 1389 = ordinary 395 + cross 994 (72%);
  gapped teacher-days 272, crossDays 213 (78%); TOP-учителя 29–35 юнитов (cross 18–33).
- Baseline final (budget 20, seed 11, v2-динамика): soft 7645 = teacher 7360 (96.3%) + subj 285,
  student 0/0, Hard 0; units 736 = ordinary 325 + cross 411 (56%); gapped 199, crossDays 142.
- Матрица cross-веса (budget 20, seed 11, новый fixture; oldScale = ord×10+cross×10+subj):

| wCross | ordinary | cross | subjU | newTotal | oldScale | gapped/crossDays | wall | Hard/student |
|---|---|---|---|---|---|---|---|---|
| 0 | 193 | 772 | 28 | 2350 | 10070 | 219/161 | 12.2с | 0 / 0/0 |
| 1 | 176 | 619 | 29 | 2814 | 8385 | 199/140 | 12.0с | 0 / 0/0 |
| **2 (дефолт)** | **200** | **477** | **28** | **3374** | **7190** | **183/116** | **12.0с** | **0 / 0/0** |
| 5 | 243 | 336 | 30 | 4560 | 6240 | 170/99 | 12.0с | 0 / 0/0 |
| 10 (old-динамика) | 310 | 333 | 31 | 6895 | 6895 | 188/104 | 12.2с | 0 / 0/0 |

- Изоляция эффекта (w10→w2, тот же fixture/seed/бюджет): ordinary 310→200 (−35%),
  cross 333→477 (+43%), old-scale 6895→7190 (+4% — цена честной метрики), student 0/0, Hard 0.
- Gate (budget 10, seed 11, w2): **Feasible, Hard=0, soft 3549, wall 6.3с** (fixture-эффект: 4000→3549).
- Seed-stability (LS 5с, w2, repaired-старт): totals 3075/3184/3249/3127/3160 —
  best 3075 / median 3160 / worst 3249 / spread 174 (5.5%); ord 158–170, cross 510–567.
- Full suite: **184/184 за 2м10с** (163 + 15 split + 5 profiles + 1 primary).
- Отвергнуто замером: w0 (метрика слепнет), w5 (ordinary worst), ILS/tabu/chain (не понадобились).
- Пример: `RealSchool_Schedule_EPICJ.xlsx` (STANDARD, budget 30, wall 18с, Feasible Hard=0,
  soft 3344 = ordinary 1970 + cross 954 + subj 420; python-аудит: 40 классов, 0 окон, 0 стартов позже 2-го).
- «Оптимум» НЕ заявляется.

## V2 FULL UI (13.09.2026) — MEASURED
- Full suite: **188/188 за 2м25с** (184 + 3 QualityUx + 1 STA NewWindows).
- STA-smoke: все 6 окон конструируются (MainWindow-dashboard, Generate, Schedule,
  Settings, SchoolData, QualityDetails, Help); генерация закрыта без данных.
- Exe `Amsur.Wpf.exe` запускается, жив 12с headless без стартового краша.
- Backend-контракты не менялись (только аддитивно): E1–E9/EPIC-H/I/J зелёные как были.

## Стенд P1 (план, D-09)
- Фикстуры tiny/small/medium/dense/subgroup-heavy/room-constrained/teacher-constrained × плотности 70/85/97%
- `workers=1+seed`, метрики firstFeasibleMs / bestAt60s / gap + PhaseMs
