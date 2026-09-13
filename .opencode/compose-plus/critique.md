# S4 CRITIQUE (сам, без медленных моделей; перекрёстная uiux↔arch↔logic)

## КРИТИКА uiux:
- SEVERITY: HIGH — TEACHER_FRIENDLY описан как «окна учителей усилены за счёт допусков
  у учеников»: противоречит D-28 (student-gap/late-start HARD, весами не торгуются).
  Как проверить: PlacementValidator HARD-блок. Рекомендация: профили крутят ТОЛЬКО
  soft (ordinary/cross/subj/room/heavy), student — gate. Принято в синтез.
- SEVERITY: MEDIUM — полный 6-табовый SettingsWindow в одном эпике со solver-изменениями:
  риск scope creep + непроверенный XAML. Рекомендация: backend полностью, WPF — профиль-селектор
  + минимальный диалог, полный диалог — backlog с готовым контрактом. Принято частично
  (минимальный диалог делаем, если сьют зелен после ядра; иначе backlog).

## КРИТИКА logic:
- SEVERITY: HIGH — вес cross=2 без замера = «красивый Soft» (запрет ТЗ §2).
  Как проверить: benchmark old/new + требование ordinary не растёт. Принято: вес настраиваем,
  дефолт 2 — гипотеза, валидируется S7 (прогоны cross 0/1/2/5, выбор с объяснением).
- SEVERITY: MEDIUM — смена teacher-gap на DISTINCT меняет число на битых размещениях
  (QualityExplainer считает без Distinct). Рекомендация: унифицировать везде одним GapUtils.
  Принято.
- SEVERITY: MEDIUM — targeted-обход по teacher-gap может сломать детерминизм/seed-diversity.
  Рекомендация: порядок = (gap desc, seed-shuffle внутри равных), seed детерминирован.
  Принято.
- SEVERITY: LOW — формула cross=total-ordinary на {7,8} даёт 0 — корректно (смежные уроки
  разных смен без дыры). Проверено разбором. Без действий.

## КРИТИКА arch:
- SEVERITY: HIGH — ShiftBands как глобальные [(1,7),(8,14)] захардкожены под фикстуру;
  произвольная школа с другой сеткой получит неверный cross. Рекомендация: bands из входа
  (ProblemInput+ShiftBands, дефолт по SlotsPerDay), не константа. Принято.
- SEVERITY: MEDIUM — EffectiveRuleSet опциональным параметром (`=null → дефолт`) рискует
  рассинхроном (кто-то забудет передать). Рекомендация: обязательный параметр в новых
  перегрузках + старый метод как тонкая обёртка `=> Evaluate(p, x, EffectiveRuleSet.Default)`.
  Принято.
- SEVERITY: MEDIUM — персистентность профилей новой таблицей + Candidates snapshot:
  объём. Рекомендация: таблица QualityProfiles сейчас; snapshot в Candidate — только
  ProfileName+WeightsHash+CatalogVersion (без полного JSON). Принято.

## ПРОТИВОРЕЧИЯ МЕЖДУ РОЛЯМИ:
- uiux «TEACHER_FRIENDLY ослабляет ученика» vs logic/arch «student HARD неторгуем» →
  побеждают logic/arch; профили только soft. (разрешено)
- uiux «число скрыть, шкала человеческая» vs arch «паритет по числам» →
  гибрид: UI показывает шкалу + подпись числом мелко; движок — числа. (разрешено)
- logic «cross вес 2» vs arch «настраиваемо» → гибрид: дефолт 2 + настройка 0..50. (разрешено)

## РИСКИ (в risks.md):
R1 вес cross подобран под одну фикстуру; R2 fixture 1–4 ломает ассёрт 100 учителей;
R3 targeted-обход регрессирует детерминизм; R4 scope WPF; R5 старые БД/архивы без версии.
