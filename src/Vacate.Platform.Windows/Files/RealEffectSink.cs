using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Vacate.Abstractions.Execution;
using Vacate.Abstractions.Model;
using Vacate.Abstractions.Safety;

// Наш тип значения реестра и системный называются одинаково.
// Псевдонимы убирают неоднозначность и заодно делают явным, о каком из двух идёт речь.
using ModelValueKind = Vacate.Abstractions.Model.RegistryValueKind;
using Win32ValueKind = Microsoft.Win32.RegistryValueKind;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Боевой приёмник действий: единственное место в продукте, где что-то реально удаляется.
/// </summary>
/// <remarks>
/// Парный к нему <c>RecordingEffectSink</c> используется для предпросмотра. Какой из двух
/// подставлен — решается при сборке зависимостей, и исполнитель этого не знает.
/// </remarks>
public sealed class RealEffectSink(IQuarantine quarantine) : IEffectSink
{
    public async Task<EffectOutcome> DeleteFileAsync(FileTarget target, DeleteDisposition disposition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();

        // Повторная проверка непосредственно перед действием.
        //
        // Между составлением плана и удалением проходит время, и за это время содержимое
        // общедоступных каталогов может измениться. Проверка закрывает случай, когда объект
        // подменили ссылкой на системный каталог: тогда удаление с повышенными правами
        // уничтожило бы систему, а программа стала бы инструментом чужой атаки.
        //
        // ОГРАНИЧЕНИЕ первой версии: проверка идёт по пути, а не по открытому дескриптору.
        // Полное закрытие гонки требует работы через дескриптор с относительным открытием
        // потомков — это отдельная задача платформенного слоя, отмеченная в описании проекта.
        var recheck = Recheck(target);

        if (recheck is not null)
        {
            return recheck;
        }

        try
        {
            ClearReadOnly(target.Path);

            switch (disposition)
            {
                case DeleteDisposition.Quarantine:
                    var stored = await quarantine.StoreAsync(target, ct).ConfigureAwait(false);

                    return stored.Success
                        ? EffectOutcome.Success(target.SizeOnDiskBytes, stored.UndoToken)
                        : EffectOutcome.Failed(stored.Reason ?? LocalizedText.FromResource("Sink.QuarantineFailed"));

                case DeleteDisposition.RecycleBin:
                    if (target.IsDirectory)
                    {
                        FileSystem.DeleteDirectory(target.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    else
                    {
                        FileSystem.DeleteFile(target.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }

                    return EffectOutcome.Success(target.SizeOnDiskBytes);

                default:
                    if (target.IsDirectory)
                    {
                        Directory.Delete(target.Path, recursive: true);
                    }
                    else
                    {
                        File.Delete(target.Path);
                    }

                    return EffectOutcome.Success(target.SizeOnDiskBytes);
            }
        }
        catch (IOException ex)
        {
            // Занятый файл — не ошибка программы, а обычное состояние системы.
            // Пользователю называем держателя, а не код ошибки.
            return EffectOutcome.Failed(
                LocalizedText.FromResource("Sink.FileInUse", target.Path),
                FileLockInspector.DescribeHolder(target.Path) ?? ex.Message);
        }
        catch (UnauthorizedAccessException)
        {
            // Отказ у процесса, который уже работает с повышенными правами, означает
            // не нехватку прав, а владельца в лице системной службы, защиту папок
            // антивирусом или чужой зашифрованный файл. Совет «запустите от администратора»
            // здесь был бы бесполезен и только подорвал бы доверие к остальным подсказкам.
            return EffectOutcome.Failed(LocalizedText.FromResource("Sink.AccessDenied", target.Path));
        }
    }

    public Task<EffectOutcome> DeleteRegistryAsync(RegistryTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();

        try
        {
            using var root = OpenHive(target, writable: true);

            if (root is null)
            {
                return Task.FromResult(EffectOutcome.Skipped(LocalizedText.FromResource("Sink.RegistryKeyMissing")));
            }

            if (target.IsWholeKey)
            {
                root.DeleteSubKeyTree(target.SubKeyPath, throwOnMissingSubKey: false);
            }
            else
            {
                using var key = root.OpenSubKey(target.SubKeyPath, writable: true);

                if (key is null)
                {
                    return Task.FromResult(EffectOutcome.Skipped(LocalizedText.FromResource("Sink.RegistryKeyMissing")));
                }

                key.DeleteValue(target.ValueName!, throwOnMissingValue: false);
            }

            return Task.FromResult(EffectOutcome.Success(0));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(EffectOutcome.Failed(LocalizedText.FromResource("Sink.RegistryAccessDenied")));
        }
    }

    public Task<EffectOutcome> SetRegistryValueAsync(RegistryTarget target, RegistryValueData value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();

        try
        {
            using var root = OpenHive(target, writable: true);

            if (root is null || target.ValueName is null)
            {
                return Task.FromResult(EffectOutcome.Skipped(LocalizedText.FromResource("Sink.RegistryKeyMissing")));
            }

            using var key = root.CreateSubKey(target.SubKeyPath, writable: true);

            if (key is null)
            {
                return Task.FromResult(EffectOutcome.Failed(LocalizedText.FromResource("Sink.RegistryCreateFailed")));
            }

            key.SetValue(target.ValueName, Decode(value), Translate(value.Kind));

            return Task.FromResult(EffectOutcome.Success(0));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(EffectOutcome.Failed(LocalizedText.FromResource("Sink.RegistryAccessDenied")));
        }
    }

    public Task<EffectOutcome> EmptyRecycleBinAsync(string volumeRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Очистка Корзины необратима по определению, поэтому выполняется только
        // отдельной операцией — охрана не пропускает её в одном пакете с удалением в Корзину.
        var result = NativeMethods.EmptyRecycleBin(volumeRoot);

        return Task.FromResult(result
            ? EffectOutcome.Success(0)
            : EffectOutcome.Failed(LocalizedText.FromResource("Sink.EmptyRecycleBinFailed")));
    }

    private static EffectOutcome? Recheck(FileTarget target)
    {
        try
        {
            var attributes = File.GetAttributes(target.Path);

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return EffectOutcome.Skipped(LocalizedText.FromResource("Sink.BecameReparsePoint", target.Path));
            }

            var isDirectoryNow = attributes.HasFlag(FileAttributes.Directory);

            if (isDirectoryNow != target.IsDirectory)
            {
                // Объект подменили другим типом — работать с ним нельзя.
                return EffectOutcome.Skipped(LocalizedText.FromResource("Sink.TargetChanged", target.Path));
            }

            return null;
        }
        catch (FileNotFoundException)
        {
            // Объект исчез сам. Это успешный исход задачи «его не должно быть»,
            // но приписывать себе освобождённое место нельзя.
            return EffectOutcome.Skipped(LocalizedText.FromResource("Sink.AlreadyGone", target.Path));
        }
        catch (DirectoryNotFoundException)
        {
            return EffectOutcome.Skipped(LocalizedText.FromResource("Sink.AlreadyGone", target.Path));
        }
    }

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);

        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static RegistryKey? OpenHive(RegistryTarget target, bool writable)
    {
        var view = target.View switch
        {
            RegistryViewKind.Registry32 => RegistryView.Registry32,
            RegistryViewKind.Registry64 => RegistryView.Registry64,
            _ => RegistryView.Default,
        };

        var hive = target.Hive switch
        {
            RegistryHiveKind.LocalMachine => RegistryHive.LocalMachine,
            RegistryHiveKind.Users => RegistryHive.Users,
            _ => RegistryHive.CurrentUser,
        };

        return RegistryKey.OpenBaseKey(hive, view);
    }

    private static object Decode(RegistryValueData value) => value.Kind switch
    {
        ModelValueKind.Binary => Convert.FromBase64String(value.Data),
        ModelValueKind.DWord => BitConverter.ToInt32(Convert.FromBase64String(value.Data)),
        ModelValueKind.QWord => BitConverter.ToInt64(Convert.FromBase64String(value.Data)),
        ModelValueKind.MultiString => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value.Data)).Split('\0'),
        _ => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value.Data)),
    };

    private static Win32ValueKind Translate(ModelValueKind kind) => kind switch
    {
        ModelValueKind.ExpandString => Win32ValueKind.ExpandString,
        ModelValueKind.Binary => Win32ValueKind.Binary,
        ModelValueKind.DWord => Win32ValueKind.DWord,
        ModelValueKind.QWord => Win32ValueKind.QWord,
        ModelValueKind.MultiString => Win32ValueKind.MultiString,
        _ => Win32ValueKind.String,
    };
}
