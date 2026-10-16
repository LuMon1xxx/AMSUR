namespace Amsur.Tests;

// Демо-xlsx пишутся тестами-витринами прямо в ОТСЛЕЖИВАЕМЫЙ каталог samples/.
// Побочный эффект: любой полный прогон изменял 8 бинарников и грязнил рабочее
// дерево (риск случайно закоммитить мусор). Теперь запись явная:
//     AMSUR_WRITE_SAMPLES=1 dotnet test src/Amsur.Tests
// Без флага экспорт всё равно исполняется (код под тестом), но результат уходит
// в память, а не в репозиторий.
internal static class SampleArtifacts
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("AMSUR_WRITE_SAMPLES") == "1";

    private static string Dir => System.IO.Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples");

    public static string Path(string name) => System.IO.Path.Combine(Dir, name);

    /// <summary>
    /// Поток для демо-артефакта: файл в samples/ при AMSUR_WRITE_SAMPLES=1,
    /// иначе MemoryStream. Экспортёр как код исполняется в обоих случаях.
    /// </summary>
    public static Stream OpenWrite(string name)
    {
        if (!Enabled) return new MemoryStream();
        Directory.CreateDirectory(Dir);
        return new FileStream(Path(name), FileMode.Create);
    }
}
