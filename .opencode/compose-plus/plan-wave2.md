# PLAN Wave-2: пакеты (15.09.2026)

> База: commit b3ae5fd. Ворота пакета: build 0 errors + затронутые тесты зелёные
> + новых падений нет (кроме известного R-W2-1) + запись в WORK_LOG.

| Пакет | Что | Файлы | Приёмка |
|---|---|---|---|
| P-CLEAN | Combat×3 → _archive/samples-20260915; publish-single → _archive/builds-20260915; RealSchool_Schedule.xlsx + ~$… → _archive/samples-20260915 (git mv) | корень | git status до/после; сборки/тесты не затронуты |
| P-AUDIT | PROJECT_STATUS: 188/188→259 (1 KNOWN-FAIL), RuleCatalog v3→v5, session-only→персист-есть; BENCHMARKS: хвост V2; D-34+ D-записи (карантин MidTight, _archive, запрет nemotron) | *.md | цифры сверены с прогоном 15.09 |
| P-B47 | .gitignore (bin/obj, publish, _archive локалки?, *.user), LICENSE MIT, README.md (что/запуск/скриншот-плейсхолдер/ссылки) | 3 новых файла | build+test зелёные (кроме R-W2-1); git status чист от новых bin/obj |
| P-B4 | ScheduleHtmlExporter.ExportHtml (класс-блоки × дни × слоты, печать-CSS) + тест на DemoRows + кнопка/пункт в ExportWindow | Application, Tests, Wpf | тест зелёный; HTML открывается в браузере, печать A4 |
| P-B5 | Fuzzy-словарь сокращений (Матем→Математика, ИЗО→…, Физра→…, ОБЖ→…, Труд→…) + список «что исправить» + тесты | SchoolDataImporter, Tests | тесты зелёные; неизвестное — fail-loud как раньше |
| P-F2 | Колонки НеДоступенДни/НеДоступенСлоты в шаблоне + парсинг в importer → daysOff/unavailability (вместо [],[]) + тесты | ExcelLoadExchange, SchoolDataImporter, Tests | тесты зелёные; ToProblemInput больше не пустой |
| BACKLOG | F1 Splits v2; F3 RoomCaps UI; F4 doubles/PE soft; F5 6-й день/WeekType/TimeSlot; F6 справочник из данных; F7 DnD/Undo; solver-профилирование под MidTight | — | отдельные EPIC, не эта волна |

Порядок: P-CLEAN → P-AUDIT → P-B47 → P-B4 → P-B5 → P-F2. P-B4/P-B5/P-F2 независимы
друг от друга (разные файлы) — можно подряд, тесты после каждого.
