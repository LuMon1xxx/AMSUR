using System.Windows;
using System.Windows.Controls;

namespace Amsur.Wpf;

// Номера строк в заголовках гридов (таблицы DataView).
// Включение: wpf:DataGridRowNumbers.ShowRowNumbers="True" (xmlns:wpf="clr-namespace:Amsur.Wpf").
// При true подписываемся на LoadingRow и проставляем e.Row.Header = индекс + 1;
// отписка — при false и при Unloaded (без утечек подписок на выгруженных гридах).
public static class DataGridRowNumbers
{
    public static readonly DependencyProperty ShowRowNumbersProperty =
        DependencyProperty.RegisterAttached(
            "ShowRowNumbers",
            typeof(bool),
            typeof(DataGridRowNumbers),
            new PropertyMetadata(false, OnShowRowNumbersChanged));

    public static bool GetShowRowNumbers(DependencyObject obj) =>
        (bool)obj.GetValue(ShowRowNumbersProperty);

    public static void SetShowRowNumbers(DependencyObject obj, bool value) =>
        obj.SetValue(ShowRowNumbersProperty, value);

    private static void OnShowRowNumbersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
            return;
        if ((bool)e.NewValue)
        {
            grid.LoadingRow += OnLoadingRow;
            grid.Unloaded += OnUnloaded;
            grid.RowHeaderWidth = 34;
            // Строки, уже материализованные до включения (сортировка/фильтр
            // пересоздаёт их через LoadingRow сами).
            foreach (var item in grid.Items)
            {
                if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
                    row.Header = (row.GetIndex() + 1).ToString();
            }
        }
        else
        {
            grid.LoadingRow -= OnLoadingRow;
            grid.Unloaded -= OnUnloaded;
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            grid.LoadingRow -= OnLoadingRow;
            grid.Unloaded -= OnUnloaded;
        }
    }

    private static void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    }
}
