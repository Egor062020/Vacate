using System.IO;
using System.Windows;
using System.Windows.Controls;

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

    public ListPage()
    {
        InitializeComponent();
    }

    /// <summary>Выделенная строка, если раздел позволяет выбирать.</summary>
    protected ListRow? Selected => Items.SelectedItem as ListRow;

    /// <summary>Настроить страницу под конкретный раздел.</summary>
    /// <param name="extraButtonText">
    /// Надпись на дополнительной кнопке. Кнопка остаётся недоступной, пока строка
    /// не выбрана: действие без выбранной цели выполнить не над чем, и предложение
    /// нажать её раньше времени было бы обманом.
    /// </param>
    protected void Configure(
        string title,
        string subtitle,
        Func<CancellationToken, Task<(IReadOnlyList<ListRow> Rows, string Status)>> loader,
        string? extraButtonText = null,
        Func<Task>? extraAction = null)
    {
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        _loader = loader;
        _extraAction = extraAction;

        if (extraButtonText is not null)
        {
            ExtraButton.Content = extraButtonText;
            ExtraButton.Visibility = Visibility.Visible;
            ExtraButton.IsEnabled = false;

            Items.SelectionChanged += (_, _) => ExtraButton.IsEnabled = Selected is not null;
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
            ExtraButton.IsEnabled = Selected is not null;
        }
    }

    protected async Task LoadAsync()
    {
        if (_loader is null)
        {
            return;
        }

        RefreshButton.IsEnabled = false;
        StatusText.Text = "Читаю…";

        try
        {
            var (rows, status) = await _loader(CancellationToken.None);

            Items.ItemsSource = rows;
            StatusText.Text = status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Пользователю нужна причина, а не тип исключения.
            StatusText.Text = "Часть данных недоступна без прав администратора.";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }
}
