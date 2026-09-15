using Amsur.Domain;

namespace Amsur.Application;

// R2: разрешение часов/нед по приоритету Класс > Параллель > Предмет-дефолт > строка Excel.
// Чистые функции (P1: только импорт; solver-логика — P2).
public static class HourResolution
{
    /// <summary>Параллель из имени класса: ведущие цифры ("9Б"→9, "11А"→11). Нет цифр → 0.</summary>
    public static int ParseGrade(string className)
    {
        int i = 0;
        while (i < className.Length && char.IsDigit(className[i])) i++;
        return i > 0 && int.TryParse(className[..i], out int g) ? g : 0;
    }

    /// <summary>
    /// Итоговые часы: override(класс,предмет) → норма(предмет,параллель) →
    /// норма(предмет,0=дефолт) → часы строки. Имена — OrdinalIgnoreCase.
    /// Неположительные часы в норме/override — громкая ошибка (fail-loud, D-34/R-D).
    /// </summary>
    public static int ResolveHours(
        string className,
        int grade,
        string subjectName,
        int rowHours,
        IReadOnlyList<HourNormRow> norms,
        IReadOnlyList<HourOverrideRow> overrides)
    {
        var ov = overrides.FirstOrDefault(o =>
            string.Equals(o.ClassName, className, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.SubjectName, subjectName, StringComparison.OrdinalIgnoreCase));
        if (ov is not null)
        {
            if (ov.HoursPerWeek <= 0)
                throw new InvalidOperationException(
                    $"Переопределение часов '{ov.ClassName} · {ov.SubjectName}': " +
                    $"часов должно быть больше 0 (задано {ov.HoursPerWeek}).");
            return ov.HoursPerWeek;
        }
        var norm = norms.FirstOrDefault(n =>
                string.Equals(n.SubjectName, subjectName, StringComparison.OrdinalIgnoreCase) &&
                n.Grade == grade)
            ?? norms.FirstOrDefault(n =>
                string.Equals(n.SubjectName, subjectName, StringComparison.OrdinalIgnoreCase) &&
                n.Grade == 0);
        if (norm is not null)
        {
            if (norm.HoursPerWeek <= 0)
                throw new InvalidOperationException(
                    $"Норма часов '{norm.SubjectName}' (параллель " +
                    $"{(norm.Grade == 0 ? "дефолт" : norm.Grade.ToString())}): " +
                    $"часов должно быть больше 0 (задано {norm.HoursPerWeek}).");
            return norm.HoursPerWeek;
        }
        return rowHours;
    }
}
