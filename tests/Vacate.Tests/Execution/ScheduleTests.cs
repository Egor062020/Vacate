using Vacate.Platform.Windows.Registry;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>Проверки автоматической очистки по расписанию.</summary>
/// <remarks>
/// Тесты намеренно не создают настоящую задачу в планировщике: она пережила бы прогон
/// и осталась на машине. Проверяется то, что можно проверить без побочных эффектов,
/// а работа с настоящим планировщиком подтверждена запуском на живой системе.
/// </remarks>
public sealed class ScheduleManagerTests
{
    [Fact]
    public void Состояние_читается_без_ошибок_даже_когда_задачи_нет()
    {
        var state = new ScheduleManager().GetState();

        // Отсутствие задачи — обычное состояние, а не сбой.
        Assert.NotNull(state);
    }

    [Fact]
    public void Несуществующий_файл_программы_не_попадает_в_расписание()
    {
        // Задача, ссылающаяся в пустоту, молча падала бы каждую неделю,
        // и пользователь узнал бы об этом только по отсутствию результата.
        var result = new ScheduleManager().Enable(
            @"C:\нет-такого-файла-вообще.exe",
            ScheduleFrequency.Weekly,
            atLogon: false);

        Assert.False(result.Success);
        Assert.Contains("не найден", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Отключение_несуществующего_расписания_не_считается_ошибкой()
    {
        // Пользователь мог удалить задачу вручную. Сообщать об ошибке в ответ
        // на просьбу «выключи» означало бы пугать без причины.
        var result = new ScheduleManager().Disable();

        Assert.True(result.Success);
    }

    [Fact]
    public void Пустой_путь_отклоняется_а_не_роняет_программу()
    {
        var manager = new ScheduleManager();

        Assert.Throws<ArgumentException>(() => manager.Enable(string.Empty, ScheduleFrequency.Weekly, false));
    }
}
