# PLAN — пакеты реализации (только XAML + thin code-behind)

## Маппинг UI↔контракты (источник истины)
| Блок Stitch | Контракт |
|---|---|
| Школа counts | AppSession.Summary (Classes/Teachers/Subjects/Lessons/Days/Slots) |
| Ошибки импорта | AppSession.LastImportErrors |
| Профиль/режим | AppSession.QualityRules/ProfileName, GenerateModes.All/BudgetSeconds/Seeds |
| Качество | AppSession.LastQuality/GetActiveQualityAsync → QualityRatingResult(Label/Improvements) |
| Генерация стадии/Top5/стоп/график | GenerateViewModel (Stages/Status/Top5/Trajectory/Diagnostics) + GenerateHost |
| Правка | ManualEditService.Preview/CommitAsync → VerdictText/Message |
| Экспорт | AppSession.ExportActiveAsync → ScheduleExcelExporter.ExportGrid (1 лист, gate Hard>0) |
| Настройки слайдеры | QualitySettingsEditor/RuleResolver/RuleCatalog.WeightRange/HumanScale/QualityHints |
| CUSTOM save | AppSession.SaveCustomProfileAsync/GetSavedProfileAsync |

## Пакеты (порядок = приоритет пользователя)
- **P0-0 App.xaml**: токены #F8FAFC/#FFFFFF/#F1F5F9/#E2E8F0/#CBD5E1/#0F172A/#475569/#94A3B8, акцент #4F46E5/#4338CA/#3730A3/#EEF2FF/#C7D2FE, семантика #059669/#D97706/#DC2626 + фоны, R10/6/4, TH1 22 SemiBold, стили Card/BtnPrimary/Secondary/Badge/RadioCard/Slider/Tab/Chip, тени L1. Критерий: build green, визуально светлая спокойная тема.
- **P0-1 MainWindow**: хром 240/340, hero-градиент + CTA (F5 KeyBinding → Generate), карточки Школа/Профиль (Изменить → окна), 4 мини-карточки (Просмотреть/Скачать шаблон/Настроить/Перейти к сетке — всё существующие хендлеры), шаги 1-5, правая режимы из GenerateModes (лимит-подпись из BudgetSeconds, EXPERT открывает Settings), gauge из LastQuality (Label + Improvements топ-3, без %), совет. Убрать DaysBox/SlotsBox с главной? НЕТ — они нужны импорту (перенести в School). Профили▾ не рисовать. Критерий: build+test+exe+скрин vs mainwindow.
- **P0-2 School**: окно → полноэкранный модуль-стиль (в пределах Window): слева вкладки-счётчики (только Нагрузка активна, остальные disabled с тултипом «правка через Excel»), поиск-фильтр UI-only, грид из Summary/Data (read-only, чипы групп), ошибки из LastImportErrors + «Выбрать другой файл» (без Проигнорировать). Критерий: build+test+exe+скрин vs amsur_1.
- **P0-3 Schedule**: матрица 5×7 из _allRows (группировка Day×Slot, чипы каб., dashed пустые, ring выбранной), топ-статус из GetActiveQualityAsync, табы Все/Класс/Учитель/Кабинет (новое: RoomName фильтр + FillNameBox rooms — UI-only), селектор + поиск UI-only, низ панель Переместить (Day/Slot ComboBox с временами-подписями статикой смен + Проверить/Сохранить существующие), вердикты — один VerdictText стилизованный под 3 состояния парсингом префикса (можно/ухудшение/запрещено — текст уже приходит из Preview). Без DnD. Критерий: build+test+exe+скрин vs amsur_2.
- **P0-4 Generate**: широкий layout, стадии VM (4 шага маппить на VM.Stages без переименования логики), метрики только из VM (FirstFeasible/Best/Candidates/Elapsed + лимит из режима), Остановить (RequestStop), Chart существующий, Top-5 карточки (Title/Badge/Soft/Hard/Summary/Lines/Difference + Открыть/Принять по CanAccept), diagnostics под «Подробнее». Без итераций/ID/памяти. Критерий: build+test+exe+скрин vs amsur_4.
- **P0-5 Settings**: пресеты-карточки (4, описания из QualityHints/GenerateModes), категории-счётчики (Counts = AllCodes/Subjects/Classes/Teachers из Session.Data, иначе 0 — честно), слайдеры поверх QualityPanel (мин/макс из WeightRange, подпись HumanScale + вес мелко, бейдж СТРОГОЕ для (0,0) + NeedsConfirmation), эксперт числа + JSON-превью текстом, низ Имя + Сохранить/Сброс/Отмена/Применить существующие. Без индекса. Критерий: build+test+exe+скрин vs amsur_5.
- **P0-6 Export (новое окно)**: что выгружается (1 лист «Расписание» по классам — честное описание), путь + Обзор (SaveFileDialog существующий паттерн), Выгрузить (ExportActiveAsync, gate), результат-строка, превью-таблица из placements (первые N строк, UI-only), PDF карточка disabled «Скоро». Кнопки из Main/Schedule переключить на открытие окна. Без журнала/fullscreen/табов листов. Критерий: build+test+exe+скрин vs amsur_6.
- **P0-7 Help + модалка**: Help-центр рестайл (Быстрый старт/Профили/Ошибки + шаги 1-5 статикой пути Данные→Профиль→Генерация→Проверка→Печать), FAQ только про реализованное (без DnD/PDF/Undo), хоткеи только F1/F5 (реальные), QualityDetails группировка 4 блока UI-only. Критерий: build+test+exe+скрин vs amsur_3.

## Ворота пакета (каждый)
`dotnet build src/Amsur.slnx` → `dotnet test src/Amsur.Tests` 188/188 → exe запуск → скриншот → сравнение со screen.png → fix (max 3 попытки, каждая новая гипотеза). Окно не считается готовым по одному XAML.
