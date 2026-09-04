using Vacate.Platform.Windows.Files;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>
/// Проверки механизма обновлений.
/// </summary>
/// <remarks>
/// Обновление запускает чужой код на компьютере пользователя, поэтому проверки здесь
/// важнее, чем где-либо ещё в продукте.
/// </remarks>
public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("V2.5.1", "2.5.1")]
    [InlineData(" v3.0.0 ", "3.0.0")]
    public void Номер_версии_разбирается_из_метки_выпуска(string tag, string expected)
    {
        Assert.True(UpdateChecker.TryParseVersion(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("не-версия")]
    [InlineData("")]
    [InlineData("latest")]
    public void Неразбираемая_метка_отклоняется(string tag)
    {
        Assert.False(UpdateChecker.TryParseVersion(tag, out _));
    }

    [Fact]
    public void Неподписанный_файл_не_считается_доверенным()
    {
        // Самая важная проверка модуля. Скачанный файл без подписи — это код
        // неизвестного происхождения, и запускать его с правами администратора нельзя.
        var temp = Path.Combine(Path.GetTempPath(), $"vacate-test-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(temp, [0x4D, 0x5A, 0x90, 0x00]);

        try
        {
            var verdict = UpdateChecker.VerifySignature(temp);

            Assert.False(verdict.Trusted);
            Assert.NotEmpty(verdict.Detail);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Отсутствующий_файл_не_считается_доверенным()
    {
        var verdict = UpdateChecker.VerifySignature(@"C:\нет-такого-файла.exe");

        Assert.False(verdict.Trusted);
    }

    [Fact]
    public void Подпись_чужого_издателя_не_принимается()
    {
        // Действительная подпись сама по себе ничего не доказывает: подписать
        // программу может кто угодно. Проверяется именно ЧЬЯ она.
        var systemFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "notepad.exe");

        if (!File.Exists(systemFile))
        {
            return;
        }

        var verdict = UpdateChecker.VerifySignature(systemFile);

        // Файл подписан Microsoft — подпись действительна, но издатель не наш.
        Assert.False(verdict.Trusted);
    }

    [Fact]
    public void Интервал_проверки_не_чаще_раза_в_сутки()
    {
        // Более частые обращения в сеть ничего не дают и раздражают пользователей,
        // которые не хотят, чтобы программа вообще куда-то ходила.
        Assert.True(UpdateChecker.MinimumCheckInterval >= TimeSpan.FromHours(24));
    }
}
