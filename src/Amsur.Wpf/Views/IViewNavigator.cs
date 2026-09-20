namespace Amsur.Wpf.Views;

// P-D0: навигация внутри одного окна (вместо всплывающих Window).
public interface IViewNavigator
{
    void NavigateTo(string view);

    // P-D3: запуск генерации с готовым входом задачи (строит view + оркестратор).
    void OpenGenerate(Amsur.Scheduling.Core.ProblemInput input);
}
