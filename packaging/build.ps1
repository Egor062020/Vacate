# Сборка поставки Vacate: переносимый архив и установщик.
#
# Запуск из корня репозитория:
#   pwsh packaging\build.ps1
#
# Требуется .NET 10 SDK. Для установщика — Inno Setup 6.

[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [switch]$SkipInstaller,

    # Автономная сборка включает в себя среду выполнения: работает на машине,
    # где .NET не установлен. Это и есть вариант для раздачи людям — требовать
    # от них сначала поставить среду выполнения значит потерять большинство.
    # Цена: около 150 МБ вместо одного.
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "publish"
$appOut = Join-Path $publish "app"
$dist = Join-Path $root "dist"

$dotnet = "dotnet"
if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") {
    $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe"
}

Write-Host "Сборка поставки Vacate $Version" -ForegroundColor Cyan

# Публикация очищает выходной каталог, поэтому проекты собираются в отдельные
# папки и объединяются вручную. Иначе второй проект стирает файлы первого —
# на этом и попались при первой сборке.
foreach ($path in @($publish, $dist)) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

New-Item -ItemType Directory -Force -Path $appOut | Out-Null

# ПОРЯДОК ВАЖЕН: графическое приложение публикуется ПОСЛЕДНИМ.
#
# Консольный проект не использует графическую подсистему, но в его публикацию всё равно
# попадают файлы с теми же именами, что у настоящих библиотек графики, — пустые заглушки
# для совместимости. При копировании поверх они затирают настоящие библиотеки, и
# приложение падает при запуске с сообщением «не удаётся найти WindowsBase».
#
# Найдено установкой собранной поставки и попыткой её запустить: настоящая библиотека
# весит два мегабайта, заглушка — шестнадцать килобайт.
$projects = @(
    @{ Name = "Vacate.Cli"; Path = "src\Vacate.Cli\Vacate.Cli.csproj" },
    @{ Name = "Vacate.App"; Path = "src\Vacate.App\Vacate.App.csproj" }
)

foreach ($project in $projects) {
    Write-Host "  публикую $($project.Name)"
    $stage = Join-Path $publish $project.Name

    $selfContainedFlag = if ($SelfContained) { "true" } else { "false" }

    & $dotnet publish (Join-Path $root $project.Path) `
        -c Release -r win-x64 --self-contained $selfContainedFlag `
        -p:Version=$Version `
        -o $stage --nologo | Out-Null

    if ($LASTEXITCODE -ne 0) { throw "Публикация $($project.Name) не удалась" }

    # Отладочные символы в поставку не идут.
    Get-ChildItem $stage -Filter "*.pdb" | Remove-Item -Force
    Copy-Item "$stage\*" $appOut -Recurse -Force
    Remove-Item $stage -Recurse -Force
}

$executables = Get-ChildItem $appOut -Filter "*.exe" | Select-Object -ExpandProperty Name
Write-Host "  собрано: $($executables -join ', ')"

if ($executables -notcontains "Vacate.exe" -or $executables -notcontains "vacate-cli.exe") {
    throw "В поставке не хватает исполняемых файлов"
}

# Проверка от повторения найденной ошибки: если библиотека графики окажется
# заглушкой, приложение соберётся и установится, но упадёт при первом запуске
# у пользователя. Такое нельзя выпускать.
if ($SelfContained) {
    $wpfCore = Join-Path $appOut "WindowsBase.dll"

    if (-not (Test-Path $wpfCore)) {
        throw "В поставке нет WindowsBase.dll — графическое приложение не запустится"
    }

    $size = (Get-Item $wpfCore).Length

    if ($size -lt 1MB) {
        throw "WindowsBase.dll размером $size байт — это заглушка, а не настоящая библиотека. " +
              "Проверьте порядок публикации: графическое приложение должно идти последним"
    }

    Write-Host "  проверка библиотек графики пройдена"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Переносимая версия: папка в архиве. Единый файл невозможен — у графической
# подсистемы есть собственные библиотеки, которые всё равно распаковываются
# во временный каталог, тот самый, который программа чистит.
$suffix = if ($SelfContained) { "portable-standalone" } else { "portable" }
$archive = Join-Path $dist "Vacate-$Version-$suffix.zip"
Compress-Archive -Path "$appOut\*" -DestinationPath $archive -Force
Write-Host "  переносимая версия: $archive" -ForegroundColor Green

if ($SkipInstaller) { return }

# Inno Setup ставится в разные места в зависимости от того, как его установили:
# в общий каталог программ при обычной установке и в профиль пользователя
# при установке через диспетчер пакетов.
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host "  Inno Setup не найден, установщик пропущен" -ForegroundColor Yellow
    return
}

& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot "Vacate.iss") | Out-Null

if ($LASTEXITCODE -ne 0) { throw "Сборка установщика не удалась" }

# Ищем установщик именно этой версии, а не первый попавшийся: раньше сюда попадал
# файл прошлой сборки, и вывод рапортовал об успехе, показывая чужой номер версии.
$setupPath = Join-Path $dist "Vacate-$Version-setup.exe"

if (-not (Test-Path $setupPath)) {
    throw "Установщик Vacate-$Version-setup.exe не создан. Проверьте, что версия дошла до Vacate.iss."
}

Write-Host "  установщик: $setupPath" -ForegroundColor Green

Write-Host ""
Write-Host "Готово. Файлы поставки в $dist" -ForegroundColor Cyan
Write-Host "Напоминание: без подписи кода система защиты будет предупреждать" -ForegroundColor Yellow
Write-Host "о программе каждого нового пользователя." -ForegroundColor Yellow
