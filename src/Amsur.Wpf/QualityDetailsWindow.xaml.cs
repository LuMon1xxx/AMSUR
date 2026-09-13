using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Amsur.Wpf;

public partial class QualityDetailsWindow : Window
{
    // P0-7: группировка строк — чисто оформление (UI-only) по подстрокам.
    // Новых чисел не выдумываем: показываем те же строки, только раскладываем по 4 блокам.
    private static readonly (string Title, string[] Keys)[] Groups =
    [
        ("Окна у учителей", ["учител"]),
        ("Перерывы между сменами", ["межсмен", "смен"]),
        ("Повторы предмета за день", ["повтор", "сдвоен"]),
        ("Нормы СанПиН", ["санпин", "норм", "тяжёл", "тяжел"]),
    ];

    public QualityDetailsWindow(string title, IReadOnlyList<string> lines)
    {
        InitializeComponent();
        TitleText.Text = title;
        BuildGroups(lines);
    }

    private void BuildGroups(IReadOnlyList<string> lines)
    {
        GroupsPanel.Children.Clear();
        var rest = new List<string>(lines);
        foreach (var (gtitle, keys) in Groups)
        {
            var mine = rest.Where(s =>
                keys.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();
            if (mine.Count == 0) continue;
            foreach (var s in mine) rest.Remove(s);
            GroupsPanel.Children.Add(GroupCard(gtitle, mine));
        }
        if (rest.Count > 0)
            GroupsPanel.Children.Add(GroupCard("Прочее", rest));
        if (lines.Count == 0)
            GroupsPanel.Children.Add(new TextBlock
            {
                Text = "Замечаний нет — расписание чистое по всем проверкам.",
                FontSize = 13, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("BGood"),
            });
    }

    private Border GroupCard(string title, List<string> items)
    {
        var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8) };
        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new Border
        {
            Style = (Style)FindResource("Badge"),
            Background = (Brush)FindResource("BSubtle"),
            Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock
            {
                Text = items.Count.ToString(), FontSize = 11,
                Foreground = (Brush)FindResource("BTextSoft"),
            },
        });
        sp.Children.Add(head);
        foreach (var s in items)
            sp.Children.Add(new TextBlock
            {
                Text = "•  " + s, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        card.Child = sp;
        return card;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
