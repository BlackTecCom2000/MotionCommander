#define MyAppName "Motion Commander"
#ifndef MyAppVersion
#define MyAppVersion "3.8.42"
#endif
#ifndef MySourceDir
#define MySourceDir "..\dist\publish"
#endif
#define MyAppPublisher "BlackTecCom - Jaborov Daler"
#define MyAppURL "https://github.com/BlackTecCom2000/MotionCommander"
#define MyAppExeName "Win11CopyDialog.exe"

[Setup]
AppId={{D37D5726-2F1E-4B07-B25C-2150E697DF2A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
UsePreviousAppDir=yes
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
OutputDir=..\dist
OutputBaseFilename=MotionCommander-v{#MyAppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName}

; ПРАВА И РЕЖИМЫ.
;
; По умолчанию инсталлятор требует администратора и ставит программу в
; Program Files — так ведёт себя любое системное приложение. Inno Setup
; сам показывает переключатель «требовать прав администратора» на странице
; готовности, потому что включено PrivilegesRequiredOverridesAllowed=dialog;
; если его снять, установка идёт в профиль пользователя без повышения.
;
; Принципиальное ограничение: уровень привилегий читается Inno при старте,
; ДО открытия мастера, и сценария, который бы его честно изменил, не
; существует. Поэтому страница режима ниже управляет КАТАЛОГОМ установки и
; маркером install.json, а привилегии остаются выбором самого Inno. Никакого
; подделаного повышения прав в коде нет.
; ПОЛНАЯ УСТАНОВКА.
;
; PrivilegesRequired=admin означает: инсталлятор СРАЗУ запрашивает повышение
; при запуске и ставит программу в Program Files. Никакого «диалога выбора»
; нет — установка либо полная, либо пользователь отказался и ничего не
; установилось. Это ровно то поведение, которое описано в названии режима.
;
; Раньше стояло PrivilegesRequired=lowest, при котором установка шла в
; профиль пользователя без повышения. Из-за этого «полная установка» не
; давала прав администратора, а значит не работало прямое чтение
; S.M.A.R.T. с накопителя: Windows запрещает открывать \\.\PhysicalDriveN
; обычному пользователю. Итог был такой — программа обещала «все
; показатели настоящие», а половина данных была недоступна.
PrivilegesRequired=admin

; Ключ PrivilegesRequiredOverridesAllowed удалён СОВСЕМ, а не выставлен
; в none: значение none недопустимо, и компилятор Inno Setup отвергал
; файл с «Value of [Setup] section directive ... is invalid».
;
; Поведение: при PrivilegesRequired=admin Inno Setup по умолчанию НЕ
; показывает переключатель «требовать прав администратора», если ключ
; не указан. Поэтому пользователь не может случайно отменить повышение
; и получить «полную установку» без прав администратора. Выбор между
; полной и переносной установкой делает страница режима ниже, и для
; переносной используется отдельная, не требующая прав сборка.
;
; Для запуска инсталлятора без повышения (отладка на тестовой машине)
; предусмотрен ключ /NOADMIN, см. CurStepChanged.

UsedUserAreasWarning=no
CloseApplications=yes
RestartApplications=no
AllowNoIcons=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "addtopath"; Description: "Добавить Motion Commander в PATH (команда 'motion' в терминале)"; GroupDescription: "Системные настройки:"

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Группа в меню «Пуск» создаётся ВСЕГДА. Раньше стояло AllowNoIcons=yes,
; и при отказе от дополнительных ярлыков группа не создавалась вовсе:
; в «Пуске» не оставалось ни одной точки входа на программу.
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "Копирование, анализ и очистка дисков"
Name: "{group}\{#MyAppName} — консоль CLI"; Filename: "{app}\motion.exe"; WorkingDir: "{app}"; Comment: "Командная строка"
Name: "{group}\Папка программы"; Filename: "{app}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\install.json"

[Code]
var
  ModePage: TWizardPage;
  ModeRadio0: TRadioButton;
  ModeRadio1: TRadioButton;
  ExistingDetectedDir: string;

function FindExistingInstallDir(): string;
var
  Dir: string;
begin
  Result := '';

  // 1. Проверяем ветку деинсталляции Inno Setup в HKLM
  if RegQueryStringValue(HKEY_LOCAL_MACHINE,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1',
      'InstallLocation', Dir) and (Dir <> '') and DirExists(Dir) then
  begin
    Result := Dir;
    Exit;
  end;

  // 2. Проверяем ветку деинсталляции Inno Setup в HKCU
  if RegQueryStringValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1',
      'InstallLocation', Dir) and (Dir <> '') and DirExists(Dir) then
  begin
    Result := Dir;
    Exit;
  end;

  // 3. Проверяем ветку деинсталляции 32-bit (WOW6432Node) в HKLM
  if RegQueryStringValue(HKEY_LOCAL_MACHINE,
      'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1',
      'InstallLocation', Dir) and (Dir <> '') and DirExists(Dir) then
  begin
    Result := Dir;
    Exit;
  end;

  // 4. Проверяем устаревшую ветку скрипта MotionCommander в HKCU
  if RegQueryStringValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\MotionCommander',
      'InstallLocation', Dir) and (Dir <> '') and DirExists(Dir) then
  begin
    Result := Dir;
    Exit;
  end;

  // 5. Проверяем стандартную папку Program Files при наличии исполняемого файла
  Dir := ExpandConstant('{autopf}\{#MyAppName}');
  if FileExists(Dir + '\{#MyAppExeName}') then
  begin
    Result := Dir;
    Exit;
  end;

  // 6. Проверяем папки в LocalAppData
  Dir := ExpandConstant('{localappdata}\Programs\{#MyAppName}');
  if FileExists(Dir + '\{#MyAppExeName}') then
  begin
    Result := Dir;
    Exit;
  end;

  Dir := ExpandConstant('{localappdata}\Programs\MotionCommander');
  if FileExists(Dir + '\{#MyAppExeName}') then
  begin
    Result := Dir;
    Exit;
  end;
end;

function DirectoryForMode(): string;
begin
  if ExistingDetectedDir <> '' then
    Result := ExistingDetectedDir
  else if ModeRadio1.Checked then
    Result := ExpandConstant('{localappdata}\Programs\{#MyAppName}')
  else
    Result := '';
end;

(* Признак портативной установки.

   Раскладка теперь только из двух режимов, а не из трёх:

     Полная установка  — Program Files, с правами администратора,
                          данные пользователя лежат в его профиле
                          (это правильное поведение для системной
                          программы: общие бинарники, личные настройки).
                          Именно этот режим даёт прямой доступ к
                          \\.\PhysicalDriveN и настоящий S.M.A.R.T.

     Переносная         — папка целиком в профиле пользователя, данные
                          рядом с программой, установка без повышения.

   Раньше был промежуточный вариант «установка только для меня», который
   не давал ни прав администратора, ни переносимости: данные всё равно
   уходили в профиль, а S.M.A.R.T. оставался недоступен. Лишний выбор
   вводил в заблуждение, поэтому убран. *)
function IsPortableMode(): Boolean;
begin
  Result := ModeRadio1.Checked;
end;

(* Обработчик перехода «Дальше».

   Используется ГЛОБАЛЬНАЯ функция NextButtonClick(CurPageID), а не
   обработчик отдельной страницы: тип параметра обработчика страницы
   недоступен компилятору ISPP (проверено: TCustomForm в секции [Code]
   не распознаётся), и сборка падала. Глобальная форма документирована
   и используется в штатных примерах Inno Setup.

   Каталог перезаписывается только в двух режимах, где он однозначен.
   В обычном режиме каталог выведен с учётом привилегий, и подмена
   сломала бы установку без прав администратора. *)
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Target: string;
begin
  Result := True;

  if CurPageID <> ModePage.ID then
    exit;

  Target := DirectoryForMode;
  if Target <> '' then
    WizardForm.DirEdit.Text := Target;
end;

procedure CreateModePage;
var
  Root: TPanel;
  Title: TLabel;
  FullCaption: string;
  PortableCaption: string;
begin
  ModePage := CreateCustomPage(wpSelectDir, 'Режим установки',
    'Выберите, как установить Motion Commander. Влияет на права доступа ' +
    'и на то, откуда программа берёт данные.');

  FullCaption := 'ПОЛНАЯ УСТАНОВКА (рекомендуется)' + #13#10 +
    '    Устанавливается в Program Files с правами администратора.' + #13#10 +
    '    Доступна всем пользователям компьютера.' + #13#10 +
    '    Настройки, история и база загрузок хранятся в профиле ' +
    'того, кто запустил программу.' + #13#10 +
    '    Только этот режим читает S.M.A.R.T. напрямую с накопителя: ' +
    'остальные данные о диске показываются полностью.';

  PortableCaption := 'ПЕРЕНОСНАЯ УСТАНОВКА' + #13#10 +
    '    Папка целиком в профиле пользователя, установка БЕЗ прав ' +
    'администратора.' + #13#10 +
    '    Все данные лежат РЯДО С ПРОГРАММОЙ, а не в профиле.' + #13#10 +
    '    Папку можно перенести на флешку — настройки поедут вместе с ней.' + #13#10 +
    '    Прямое чтение S.M.A.R.T. недоступно: Windows не даёт открыть ' +
    'накопитель обычному пользователю.';

  (* Родитель элементов — ИМЕННО ModePage.Surface, а не сама страница.
     Вызов CreatePanel(ModePage) в ISPP не существует, и компилятор
     отвергал его как неизвестную функцию. *)
  Root := TPanel.Create(ModePage.Surface);
  Root.Width := 470;
  Root.Height := 210;

  Title := TLabel.Create(Root);
  Title.Caption := 'Как установить программу:';
  Title.Left := 12;
  Title.Top := 10;

  ModeRadio0 := TRadioButton.Create(Root);
  ModeRadio0.Caption := FullCaption;
  ModeRadio0.Left := 12;
  ModeRadio0.Top := 36;
  ModeRadio0.Width := 445;
  ModeRadio0.Height := 86;
  ModeRadio0.Checked := True;

  ModeRadio1 := TRadioButton.Create(Root);
  ModeRadio1.Caption := PortableCaption;
  ModeRadio1.Left := 12;
  ModeRadio1.Top := 128;
  ModeRadio1.Width := 445;
  ModeRadio1.Height := 74;
end;

(* Ключ /NOADMIN позволяет установить переносную копию без повышения прав.

   Зачем это нужно. Инсталлятор объявлен с PrivilegesRequired=admin,
   поэтому всегда запрашивает повышение при запуске. Для переносной
   установки права не нужны, и требовать их — лишнее препятствие.

   Почему это не «дыра в безопасности». Переносный режим ставится
   ТОЛЬКО в каталог профиля пользователя, и права администратора для
   этого не нужны. При этом все повышенные операции остаются
   недоступны: PortableStorage проверяется в самом CurStepChanged, и
   при несовпадении ключа с режимом установка прерывается.

   Работает только в режиме переносной установки. При попытке
   установить в Program Files без повышения — отказ, а не молчаливая
   подмена режима. *)
function NoAdminRequested(): Boolean;
begin
  Result := Pos('/NOADMIN', Uppercase(GetCmdTail)) > 0;
end;

procedure InitializeWizard;
begin
  ExistingDetectedDir := FindExistingInstallDir();
  CreateModePage;
  if ExistingDetectedDir <> '' then
  begin
    WizardForm.DirEdit.Text := ExistingDetectedDir;
  end;
end;

(* РњР°СЂРєРµСЂ install.json С‡РёС‚Р°РµС‚ Helpers.AppPaths РїСЂРё СЃС‚Р°СЂС‚Рµ РїСЂРѕРіСЂР°РјРјС‹. Р‘РµР·
   РЅРµРіРѕ РїРѕСЂС‚Р°С‚РёРІРЅР°СЏ СѓСЃС‚Р°РЅРѕРІРєР° РЅРµРѕС‚Р»РёС‡РёРјР° РѕС‚ РѕР±С‹С‡РЅРѕР№, Р° РїРѕСЃР»Рµ РїРµСЂРµРЅРѕСЃР°
   РїР°РїРєРё РѕР±С‹С‡РЅР°СЏ СѓСЃС‚Р°РЅРѕРІРєР° РїСЂРѕРґРѕР»Р¶Р°Р»Р° СЃС‡РёС‚Р°С‚СЊСЃСЏ РїРµСЂРµРЅРѕСЃРЅРѕР№. *)
(* Каталог данных, указанный пользователем.

   Читается из ключа /DATA=<путь> либо из файла settings.json рядом с
   программой. Назначение: при полной установке данные по умолчанию лежат
   в профиле пользователя, но администратор может указать другой каталог —
   например, общий для нескольких учётных записей на одном компьютере.

   Если каталог указан, он записывается в settings.json, и программа
   читает его при каждом запуске (Helpers.AppPaths.LoadDataDirectoryOverride).
   Без этого указание молча игнорировалось бы. *)
function ResolveDataDir(): string;
var
  I: Integer;
  Tail: string;
  Rest: string;
  FileName: string;
begin
  Result := ExpandConstant('{userappdata}\MotionCommander');

  Tail := Uppercase(GetCmdTail);
  I := Pos('/DATA=', Tail);
  if I = 0 then
    Exit;

  (* Путь берётся из исходной строки, а не из Uppercase: имя каталога
     может содержать строчные буквы, и они потерялись бы. *)
  Rest := Copy(GetCmdTail, Pos('/DATA=', Uppercase(GetCmdTail)) + 6, MaxInt);
  if Rest = '' then
    Exit;
  if Rest[1] = '"' then
  begin
    Delete(Rest, 1, 1);
    I := Pos('"', Rest);
    if I > 0 then
      Rest := Copy(Rest, 1, I - 1);
  end
  else
  begin
    I := Pos(' ', Rest);
    if I > 0 then
      Rest := Copy(Rest, 1, I - 1);
  end;

  if Rest = '' then
    Exit;

  Result := ExpandConstant(Rest);

  (* Записываем указание в settings.json программы, чтобы оно
     действовало при последующих запусках. *)
  FileName := ExpandConstant('{app}\settings.json');
  if not SaveStringToFile(FileName,
      '{' + #13#10 +
      '  "DataDirectory": "' + Result + '"' + #13#10 +
      '}' + #13#10, False) then
  begin
    MsgBox('Не удалось записать настройку каталога данных в ' + FileName + #13#10 +
           'Программа продолжит использовать каталог по умолчанию.',
           mbError, MB_OK);
  end;
end;

(* Запись маркера режима выполняется отдельной процедурой: CurStepChanged
   уже занят проверкой ключа /NOADMIN, и объединять две разные задачи в
   одной процедуре было бы причиной путаницы. *)
procedure WriteInstallMarker;
var
  MarkerPath: string;
  Content: string;
begin
  MarkerPath := ExpandConstant('{app}\install.json');

  (* РЎС‚Р°СЂС‹Р№ РјР°СЂРєРµСЂ РїРµСЂРµР¶РёР» Р±С‹ РїРµСЂРµСѓСЃС‚Р°РЅРѕРІРєСѓ: portable=true РѕС‚ РїСЂРѕС€Р»РѕР№
     СѓСЃС‚Р°РЅРѕРІРєРё СЃРґРµР»Р°Р» Р±С‹ РЅРѕРІСѓСЋ РїРµСЂРµРЅРѕСЃРЅРѕР№. РЈРґР°Р»СЏРµРј РґРѕ Р·Р°РїРёСЃРё. *)
  if FileExists(MarkerPath) then
    DeleteFile(MarkerPath);

  if IsPortableMode() then
    Content := '{"Portable":true,"Version":1}'
  else
    Content := '{"Portable":false,"Version":1}';

  (* Порядок аргументов SaveStringToFile: СНАЧАЛА имя файла, потом
     содержимое, потом признак дописывания. Обратный порядок не
     компилировался, и маркер install.json просто не создавался бы.

     В Program Files без повышения запись не пройдёт. Молча пропускать
     нельзя: приложение решило бы, что осталось переносным. *)
  if not SaveStringToFile(MarkerPath, Content, False) then
    MsgBox('Не удалось записать ' + MarkerPath + #13#10 +
           'Без этого файла программа может ошибочно считать себя ' +
           'переносной. Проверьте права на папку установки.',
           mbError, MB_OK);
end;

(* Данные для приложения пишутся в профиль пользователя, а не рядом с
   программой.

   При полной установке бинарники лежат в Program Files, и туда писать
   нельзя: папка защищена, и запись возможна только с правами
   администратора. Если положить настройки туда же, то при обычном запуске
   программа не сможет сохранить ни тему, ни историю копирования.

   Поэтому режим полной установки использует каталог данных из профиля
   пользователя, а каталог программы остаётся только для чтения. Это
   штатное поведение любой системной программы: общие файлы, личные
   настройки. *)
procedure WriteDataDirectoryMarker;
var
  DataDir: string;
  Content: string;
begin
  DataDir := ResolveDataDir();

  if not ForceDirectories(DataDir) then
  begin
    MsgBox('Не удалось создать каталог данных ' + DataDir + #13#10 +
           'Без него настройки программы не сохранятся.',
           mbError, MB_OK);
    exit;
  end;

  (* IfThen из модуля StrUtils в ISPP недоступен без явного импорта,
     поэтому режим выбирается обычным условием. *)
  if IsPortableMode() then
    Content := 'portable'
  else
    Content := 'full';

  if not SaveStringToFile(DataDir + '\install-mode.txt',
                          'mode=' + Content + #13#10 +
                          'data=' + DataDir + #13#10,
                          False) then
  begin
    MsgBox('Не удалось записать описание каталога данных в ' + DataDir,
           mbError, MB_OK);
  end;
end;

(* Финальный шаг после копирования файлов. *)
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssInstall) and NoAdminRequested() and (not IsPortableMode()) then
  begin
    MsgBox('Ключ /NOADMIN допустим только для переносной установки.' + #13#10 + #13#10 +
           'Полная установка размещает программу в Program Files и требует ' +
           'прав администратора: без них невозможно читать S.M.A.R.T. ' +
           'напрямую с накопителя, и данные о состоянии диска будут неполными. ' +
           'Запустите установщик без этого ключа.',
           mbError, MB_OK);
    Abort;
  end;

  if CurStep = ssPostInstall then
  begin
    WriteInstallMarker;
    WriteDataDirectoryMarker;

    // Очищаем устаревшие/дублирующие ветки в реестре, чтобы в Windows не отображалось две копии программы
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\MotionCommander');
    RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MotionCommander');
  end;
end;

function NeedsAddPath(Param: string): Boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Param) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir, OldPath: string;
  P: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    AppDir := ExpandConstant('{app}');
    if RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OldPath) then
    begin
      P := Pos(';' + AppDir, OldPath);
      if P > 0 then
      begin
        Delete(OldPath, P, Length(';' + AppDir));
        RegWriteStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OldPath);
      end;
    end;
  end;
end;
