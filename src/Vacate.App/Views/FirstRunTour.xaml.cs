using System.Windows;
using System.Windows.Media;
using Vacate.App.Localization;

namespace Vacate.App.Views;

/// <summary>
/// Знакомство при первом запуске.
/// </summary>
/// <remarks>
/// Не перечисление возможностей и не мастер настройки. Четыре страницы о том,
/// что программа делает с чужими файлами и как вернуть сделанное: это ровно то,
/// чего человек не узнает, нажимая наугад, и что должен знать до первого нажатия.
///
/// Показывается один раз. Программа, здоровающаяся при каждом запуске, воспитывает
/// привычку закрывать её окна не читая — и тогда предупреждения перестают работать.
/// </remarks>
public partial class FirstRunTour : Window
{
    private static readonly TourStep[] Steps =
    [
        new("Tour.1.Title", "Tour.1.Body", "Tour.1.Aside"),
        new("Tour.2.Title", "Tour.2.Body", "Tour.2.Aside"),
        new("Tour.3.Title", "Tour.3.Body", "Tour.3.Aside"),
        new("Tour.4.Title", "Tour.4.Body", "Tour.4.Aside"),
    ];

    private int _index;

    public FirstRunTour()
    {
        InitializeComponent();

        Show(0);
    }

    private void Show(int index)
    {
        _index = index;

        var step = Steps[index];

        StepNumber.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Strings.Get("Tour.StepOf"),
            index + 1,
            Steps.Length);

        StepTitle.Text = Strings.Get(step.TitleKey);
        StepBody.Text = Strings.Get(step.BodyKey);
        StepAsideText.Text = Strings.Get(step.AsideKey);

        NextButton.Content = Strings.Get(index == Steps.Length - 1 ? "Tour.Done" : "Tour.Next");
        SkipButton.Visibility = index == Steps.Length - 1 ? Visibility.Collapsed : Visibility.Visible;

        // Пройденное закрашено, оставшееся приглушено: сколько ещё осталось,
        // видно без счётчика.
        Dots.ItemsSource = Enumerable.Range(0, Steps.Length)
            .Select(i => i <= index
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("BorderBrush"))
            .ToList();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_index == Steps.Length - 1)
        {
            Close();
            return;
        }

        Show(_index + 1);
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Close();

    private sealed record TourStep(string TitleKey, string BodyKey, string AsideKey);
}
