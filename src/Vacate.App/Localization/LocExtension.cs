using System.Windows.Markup;

namespace Vacate.App.Localization;

/// <summary>
/// Подстановка перевода прямо в разметке: <c>Text="{loc:Loc Clean.Title}"</c>.
/// </summary>
/// <remarks>
/// Язык выбирается один раз при запуске и в работающем окне не меняется, поэтому
/// расширение возвращает готовую строку, а не привязку. Живое переключение потребовало бы
/// уведомлений на каждый текст ради возможности, которой пользуются раз в жизни —
/// и то, что при смене языка нужен перезапуск, честно сказано в настройках.
/// </remarks>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    /// <summary>Ключ строки.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
