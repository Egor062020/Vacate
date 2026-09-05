; Установщик Vacate
;
; Собирается командой:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" packaging\Vacate.iss
;
; Перед сборкой нужно выполнить публикацию:
;   dotnet publish src\Vacate.App\Vacate.App.csproj -c Release -r win-x64 --self-contained false -o publish\app
;   dotnet publish src\Vacate.Cli\Vacate.Cli.csproj -c Release -r win-x64 --self-contained false -o publish\app
;
; Поставка папкой, а не одним файлом: у графической подсистемы несколько собственных
; библиотек, которые при сборке в один файл всё равно распаковываются во временный
; каталог — тот самый, который программа чистит.

#define AppName "Vacate"

; Версия приходит из скрипта сборки ключом /DAppVersion. Безусловный #define
; молча перекрывал переданное значение: сборка объявляла версию 1.1.0, а установщик
; получался с номером 1.0.0 — и это выяснилось только по имени готового файла.
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

#define AppPublisher "Egor062020"
#define AppUrl "https://github.com/Egor062020/Vacate"
#define AppExeName "Vacate.exe"

[Setup]
AppId={{8E4C5F21-3A7D-4B9E-9C2F-1D6A8B3E5C74}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=Vacate-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Только 64-разрядные системы: из 32-разрядного процесса системные проверки
; уходят в каталог перенаправления и не работают.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Программа сама по себе прав администратора не требует: интерфейс работает
; без повышения. Установка в общий каталог — единственное, для чего они нужны.
PrivilegesRequired=admin

MinVersion=10.0.19041
LicenseFile=..\LICENSE
UninstallDisplayName={#AppName}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Удалить {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Запустить {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Задача автоматической очистки должна исчезнуть вместе с программой,
; иначе планировщик будет еженедельно запускать несуществующий файл.
Filename: "{app}\vacate-cli.exe"; Parameters: "schedule off"; Flags: runhidden; RunOnceId: "RemoveSchedule"

[Code]
// Карантин содержит файлы пользователя, которые он ещё может вернуть.
// Молча стереть их при удалении программы недопустимо, поэтому спрашиваем.
function InitializeUninstall(): Boolean;
var
  QuarantineFound: Boolean;
  Drives: TArrayOfString;
  I: Integer;
  Path: String;
begin
  Result := True;
  QuarantineFound := False;

  Drives := ['C:\', 'D:\', 'E:\', 'F:\'];

  for I := 0 to GetArrayLength(Drives) - 1 do
  begin
    Path := Drives[I] + '$Vacate.Quarantine';
    if DirExists(Path) then
      QuarantineFound := True;
  end;

  if QuarantineFound then
  begin
    if MsgBox('В карантине остались файлы, которые ещё можно вернуть.' + #13#10#13#10 +
              'Они будут удалены безвозвратно вместе с программой.' + #13#10#13#10 +
              'Продолжить удаление?',
              mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;
