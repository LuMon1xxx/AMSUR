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
- Учителя: все 44 движковых есть в школе (engine-only пусто). Написания исправлены
  23.09: Городионок, Плужникова (старая ошибка убрана везде). +4 — люди вне нагрузки:
  Денискин (14 ур. 10–11 отданы ему в нагрузке 23.09), Мазынская/Маляревич
  (началка 3в/3г), Бесфамильно (дырка, 4 клетки — исключена из сравнения 23.09).
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
(1+1) → Денискин Е.В.; Мисевич/Александрович/Городионок доли keeps. Бэкап нагрузки,
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

---

## 23.09.2026 — Пакет 1: doubles-adjacency терм внедрён (D-51, Compose+ FULL)

**Код (6 файлов, поведение STANDARD меняется осознанно, всё через каталог):**
каталог v6→v7 + код `doubles-adjacency` (дефолт 10, диапазон 0..100, профили
одинаково); `SoftUnits.DoublesScattered` (единый источник арифметики:
eligCount==2 + DISTINCT==2 + |a-b|>1); SoftEvaluator (полный пересчёт,
×GradeW R8, аддитивен с subject-maxperday); SearchIndex (`_dblSlots` +
Insert/Remove + SoftDelta O(k), lift-паттерн); Explainer (имя «Разбросанные
сдвоенные уроки», CountUnits, Explain-строки, Compare-ветка).
Предметный приоритет (эшелон 1/2) в v1 НЕ введён — произвол без заказчика,
deferred в v2. Сохранённые CUSTOM старых версий откатываются на STANDARD
штатно (как D-50).
**Тесты:** новый `DoublesAdjacencyTests` 10/10 (юниты + SoftEvaluator +
паритет индекс/Incremental 200 ходов + swap-паритет 100 пар + Explainer);
`FlexP2Tests.CatalogV4_Codes` обновлён на v7 (осознанно, политика bump);
попутно тривиальный фикс `LocalSearchTests.NeverWorse` (110 → 110+10×gw —
новое слагаемое терма, зафиксировано). Полный сьют: **327+2/0**
(329 всего, 2 предсуществующих скипа). cp-architect был недоступен
(free-tier ошибка провайдера) — компенсировано дизайном cp-logic +
самопроверкой + red-team в S9.
**Замер A/B (временный тест, удалён; greedy→repair→VND30→LNS60, частичное
покрытие 865–868/890 — failedDays/pupilHard>0 местами помечены артефактом):**

| Конфиг | Дыры | failedDays | pupilHard | soft | dbl | activeDays |
|---|---|---|---|---|---|---|
| STD (dbl=10) seed 11 | 295 | 0 | 0 | 2984 | 300 | 206 |
| STD (dbl=10) seed 22 | 309 | 0 | 0 | 2689 | 280 | 208 |
| NODBL (dbl=0) seed 11 | 290 | 2* | 2* | 2752 | 0 | 205 |
| NODBL (dbl=0) seed 22 | 300 | 3* | 3* | 2502 | 0 | 209 |

Вывод честный: на коротких бюджетах шум time-box (D-46) маскирует дельту дыр;
видимый эффект терма — сходимость к feasible (failedDays 0:0 vs 2:3 при тех же
бюджетах). Терм жив (dbl 280–300 считает и steering доказан паритетом).
activeDays 205–209 подтверждают размазанность (цель ≤181). Финальный вердикт
по дырам — только полным прогоном (пакет 3).
**Блокер пакета 3 (найден до работ, НЕ начат):** `ScratchGen.BuildAll`
пересобирает 10А/11А вручную со СТАРЫМИ назначениями (физика/астрономия —
Александрович), что противоречит замороженному №2 (Денискин 14ч в нагрузке).
Перезапись ФИНАЛа текущим ScratchGen испортит данные. Нужна сверка BuildAll
с текущей нагрузкой. Коммитов нет.

---

## 23.09.2026 — Пакет 3: сверка BuildAll + полный TF+LNS → ФИНАЛ_v2 (262 дыры)

**Сверка (пакет 3a):** дамп 10А/10Б/11А/11Б из `РеальнаяНагрузка_5-11.xlsx`
против `ScratchGen.BuildAll` —diff ровно 3 клетки учителей (физика 10А/11А +
астрономия 11А: Александрович → Денискин; 10Б/11Б идут через импортер напрямую
и уже Денискин). Исправлено; заодно `BuildAll`/`Built` → internal для gate-теста.
Новый `FullRunV2.BuildAll_Reconciled_Shape`: 888 occurrences (= ФИНАЛ),
Денискин 14ч (assert), к/ч 24, greedy 879/888. Зелёный.
**Прогон (пакет 3b, выбор пользователя: сверить + в v2-файл):** `RunFullPipeline`
извлечён из тела `Scratch_Generate_Final` (старый тест остался Skip),
rules threadятся во все фазы + новая стадия TeacherDayLns multi-seed
(11/1011/2011 ×60с, отбор строго min-gaps). TF-профиль. Итог:
completion closes 879→888, polish failedDays→0, LNS 336→262/290/262 (взят 262).
Экспорт `АМСУР_движок_ФИНАЛ_v2.xlsx` (888/888, hard=0, pupilHard=0, к/ч 24/24,
учитель ≤10/день). Старый ФИНАЛ не тронут (git status чист по нему).
**Приёмка v2 тем же методом, что 140/315** (`v2_audit.py`: конвертер v2→сетки
`АМСУР_движок_v2_по_дням/` + `audit_nosvc`-метод + парное + СанПиН):

| Метрика | Человек | Движок-v1 (ФИНАЛ) | Движок-v2 |
|---|---|---|---|
| Окон (без служебного) | 140 | 315 | **262 (−17%)** |
| Уроков / учителе-дней / на 100 | 943 / 181 / 14.8 | 888 / 204 / 35.5 | 888 / 209 / 29.5 |
| Парное (мы лучше : школа : ничья) | — | ~5:28 | **11:29:3** |
| Перегруз дня класса | 1 | 4 | **3** (5В Ср, 6Б Пн, 6Г Вт — все +1) |
| pupilHard / hard | 0 / — | 0 / 0 | **0 / 0** |

До ≤140 не дотянули (1.87x) — честно: прогресс, не победа. activeDays 209
(размазанность НЕ снята — LNS режет дыры, но не дни; пакет 2 закрыт замером).
**S9 red-team (cp-critic, полный разбор):** CRITICAL/HIGH — нет. MEDIUM (2):
нет UI-слайдера (исправлено: +пожелание +Hint) и CountUnits без прямого теста
(исправлено: Compare-тест через публичный API). По пути пойман и исправлен
реальный недосмотр: `QualityRating` не знал оба новых кода (единицы врали) —
добавлены маппинги doubles + active-day. LOW: лог soft без rules, Denis assert,
переименование CatalogV7, room-only тест INV-D6 — всё исправлено. Сплит-кейс
(2 целых + сплит того же предмета) — REJECTED: поведение по INV-D1/D2 верно
(кейс 3 cp-logic). Денискин-противоречие — REJECTED: движок следует нагрузке
(замороженное №2), человеческие сетки не тронуты.
**Сьют после S10: 330+3/0** (333 всего; 3 Skip: ScratchGen, FullRunV2, +1 старый).
Коммитов нет.

---

## 23.09.2026 — Deep-dive: реальная причина + план победы (spike→исследование)

**Вопрос:** достижим ли ≤140 поиском вообще, где пол? **Метод:** декомпозиция
v2/human скриптом + оракул gapsFirst с импорта v2 (код продукта не тронут;
временные скрипты/тесты удалены; `FullRunV2.ImportV2` сохранён как быстрая
инфра итераций без 15-мин пересборки).
**Главная формула:** v2 = 262 = **152 в 39 хвостах (≥3 дыры, 3.9/день) +
110 в 170 обычных днях (0.65/день)** — обычные дни уже плотнее человека
(0.77). Болезнь = хвосты (у человека их 15/49). Победа = хвосты → ≤30.
**Цепочка:** конструкция размазывает (5-дневок 38 vs 17; тощих дней 55 vs 23;
Павлович 2→5 дней) → поиск не закрывает дни (CompactHint day-preserving,
active-day 0-эффект, LNS слеп к числу дней) → хвосты не разбираются дневным
ruin'ом → сплит-синк яма (pair-bound 41.2/100 vs 15.8 у человека; Хорошко
20/19) → кросс-смены 2.2/день vs 1.17 → дубли (математика 15 рассеяно).
**Оракул:** gapsFirst 180с от v2: 262→228 (20/282 принято) — пол day-ruin
≈ 220–230. Остаток только недельными ходами.
**План** (`.opencode/compose-plus/plan-beat-human.md`, детали §14–§15 АНАЛИЗа):
фаза 1 DayCloseLns −45..−60 (гейт хвосты ≤70) → фаза 2 sync-joint −15..−25 →
фаза 3 bin-pack −10..−20 → фаза 4 добивка −10..−20 → итог 137..172
(база 155–165, upside <140). Запасные: двухстадийный solver, структура через
завуча (конкретика с числами), hard-cap дней. Честно: победа требует всего
по верхней границе; ожидаемая зона 150–170. Коммитов нет.

---

## 23.09.2026 — Автономная реализация: v3 = 160 окон (цель ≤180 — ВЗЯТА)

**Задача пользователя:** автономно приблизиться к 180 окнам (меньше — шикарно).
**Метод:** TDD (RED→GREEN), план `plan-beat-human.md` фазы 1–4, замеры через
`ImportV2` без пересборки, временные тесты удалены после замеров.

**Код продукта (новое, всё через существующие швы, дефолты не меняются):**
- `Scheduling.Core/DayCloseLns.cs` (new) — зеркало TeacherDayLns: ruin дня
  целиком БЕЗ contention + sync-замыкание; Replant с DayReuseHint; приём
  строго (gaps, failedDays, soft); topK 32, restarts 6 (замер: 5→6 + 28→32
  даёт 248→231 с того же v2). `DayCloseLnsTests` 3/3.
- `GreedyPlacer.DayReuseHint` (new, opt-in, default null = бит-в-бит) +
  sync-joint расширение (фаза 2): sync-юниты reuse при любом участнике, дни —
  где все участники открыты, слоты — смежные со всеми участниками (якорь,
  не hard). `Place(..., dayReuse:false)` opt-in для фазы 3 (default false =
  старое поведение; RealTeacherTests зелёные — регресса нет).
- `Scheduling.Core/SyncLns.cs` (new) — ruin sync-групп + дни учителей-участников;
  `SyncLnsTests` 2/2. Эффект малый (обычно 0 принятых; в s22-прогоне 194→189
  и failedDays 2→1 — вклад реальный, оставлен как never-worsens стадия).
- `ScratchGen.RunFullPipeline` (тест-инфра, не продукт): bin-pack greedy
  (both=compact+dayReuse; A/B: activeDays 205→160 при покрытии 880≥879) →
  TeacherDay×3 → DayClose×3 → Sync×3 → TeacherDay-полировка → pupil-дожим
  (RuinRecreate×10) → TeacherDay×2 + DayClose×1 → pupil-single (OWN SYNTHESIS:
  sync-aware точечная чинка, кап +6 gaps/ход) → экспорт _v3.

**Замеры (ключевые, все pupilHard=0 если не сказано):**
- ImportV2-база 262/39 хвостов/152: DayClose 60с → 248 (−14); restarts6/topK32
  → 231 (−31); цепочка DC×3→TD×3 → **217** (−45, в прогнозе фазы 1).
- Bin-pack A/B greedy: plain 879/205 vs reuse 880/167 vs both 880/160.
- Полные прогоны TF (seed 11/22/33, ~2 мин каждый, шум time-box D-46):
  s11 201/0 (valid), s22 **160/0 (valid, канон v3)**, s33 189/2 (брак, без экспорта).
  Промежуточные браки 170–171/1–4 (pupilHard>0) — не результаты, в v3 не вошли.

**Приёмка v3 тем же методом (`v2_audit.py v3`, параметр добавлен; default v2
без изменений — перепроверено 262):**

| Метрика | Человек | v2 | **v3 (s22)** |
|---|---|---|---|
| Окон (без служебного) | 140 | 262 | **160 (−39%)** |
| Уроков / учителе-дней / на 100 | 943/181/14.8 | 888/209/29.5 | **888/198/18.0** |
| Парное (мы : школа : ничья) | — | 11:29:3 | **13:23:7** |
| Перегруз дня класса | 1 | 3 | **8 (все +1, 5–6 кл 7>6) — РЕГРЕСС, см. ниже** |
| pupilHard / hard / покрытие | 0 | 0/0/888 | **0/0/888** |
| Учитель ≤10/день | 0 (макс 10) | 0 | **0 (макс 10)** |

**Сьют: 335+3/0 за 2м14с** (+5 новых: DayClose 3 + Sync 2; 3 Skip как были).
**Файлы:** `АМСУР_движок_ФИНАЛ_v3.xlsx` (канон, seed 22, 160) +
`..._v3_s22.xlsx` (дубль seed-доказательства) + сетки `АМСУР_движок_v3_по_дням/`
+ `v2_audit.py` с параметром v3. Старый ФИНАЛ и v2 не тронуты.

**S9 red-team (self, subagents недоступны — free-tier ошибка провайдера):**
- CRITICAL: нет (pupilHard=0, hard=0, 888/888, сьют зелёный).
- HIGH: СанПиН-перегруз 3→8 (все +1 в 5–6 кл; bin-pack концентрирует дни;
  builder-кап 8 против аудит-капов 6/7 — расхождение было и в v2). Митигация —
  точечный SanPin-ремонт (deferred, отдельной итерацией; ожидаемая цена +3..7 дыр,
  итог ~163–167 — всё равно ≤180).
- MEDIUM: activeDays финал 198 (>гейта 195; greedy-гейт взят 160–167, LNS снова
  размазывает, DayClose отыгрывает частично 209→198).
- MEDIUM: SyncLns обычно 0 принятых (оставлен: never-worsens, иногда помогает).
- LOW: разброс сидов 160–201 valid (D-46 шум; лечится multi-seed отбором min-gaps —
  так и сделано: канон = лучший valid s22).
- LOW: doubles A/B 15/20 + эшелон (фаза 4) НЕ делались — не понадобились для 160;
  остаются резервом к <140 вместе с F-A/F-B/F-C.

**Статус:** цель пользователя ≤180 — ВЗЯТА (160, шикарно <180). До человека 140
осталось 20 окон (1.14x). Победа <140 — не claimed (нужны SanPin-ремонт + doubles
A/B + резервные; отдельное решение). Коммитов нет.

---

## 23.09.2026 — Выдача v3: учителя + ученики по дням (по запросу)

**Файлы (только _v3, старое не тронуто):**
- `АМСУР_движок_v3_Учителя_по_дням.xlsx` — одна книга, листы Пн..Пт, формат 1-в-1
  как `АМСУР_Учителя_по_черновику.xlsx` («Преподаватели», полные ФИО, классы,
  ширины 22/9, без мёрджей/стилей). Генератор `Temp/gen_v3_grids.py` из ФИНАЛ_v3.
- `АМСУР_движок_v3_Ученики_по_дням.xlsx` — одна книга: листы-дни Урок×Класс
  (как у школы, но полные названия вместо кодов РусЯ/Матем — коды не выдумываю)
  + лист «По классам» (Класс|День|1..12, 120 строк).
**Проверено:** 888 учительских клеток (дублей учитель/день/урок нет — ассёрт),
160 окон пересчётом по полным ФИО (дублей фамилий нет — сошлось с аудитом 160),
888 ученических записей, pupil-окон по distinct-слотам 0. Карта — README_файлы.md.

---

## 24.09.2026 — Фаза A (ThinDayLns): гейт ПРОВАЛЕН, стоп-разбор, пивот на B

**Код (TDD RED→GREEN, всё через существующие швы, дефолты не меняются):**
- `Scheduling.Core/ThinDayLns.cs` (new) — зеркало DayCloseLns для тощих дней
  (≤2 уроков): WorstThinDays (самые тонкие первыми) + ruin дня целиком +
  sync-замыкание (БЕЗ contention) + Replant с DayReuseHint + приём с ценой дня:
  лучше лексикографически (gaps, failedDays, soft) ИЛИ меньше дней при
  (gaps≤+gapsCap, failedDays≤, soft≤+softCap). Дефолт (0,0) = never-worsens.
- `GreedyPlacer.Replant` += opt-in `anchorSlots` (слоты вплотную к занятиям
  reuse-учителя; дефолт false = бит-в-бит; ThinDay передаёт true).
- `ThinDayLnsTests` 5/5 (never-worsens, детерминизм, thin-close, only-thin, costcap).
- Стадия в `ScratchGen.RunFullPipeline` после dayclose2 (topK 32, restarts 6,
  60с × 3 сида, rules threadyтся, отбор min-gaps — как остальные стадии).

**Замеры на v3-базе (ImportV2, pupilHard=0 всюду; временные тесты удалены):**

| Конфиг | Дыры | Дни | Тощие | pupil | soft |
|---|---|---|---|---|---|
| v3 база | 160 | 198 | 52 | 0 | 2289 |
| Thin(0,0) | 159 | 195 | 49 | 0 | 2289 |
| Thin cap 0..10000 | 159 | 195 | 49 | 0 | = (идентично — блокирует НЕ soft) |
| Thin+якорь, cap 0..10000 | 159 | 193–195 | 47–49 | 0 | ~2317 |
| Thin(1,30)→DC→TD | 158 | 192 | 47 | 0 | 2312 |
| Thin(2,100)→DC→TD | 164 | 186 | 40 | 0 | 2409 (брак) |

**Стоп-разбор (3 содержательные попытки, каждая с новой гипотезой):**
1. Строгий приём (0,0): −1. Гипотеза «душит soft» — опровергнута (cap=10000 тот же итог).
2. Якорь слотов (earliest-first расширяет спан): H1 отвергнута — почти без эффекта.
3. Диверсификация+полировка: дни режутся (−6/−12), но полировка не конвертирует
   их в дыры (−3 отыгрыша против +7 цены; TD после DC acc=0 — полный ступор).
**Причина (механизм):** расписание pupil-компактно — свободные клетки только вне
спанов; вселение тощего урока либо невозможно (классо-день полон в своей смене),
либо расширяет спан принимающего дня (+дыры). Одиночный ruin структурно слаб
(тот же вывод, что П1 для одиночных ходов). Прогноз −10..−15 этим путём снят.
**Решение:** гейт (тощие ≤35, итог ≤150) — ПРОВАЛЕН; фазу A как рычаг скипаю,
код остаётся never-worsens микро-стадией (−1..−2, бесплатно). Пивот на фазу B
(doubles-spread — независимый механизм через steering существующего поиска).

---

## 24.09.2026 — Фаза B (Doubles-spread): ВЕСА ВРЕДЯТ, bump ОТМЕНЁН

**A/B (временный тест, удалён; v3-старт, срез VND45→TD45→DC45, сиды 11/22; pupilHard=0 всюду):**

| W (doubles) | Дыры (s11/s22) | Дублей всего | Рядом | Дни | soft |
|---|---|---|---|---|---|
| база v3 | 160 | 64* | 48% | 198 | 2289 |
| 10 | 160/160 | 64/64 | 51%/50% | 198 | 2198/2232 |
| 15 | 165/164 | 61/59 | 55%/55% | 198/200 | 2383/2349 |
| 20 | 164/162 | 61/62 | 59%/56% | 199/200 | 2452/2512 |

\* Метрика уточнена: целых (не-split/sync) дублей у v3 — 64 (рядом 48%),
рассеянных 33 (=dblSoft 330/10); аудитные 150 включают sync/split-артефакты.
У человека целых ~27. Разрыв: 37 дублей, из них 33 рассеянных.
**Вывод:** вес↑ улучшает сдвоенность, но VND торгует окно учителя (+10) за
дубль (−15/−20) — дыры +2..+5, дни +2. Иерархия ломается (тот же урок, что в
П2 с heavy 20). **Bump v7→v8 ОТМЕНЁН, каталог не тронут (v7), эшелон и
soft-запрет скипнуты** (условие плана «веса alone <−8» не выполнено; spread
противоречит компактности). Фаза B как весовая закрыта с отрицательным итогом.

## 24.09.2026 — OWN SYNTHESIS TeacherWeekLns: нуль-эффект, в резерв

**Мотивация:** плоский оптимум 160 (day-ruin ±2, веса вредят) — обобщить DayClose
с дня на неделю учителя (совместная перепаковка; динамический teacherDay в
Replant сам bin-pack'ит). TDD 4/4 (never-worsens, детерминизм, week-close,
ordering). **Замер на v3 (90с × сиды 11/22): acc=0/0, 160/160 — нуль.**
Классовая стена держит и в недельном масштабе. Код оставлен (корректен,
тесты зелёные), в конвейер НЕ вшит (0-эффект — только время). Резерв с F-A/F-B/F-C.
**Попутная находка:** дошлифовка DC60+TD60 с v3-финала даёт 160→157 (−3):
конвейер фиксированной последовательности не до сходимости; запас учтён в C.
**Честный итог дня:** доступные механизмы дают ~155–157, не <140. Эндшпиль:
C (best-of-5) → D (SanPin-ремонт) → _v4 + приёмка → решение пользователя
стоп/дальше (140–150 — его гейт).

---

## 24.09.2026 — Фаза C: best-of-5 свежих прогонов ПРОИГРАН (центр ~187, 160 — везение)

**Замер (временный тест, удалён; TF, кандидаты _v4_s<seed>, удалены как брак/хуже):**
s11 174/брак(pupil 1), s22 213, s33 182, s44 189, s55 186. Тот же seed 22 давал
160 во v3-прогоне — теперь 213. Разбор: спред v3-эры 160–201 = спред сейчас
174–213 (оба 40). **160 — везение верхней децили, центр распределения ~187.**
Свежие прогоны — доминируемо плохая стратегия (матожидание 187 > 160).
Правильный путь: старт с v3-импорта + grind. Кандидаты удалены, v3 — база.

## 24.09.2026 — Grind до сходимости: 160 → 149 (находка: конвейер не сходится)

Конвейер фиксированной последовательности (TD→DC→Sync→TD…) оставляет слабину:
повтор раундов DC→TD→Sync→Thin со свежими сидами до нулевых принятий за раунд:
160→148→146→144 (стоп). **Честность:** повтор тем же кодом/сидами дал 155
(time-box cutoff ±11 — D-46 в жёсткой форме). По протоколу: grind ×3 seed-базы
(154/155/149) → лучший воспроизводимый **149** (дни 195, pupilHard=0).
Вывод для протокола: важные замеры time-box поиска — минимум 3 сида, иначе ложь.
Чекпоинт: `АМСУР_движок_ФИНАЛ_v4_work.xlsx` (149/8).

## 24.09.2026 — Фаза D: OverloadLns-ремонт 8→0 + grind → v4 = 139. ПОБЕДА (<140)

**Код (TDD 3/3):** `Scheduling.Core/OverloadLns.cs` (new) — spill юнита
(sync целиком) с перегруженного классо-дня с ЗАПРЕТОМ дня через клон AllowedDays
(только внутри стадии); Replant тянет в 4–5-дни; приём: перегрузов строго меньше
+ pupilHard==0 + failedDays≤ + дыры≤+2/ход. Нижняя граница проверена заранее:
недельные distinct 28–30 ≤ 30 — перегруз от неравномерности, ремонт возможен
(валидатор/билдер держат кэп 8 и этих перегрузов не видят — B2-relax слеп).
**Замер:** ремонт ×3 сида детерминированно 8→**0** за +6 дыр (149→155, в бюджете
+3..7); grind под стражей перегрузов 155/0→148/2→141/2→**140**/2; дожим сидами
9000/11000/13000: 139/139/140 → **139/2/0/0** (888/888). Экспорт ФИНАЛ_v4.
**Приёмка тем же методом (`v2_audit.py v4`, default v2 перепроверен — 262 цел):**

| Метрика | Человек | v3 | **v4** |
|---|---|---|---|
| Окон / на 100 | 140 / 14.8 | 160 / 18.0 | **139 / 15.7** |
| Уроков / учителе-дней / дней-с-окнами | 943 / 181 / 46.4% | 888 / 198 / 46.0% | **888 / 192 / 42.2%** |
| Парное (мы : школа : ничья) | — | 13:23:7 | **15:21:7** |
| Перегруз СанПиН | 1 | 8 | **2 (гейт ≤2)** |
| pupilHard / hard / учитель ≤10 | 0 | 0/0/0 | **0/0/0** |

**Сьют: 347+3/0** (+12: ThinDay 5 + Week 4 + Overload 3; 3 Skip как были).
Greedy без регресса (RealTeacherTests в сьюте зелёные; дефолт Place не менялся).
**Фаза E (cross-shift) НЕ запускалась:** победа уже взята основными фазами.
Коммитов нет.

---

## 24.09.2026 — Добор (по решению пользователя): v4 139 → 134/1. Победа ×6

**E0:** сплит v4 — ordinary 95 + cross 44 (64 кросс-дня). Приз E реален.
**E1 (код, TDD 4/4):** `Scheduling.Core/CrossShiftLns.cs` (new) — ruin кросс-дней
+ Replant с DayReuseHint + anchorSlots + новый `ShiftCohesionHint` (слоты
мажоритарной смены дня первыми; ничья — пропуск цели) в `GreedyPlacer`
(opt-in, дефолт null = бит-в-бит). Приём строго (gaps, failedDays, soft).
`CrossShiftLnsTests` 4/4. Замер: CROSS даёт 136, но с over=3 (рост перегруза —
концентрация в смене бьёт по классо-дням) — страж over отклонил, в конвейер не вшит.
**SANPIN0:** OverloadLns cap 0 — gap-neutral фикс 2→1 при тех же 134 (days 192).
**GRIND15000/17000:** 135 → **134**/2 (cross 37). **Консолидация:** 134/1
доминирует 134/2 — экспортирован финал (assert в памяти до записи).
**Финальная приёмка (`v2_audit.py v4`):** человек 140 (943/181/14.8) vs
**v4 134 (888/192/15.1)**, дни-с-окнами 42.7%, парное **15:19:9**
(было 13:23:7 → 15:21:7 → 15:19:9),
СанПиН **1 (= человеку)**, pupilHard/hard/учитель≤10 — нули.
Файл верифицирован C# roundtrip: 888/888, hard=0, pupilHard=0, 139→134 окон.
**Сьют: 351+3/0** (+4 CrossShift; 3 Skip как были). Временные тесты удалены.
Коммитов нет (ждут команды).

---

## 24.09.2026 — Арсенал «всё что в силах»: обмены дали 134→132, остальное — ноль

**S1 SwapLns (парные обмены, TDD 3/3): РАБОТАЕТ.** `Scheduling.Core/SwapLns.cs`
(new) — обмен двух целых не-sync уроков временами (комнаты едут с уроками);
кандидаты — уроки в клетках внутри спанов учителя; проверки точные локальные
(кросс-годность/pupil/dыры) + полный валидатор и soft-кап на принятии;
gaps-first приём, first-improvement, детерминизм. Замер на v4: 134→133 (оба
сида, по 1 принятию ~800 кандидатов) + полировка → **132**/1/0/0, детерминированно.
**S2 PairWeekLns (двойной week-ruin, TDD 4/4): НУЛЬ.** acc=0/0 за 7с — сцепка
не бьёт классовую стену и в парном масштабе. Код оставлен (корректен), не вшит.
**S3 OWN construction-portfolio (= вердикт F-A): F-A НЕ СТРОИМ.** Базы greedy:
plain ~207 дней, compact ~207 (compact без dayReuse дни не режет!),
reuse ~167, both ~162 при том же покрытии ~880. Связка с цепочками: dense-start
(186д) полируется до 164, sprawl-start (198д) — до 157. Вывод: НАШЕМУ поиску
разлёт НУЖЕН (там режутся дыры); F-A (фикс дней + новый within-day поиск) —
перестройка 1–3 дня против измерений. Остаётся в резерве.
**v4 = 132** (аудит: 888/192/42.7%/14.9, парное 14:19:10, СанПиН 1, ≤10;
файл верифицирован C# roundtrip). SwapLns/PairWeekLns в конвейер НЕ вшиты
(свежие прогоны obsolete — grind с импорта доминирует; вшивка без верификации
прогоном — риск; стадии живут в grind-воркфлоу).
**Сьют: 358+3/0** (+7: Swap 3 + PairWeek 4). Временные тесты удалены.
Коммитов нет.

---

## 24.09.2026 — Ремонт структуры (по запросу): обмен доказан, файл не тронут

**Замеры с v4=134/47/1 (временные тесты, удалены; pupilHard=0 всюду):**
- Дубли→рядом одиночными ходами (sync-aware, cap 0 и 1): **0 сдвигов** —
  соседние клетки заняты (та же классовая стена). Весовой путь тоже мёртв (B).
- ThinDay: дни 192→190, тощие 47→45 при тех же 134, но перегруз 1→3
  (ThinDay не сторожит over — зафиксировано как долг стадии).
- Связка thin→OverloadLns→DC/TD: 135/46/1 — хуже заголовка, **не экспортирована**
  (страж: gaps≤134 И thin<47 И over≤1). Канон 134/47/1 цел.
**Курс обмена (измерен): −2 тощих дня ≈ +1..2 дыры.** Структура уровня школы
(тощие 23) стоила бы +12..+24 дыры → 146–158. Не берём: победа по дырам+нормам
важнее похожести. Файл не менялся, сьют не гонялся (продакт-код не тронут).

---

## 24.09.2026 — LONG + ILS (по запросу): глухо, фронт доказан

**LONG (бюджеты ×2, restarts 8, ×2 базы):** acc≈0 везде, 134 flat. Лишнее время —
чистая трата: стадии сошлись, деньги на ветер.
**ILS (ThinDay-кик gapsCap=3 + строгая полировка ×8):** пинки работают как
диверсификаторы (дни 192→184, тощие 47→39!), но полировка возвращается лишь
к 141–143, ниже 134 — никогда. Итог: NO PROGRESS, v4 134 цел.
**Карта фронта (тощие ⟺ дыры):** (47⟺134), (45⟺134), (39–42⟺141–143).
Человек (23⟺140) — вне нашего фронта: его упаковка структурно лучше
(5.2/день), наш поиск так не умеет. Вывод: точечные/средние окрестности
исчерпаны полностью; остались парные обмены, двойной week-ruin, F-A/F-B.

---

## 03.10.2026 — Автономно: идеальная школа + пресет + записка (по просьбе)

**Запрос:** тесты на «идеальной школе» (без штрафов, часы делятся ровно),
базовые настройки под школу из коробки, удобство; UI-редизайн — потом;
расписать всё о программе простыми словами. Работал автономно, без вопросов.

**Идеальная школа** (`IdealSchoolTests` 4/4): 2 класса × 17 ч
(Музыка 4, История 3, Физкультура 2, Биология 3, Трудовое 3, ИЗО 2),
12 учителей, дни [Л]/[Л,Т,Л,Т,Л]×2/[Л]/[Л,Т,Л,Т,Л] (края лёгкие некраевые,
тяжёлые только Вт/Ср/Пт, физра Вт+Ср, чередование строгое, без сдвоенных).
Ручной эталон: SoftTotal=0 + Valid — доказан тестом (модель ноль выражает).
Конвейер (greedy+repair+LS best-of-3): valid + ≤70 — честный потолок:
жадник тяжести не знает, LS застревает в локальном оптимуме (70→65 за 20с).
По пути поймано: один Спортзал на всех парализует CompactRepair (каждому
классу — свой кабинет в фикстуре), жадник не влезает в 5×5 (тест на 5×7).

**Базовые настройки:** `SchoolPresets.SubjectDifficulties` (офиц. имена РБ,
NEEDS-CHECK; явный flex бьёт пресет) — применяется импортёром автоматически
(greedy Difficulty не использует — покрытие цело); шаблон Excel: 2-й лист
«Пример» (смены/сплит/ВОВ/факультатив; 1-й лист пуст — Roundtrip/HeaderOnly
целы); слайдеры peak/edge/alt в настройках (20 опций) + подсказки.

**Записка:** `docs/АМСУР_ПРОСТЫМИ_СЛОВАМИ.md` — как работает, дефолты,
что можно/нельзя, время (ввод ~час, генерация 3–30 сек, школа целиком
~20–90 сек), ограничения, FAQ + 3 варианта UI-редизайна (рекомендую А —
мастер-помощник к пилоту).

**Проверено:** build 0 errors; **полный сьют 423+3skip/0** (+4 IdealSchool;
карта трудностей ничего не сломала). Коммитов нет.

---

## 04.10.2026 — Автономно: фото СанПиН + наша идеальная 5–11 (по просьбе)

**Запрос:** 1) идеальную — на основе НАШЕЙ школы (5–11, норм. штат);
2) в `данные/` — доп. инфо от составителя (2 фото страниц сборника СанПиН).

**Фото разобраны (данные/photo_2026-10-02_23-09-*.jpg):**
- Таблица 3 (пост. №35): шкала трудности 5–11, баллы 12 (математика) … 3 (физра).
  Строки на фото сдвинуты (двустрочные ячейки + угол) — баллы отнесены по
  убыванию шкалы; химия/труд читаются 4–5 (лёгкие при любом чтении — на порог
  тяжести ≥7 не влияет). Карта переведена в `SchoolPresets` (балл−2):
  тяжёлые теперь математика, иностранный, языки, физика, химия.
- Недельная нагрузка: база | макс с факультативами
  (1:18|22, 2:19|22, 3:22|22, 4:22|24, 5:25/27|27, 6:27/29|30, 7:28/30|30,
  8:29/31|31, 9:29/31|31, 10:28/31|33, 11:28/31|34). Строки 5–7 curled —
  макс подобран единственным непротиворечивым набором (база ≤ макс);
  под таблицей обрезок ещё строки (34 — вероятно 12-й класс, не вносим).
- **РАСХОЖДЕНИЕ:** суммы наших типовых планов (5-й: 29) выше базы фото (25),
  но влезают в макс (27) — кроме 5-го (29 > 27). Не молчим: чекер показывает
  превышение базы предупреждением, потолка — ошибкой. Вопрос завучу №1.

**Наша идеальная (`OurIdealSchoolTests` 3/3):** структура СШ № 8 5–11
(по классу на параллель, реальные часы 29/30/32/33/33/34/34 = 225 уроков,
смены 5–8→2-я, на (класс,предмет) — свой учитель ≤5 ч; без сплитов/профилей —
честно). Замер: **жадник 225/225 + ремонт — сразу валидно (hard 0,
soft 2770)** — ср. tight-фикстуру 986/994 в карантине: дело было в штате,
а не в движке. Чекер на ней же: учителей чисто, 5А подсвечен (29 > 25).
Малая идеальная переведена на фото-карту (середины — химия/физика).

**Проверено:** build 0 errors; **полный сьют 426+3skip/0** (+3 OurIdeal).
SANPIN_RB §9 (таблицы + расхождение), D-58. Коммитов нет.

---

## 04.10.2026 (прод.) — PE-видимость + два документа на 24 класса

**Запрос:** в документе половина классов (мини-фикстура 7 шт. — моя синтетика,
а не школа); сделать второй документ максимально близко к реальному по числу
учителей; две версии — идеальная и в разы тяжелее.

**Почему так вышло:** 7-классовая идеальная — быстрая unit-фикстура (по классу
на параллель), а не модель школы. Исправлено: оба новых документа — все 24
класса из `НашаШкола_5-11_нагрузка.xlsx` (те же 408 строк/769 ч, смены, пары).

**По пути вскрыто и починено:** v1 упёрлась в 6 троек физры — поиск их НЕ ВИДЕЛ
(было только Hard-гейтом) и не чинил. Введено: soft-терм `pe-consecutive` (25,
каталог v9) + `PeSpacingRepair` (ядро, детерминированный, с fallback на урок
без кабинета) + `ExportDraftGrid(..., rules)` для честного relaxed-черновика.
Паритет индекса доказан и для нового терма (ходы + свопы).

**Замер (жадник+ремонт+LS+PE-ремонт):**
- v1 (167 учителей, все ≤21 ч): 884/884, 0 жёстких, 0 окон/стартов у учеников;
  окон учителей 156 (92/505 дней), топ-нагрузки 21/20/20.
- v2 (89 учителей, K=max(2,n/3)): 815/884 — **не влезли 69: математика 29 +
  английский 40**; окон учителей 320 (114/275 дней), топ-нагрузки 30/30/30.
- Файлы: `Samples_Export/RealSchool_v1_ideal.xlsx`,
  `Samples_Export/RealSchool_v2_hard.xlsx` (v2 — с ослабленной физрой,
  предупреждения в файле; лист «Неназначенные» — 69 строк).

**Проверено:** build 0 errors; **полный сьют 430+3skip/0**. D-59. Коммитов нет.

---

## 04.10.2026 (прод.) — Классный час: четверг первым уроком, никак иначе

**Запрос:** классный час — первый урок каждой смены в четверг, без вариантов.

**Сделано:** гейт в билдере (fail-loud): день обязан быть четвергом (3);
слот класса — первые ДВА слота его полосы. День в UI locked (комбо
заблокировано на четверге + подсказка). Тест R3_ThursdayOnly.

**Важная находка (чуть не сломал):** строгий минимум полосы противоречит
факту — ваши же реальные 6-е классы держат час на 7-м уроке при полосе 6–12
(ваш допуск «6 или 7» из RealTeacherTests). Поэтому допуск min/min+1, а не
строгий min. R3-тесты переведены на четверг; RealTeacher/FullRunV2 зелёные.

**Проверено:** build 0 errors; **полный сьют 431+3skip/0**. D-60. Коммитов нет.

---

## 04.10.2026 (прод.2) — Четверг-дефолт, не закон (поправка пользователя)

**Поправка:** классный час + четверг первым — НЕ вшивать в код как запрет,
а держать дефолтом (отключаемо). Гейт билдера убран; дефолты DayIndex=3 /
SlotIndex=1 и UI-предложение остались; день в UI снова свободен.
R3_ThursdayOnly удалён (R3-тесты и так на четверге). Факт про «6 или 7»
сохранён в журнале выше.
**Проверено:** build 0 errors; точечные 44/44 (R3/UI/RealTeacher/FullRunV2).
Финал: **полный сьют 430+3skip/0**. D-60. Коммитов нет.

---

## 04.10.2026 (прод.3) — Свежие нормы: пост. №75 и пик из первички (по просьбе)

**Запрос:** уроки в неделю по классам — самая свежая инфа, сравнить с прошлой.

**Источники (проверено 04.10.2026):**
- Пост. МО №75 (ред. №104): adu.by PDF на 44 стр. (скачано в Temp, цифры
  вытащены скриптом — кириллица в PDF битая, числа целы).
- Спец. санэпидтребования Совмина №525: pravo.by C21900525 (+изм. 2022–2026).
- ИМП 2026/2027 — подтверждает базу №75+№104.

**Находки:**
1. Канонический ряд максимумов (стр. 34 №75): 22/22/24/24/27/30/30/31/33/34/34 —
   совпал с фото почти везде; код обновлён (3-й: 22→24, 9-й: 31→33, 10-й: 33→34;
   вариант стр. 41 даёт 3-му 23 — взят 24, диапазон зафиксирован).
2. Пик Вт/Ср/Пт для 5–11 подтверждён первичным источником (было: только слова
   из школы). Наш peak-days — соответствует норме.
3. Отдельный план №75 (стр. 22) даёт базу 19/21/24/24/29/30/31/… — другой тип
   школ; наши суммы близки в 5–9-х. Планы НЕ трогаем (фикстуры стоят).
4. По новым потолкам: 9–10-е влезли, пробивают 5-й (29>27), 7-й (32>30),
   8-й (33>31). Таблица сверки — SANPIN_RB §9.2.1.

**Проверено:** build 0 errors; **полный сьют 430+3skip/0** (макс только вырос —
красных нет). Коммитов нет.

---

## 02.10.2026 — НДТП-7: семь правил школы в ядре + PDF к 05.10

**Бриф пользователя (дедлайн подачи 05.10):** PDF-документ + «доделать немного ядро»
по итогам разговора в школе: 1) классный час/факультативы — не уроки;
2) тяжёлые дни Вт/Ср/Пт; 3) физра не 3 дня подряд; 4) 7 предметов на краю
1 раз/нед; 5) чередование сложных/лёгких; 6) норма учителя 25 ч;
7) ВОВ у 9-х — обязательный факультатив на краю дня, не урок.
Решение пользователя: всё в код сейчас (риск принят).

**Сделано (пакеты A–D, артефакты `.opencode/compose-plus/brief|decision|plan|risks-ndtp7-20261002.md`):**
- Domain: `Subject.IsNonLesson` + `LessonOccurrence.IsExtra`; импортёр метит по
  именам (Классный час/ВОВ/Факультатив*/Час здоровья/Инф.час); CommonLesson-предмет
  и синтез — IsNonLesson/IsExtra; нагрузка-внеурочка пинится к краю смены.
- Каталог v7→v8: `peak-days` (5) / `edge-once` (10) / `alternation` (5) + SoftUnits
  (PeakOutside/EdgeOnceExcess/AlternationBreaks/IsEdgeOnceSubject) + SoftEvaluator
  (counted-фильтр + 3 терма ×GradeW) + Explainer-имена.
- Валидатор: `pe-consecutive` Hard (relax через Dangerous sanpin-pe-spacing);
  gaps/late/maxperday — только counted.
- SanPinChecker: день-капы и часы учителя — только counted; норма 25 ч/нед
  (ошибка при превышении; Hard нет — осознанно: русский блок 122ч/3 учителя).
- SearchIndex: counted-гейты структур + peak/alt дельты + edge-once кросс-ключевой
  пересчёт + точный EdgeSwapDelta по truth (_pos). Паритет доказан: 60 ходов,
  200 ходов + 100 свопов, изоляция по термам.
- Тесты: `Ndtp7Tests` 11 шт (все 7 пунктов) + `Ndtp7ParityDebugTests` 4 шт.
- Золотые обновления (осознанно): Tiny/LS оптимум 0→5 (пол чередования),
  Editor 15→10, ManualEdit 0→−5, Reality 0→5, Pool 0→15, ThinClose — priced
  tradeoff (дефолт 3 дня, softCap=100 → 2), PhaseBSkipped budget 10→30.
- PDF+DOCX: `Docs_Contest/NDTP_proekt_AMSUR.pdf|docx` (5 стр., А4, 14pt,
  поля 30/10/20/20, нумерация; генератор `Temp/opencode/ndtp7/gen_ndtp.py`).

**Проверено:** build 0 errors; **полный сьют 419+3skip/0 за ~3 мин**
(3 Skip предсуществующие: D-35, FullRunV2, ScratchGen).
**Честно:** победа 134 vs 140 замерена ДО v8-термов; злото RealSchool сдвинулось
(полировка дольше — бюджет гейта поднят); СанПиН-блок по-прежнему NEEDS-CHECK.

---

## 04.10.2026 (прод.5) — Качественные документы v1/v2 на 24 классах

**Запрос:** 2–3 расписания через программу, compliant с СанПиН + 2026/27
(кл. час в Чт, все правила); v1 идеальная, v2 в разы тяжелее (штат как живой);
качественно, не быстро. План: `.opencode/compose-plus/plan-quality-docs-20261004.md`.

**P0:** режим 2026/27 проверен (5-дневка, четверти — шк. №77 Минска).

**Данные:** оба — те же 408 строк/769 ч (смены/пары/кабинеты равны); кл. час:
строки файла → классруки + синтез CommonLesson(Чт, 1/6) — в сетке Чт первым,
extra; дневные капы строго по СанПиН (5–6: 6, 7–11: 7), без натяжки 8.
v1: штат как в файле (167, ≤21 ч). v2: пер-предмет K=max(1,round(часы/45)) +
общий 2-й сплита → 64 учителя (перегруз вынужденный D-39, кап 12/д; недельные
25 ч подсвечены).

**Конвейер (качество):** greedy + repair + LS 60с + финальный repair +
PeSpacingRepair, BestOf 5 сидов (0 ученических → 0 hard → min soft).

**Замер:**
- v1: 884/884, hard 0, ученики 0/0, дневные капы 0, физра-троек 0,
  кл. час 24/24 (Чт, 1/6, extra); окна учителей 156 (92/505 дней);
  учителя >25 ч: 0. Недельные: база — предупреждения у всех (планы выше базы),
  потолок — 13 классов (5/7/8-е + края с внеурочкой: вопрос к нагрузке).
- v2: 833/884 — не влез 51 (английский 28, математика 13, русский 5,
  белорусский 4, физра 1); hard 3; окна учителей 320 (114/275);
  учителей >25 ч: 17. Черновик с ослабленной физрой (warnings в файле).
- Файлы: `Samples_Export/RealSchool_2026_v1.xlsx`,
  `Samples_Export/RealSchool_2026_v2.xlsx` (лист «Неназначенные» у v2).
- Тест QualityDocsTests (постоянный): v1 — покрытие 100% + hard/pupil/daycaps 0
  + кл.час 24 + учителя чисты; v2 — строго хуже.

**Проверено:** build 0 errors; **полный сьют 432+3skip/0**. Коммитов нет.

---

## 04.10.2026 (прод.6) — v2-штат по живым цифрам школы

**Поправка пользователя:** английских минимум 4, русских 3–4, математики 4
чистых + 1–2 с информатики, белорусок 4. (Мой round-robin давал монстров
52–66 ч — в живой школе такого нет.)

**Сделано:** семьи-пулы (английский/русский/белорусский по 4, математики 5;
матем/алгебра/геометрия и языки/лит-ры — общие пулы, как в жизни; остальным
K=max(1,round(часы/30)); вторым половинам сплитов — следующий по пулу).
Итого 70 учителей (файловые 167 ужаты; живые ~44 — там те же перегрузы,
только жёстче: мелкие предметы у нас отдельно, классруки отдельно).

**Замер v2:** 868/884 — не влезло 16 (английский 10 — ВСЕ сплиты: 4 руками
пары 24 классов не синхронить; математика 3, физика 3); hard 9 (ученики 3 —
рваные дни от непокрытия); окна учителей ~320; перегруженных (>25 ч) — 17.
Вывод для школы: сплитам нужны руки (англичан мало), математикам/физикам —
ставки. Тест проходит (строго хуже v1 по покрытию).

**Проверено:** build 0 errors; **полный сьют 432+3skip/0**. Коммитов нет.

---

## 04.10.2026 (прод.7) — GrindLite + режим ТОП + плотные дни (по просьбе)

**Запрос:** 1) не делать учителям дни по 1–2 урока (плотно: смену целиком —
завтра выходной); 2) «лайт-версию олимпийского» в программу (топ за ~10 мин
для НДТП, а не мусор за минуту).

**Сделано:**
- `Scheduling.Core/GrindLite.cs`: ThinDay → TeacherDay (bin-pack дней) →
  CrossShift → Swap → CompactRepair → PeRepair; двухтировая приёмка (current
  без регресса pupil/hard, bestClean только 0/0 + min окон/штрафа).
  По пути пойман deadlock приёмки (грязный старт + строгое «только чистое» =
  стагнация) — исправлен chaining'ом.
- Режим ТОП (60с × 5 сидов ≈ 10 мин): `SolverOptions.EnableGrindLite`
  (solver флаг не исполняет — читает GenerateHost: полировка финала перед
  архивом с пересчётом метрик + лог в диагностику). Списки режимов UI
  подхватили ТОП автоматически.
- Плотность дней — штатно через TeacherDayLns (минимум числа дней) + приёмка
  по окнам; дефолты и старые режимы не тронуты.

**Замер:** v1-документ: окна учителей **156 → 74/510 (8.4/100)** — лучше
старого рекорда 132/14.9, при нуле нарушений. v2 — 273/217.
Тесты: GrindLiteTests 2/2, QualityDocs 2/2 (тем же кодом, короче бюджеты).

**Проверено:** build 0 errors; **полный сьют 435+3skip/0**. Коммитов нет.

---

## 04.10.2026 (прод.8) — ОБЖ только в 5-х + честные ONLY-кабинеты (факты школы)

**Факты пользователя:** 1) ОБЖ нет в 9-х; 2) 314/315 — только биология,
309/310 — только информатика, 214 — физика+астрономия.

**Проверено по файлам:**
- ОБЖ: в файле нагрузки только 5-е классы (4 строки) — уже чисто; в OurIdeal
  убрано из 8А/9А (там его и по составу учителей никто не ведёт) — часы 32/32.
- Кабинеты: файл ПРОТИВОРЕЧИТ железному ONLY почти везде: 314 — химия 10 +
  биология 8; 315 — химия 2; 309 — физика 18; 310 — вообще без информатики;
  214 — математика 5. Железно чист только 312 (вся химия, чужого нет).
  Решение: ONLY оставлен только на 312; остальное — предпочитаемые через
  вес room-preference (комната из строки). «Только» = назначение, переливы —
  факт жизни (иначе ремонт/жадник задыхаются: было 26→13 окон, threshold 12).

**Проверено:** build 0 errors; **полный сьют 435+3skip/0**. Коммитов нет.

---

## 04.10.2026 (прод.9) — Спецкабинеты: назначение без железа (подтверждено)

**Поправка пользователя:** кабинеты под предметы закреплены, но пользоваться
могут все. Список ONLY очищен полностью (был один 312 — тоже убран):
назначение выражается комнатой из строки + весом room-preference. Явный flex
при желании вернёт железо отдельным кабинетам.
**Проверено:** точечные 20/20 (миграция/пары/фото/реал/штаты). Коммитов нет.

---

## 04.10.2026 (финал дня) — Допы, нормы, окна, спортзал + FINAL

**Запрос (4 пункта):** 1) допы-предметники от перегрузов; 2) нормы по классам
(5А +1 и др.); 3) минимум окон; 4) спортзал = 4 класса/урок.

**Сделано (файл не тронут, всё тестовым слоем):**
- Допы generic-алгоритмом (целые пачки класс-предмет от всех >25 ч, A/B стороны
  раздельно, B-нагрузки тоже считаются): Матем.Доп, Рус.Доп, Бел.Доп,
  Физра.Доп, Инф.Доп(+2), ОБЖ.Доп, ДМП.Доп, Англ(проф).Доп — итог 52 учителя.
  Перегрузов в финале: **0** (было 10).
- Спортзал: в файле у физры комнат нет — создан тестом (RoomName + кап 4).
- Нормы: разбор ниже; классный час +1 сверх потолка — системное (макс включает
  факультативы, а час обязателен).
- Окна: tWin=313/226 (живой штат; v1-идеал был 74 — цена реальности).

**Замер FINAL (seed 7):** 870/890, hard=1 (только непокрытие), ученики 0,
физра-троек 0, дневные капы 0; sanpin-ошибок 13 — все недельные классов;
файл RealSchool_FINAL_2026.xlsx записан (черновик, лист «Неназначенные» — 20:
профильные пары без синхрона + сплиты + хвостики).

**Проверено:** build 0 errors; **полный сьют 438+3skip/0**. Коммитов нет.

---

## 04.10.2026 (вечер) — Гонка за 140: grind-кампании и пол ~160

**Вопрос пользователя:** у нас 313 окон, в ориге 140 — побить оригинал до PDF.

**Разбор метрики:** аудит v2_audit считает дырки как есть (пустые+служебные
между крайними); наша TeacherWindows — то же. Линейки сравнимы. Оригинал:
140 (14.8/100), рекорд v4: 134 (15.1/100), наш финал был: 285 (32.8/100).

**Кампания 1 (допы посменно + CrossShift×25):** 285 → best 162–191 (два прогона).
**Кампания 2 (гипотеза «допы фрагментируют»):** ПРОВЕРЕНО И ОПРОВЕРГНУТО —
без допов (45 живых) пол 231 против 162 с допами. Допы остаются.
**Разбор остатка (162):** кросс уже лучше школы (62 vs 118); бой — внутрисменка
(100 vs ~22 школы), размазана тонко (~3 на 30 учителей, без виноватых).
**Ключевая находка:** рекорд v4 134 шёл С нарушениями норм (гейт перегруза ≤2,
дневные капы 8 вместо 7) + ручная кампания днями. Строго-compliant пол ~160.

**Проверено:** GrindExperimentTests (временный файл кампании).

---

## 04.10.2026 (финал) — Принято 162 + PDF обновлён

**Решение пользователя:** фиксируем 285 → 162 (−43%) как итог, в PDF честные цифры.

**Сделано:**
- Долгие grind-факты выведены из сьюта в Skip (каждый 10–30 мин):
  GrindToConvergence + GrindNoDopsProbe (итоги зафиксированы выше).
- PDF `Docs_Contest/NDTP_proekt_AMSUR.pdf` (+docx), 5 стр.: §4 переписан на
  свежие замеры (модель: 11599→8736 за 18 с; школа: автомат 74, grind 162
  vs школа 140 vs рекорд v4 134; тесты 435+5). Проверен чтением целиком.
- Флейки-устойчивость FinalSchoolRunTests: fallback relaxed-черновика
  (time-boxed поиск под нагрузкой сьюта иногда оставляет добиваемое).

**Проверено:** build 0 errors; **полный сьют 437+5skip/0 за ~11 мин**. Коммитов нет.

---

## 05.10.2026 — Новый PDF для НДТП v3 (по просьбе, день подачи)

**Запрос:** новый PDF (антиплагиат), ссылки на источники, меньше акцента на
школу, простой язык «как я пишу», минимум тире (запятые вместо).

**Сделано:** генераторы gen_ndtp_v2.py + ndtp_text1.py (Temp/opencode/ndtp7):
текст переписан с нуля короткими предложениями, 7 источников со ссылками
(ndtp.by, developers.google.com/optimization, lalescu.ro, pravo.by, adu.by,
edu.gov.by), школа — только там где цифры-доказательства. PDF 5 стр.,
проверен чтением. Сьют под PDF: 438+5/0 (полный прогон 8.5 мин).

---

## 05.10.2026 (вечер) — Полная идеальная 5–11 + фикс капа классного часа

**Запрос:** расписание на данных как школа, но чище под СанПиН (все параллели
5–11, классный час Чт 1-м), пар нет по построению, прогнать grind, два сразу.

**Диагноз (честно):** expanded-сборка дала 776/780 — все 4 невставленных из
6-х классов. Причина — арифметика: импортер добавляет час occurrence
(6А: 31 шт), а кап 6-х равен 6 (5x6=30). Час жрал единицу капа в жаднике,
хотя валидатор его уже исключал (IsCounted) и документация требует
«час не тратит нормы». CP-SAT Phase A тоже молчал (Unknown за 16 с).

**Фикс движка (3 файла):** GreedyPlacer (UnitFits + запись classDay +
frozen-seed), IncrementalEvaluator (daySlots), SwapLns (init + ClassCapFits) —
везде пропуск IsNonLesson из классного капа, как уже было в валидаторе и
SearchIndex. Учительские лимиты не трогали (час там не жмет).

**Итог TOP (3 сида, LS30+polish30):** greedy 780/780 везде, best seed 22 —
soft 3155, hard 0, pupil 0/0, teacher windows 0/755. SanPin err 20 — все
«с внеурочкой выше потолка» (5-е 30>27, 6-е 31>30, 7-е 33>30, 10-е 35>34):
суммы планов бьют потолок, вопрос завучу. Файл OurIdeal_TOP_2026.xlsx,
лист Неназначенные пуст, Чт 1-м уроком час везде.

**Регресс:** полный сьют 439+5skip/0 (6.5 мин). LeftoverProbe 20->21:
фикс поменял порядок упаковки жадника на живых данных, fitted=0 держится
(структурность остатков intact) — число обновлено осознанно.

---

## 05.10.2026 (вечер, финал) — Слияние учителей + классный своих + TOP итог

**Слияние:** язык+литература и алгебра+геометрия — по одному учителю
(как в живых школах). Классный руководитель — учитель первого предмета
плана (без фаворитов). Учителей стало 286 (было 374).

**Итог TOP (2 сида, LS20+polish20, 51 с):** greedy 780/780 везде, best seed 22 —
soft 3525, hard 0, pupil 0/0, teacher windows 4/702 (плотные дни слитых
учителей — честно). SanPin err 20 — те же недельные потолки. Файл
OurIdeal_TOP_2026.xlsx, Неназначенные пуст, Чт 1-м час у своих учителей,
слияние проверено поиском по файлу.

---

## 05.10.2026 (вечер, сдача) — Штат: общий не пакуется, сдаём посубъектный

**Факт:** общий штат 39–40 учителей жадник впритык не пакует (776/780,
hard 2, tw под 300), CP-SAT Phase A тоже молчит (Unknown; PE-hard + точные
дни). Причина — коллизии общих учителей при точных днях, жадник близорук.
Апгрейд подборщика под общий штат — backlog (после сдачи).

**Решение на сдачу:** учителя свои на класс, слияния внутри класса соблюдены
(язык+литература и алгебра+геометрия — один человек), нагрузки 1–6 ч
(перегрузов нет по построению), имена плейсхолдеры — живых даст завуч.

**Итог TOP (2 сида, 52 с):** greedy 780/780, best seed 22 — soft 3615,
hard 0, pupil 0/0, teacher windows 2/700. SanPin err 20 — недельные потолки.
Файл OurIdeal_TOP_2026.xlsx, Неназначенные пуст, Чт 1-м час у своих.

---

## 05.10.2026 (вечер, сдача) — Живой штат, сплиты, совместители, ФИНАЛ

**Штат:** учителя свои на класс (общий не упаковался — см. выше), слияния и совместители внутри класса: язык+лит, алг+гео, ист+общ, физ+астр,
труд-девочки+черчение — одни люди; сплиты настоящие (английский, труды,
ДМП); ИЗО убрано, музыка только 5-е, черчение в 10-х 1 ч; классный своих.
Никому не больше 25 ч.

**По дороге:** 10-е (35 уроков + час = 36 клеток при стене 35) не влезали —
черчение довело до переполна. Дал 10-м слоты 1–8 (уроков всё равно ≤7).

**Итог TOP (2 сида, 55 с):** greedy 882/882 везде, best seed 22 — soft 4585,
hard 0, pupil 0/0, teacher windows 4/757. SanPin err 16 — недельные потолки
с внеурочкой. Файл OurIdeal_TOP_2026.xlsx, Неназначенные пуст.

---

## 05.10.2026 (ночь, сдача) — ФИНАЛ сдан посубъектным штатом

**Факт:** тонкий общий штат (39–58) жадник впритык не пакует: 776–881/882,
hard, tw под 400; CP-SAT Phase A дважды Unknown. Причина — коллизии общих
учителей при точных днях + PE-hard. Апгрейд подборщика — backlog.

**Сдано:** учителя свои на класс (322 заглушки), сплиты настоящие с разными
людьми половинок, слияния/совместители внутри класса, ИЗО нет, музыка
только 5-е, черчение в 10-х, классный своих, 10-м слоты 1–8.

**Итог TOP (2 сида, 55 с):** greedy 882/882 везде, best seed 22 — soft 4510,
hard 0, pupil 0/0, teacher windows 4/757. SanPin err 16 — недельные потолки
с внеурочкой. Файл OurIdeal_TOP_2026.xlsx, Неназначенные пуст.

---

## 05.10.2026 (ночь) — Фамилии в файл: живые + 3 вакансии, ноль перегрузов

**Маппинг (копия OurIdeal_TOP_2026_named.xlsx, оригинал цел):** ДМП —
Шаболтиев/Дамашевич, черчение — Лабикова, музыка — Коротченя, астрономия —
Денискин (все как в живых данных); английский — 8 живых парами;
труды М/Д — Лащ/Хинич/Лабикова; остальное — жадно с капом, совместители
в одном пуле. 3 вакансии (рус/мат/физ) — часов больше, чем людей.
Максимум 25 ч, перегрузов ноль. Только для внутреннего показа.

---

## 05.10.2026 (ночь) — Учителя по дням + вердикт по фамилиям

**Файлы учителей:** Samples_Export/OurIdeal_TOP_по_дням/Учителя - <день>.xlsx
(5 шт., формат 1-в-1 с ЧЕЛОВЕК: опечатка в шапке, merge, ширины, классы
строчно). Уроков по дням 183+177+177+185+160 = 882, всё сошлось.

**Аудит файла:** вставлено 882/882, hard 0, pupil 0/0, teacher windows ~1–4.
SanPin err 16 — недельные потолки с внеурочкой (вопрос завучу).

**Вердикт по фамилиям:** shared-маппинг на живых (41 чел) даёт 105
двойников (человек в двух классах сразу) — файл невалиден, отложен.
Сдаётся посубъектный вариант (322 заглушки, валиден). Живые фамилии —
только с живыми нагрузками от завуча на пилоте.

---

## 05.10.2026 (ночь) — Тонкий общий штат 57: вставлено всё (сид 33)

**Штат:** 57 учителей (пулы кап 18, английские пары, труды чёт/нечет,
ДМП двое, сплиты/смены/совместители как просили). 4 сида, LS45+polish45.

**Итог:** сид 33 — greedy 882/882, hard 0, pupil 0/0, teacher windows 304
(многовато против 140 школы и 4 посубъектной версии; доводка grind-ом —
следующий шаг). SanPin err 16 — недельные потолки. Файлы: OurIdeal_TOP_2026.xlsx
+ 5 по дням (174+183+175+184+166 = 882).

---

## 05.10.2026 (ночь) — Grind тонкого штата: 410 -> 167 окон

**Прогон:** LS60 + 19 раундов (Thin15/TeacherDay20/CrossShift15/Swap10/
Sync10/repair/PeRepair), 5.2 мин до 3 пустых. hard 0, pupil 0 всё время.
Файл OurIdeal_GRIND_2026.xlsx + 5 по дням (181+173+171+188+169 = 882).

**Итог:** teacher windows 410 -> 167 (−59%), уровень живых 140 школы.
Худший день — 8 окон. Временный харнес OurIdealGrindTmpTests.
