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

