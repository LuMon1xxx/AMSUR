# BASELINE-V1 (13.09.2026, до редизайна)

- Функции: импорт Excel (шаблон/загрузка, Days/Slots, inline-ошибки), профили Стандарт/Ученикам/Учителям/Наш (персист CUSTOM), режимы QUICK 3с/1seed / STANDARD 12с/3 / MAXIMUM 30с/5 / EXPERT+настройки, генерация Top-5 + стоп + график + «Почему такая оценка», рейтинг Отличное/Хорошее/Требует внимания + топ-3, Accept лучшего, правка Preview/Commit (можно/ухудшение/запрет+причина), экспорт Excel активного (gate Hard>0), dashboard primer-style, STA 6 окон.
- Роуты/экраны: MainWindow 1360×820 (164/*/264), GenerateWindow 760×640, ScheduleWindow 820×560 DataGrid, SettingsWindow 920×640 5 разделов, SchoolData 620×480, QualityDetails 620×440, Help 620×480.
- API-поведения: timeout≠infeasible («не найдено за время»/«остановлено, лучшее сохранено»), solverSettings — диагностика под «Подробнее», SyncGroupId per-build, OccurrenceId MD5(StableKey) детерминированы.
- Инварианты: Hard==0 gate везде (generate/move/swap/export/accept), teacher фиксирован, sync shared-start, maxperday Hard, смена Hard, полнота Hard, student 0 окон/старт≤2 HARD для всех профилей.
- Тесты: 188/188 (~2.5 мин), STA всех окон, exe headless 12с без краша.
- Скриншоты «до»: отсутствуют (V1-скрины не снимались); «до» = текущий XAML в git-рабочей копии + Stitch screen.png как target (пары v1/v2 будут сниматься с P0-1).
