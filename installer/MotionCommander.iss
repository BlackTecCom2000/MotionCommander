#define MyAppName "Motion Commander"
#ifndef MyAppVersion
#define MyAppVersion "3.8.23"
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
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
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
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

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
  ModeRadio2: TRadioButton;

(* РљР°С‚Р°Р»РѕРі, СЃРѕРѕС‚РІРµС‚СЃС‚РІСѓСЋС‰РёР№ РІС‹Р±СЂР°РЅРЅРѕРјСѓ СЂРµР¶РёРјСѓ.

   РџСѓСЃС‚Р°СЏ СЃС‚СЂРѕРєР° РѕР·РЅР°С‡Р°РµС‚ В«РѕСЃС‚Р°РІРёС‚СЊ РєР°С‚Р°Р»РѕРі, РІС‹Р±СЂР°РЅРЅС‹Р№ InnoВ»: РІ РѕР±С‹С‡РЅРѕРј
   СЂРµР¶РёРјРµ СЌС‚Рѕ РєР°С‚Р°Р»РѕРі Program Files РїСЂРё РЅР°Р»РёС‡РёРё РїСЂР°РІ Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР° Рё
   РєР°С‚Р°Р»РѕРі РІ РїСЂРѕС„РёР»Рµ РїРѕР»СЊР·РѕРІР°С‚РµР»СЏ Р±РµР· РЅРёС…. РџРѕРґРјРµРЅСЏС‚СЊ РµРіРѕ СЃРІРѕРёРј Р·РЅР°С‡РµРЅРёРµРј
   Р±С‹Р»Рѕ Р±С‹ РїСЂСЏРјРѕР№ РїРѕРґРјРµРЅРѕР№ СЂРµС€РµРЅРёСЏ РїРѕР»СЊР·РѕРІР°С‚РµР»СЏ. *)
function DirectoryForMode(): string;
begin
  if ModeRadio1.Checked then
    Result := ExpandConstant('{localappdata}\Programs\{#MyAppName}')
  else if ModeRadio2.Checked then
    Result := ExpandConstant('{localappdata}\{#MyAppName}')
  else
    Result := '';
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
  Caps: array[0..2] of string;
  Tops: array[0..2] of Integer;
begin
  ModePage := CreateCustomPage(wpSelectDir, 'Р РµР¶РёРј СѓСЃС‚Р°РЅРѕРІРєРё',
    'Р’С‹Р±РµСЂРёС‚Рµ, РєР°Рє СѓСЃС‚Р°РЅРѕРІРёС‚СЊ Motion Commander. Р РµР¶РёРј РѕРїСЂРµРґРµР»СЏРµС‚, РіРґРµ ' +
    'С…СЂР°РЅСЏС‚СЃСЏ РЅР°СЃС‚СЂРѕР№РєРё Рё РёСЃС‚РѕСЂРёСЏ, Рё РЅСѓР¶РµРЅ Р»Рё РїРµСЂРµР·Р°РїСѓСЃРє СЃ РїСЂР°РІР°РјРё ' +
    'Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР° РїСЂРё РѕР±РЅРѕРІР»РµРЅРёРё РїСЂРѕРіСЂР°РјРјС‹.');

  Caps[0] := 'РћР±С‹С‡РЅР°СЏ СѓСЃС‚Р°РЅРѕРІРєР°' + #13#10 +
             '    Program Files, РґРѕСЃС‚СѓРїРЅРѕ РІСЃРµРј РїРѕР»СЊР·РѕРІР°С‚РµР»СЏРј РєРѕРјРїСЊСЋС‚РµСЂР°.' + #13#10 +
             '    РќР°СЃС‚СЂРѕР№РєРё С…СЂР°РЅСЏС‚СЃСЏ РІ РїСЂРѕС„РёР»Рµ РїРѕР»СЊР·РѕРІР°С‚РµР»СЏ.' + #13#10 +
             '    РћР±РЅРѕРІР»РµРЅРёРµ РїСЂРѕРіСЂР°РјРјС‹ Р·Р°РїСЂРѕСЃРёС‚ РїСЂР°РІР° Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР°.';
  Caps[1] := 'РЈСЃС‚Р°РЅРѕРІРєР° С‚РѕР»СЊРєРѕ РґР»СЏ РјРµРЅСЏ' + #13#10 +
             '    РџР°РїРєР° РІ РїСЂРѕС„РёР»Рµ РїРѕР»СЊР·РѕРІР°С‚РµР»СЏ, РїСЂР°РІР° Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР° РЅРµ РЅСѓР¶РЅС‹.' + #13#10 +
             '    РћР±РЅРѕРІР»РµРЅРёРµ С‚РѕР¶Рµ РІС‹РїРѕР»РЅСЏРµС‚СЃСЏ Р±РµР· РїРѕРІС‹С€РµРЅРёСЏ РїСЂРёРІРёР»РµРіРёР№.';
  Caps[2] := 'РџРѕСЂС‚Р°С‚РёРІРЅР°СЏ СѓСЃС‚Р°РЅРѕРІРєР°' + #13#10 +
             '    РќР°СЃС‚СЂРѕР№РєРё, РёСЃС‚РѕСЂРёСЏ Рё Р±Р°Р·Р° Р·Р°РіСЂСѓР·РѕРє Р»РµР¶Р°С‚ Р РЇР”Рћ РЎ РџР РћР“Р РђРњРњРћР™.' + #13#10 +
             '    РџР°РїРєСѓ РјРѕР¶РЅРѕ РїРµСЂРµРЅРµСЃС‚Рё РЅР° С„Р»РµС€РєСѓ, РЅР°СЃС‚СЂРѕР№РєРё РїРѕРµРґСѓС‚ РІРјРµСЃС‚Рµ СЃ РЅРµР№.';

  Tops[0] := 40;
  Tops[1] := 102;
  Tops[2] := 158;

  (* Родитель элементов — ИМЕННО ModePage.Surface, а не сама страница.
     Раньше стоял вызов CreatePanel(ModePage), которого в ISPP нет:
     компилятор отвергал его как неизвестную функцию. *)
  Root := TPanel.Create(ModePage.Surface);
  Root.Width := 460;
  Root.Height := 220;

  Title := TLabel.Create(Root);
  Title.Caption := 'РљСѓРґР° СѓСЃС‚Р°РЅРѕРІРёС‚СЊ РїСЂРѕРіСЂР°РјРјСѓ:';
  Title.Left := 12;
  Title.Top := 12;

  ModeRadio0 := TRadioButton.Create(Root);
  ModeRadio0.Caption := Caps[0];
  ModeRadio0.Left := 12;
  ModeRadio0.Top := Tops[0];
  ModeRadio0.Width := 430;
  ModeRadio0.Height := 56;
  ModeRadio0.Checked := True;

  ModeRadio1 := TRadioButton.Create(Root);
  ModeRadio1.Caption := Caps[1];
  ModeRadio1.Left := 12;
  ModeRadio1.Top := Tops[1];
  ModeRadio1.Width := 430;
  ModeRadio1.Height := 52;

  ModeRadio2 := TRadioButton.Create(Root);
  ModeRadio2.Caption := Caps[2];
  ModeRadio2.Left := 12;
  ModeRadio2.Top := Tops[2];
  ModeRadio2.Width := 430;
  ModeRadio2.Height := 56;
end;

procedure InitializeWizard;
begin
  CreateModePage;
end;

(* РњР°СЂРєРµСЂ install.json С‡РёС‚Р°РµС‚ Helpers.AppPaths РїСЂРё СЃС‚Р°СЂС‚Рµ РїСЂРѕРіСЂР°РјРјС‹. Р‘РµР·
   РЅРµРіРѕ РїРѕСЂС‚Р°С‚РёРІРЅР°СЏ СѓСЃС‚Р°РЅРѕРІРєР° РЅРµРѕС‚Р»РёС‡РёРјР° РѕС‚ РѕР±С‹С‡РЅРѕР№, Р° РїРѕСЃР»Рµ РїРµСЂРµРЅРѕСЃР°
   РїР°РїРєРё РѕР±С‹С‡РЅР°СЏ СѓСЃС‚Р°РЅРѕРІРєР° РїСЂРѕРґРѕР»Р¶Р°Р»Р° СЃС‡РёС‚Р°С‚СЊСЃСЏ РїРµСЂРµРЅРѕСЃРЅРѕР№. *)
procedure CurStepChanged(CurStep: TSetupStep);
var
  MarkerPath: string;
  Content: string;
begin
  if CurStep <> ssPostInstall then exit;

  MarkerPath := ExpandConstant('{app}\install.json');

  (* РЎС‚Р°СЂС‹Р№ РјР°СЂРєРµСЂ РїРµСЂРµР¶РёР» Р±С‹ РїРµСЂРµСѓСЃС‚Р°РЅРѕРІРєСѓ: portable=true РѕС‚ РїСЂРѕС€Р»РѕР№
     СѓСЃС‚Р°РЅРѕРІРєРё СЃРґРµР»Р°Р» Р±С‹ РЅРѕРІСѓСЋ РїРµСЂРµРЅРѕСЃРЅРѕР№. РЈРґР°Р»СЏРµРј РґРѕ Р·Р°РїРёСЃРё. *)
  if FileExists(MarkerPath) then
    DeleteFile(MarkerPath);

  if ModeRadio2.Checked then
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
