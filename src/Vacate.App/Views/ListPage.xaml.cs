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
public sealed record ListRow(string Title, string Subtitle, string Value, string? Badge = null, string? Note = null)
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
    private Action? _extraAction;

    public ListPage()
    {
        InitializeComponent();
    }

    /// <summary>Настроить страницу под конкретный раздел.</summary>
    protected void Configure(
        string title,
        string subtitle,
        Func<CancellationToken, Task<(IReadOnlyList<ListRow> Rows, string Status)>> loader,
        string? extraButtonText = null,
        Action? extraAction = null)
    {
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        _loader = loader;
        _extraAction = extraAction;

        if (extraButtonText is not null)
        {
            ExtraButton.Content = extraButtonText;
            ExtraButton.Visibility = Visibility.Visible;
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

    private void OnExtra(object sender, RoutedEventArgs e) => _extraAction?.Invoke();

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
