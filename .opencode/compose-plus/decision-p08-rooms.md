# P0-8 DECISION — проброс Rooms в ProblemInput (13.09.2026)

## Точка поломки
`SchoolData.ToProblemInput()` (`src/Amsur.Application/SchoolDataImporter.cs:26-29`):
`new(Classes, Teachers, Subjects, Curriculum, Groups, [], [], ...)` — позиционные `[], []`
это `daysOff`/`unavailability` (их в `SchoolData` нет — корректно), а `rooms`/`roomCaps`
остаются дефолтными `null → []`. `SchoolData.Rooms` при этом заполнен импортёром
(имя → `Room{PhysicalCapacity=30, MaxSimultaneousGroups=1}`, `CurriculumItem.RoomId` проставлен).

## Проверка остальных полей — потерь нет
`DaysOff`, `Unavailability`, `Relations`, `RoomCaps`, `ClassSlots`, `ShiftBands`,
`ExcludedPairs` в `SchoolData` отсутствуют как класс → передавать нечего, не баг.
`CurriculumItem.RoomId` (предпочтение) уже течёт через `Curriculum`.

## Фикс (минимальный)
`rooms: Rooms` в `ToProblemInput()`. `RoomCaps` передавать нечего (источника capability
в импорте нет) — оставить дефолт.

## Принятое следствие (не баг, требование SPEC)
По D-14 + `RoomAuditTests`: комнаты в solver — binding (lanes по `MaxSimultaneousGroups`,
`Forbidden` — Hard, `PhysicalCapacity < need` — исключение кандидата, нет кандидатов →
infeasible). Раньше app-путь молча игнорировал комнаты; после фикса переподписанные
кабинеты дают infeasible/validator-hard вместо «решения» без кабинетов. Это SPEC-поведение,
а не регрессия. Митигация: импортерные дефолты (30 мест / 1 группа) покрывают типовой класс
(25 учеников); RealSchool-путь (40 комнат) уже работает с rooms напрямую.

## Backlog (не изобретать здесь)
- `RoomCapability` (special/forbidden) не выразить через Excel-импорт — нужен источник (P1).
- Required vs Preferred: движок считает все не-Forbidden равными (уже зафиксировано в D-14).
- `room-модель v2` / декомпозиция по сменам — ворота D-23, вне скоупа.
- Звонки/слоты смен — вне скоупа.

## Противоречия
Нет: D-14 требует rooms в solver; D-23 ограничивает масштаб прогонов, не путь данных.
Ни один существующий тест не требует пустых `Rooms` через `SchoolData` (проверено grep).
