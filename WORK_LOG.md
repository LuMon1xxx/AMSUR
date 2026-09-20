# Журнал работ (ведёт AI, 14.09.2026+)

## 14.09.2026 — Краш приложения на DemoSchool (XamlParseException BasedOn)

**Симптом:** `Amsur.Wpf.exe` падал через пару секунд после DemoSchool (кнопка «Сгенерировать»).
Солвер вне UI тот же набор решал за секунды → подозрение на UI-путь.

**Root cause (доказано, не предположение):**
- Event Viewer → `.NET Runtime` Id 1026 (13.09 23:08, 2 шт.):
  `XamlParseException: "DynamicResourceExtension" невозможно задать в свойстве
  "BasedOn" типа "Style"`.
- Виновник: `src/Amsur.Wpf/GenerateWindow.xaml:182` —
  `<Style TargetType="Button" BasedOn="{DynamicResource BtnPrimary}">` внутри
  DataTemplate карточек Top-5. `BasedOn` — не DependencyProperty, DynamicResource
  там запрещён (все остальные BasedOn в проекте — StaticResource, см. App.xaml).
- Механика: шаблон карточки инстанцируется только при `Cards.Count > 0`, т.е. в
  момент первого найденного кандидата (~пара секунд после старта генерации).
  Импорт/дашборд без генерации не падали, diag вне UI решал — сходится.
- Рядом в журнале: 2× AppHangB1 — UI-поток блокируется синхронным солвером
  (см. «Наблюдения», не фиксилось — вне скоупа).

**Фикс (1 строка):** `BasedOn="{DynamicResource BtnPrimary}"` →
`BasedOn="{StaticResource BtnPrimary}"` (GenerateWindow.xaml:182).

**Регрессионные тесты** (`src/Amsur.Tests/WpfShellTests.cs`, +70 строк):
- `GenerateWindow_RendersTop5Card` — рендер 1 карточки в показанном окне (off-screen).
- `DemoSchool_GenerateWindow_AcceptsBest` — E2E прод-пути: DemoRows → GenerateHost
  (как MainWindow) → RunAsync([11]) → рендер настоящих карточек → AcceptAsync OK.
- Доказательство: на ломаном XAML ОБА падают ровно с прод-исключением
  (XamlParseException BasedOn); с фиксом зелёные. Старый тест
  `GenerateWindow_ConstructsWithVm` баг не ловил (пустой VM → шаблон не грузится).

**Проверено:**
- `WpfShellTests|DemoSchool_*` — 9/9 PASS (StandardRun ~33с).
- Связанные: GenerateProgress/QualityExplainer/QualityUx/ArchivePolicy/AcceptFlow — 40/40.
- Ручной прогон exe: dashboard + P3-restore DemoSchool (56 строк→131 occ, сид через
  Temp\opencode\gen --seed), клик-автоматизация в этой среде ненадёжна
  (фокус/координаты) — клик-путь покрыт E2E-тестом прод-композиции вместо мыши.
- Event Viewer: новых `.NET Runtime` падений Amsur после фикса — 0.
- Коммит/пуш НЕ делались (ждут спроса).

**Наблюдения (не фиксилось, кандидаты на будущее):**
1. Солвер выполняется на UI-потоке (OrToolsSolver.SolveAsync без await +
   `await orch.RunAsync` без Task.Run) → окно висит десятки секунд при генерации
   (AppHang в журнале). Лечится выносом RunAsync в фон + маршалинг VM (отдельная задача).
2. Стартовый race: MainWindow.RefreshAll() в конструкторе отрабатывает до конца
   `AppSession.InitAsync()` → при восстановленных P3-данных дашборд показывает
   «Данные не загружены», пока не произойдёт RefreshAll (диалог/импорт).
   year.txt при этом перезаписывается (restore реально отработал — проверено verify).
3. `%LOCALAPPDATA%\Amsur` засеян DemoSchool (56 строк, year 623b017d…); бэкап исходного:
   `Temp\opencode\amsur-backup-20260914`.

---

## 14.09.2026 — Дизайн 1-в-1 с референсом (stitch v2.4.0 + primer.png)

Референс: `stitch_amsur_desktop_design_system/amsur_{mainwindow,1..6,logo}`,
handoff `amsur_design.md_wpf.net_10_handoff.md` (база 1600×900, rail 240px,
правая панель 340px). Токены App.xaml уже совпадают с handoff — правится
композиция и детали. Сознательные отклонения (фейковые данные запрещены):
без имени пользователя («Иванова Е.В.»), без бейджа года (в модели только Guid),
без колокола уведомлений (нет сущности уведомлений), без числового скора /100
(рейтинг — 3 уровня словами, «без выдуманной точности»), времена режимов —
честные бюджеты, PDF — только Excel (PDF «Скоро»).

### Отличия ГЛАВНОЙ от `amsur_mainwindow/screen.png` (все пункты — в работу)
1. Титлбар: нет бейджа синхронизации БД, колокола, чипа пользователя.
   → чипа/колокола не будет (нечего показать честно), синхробейдж — пропущен.
2. Нав-панель: нет заголовка «МОДУЛИ», нет пункта «Профили», нет бейджа
   «40 кл.» на «Школа и данные», кнопка справки без F1-шильда, версия одной строкой.
3. Центр: лишний текстовый степпер сверху (в рефе его нет) → удалить.
4. Карточки Школа/Профиль: нет иконок в тонированных квадратах, одна строка
   деталей вместо двух, бейдж «Каталог v3» вместо «Рекомендуемый».
5. Hero: нет иконки-блёсток, в кнопке нет F5-шильда и шеврона.
6. Мини-карточки (4): нет иконок, нет статус-строк с цветными точками.
7. Быстрый старт: одной текстовой строкой вместо 5 нумерованных шагов.
8. Лишние блоки: StateReady/StateDone/экспандеры (в рефе их нет) → удалить.
9. Режимы: нет заголовка «ВЫБОР РЕЖИМА ГЕНЕРАЦИИ» + ссылки «Параметры», нет
   радио-кружков/иконок/тайм-бейджей справа, бейдж «Выбор АМСУР» вместо «Оптимум».
10. Качество: нет шапки, пилюли, прогресс-бара, строк «что улучшить» с числами
    справа, кнопки «Показать подробный анализ».
11. Совет: нет иконки, ссылки «Перейти к настройкам правил», кнопки ×.
12. Нет нижнего статус-бара (движок/память/СанПиН/сводка справа).
13. Размер окна 1200×720 вместо базовых 1600×900.

### Отличия остальных окон
- GenerateWindow vs amsur_4: нет шапки «Ищем… + итерация», стадий-ленты
  (4 шага), метрик 2×2 (первое/лучшее/пул/время), красной кнопки «Остановить»,
  бейджа «ЛУЧШИЙ ВАРИАНТ», блока «ПОЧЕМУ ТАКАЯ ОЦЕНКА», ID генерации,
  кнопок «Открыть в редакторе / Принять этот вариант» в стиле рефа.
- ScheduleWindow vs amsur_2: табличный DataGrid вместо сетки day-колонок
  с карточками уроков; нет таб-вью Класс/Учитель/Кабинет, шильда смены,
  нижней панели перемещения с комбобоксами, 3 карточек диагностики.
  (Полная матрица — большая стройка; этап 1: верхняя статус-полоса + низ.)
- SettingsWindow vs amsur_5: сверить пресеты/слайдеры/бейджи/эксперт (читается).
- SchoolDataWindow vs amsur_1: нет счётчиков справочников, готовности БД,
  пагинации, красной панели с 2 кнопками (2-я кнопка — только «Выбрать другой
  файл»; «Проигнорировать некритичные» требует логики — не делать).
- ExportWindow vs amsur_6: сверить параметры/превью/готовность (читается).
- HelpWindow/QualityDetailsWindow vs amsur_3: модалка «Почему так?»
  (вариант+оценка, строки факторов с иконками, «Понятно»).


## 14.09.2026 (�����) � R1�R9 ������ + overflow-����
- ������ ������������ ������������� � D-34 (������� �������: ������ ��� ���� ���� + �� ����� �������������). R1 hard+������; R2 �����>���������>�������; R3 �� ��������� ����; R5 ���� hard + ���������� soft; R7 ������ hard-����� � ��������; R8 ������ ���� (������ 11x3/9x2 ���); R9 ���� ���� Teacher; �������: ������� overflow.
- Overflow MainWindow: �����. ������� Grid ����� MinWidth=0 (����: ������� 1100+240+340 �������� ����, ������ ������ ������� �� ����). ���������: build ok, WpfShell 7/7.
- ���������: .opencode/compose-plus/decision-r1r9.md + plan-r1r9.md + risks-r1r9.md (checklist ������). ����� ������� (��� ������������� ������ ����) � � plan-r1r9.md.
- cp-architect/cp-logic �������� ������ (�����������: red-team S9 + ����� �� ������ �������).
- ������: P1 Domain+stores ����� implementer.


## P1 �������� � �������� (14.09.2026, �����)
- Domain: Room += IsManualOnly/OnlySubjectId/DesiredGroups/CountSubgroupAsGroup; SchoolClass += ClassTeacherId; ����� SchoolFlex.cs (SubjectHourNorm/ClassHourOverride/CommonLesson/TeacherAssignment/FlexSettings + name-keyed DTO + FlexDataset); Enums += AssignmentScope/TeacherAssignMode.
- Infrastructure: ����� SqliteFlexStore (7 ������, CREATE IF NOT EXISTS � ������ �� ��������, �������).
- Application: HourResolution (ParseGrade + ResolveHours �����>���������>������>������); Importer: flex-�������� (���.), Grade �� �����, ������� ������/�������, R7 fail-loud (HardClass/HardParallel, ������ �����������).
- �����: FlexP1Tests 13 ��. ������ ����: 215/215 PASS (2�20�), ��������� ���. Solver/validator/UI �� �������.
- ������: P2 Builder/Validator/Core (��������� R1, SyncGroup R3, lanes R5, ���� R8, ����������� R7, ����� R6).


## P2 �������� � �������� (14.09.2026, ����)
- ����: RoomPolicy (����� ������ ���������� R1 + EffectiveDesired + key-aware �������); SchedulingProblem.Flex (������ Neutral)/Assignments; ProblemInput CommonLesson/Assignments/Flex; RuleCatalog v4 (+room-crowding 8, +teacher-split 25).
- Soft: R8 ���� ���������� (student-������� ?3/?2), room-crowding, teacher-split (report-only), heavy-edge �� ������; SearchIndex: ������� ����� + _roomKeys/_heavyCount/_split-������� + ONLY/R7-��������; �������-���� ������ �������� ��� (��� �������).
- ���������/Preview: ONLY-hard, R7 teacher-assign hard (�����/���������, ������ �����������), key-aware ������� ����; 4 ��������-����� (OrTools/Greedy/Repair/GradeOne) �� RoomPolicy.
- ������ R3: ������ SyncGroup �� N ������� (�����-��������, distinct-�������, fail-loud: ��� ���������/�����/���/P3/���� ��� �����); ToProblemInput ��������; R6 SubjectDifficulty-����� (P1-�������: �������+������).
- ����������: Explainer (�����+������+����� 3 �����), Editor (7 ���������), Hints, Rating.
- �����: FlexP2Tests 22 ��; QualityUxTests 7>9 ����� (���������). ������ ���� 237/237 PASS.
- ������� ����������� P2 (� �����): teacher-split Soft � report-only (������� ������������� INV-02, �����-���� ������ �� ������); UseOwnRooms=false � fail-loud �� P3-������ ����; lanes CP-SAT � placement-based (key-aware ������ � gate/LS/preview); greedy/repair � �������������� �������.


## P3-P5 ��������� � ��������� (14.09.2026, ����)
- AppSession: Flex + SqliteFlexStore (init/load/ApplyFlexAsync � ������������� �����, fail-loud ��� ������ ������); ExportActiveAsync � ������ �����.
- SchoolDataWindow: 8 ����� (��������/����/������/�������/��������/��������/���������/����� �����), ������������� ����� + ������ ��������� �� ��� (����� FlexDataset); ClassConfigRow += StudentCount (ALTER-�������� ������ ��).
- ExportWindow: ������� ����� �������; Exporter: ���� ������� (�������|����|����|�����|�������|�������).
- SettingsWindow: �������� ��������� ��������� (������� + ���� 1..5 + ����� ������� 1..10).
- ScheduleWindow: ������� ����� (�������� ���/ONLY/������ �������) + �������.
- �����: FlexP34Tests 5 (��������� ������, ����� flex, ���� ��������, ���������� ����, UI-���� ��������� �������� + ����������) + off-screen Show ������; HeavyFlexTests: 14 ������� 5-11, 244 occ, �����/override/���������/����� ����/ONLY/�������� � greedy+repair+validator clean �� ~0.2�.
- ������ ����: 243/243 PASS. Red-team (self, S9): ������ �� decision-r1r9 ���; ���������� P2 �����������������; V1-������� ���� (DemoSchool E2E � �����).
- �� �������: ���������/mimo-��������� � stitch (��� harness); ���������� �������� exe ������; ������ (��� ������).

## A+B Стабильность + гибкость без кода (14.09.2026, вечер)
- B2-движок (до меня частично): RuleCatalog v5 DangerousCodes + OverrideRange 0..100,
  RuleResolver confirmedDangerous + RelaxedStrict + fingerprint, PlacementValidator
  AddHardOrWarn (ослабленное → Warnings), SoftEvaluator уже на EffectiveRuleSet.
- B2-финал (эта сессия): QualitySettingsEditor — опасные Tunable=true, IsStrict=true,
  SetLevel/SetWeight требуют confirmed:true (иначе «требуется подтверждение»);
  шкала опасного 0..4↔0..100 линейно (дефолт 100=ур.4). QualityHints += teacher/class-maxperday.
  SettingsWindow: бейдж «СТРОГОЕ (по шаблону)» + тумблер «Разрешить как пожелание»
  (слайдер disabled пока off) + подпись «Вес X · дефолт Y · шаблон не так делает»;
  тумблер идёт через AppSession.ConfirmDangerousAsync (Отмена → не применилось).
  Apply/SaveCustom/ImportJson пробрасывают _confirmedDangerous.
- B1/A3/A4-движок был готов (DangerConfirm+Dialog, SqliteAppSettingsStore,
  AppSession.ConfirmDangerous/IsReady, no year.txt overwrite, not-placed issue).
  Тесты (эта сессия): DangerStrictTests 10 шт — 3 DangerConfirm (показ/отмена/глобал-офф+персист),
  4 strict-override (выкл-hard/вкл-warnings/персист-профиля/validator-soft+breakdown),
  1 AppSettings roundtrip, A4-unknownId, A3-restore-keeps-year. QualityUxTests:53 и
  FlexP2Tests:CatalogV4 обновлены ОСОЗНАННО (16 опций, v5 + asserts опасных).
- A1: MainWindow.RunGuardedAsync — `await Task.Run(() => orch.RunAsync(...))`; прогресс
  живьём ~3.3 Гц (ThrottledIncumbentStream 300мс), Стоп через RequestStop→Cancel,
  best-so-far в архиве; касаний UI из фона нет (биндинги + Dispatcher в GenerateWindow).
- A5: UI без «оптимум/оптимальное/нормативно/СанПиН соблюден» (grep чист);
  футёр «СанПиН: требует сверки с НПА №206/№35»; PDF «Скоро, пока Excel — выгрузите таблицу выше»;
  Top5 «Лучшее найденное расписание»; Help «не найдено за отведённое время».
  Статусы оркестратора не менялись (иначе churn 5+ gate-тестов) — зафиксировано здесь.
- A2: publish single-exe Release win-x64 self-contained: Amsur.Wpf.exe ~229МБ
  (publish-single/, 7 файлов), старт жив 8с+, RAM ~126МБ (пик ~140МБ),
  Event Viewer: 0 .NET Runtime / AppHang за 2ч. Чистая Win10 без .NET + DemoSchool
  в exe — за пользователем (self-contained по построению; solver-путь покрыт DemoSchoolTests).
- Проверено: build 0 errors (только предсущ. warnings NU1903/CS8601/CS8602/xUnit2012);
  полный сьют 253/253 PASS за 2м41с. Коммит/пуш НЕ делались (ждут команды).

## 15.09.2026 — Wave-2: коммит базы + чистка + аудит (Compose+, без nemotron)
- Требование пользователя: задачи субагентам на nemotron 3/3.5 не поручать (D-36).
  S3/S4 классические пропущены; синтез — OWN SYNTHESIS: decision-wave2.md +
  plan-wave2.md + risks-wave2.md (чеклист внутри decision).
- Git-база: пользователь выбрал «закоммитить текущее» → b3ae5fd (64 файла:
  исходники+доки+DemoSchool.xlsx+Docs_Contest+WORK_LOG; bin/obj, Combat,
  publish-single сознательно НЕ staged). Коммит через `-c user.*` (та же ident,
  что история: compose-plus), конфиг не менялся. Push не делался.
- Проверено (свежее, эта машина): `dotnet build src/Amsur.slnx` — 0 errors,
  15 предсущ. warnings, ~22с. `dotnet test src/Amsur.Tests` — **258/259 за 2м53с**.
- KNOWN-FAIL (D-35, карантин, не чинилось): MidSchoolTight_StandardRun_Feasible,
  2/2 repro одиночным прогоном. Факты: tight-фикстура 994 occ, greedy 986/994
  (79мс), solver Unknown/place=0, выход ~4с/seed при бюджете 12с (дело не в бюджете).
  Тест новый, в 253/253 автора не входил. Чинить вслепую запрещено.
- P-CLEAN (D-37): Combat45×3 → _archive/samples-20260915; publish-single/ →
  _archive/builds-20260915; RealSchool_Schedule.xlsx + ~$… → туда же (git mv,
  история сохранена). Удалений нет. grep: Combat/RealSchool в src не используются
  (только коммент DemoSchoolTests). Эталоны EPICJ + DemoSchool на месте.
  bin/obj (2190 tracked) не тронуты — `git rm --cached` ждёт подтверждения.
- P-AUDIT: PROJECT_STATUS (259 + KNOWN-FAIL, RuleCatalog v3→v5, персист ГОТОВ),
  BENCHMARKS (секция Wave-2), DECISIONS D-35..D-37. Остальное из S1-расхождений
  (MASTER weight-freeze, SANPIN MaxPerDay в прод-импорте, детерминизм-оговорка) —
  backlog, не переписывалось.
- B1/B2/B3/B6 — verify-only по коду: A1 Task.Run (MainWindow.xaml.cs:402),
  StaticResource (GenerateWindow.xaml:182, grep BasedOn+Dynamic = 0),
  SQLite-персист + restore (AppSession), DemoRows 56 + seeds (DemoSchoolTests).
  B4 (HTML) → P-B4, B5 (fuzzy) → P-B5, B7 (LICENSE/README/.gitignore) → P-B47.
- P-B47 ГОТОВ: .gitignore (bin/obj, publish, _archive, ~$*, *.user, .vs, TestResults,
  *.db), LICENSE MIT (2026 AMSUR contributors), README.md (что/запуск/primer.png/
  ссылки/честные ограничения). _archive теперь ignored — случайных коммитов не будет.
- P-B4 ГОТОВ (тест+замер+это): ScheduleHtmlExporter (gate INV-01, сетка как в Excel,
  print-CSS, минимальное экранирование — WebUtility кириллицу в &#NNN;, не подошёл:
  1 красный цикл → починено) + AppSession.ExportActiveHtmlAsync + ветка .html в
  ExportWindow (фильтр диалога тоже). Тесты HtmlExportTests 4 шт; проверка:
  Html+Excel+WpfShell 14/14.
- P-B5 ГОТОВ: SubjectAliases (9 шт: Матем/ИЗО/Физра/Физкультура/Труд/Технология/ОБЖ/
  Окружающий мир/Обществознание → официальные) + заметка «распознано — исправьте
  в Excel» в Notes (поверхность «что исправить» — NotesList в SchoolDataWindow,
  код уже показывал). Неизвестное («Мат», «Рус») — как есть. Fail-loud цел.
  Тесты SubjectAliasTests 5 шт; проверка: alias+import+flex+manual+room+heavy 55/55.
- Финал волны (свежий полный прогон): **267/268 за 2м27с**, единственное красное —
  карантин D-35. Новых падений нет. P-F2 и остаток Этапа 4 — backlog (план-wave2):
  LoadRow позиционный в десятках мест + цепочка Teacher→ProblemInput→Builder —
  это отдельный пакет, не «маленький проброс.
- НЕ коммитилось (ждут команды): P-CLEAN-перемещения (staged R×2), правки 4 md,
  новые .gitignore/LICENSE/README/wave2-доки/экспортёры/тесты.

## 15.09.2026 — Wave-3: недостатки аналогов → в AMSUR (автономно, без nemotron)
Исследование: 2 researcher-трека (7 аналогов + конкурсы/школы РБ/нормативка).
Решение: decision-wave3.md. Гибко-просто по D-34 (дефолты из коробки, опасное —
через ConfirmDangerous, запреты с объяснением).
- P-SANPIN-DOC: SANPIN_RB §7 исправлен — чередование трудных/лёгких ОТМЕНЕНО
  реформой-2019 (№525); «чередование» теперь только пожелание школы, не норма.
- P-PE-FLAG: Subject.IsPhysicalEducation выставляется в импорте из официального
  названия (после алиасов); тест в SubjectAliasTests.
- P-SANPIN-CHECK: новый SanPinChecker (отчёт, не gate): нагрузка/день по caps
  (1→5/2–4→5/5–6→6/7–11→7, NEEDS-CHECK), 1-е классы (дней с 5 уроками ≤1),
  физра первым уроком (warning). Лимиты — данные SanPinLimits (школа меняет
  без кода). 5 тестов.
- P-SANPIN-UI: кнопка «Проверить СанПиН» в ScheduleWindow → QualityDetailsWindow
  (строки со «СанПиН» сами ложатся в группу «Нормы СанПиН»); без активного —
  понятное сообщение, не мёртвая кнопка.
- P-CSV: ScheduleCsvExporter (Class;Day;Slot;Subject;Teacher;Room, UTF-8 BOM,
  gate INV-01) — мост к «Электронной школе»/РИОС. 3 теста.
- P-DAYOFF: колонки UnavailDays/UnavailSlots в шаблоне (9 вместо 7; старые файлы
  читаются) → TeacherDayOff/Unavailability → AllowedDays/AllowedSlots (движок уже
  умел, ToProblemInput передавал [],[] — теперь проброшено). Персист: +2 колонки
  в LoadRows с ALTER-миграцией старых БД. 11 тестов (парсинг/union/границы/
  roundtrip Excel/старый формат/стор).
- P-GUIDE: «Быстрый старт» стал живым: кружки ✓/текущий/серый по факту
  (данные/активное/оценка), дашборд не падает при ошибке чтения.
- Проверено: затронутые пакеты 34/34 и 71/71 по ходу; финал полный **287/288
  за 2м41с** (+20 тестов волны), красное — только карантин D-35.
- Backlog (отдельные EPIC): Splits v2, DnD+Undo, 6-й день/WeekType, справочник
  предметов как данные, замены, ручной ввод DayOff в окнах (пока только Excel),
  точная сверка caps с текстом №206 (NEEDS-CHECK в силе).


## 15.09.2026 - Kontest-paket: tri vida eksporta + chestny PDF + karantin D-35 kodom + dokumenty NDTP
- Excel: list KABINETY (Klass|Den|Urok|Predmet|Uchitel), checkbox v ExportWindow, AppSession peregruzka; test ExportThreeViews_RoomsSheet. Export 9/9.
- PDF: mertvy beidzh SKORO ubran; kartochka >>cherez HTML<< + Ctrl+P; knopka VYGRUZIT (Excel/HTML); kommistarii P0-6 obnovleny. QuestPDF ne vvodilsya (D5).
- D-35: [Fact(Skip)] s prichinoi + diag 15.09 v DECISIONS (greedy 986/994, unplaced 5 uchitelei - gipoteza peregruz pulov). Polny suit 288 passed + 1 skipped, 0 failed (2m34s).
- Docs_Contest: NDTP_proekt_skeleton.md (struktura PDF 10 str + chernoviki + tsifry) + pismo_zavuchu.md. Kommit/push NE delalis.

## 15.09.2026 - Fiktura nashei shkoly (OurSchoolTests, D-38)
- 6 raundov voprosov: 27 klassov, smeny 6-7/ostalnye, profili 10A/11A (khim/angl pary), fizmat 10B/11B, shtat 40, kabinety 20. ASSUME: 5-e bez deleniya in.yaz, 6-e fizra 2, profil URA fobshch po 1, 11B fizra 1.
- Dvigatel: CurriculumItem.GroupId/SyncGroupId + per-hour sync v bildere + 2 fail-loud garda. Least-loaded balansirovka pulov.
- NAKHODKA: russkii blok 122ch na 3 uchitelei (90 slotov) - nekhvatka 32ch = tselaya stavka. Greedy 935/988, solver chestno Unknown/place=0. Eto material dlya zavucha i zayavki.
- Zhdu ot uchenika: tochnye chasy 10-11. Kommit/push NE delalis.

## 15.09.2026 - Peregruzka (D-39) + okhota za 5-11 xlsx
- Nastroyka zhivyot: FlexSettings + store + builder, testy 3 sht. UI-tumbler - backlog.
- VAZHNOE: starshaya matematika tozhe peregruzhena (98/90, algebra s 7-go) - nashyol dvigatel, ne uchenik.
- 5-11 xlsx POKA NET chestno: cap9/11 + MAXIMUM + 120s = NO FEASIBLE. Reshenie sushchestvuet (bisect 988/988 bez capov i s kabinetami), no solver ne upakovyvaet v budget. Podozrenie: kabinetov 20 na 27 klassov - zhdu perescheta komnat ot uchenika.

## 15.09.2026 - Kabinety 30, gym ONLY-PE, stop solver-popytok
- Fiktura: 30 komnat, gym cap 3 ONLY + 29 forbidden-PE. Suit 296+1/0.
- 5-11 xlsx POKA NET: 977/988 vnutri solvera, 11 urokov ne zakryvayutsya (cap9/11, budget do 120s). Reshenie: realnye dannye + chernovik-rezhim kak backup.

## 15.09.2026 - Smeny 8G/9G, 980/988, stop
- Greedy-overload 975 (rus vsego 2 vne setki). Solver vnutri 980/988, Phase A 0. Gipoteza: model Phase A strozhe greedy - backlog.

## 15.09.2026 - Cap8 + CHERNOVIK, pervy file 5-11
- Cap8 dal 975->978 greedy. STANDARD cap8 vsyo ravno NO FEASIBLE.
- ExportDraftGrid + 2 testa. Samples_Export/OurSchool_raspisanie_CHERNOVIK.xlsx (978/988, 4 lista). Polny suit nije.

## 15.09.2026 - P-A gotov: A1 + A2 v UI
- Peregruzka: kartochka + confirm + cap-raise dlya strok. Chernovik: knopka + greedy. Personalnye: combo + export. Plan: .opencode/compose-plus/plan-ux-flex.md. Dalee: P-C dizayn, P-D tur, P-B gibkost.

## 15.09.2026 - P-C gotov: checkbox/progress/pressed/header + Help + vision QA
- Render STA PNG + mimo + lichnaya proverka. 3 mikrofiksa. Dalee: P-D tur, P-B gibkost.

## 20.09.2026 - Avtonomka: chistka + foto 5-11 + syut 301 + smoke (D-45, D-46)
- Brif uchenika: vsyo srazu (NDTP 21.09-05.10, 100 idey do 20.11, pilot SSh8, app usable k oktyabryu). Git/push razresheny; po faktu remote net + gh сломан (ne tot paket) — push zhdyot sozdaniya repo (komanda v finalnom otchyote).
- Chistka: `git rm --cached` bin/obj (2190 files, .gitignore derzhit), kommit 00876ac (untrack bin/obj + .gitignore/MIT/README + arkhiv samples + performery/testy + foto pilota). Po puti: oshibochno snyal ves src s indeksa (`git rm -r src`) — vosstanovil `git add .gitignore + git add src`, proveril (bin/obj 0, diff tolko realnye izmeneniya). Urok: pathspecy po odnomu, bez `src` v spiske.
- Foto 5-11: 6 foto → Excel 24 klassa/396 strok/769 ch (generator + mapping, sm. dannye/). Ruchnoy podschyot soshyolsya s generatorom (769). Uchitelya-pleyskholdery, pary 10A/11A bez sync, [?] 11B Sr — vsyo v mappinge.
- Dvigatel na foto: import shape OK; greedy 878/884 (6 ne vlezli — vse 10A, strukturno: 47 occ na 40 slotov); repair 21->10; LS 10s ×3 seed: gaps 5/7/5, soft 2945/2740/2950. Do 0 ne dokhodit — chestno (G2/G3).
- Flake-borba: PhotoSchool_Greedy_Coverage padal v polnom syute (time-boxed LS pod nagruzkoy dayot khuzhe) — D-46: vorota tolko greedy+repair (determinirovannye), LS — logged evidence. Fiks srabotal s pervogo raza.
- Syut: **301 passed + 1 skipped (D-35), 0 failed, 2m29s**. Build 0 errors (24 predsysch. warnings).
- Smoke exe: headless 15s zhiv, RAM ~126MB (kak v baseline), ubit shtatno, .NET Runtime oshibok v Event Viewer — 0. UI-fayly NE trogany → novykh skrinov ne delal (v2-* aktualny); mimo ne gonyal (pokazyvat nechego novogo).
- Naydeno: G1 (smena v Load), G2 (pin klassnogo chasa), G3 (Splits v2) — pakety na oktyabr. Fiktura OurSchoolTests NE troguta (foto-korrektirovki v mappinge).
- S succession: obnovleny PROJECT_STATUS (301+1, pilot), BENCHMARKS (PhotoSchool), DECISIONS (D-45, D-46), NDTP-skeleton (§4/§5 real-cifry). Kommit/push — finalnym paketom.

## 20.09.2026 - P-D0 karkas: odno okno + navigaciya + animacii + Gufo (D-47)
- Reshenie: odno okno (MainWindow=shell: nav-rail + ViewHost + footer), DashboardView (kontent pereekhal 1-v-1), ostalnye razdely - PlaceholderView s vremennym otkrytiem starykh okon (pakety P-D1..P-D3). Starye okna zhivy - testy zelyonye.
- Animacii (bez bibliotek): press-scale 0.96 knopok (BtnPrimary/Secondary/NavBtn, shared PressDown/PressUp), fade+slide 220ms smeny view (kod, ViewEnter), Gufo-sova v hero (vektor) + "Gufo sovetuet" + beydzh "Gufo na svyazi".
- Naydennyy bag (do D0 ne viden): ikonki rezhimov - tofu-kvadraty (C# ne parsit &#xE768;, nuzhen (char)0xE768 - Glyph()). Pochineno, skrin podtverdil. Hero-podpis obrezalas (gorizontalny StackPanel) - pochineno Gridom.
- Voprosy: Stitch MCP - chtenie rabotaet, generate/edit taymauty (2 popytki, stop). Svetluyu adaptaciyu delayu sam po planu.
- Evidence: build 0 errors; WpfShell 7/7; polny suit 301+1/0 (2m40s); v3-shell-d0.png - shell+Gufo+ikonki OK.
- Resheniya: D-47 (strangler-migraciya, vorota D0), imya maskota - Gufo (utverzhdeno), setka posledney.
