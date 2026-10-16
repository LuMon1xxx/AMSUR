namespace Amsur.Tests;

// EPIC-H — измерительные тесты (H1–H5b). Раньше без AMSUR_EPICH=1 они молча
// «проходили» (`if (!Enabled()) return;`), не проверив ничего, и это маскировало
// неработающий харнес в отчётах. xUnit v2 (2.9.3) не умеет Assert.Skip — это API v3, —
// поэтому пропуск выставляется на этапе discovery через Skip у FactAttribute:
// тест виден как Skipped, а с AMSUR_EPICH=1 запускается как обычно.
public sealed class EpicHFactAttribute : FactAttribute
{
    public EpicHFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AMSUR_EPICH") != "1")
            Skip = "EPIC-H: задайте AMSUR_EPICH=1, чтобы прогнать измерения.";
    }
}
