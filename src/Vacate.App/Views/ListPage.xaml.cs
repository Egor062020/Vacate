using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vacate.Core.Localization;

namespace Vacate.App.Views;

/// <summary>Строка списка. Поля универсальны, чтобы одна разметка подошла всем разделам.</summary>
/// <param name="Title">Основная строка.</param>
/// <param name="Subtitle">Путь или техническая подробность — показывается моноширинным.</param>
/// <param name="Value">Величина справа: размер, версия, состояние.</param>
/// <param name="Badge">Короткая пометка рядом с названием.</param>
/// <param name="Note">Пояснение под строкой: почему нельзя трогать, чем грозит.</param>
/// <param name="Payload">
/// Объект, который строка представляет. Нужен разделам, где по строке выполняется действие:
/// показать название мало, для удаления требуется сама программа с её командой и путями.
/// </param>
public sealed record ListRow(
    string Title,
    string Subtitle,
    string Value,
    string? Badge = null,
    string? Note = null,
    object? Payload = null)
{
    public Visibility BadgeVisibility => string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Общая страница-список для разделов, которые показывают данные.
/// </summary>
public partial class ListPage : UserControl
{
    private Func<CancellationToken, Task<(IReadOnlyList<ListRow> Rows, string Status)>>? _loader;
    private Func<Task>? _extraAction;
    private Action? _secondExtraAction;
    private bool _requiresSelection = true;

    public ListPage()
    {
        InitializeComponent();
    }

    /// <summary>Выделенная строка, если раздел позволяет выбирать.</summary>
    protected ListRow? Selected => Items.SelectedItem as ListRow;

    /// <summary>Все выделенные строки. Пригодно там, где действие выполняется пакетом.</summary>
    protected IReadOnlyList<ListRow> SelectedRows => Items.SelectedItems.OfType<ListRow>().ToList();

    /// <summary>
    /// Добавить вторую кнопку — для действия, которому не нужна выбранная строка.
    /// </summary>
    protected void AddSecondaryAction(string text, Action action)
    {
        _secondExtraAction = action;

        SecondExtraButton.Content = text;
        SecondExtraButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Разрешить выделение нескольких строк.
    /// </summary>
    /// <remarks>
    /// Не по умолчанию: там, где действие применяется к одной цели, множественное
    /// выделение только путает — человек отмечает три строки и не понимает,
    /// над какой из них сработает кнопка.
    /// </remarks>
    protected void AllowMultipleSelection() => Items.SelectionMode = SelectionMode.Extended;

    /// <summary>Настроить страницу под конкретный раздел.</summary>
    /// <param name="extraButtonText">Надпись на дополнительной кнопке.</param>
    /// <param name="requiresSelection">
    /// Действию нужна выбранная строка. Тогда кнопка недоступна, пока строка не выбрана:
    /// предлагать нажатие, которому не над чем работать, — обман.
    /// </param>
    protected void Configure(
        string title,
        string subtitle,
        Func<CancellationToken, Task<(IReadOnlyList<ListRow> Rows, string Status)>> loader,
        string? extraButtonText = null,
        Func<Task>? extraAction = null,
        bool requiresSelection = true)
    {
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        _loader = loader;
        _extraAction = extraAction;
        _requiresSelection = requiresSelection;

        if (extraButtonText is not null)
        {
            ExtraButton.Content = extraButtonText;
            ExtraButton.Visibility = Visibility.Visible;
            ExtraButton.IsEnabled = !requiresSelection;

            if (requiresSelection)
            {
                Items.SelectionChanged += (_, _) => ExtraButton.IsEnabled = Selected is not null;
            }
        }

        Loaded += async (_, _) =>
        {
            if (Items.ItemsSource is null)
            {
                await LoadAsync();
            }
        };
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAsync();

    private void OnSecondExtra(object sender, RoutedEventArgs e) => _secondExtraAction?.Invoke();

    private async void OnExtra(object sender, RoutedEventArgs e)
    {
        if (_extraAction is null)
        {
            return;
        }

        // Кнопка выключается на время работы: повторное нажатие запустило бы
        // вторую попытку удаления той же программы поверх первой.
        ExtraButton.IsEnabled = false;

        try
        {
            await _extraAction();
        }
        finally
        {
            ExtraButton.IsEnabled = !_requiresSelection || Selected is not null;
        }
    }

    protected async Task LoadAsync()
    {
        if (_loader is null)
        {
            return;
        }

        RefreshButton.IsEnabled = false;
        StatusText.Text = Strings.Get("Common.Reading");

        SetScanIndicator(true);

        try
        {
            var (rows, status) = await _loader(CancellationToken.None);

            Items.ItemsSource = rows;
            StatusText.Text = status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Пользователю нужна причина, а не тип исключения.
            StatusText.Text = Strings.Get("Common.NoRightsHere");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            SetScanIndicator(false);
        }
    }

    /// <summary>
    /// Показать, что работа идёт.
    /// </summary>
    /// <remarks>
    /// Обход диска и чтение списка программ занимают заметное время, и всё это время
    /// неподвижный экран читается как «зависло». Системная настройка уменьшения
    /// анимации уважается: для того, кто её включил, движение — не мелкое неудобство.
    /// </remarks>
    private void SetScanIndicator(bool busy)
    {
        ScanIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        if (!busy || !SystemParameters.ClientAreaAnimation)
        {
            ScanPulseShift.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }

        ScanPulseShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = 0,
            To = ScanIndicator.Width - ScanPulse.Width,
            Duration = TimeSpan.FromMilliseconds(900),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }
}
