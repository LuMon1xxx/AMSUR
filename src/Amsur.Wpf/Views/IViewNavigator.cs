namespace Amsur.Wpf.Views;

// P-D0: навигация внутри одного окна (вместо всплывающих Window).
public interface IViewNavigator
{
    void NavigateTo(string view);
}
