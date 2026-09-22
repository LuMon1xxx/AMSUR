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

## 20.09.2026 - P-D5 tyomnaya tema + UiTestHost + FINAL REDESIGN (D-49)
- Dark.xaml (polny nabor tokenov) + pereklyuchatel v nav + persist ui.theme + perezapusk. Podmena slovarya na starte; Static->Dynamic v stilyakh App.xaml (inache smena ne beretsya - proof skrinami). TextElement.Foreground=BText na kornyakh vseh views (inache chyorny tekst na tyomnom).
- Yad batcha: DynamicResource + neskolko STA-potokov = VerifyAccess. Reshenie: UiTestHost (odin STA na progon, odin App). Poputno vskrylos: zakrytie pervogo okna gasilo App (ShutdownMode iz XAML perezatiral kod) - fiks poryadkom. UI-testy 13/13 za 4s (bylo 6s+).
- Ssyut FINAL: 301+1/0. Commit 37c3edb. Vse pakety V3 (D0/D1/D3/D4/D2/D5) gotovy; setka posledney kak prosili; Гуфо utverzhdyon.
- D-49: tyomnaya cherez Dynamic + restart-flow (live-pereklyuchenie bez perezapuska ne delayem osoznanno - stabilnost).

## 20.09.2026 - P-D0 karkas: odno okno + navigaciya + animacii + Gufo (D-47)
- Reshenie: odno okno (MainWindow=shell: nav-rail + ViewHost + footer), DashboardView (kontent pereekhal 1-v-1), ostalnye razdely - PlaceholderView s vremennym otkrytiem starykh okon (pakety P-D1..P-D3). Starye okna zhivy - testy zelyonye.
- Animacii (bez bibliotek): press-scale 0.96 knopok (BtnPrimary/Secondary/NavBtn, shared PressDown/PressUp), fade+slide 220ms smeny view (kod, ViewEnter), Gufo-sova v hero (vektor) + "Gufo sovetuet" + beydzh "Gufo na svyazi".
- Naydennyy bag (do D0 ne viden): ikonki rezhimov - tofu-kvadraty (C# ne parsit &#xE768;, nuzhen (char)0xE768 - Glyph()). Pochineno, skrin podtverdil. Hero-podpis obrezalas (gorizontalny StackPanel) - pochineno Gridom.
- Voprosy: Stitch MCP - chtenie rabotaet, generate/edit taymauty (2 popytki, stop). Svetluyu adaptaciyu delayu sam po planu.
- Evidence: build 0 errors; WpfShell 7/7; polny suit 301+1/0 (2m40s); v3-shell-d0.png - shell+Gufo+ikonki OK.
- Resheniya: D-47 (strangler-migraciya, vorota D0), imya maskota - Gufo (utverzhdeno), setka posledney.

---

## 22.09.2026 — Автономная итерация «Демо в 1 клик» (бриф: гибче и удобнее, просто новичку)

**Точка отката:** ветка `backup-autonomous-20260922` + тег `pre-improve-20260922`
(HEAD e9a5ec5). Грязные файлы пользователя НЕ тронуты, их дифф —
`.opencode/compose-plus/pre-improve-dirty.diff`. Бриф —
`.opencode/compose-plus/brief-autonomous-20260922.md`.

**Проблема:** главный барьер новичка — ввод данных (шаблон → 50+ строк → загрузка).
Демо-набор жил только в тестах (`DemoSchoolTests.DemoRows`), в продукте его не было.

**Сделано (только продукт + тесты, движок/веса/валидатор не тронуты):**
1. `src/Amsur.Application/DemoSchoolData.cs` (new) — демо-школа как данные продукта
   (6 классов, 56 строк, 1 A/B-сплит; дни 5, слоты 7). Единственный источник.
2. `AppSession.ImportDemoAsync()` (source="demo") + подпись «Демо-данные» на дашборде.
3. Кнопка «Нет файла под рукой? Попробовать на демо-данных →» на дашборде
   (видна только пока нет данных) + кнопка «Демо» в «Школа и данные».
4. `DemoSchoolTests.DemoRows()` теперь делегирует `DemoSchoolData.Rows()` (паритет по построению).
5. `src/Amsur.Tests/DemoDataTests.cs` (new, 4 теста: форма, построение проблемы 131 occ,
   импорт в сессию, паритет) + asserts демо-кнопок в `NewWindows_Construct`.
6. `ПРОСТАЯ_ИНСТРУКЦИЯ_ЗАВУЧУ.md` (new) — 1 страница: 5 шагов + частые вопросы.

**Проверено:**
- `dotnet build src/Amsur.slnx` — 0 ошибок.
- Точечные: DemoData 4/4 + HasExpectedShape + NewWindows_Construct — 6/6.
- Полный сьют: **307 пройдено + 2 skipped, 0 failed за 2м21с**
  (skips: карантин D-35 MidSchoolTight + пользовательский ScratchGen — оба были до меня).

**Ограничения (честно):**
- Скриншоты GUI не делались: в этой среде нет дисплея для достоверного
  GUI-скрина WPF-приложения; вместо этого — STA-конструкт-тесты окон (XAML реально
  материализуется, pattern repro 13.09.2026). Vision QA (mimo) не запускался —
  показывать было нечего.
- Клик-путь «нажать Демо → Сгенерировать» покрыт unit-уровнем (сессия + проблема),
  не E2E-мышью (клик-автоматизация в этой среде ненадёжна — см. запись 14.09.2026).
- Коммит/пуш НЕ делались (ждут спроса).

---

## 22.09.2026 — Дизайн-итерация D4 «Красивый дашборд» (по запросу: красота + скрины)

**Субагенты недоступны:** cp-uiux (2 попытки), mimo (1 попытка) — все упали с
ошибкой провайдера «free tier can only be used from within OpenCode». По протоколу
fallback (раздел 9): синтез делал сам, независимость компенсирована парой
скринов до/после + чек-листом по пикселям. Критик отдельно не запускался
(нечего критиковать извне) — риски разобраны ниже.

**Скрины без дисплея:** временный harness `DesignShotScratch` (RenderTargetBitmap,
окно показывается за экраном −10000, как в WpfShellTests) → реальные PNG 1600×900
в `.opencode/compose-plus/screens/`. По пути пойманы 2 артефакта harness (не продукта):
1) рендер неспоказанного окна = чёрный квадрат; 2) ширина 1440 при дефолте окна 1600 =
«обрезка» правой колонки (ложный баг, продукту не касается). Harness после съёмки удалён.

**Пакет D4 (только дашборд + общие токены, обе темы целы):**
1. `App.xaml`: HeroBrush 2→3 стопа (#2563EB→#4F46E5→#7C3AED, диагональ круче) +
   новый ресурс `BannerShadow` (чёрная 0.18, Blur 18 — в тёмной теме нейтральна).
2. `DashboardView.xaml`: тень на NextStepCard + hero, hero-заголовок 18→20,
   padding hero 20→24, иконки мини-карточек 36→40 (radius 9→12), QualityBar 6→8.
3. Движок/логика/тексты не тронуты; фейковых данных нет.

**Проверено:**
- До/после: `v1-dashboard-empty/demo.png` → `v2-dashboard-empty/demo.png`
  (1600×900). Самопроверка по пикселям: обрезок/наложений/tofu нет, иконки на месте,
  правая колонка цела, тени мягкие. Добро на v2 — визуально богаче при том же порядке.
- `dotnet build` — 0 ошибок; WpfShell 6/6; полный сьют **307+2/0 за 2м22с**.
- НЕ проверено: тёмная тема глазами (правка её не касается — только общие ресурсы;
  рендер тёмной в harness не поднимал, честно фиксирую).
- Коммит/пуш НЕ делались.

---

## 22.09.2026 — ФИНАЛ для учителей по дням (формат «заполненные/Учителя - <день>.xlsx»)

**Сделано:** лист «Учителя» из `Сгенерировано_движком_ФИНАЛ.xlsx` (888 строк) разложен
в 5 файлов-сеток «учитель × уроки 1–12»:
`данные/тест_реал/Сгенерировано_движком_ФИНАЛ_по_дням/Учителя - <день>.xlsx`
(Пн 183, Вт 193, Ср 174, Чт 168, Пт 170 = 888, потерь 0; даблов учитель/день/урок в
источнике нет — проверено ассёртом). Формат 1-в-1 с эталоном: A1 «Преподователи»
(опечатка оригинала сохранена сознательно), B1 день + merge B1:M1, row2
«Преподователь»,1..12, ширины A=23.78/B..M=13, стилей нет (в эталоне их тоже нет),
фамилии — первое слово, классы строчно («9В»→«9в»). Лист понедельника назван полностью
(в эталоне обрубок «Понедельни»). Скрипт: `Temp\opencode\gen_teacher_days.py`.
Папка новая (не смешиваю вывод движка с верифицированными «заполненные»).
**Проверено:** dims R46C13 ×5, заголовки/мердж/ширины, спот-чек Абрамова-Пн побуквенно
с ФИНАЛом. Движок/код проекта не тронуты.

---

## 22.09.2026 — Сравнение окон учителей: движок vs школа (честно, по клеткам)

**Метод:** только клетки сеток 1..12 (ФИНАЛ_по_дням vs заполненные/Учителя), окно =
пустой урок между первым и последним уроком учителя в день. Скрипт
`Temp\opencode\compare_gaps.py` (+ pupils/shifts-анализ).
**Итог:** ученики — ничья 0:0 (120/120 чистых классо-дней у обоих); учителя —
школа лучше вчистую: 143 vs 315 окон (14.5 vs 35.5 на 100 уроков), дней с окнами
43% vs 61%, парное сравнение по 42 общим учителям 28:5:9 в пользу школы.
Из 315 окон движка 250 — в днях через обе смены (у школы 118 при тех же ~95 таких
дней). Объёмы разные (986 vs 888 уроков, 49 vs 44 учителей) — поэтому главные
метрики нормированные. Оценки (субъективно): школа 8/10, движок 5/10.

**Разбор «откуда +98 уроков и +5 учителей» (досчитано до конца):**
- Учителя: все 44 движковых есть в школе (engine-only пусто). +2 — те же люди с другим
  написанием (Гордионок=Городионок, Плужнова=Плужникова; оба варианта гуляют даже внутри
  школьных файлов). +4 — люди вне нагрузки: Денискин (14 ур. 10–11, в нагрузке эти часы
  на Шаболтиева), Мазынская/Маляревич (началка 3в/3г), Бесфамильно (дырка, 4 клетки).
- Уроки +98 = 58 началка (1–4, движок считает только 5–11) + 39 служебных клеток
  (цех 8, к/ч 23, над./аст./3кл.над. 8; движок такого не моделирует) + 1 погрешность.
  In-scope (5–11 без служебного): школа ~889 vs движок 888 — объёмы совпадают.
- Честный in-scope пересчёт окон: школа 160 (18.2/100; было 143 — дежурства ЗАМАЗЫВАЛИ
  дыры) vs движок 315 (35.5/100). Вывод не меняется: школа лучше почти вдвое; плюс у
  движка нагрузка размазана на 204 учителе-дня против 176 (тоньше → рванее).

---

## 22.09.2026 — Файл УЧИТЕЛЯ_ПРЕДМЕТЫ_КЛАССЫ + проверка стабильности (по просьбе)

**Сделано:** `данные/тест_реал/УЧИТЕЛЯ_ПРЕДМЕТЫ_КЛАССЫ.xlsx` — лист «Учителя»
(45 учителей, 119 связок учитель+предмет: ФИО | предмет | классы с часами |
всего часов; сплиты помечены «+подгр.») + лист «Проверка». Скрипт `Temp\opencode\roster.py`.
**Проверено:** 396 пар класс+предмет в ФИНАЛ — **нестабильных 0** (один и тот же
учитель во все дни; сплит-пары A/B стабильны парой); дублей класс+предмет
в нагрузке — 0. Случая «в Пн один учитель, во Вт другой» в нашем расписании нет
(в движке это невозможно по построению: TeacherId фиксирован, INV-02 — проверка
это подтверждает фактами). Код проекта не тронут.

---

## 22.09.2026 — Нагрузка: Денискин вернулся (6ч физики/астрономии) + файл ИТОГИ

**Несостыковка (задана пользователю, отвечена):** «часов Денискина у Шаболтиева» нет
нигде — 14 уроков Денискина в школе это замены; у Шаболтиева только 4 ДМП-сплита
(как у движка). Пользователь: ДМП остаётся Дамашевич+Шаболтиев (мальчики/девочки),
физику — Денискин и другие физики.
**Сделано:** `make_real_load.py` FIX 21→20: физика 10А/11А (2+2) + астрономия 11А/11Б
(1+1) → Денискин Е.В.; Мисевич/Александрович/Гордионок доли keeps. Бэкап нагрузки,
дифф ровно 4 клетки, 396 строк, вакансий 0. Проверено: RealTeacherTests 2/2,
greedy 864/890 (без регресса), сборка 0 ошибок.
**Эксперименты (временный ExpScratch, удалён):** чинка «край→дыра» 1116 проверок →
1 принята (−2 дыры, soft +4: отказы room 939 / group 728 / domain 377);
веса TEACHER_FRIENDLY −3% (351→341). Вывод: дыры несущие, веса не решают.
**Файл:** `данные/тест_реал/АНАЛИЗ_ЧЕЛОВЕК_VS_АЛГОРИТМ.md` (метрики без служебного,
5 причин с доказательствами, план «лучше в разы»: activation-cost + цепной LNS
по топ-16 + плотный greedy; приёмка ≤160 окон при pupilHard==0).

---

## 22.09.2026 — Нагрузка v2 (Мисевич только математика, биология 6А → Чепикова) + план ①–③

**Правки (проверено join'ом сеток):** физика 10Б/11Б (8ч) → Денискин (итого 14ч);
Мисевич 22ч только математика (нарушений 0); биология 6А → Чепикова У.В.
(Пт-6 по сеткам; параллели тоже она). Дифф нагрузки ровно 7 клеток vs бэкап.
Проверено: RealTeacherTests 2/2, roster перегенерирован (45 учителей, 117 связок,
стабильность ФИНАЛа 0), сборка 0 ошибок.
**① activation-cost:** каталог v6 + `teacher-active-day` (default 0 — поведение не
меняется) + SoftEvaluator + SearchIndex-паритет + Explainer-имя; ActiveDayTests 5/5.
Замер CUSTOM-веса 0..10: ~0 эффекта (закрыть день одиночными ходами нельзя).
**② TeacherDayLns** (топ-16 + contention + compact-Replant + строгая лексикография):
без hint −2%; с hint −20% (377→~285–310, шум time-box D-46); gapsFirst-режим хуже
(333 + soft +32% — soft и дыры связаны). TeacherDayLnsTests 5/5.
**③ compact-greedy** (opt-in, default выкл): сам финал не улучшает + −2 покрытия,
но в связке compact+VND30+LNS90 = **271 (−28% от базы, pupilHard=0, soft −25%)**.
**Граница: 271 vs 160 школы (1,7x) — человеческий уровень НЕ достигнут.**
Полный сьют: **317+2/0**. Детали и следующий заход (multi-seed, полный ФИНАЛ) —
в АНАЛИЗ-файле §6–7. Решение D-50 в DECISIONS.md.

---

## 23.09.2026 — Ответы пользователя + чем пожертвовал человек (ответ: ничем)

**Применено:** фамилии Городионок/Плужникова в генераторе (+комментарий, StableKey
сменились — персист пилота не матчится), ScratchGen-имя поправлено; нагрузка
перегенерирована (дифф только переименования + прошлые 7 клеток), RealTeacher/
PhotoSchool 4/4 зелёные; roster перегенерирован (117 связок, стабильность 0).
Замены Денискина — вне скоупа (не вносим); Бесфамильно/Мазынская/Маляревич/аст —
без действий до завуча; математика 10Б за Мисевичем оставлена.
**Анализ норм (СанПиН-кэпы 5–6≤6, 7–11≤7):** перегруз дня класса — школа 1 vs движок 4;
поздние старты 0:0; окна учеников 0:0; перегруз учителя 0:0 (макс 10 оба);
**дубли: школа 27 (89% рядом, сдвоенные) vs движок 95 (28% рядом)** — главная
механика проигрыша: человек раскладывает ровно + дублит сдвоенно, движок дублит
в 3,5 раза чаще и разбрасывает (MaxPerDay=2 дубли из 2 не штрафует). Следующий
рычаг: adjacency-терм для дублей (потенциал ~−50..100). Всё в АНАЛИЗ-файле §8–9.

---

## 23.09.2026 — Прогноз «если всё исправить»: ~240–250, не уровень человека

**Вопрос пользователя:** сколько окон ожидать после всех исправлений.
**Метод:** строгая атрибуция по листу «Учителя» ФИНАЛа (888 строк; контроль:
дыры сошлись 315 в 315 — расчёт точный; по пути пойман и отброшен артефакт:
лист «Расписание» показывает только 1-ю смену 1–7 и давал заниженные 147).
**Итог:** из 315 дыр только 32 лежат строго между слотами разбросанных дублей
(верхняя граница adjacency-терма; реально −15..−25). Остальные 283 (90%) — общий
разброс одиночек. Прогноз combined: ~271 (LNS) − ~20 ≈ **240–250 (1,5x от 160)**.
Для ≤160 нужны multi-seed/full-day ruin/структура нагрузки. В АНАЛИЗ-файле §8.

---

## 23.09.2026 — Сессия сохранена, продолжение завтра
Всё состояние сессии — в .opencode/compose-plus/session-teacher-gaps-2026-09-23.md
(точки отката, нагрузка, замеры, код, решения, открытые вопросы).
Сьют на ночь: 317+2/0. Ждут добра: отбор по дырам, doubles-терм, полный
TF+LNS-прогон (перезапишет ФИНАЛ), activation default. Спокойной ночи.
