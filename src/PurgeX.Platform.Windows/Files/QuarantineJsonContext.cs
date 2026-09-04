using System.Text.Json.Serialization;

namespace PurgeX.Platform.Windows.Files;

/// <summary>
/// Контекст сериализации для манифеста карантина.
/// </summary>
/// <remarks>
/// Генерируется на этапе компиляции: без отражения во время работы и без сюрпризов
/// при публикации с обрезкой неиспользуемого кода.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(QuarantineRecord))]
internal sealed partial class QuarantineJsonContext : JsonSerializerContext;
