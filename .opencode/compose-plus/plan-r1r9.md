# PLAN — R1–R9 пакетами (после overflow-фикса; Stitch-план остаётся отдельно)

## Карта «таб → Domain → store → solver» (где настраивается — ответ на вопрос R7)
| Фича | Таб SchoolDataWindow | Domain | SQLite | Solver/валидатор |
|---|---|---|---|---|
| R1 спец-кабинеты | Кабинеты: Режим [Универсальный\|Только вручную\|ONLY-предмет] + Предмет | Room.IsManualOnly, Room.OnlySubjectId (nullable) | миграция Rooms | Builder: manual-only исключён из кандидатов; ONLY чужой = Forbidden-hard; Validator hard; ручное Preview всегда пропускает |
| R2 часы | Часы: матрица предмет×параллель + overrides (Класс/Предмет/Часы) | SubjectDefaultHours{SubjectId,Grade,Hours}, ClassHourOverride{ClassId,SubjectId,Hours} | 2 новые таблицы | Builder: часы = override > parallel > subject-default > CurriculumItem fallback; Import fail-loud при конфликте типов |
| R3 общий урок | Общие уроки: [x]Включить, День, Слот, Параллели 5–11, Кабинеты: свои/общий, Учитель=классрук (read-only) — ДЕФОЛТ ВЫКЛ | CommonLesson{Day,Slot,Grades,UseOwnRooms,Enabled} | 1 таблица | Builder: один SyncGroupId на все классы+группы этого часа; Validator: одинаковый start hard; LS: общий урок frozen (не двигать) |
| R4 классрук | Классы: колонка Классрук (ComboBox учителей) | SchoolClass.ClassTeacherId (nullable) | миграция Classes | R3 источник; отображение в Schedule бейджем; валидация: учитель существует |
| R5 многоместный | Кабинеты: Мин/Макс(hard)/Желательно(soft) + [x]Подгруппа=отдельный класс | Room.MaxGroups, Room.DesiredGroups, Room.CountSubgroupAsGroup | миграция Rooms | Lane-модель: линий = MaxGroups; validator hard sum<=Max; soft-штраф за превышение Desired (новый код room-crowding, вес настраиваемый) |
| R6 сложность | Предметы: Слайдер 1..10 + IsHeavy-авто + MaxPerDay | Subject.Difficulty (есть), Settings.IsHeavyThreshold (дефолт 7) | QualitySettings | SoftEvaluator heavy-edge уже есть; порог из настроек; UI только меняет число |
| R7 закрепление | Учителя+Нагрузка: колонка Закрепление [Нет\|Класс\|Параллель], режим в Нагрузке; глобальный режим в Settings | TeacherAssignment{TeacherId,SubjectId,Scope,ClassId?,Grade?}, Settings.AssignMode | 1 таблица + settings | Import fail-loud при нарушении Hard-класс; Soft = предпочтение (штраф за разрыв); Validator: разрыв в режиме Hard = hard-issue |
| R8 приоритет | Settings → «Приоритет выпускных»: тумблер + слайдеры 11-е/9-е | Settings.GradeWeights{11:3, 9:2, other:1}, Enabled (дефолт вкл) | QualitySettings | SoftEvaluator: штрафы × вес класса (student-gap/late-start/subj-double взвешиваются); дефолт 3/2/1, диапазон 1..5 |
| R9 экспорт | ExportWindow: Radio Класс/Учитель/Кабинет + [x]Лист Teacher; ScheduleWindow табы | — (нет Domain) | — | ScheduleExcelExporter: лист Teacher (Учитель\|День\|Урок\|Класс\|Кабинет); preview первые 12 строк выбранного вида |

## Пакеты (зависимости и приёмка)
- **P1 Domain+stores**: поля/таблицы R1,R2,R4,R5,R6-настройки,R7,R8 + миграции + importer. Приёмка: build + PersistenceTests green + roundtrip seed/verify.
- **P2 Builder/Validator/Core**: кандидаты R1, часы R2, SyncGroup R3, lanes R5, веса R8, закрепление R7, порог R6. Приёмка: существующие тесты green + новые unit-тесты на каждое правило (hard срабатывает, soft штрафует, дефолты не ломают DemoSchool).
- **P3 UI SchoolDataWindow (8 табов) + Settings (приоритет)**: табы по карте; всё на существующих токенах/стилях. Приёмка: build + STA-конструкт всех окон + скриншоты.
- **P4 Export/Schedule R9**: лист Teacher + табы + бейджи SPEC/ONLY/общий урок + легенда. Приёмка: Excel с листом Teacher открывается; preview 12 строк.
- **P5 E2E + regression gate**: DemoSchool + RealSchool stress; V1-функции живы; скриншоты до/после через mimo; Final Acceptance Report.

## Ворота пакета
build green → тесты пакета green → полный сьют без регрессий → (для UI) скриншот. Overflow-фикс (MinWidth=0 центр. колонки) уже прошёл ворота 14.09.2026: build ok + WpfShell 7/7.
