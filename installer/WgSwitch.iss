; Установщик WgSwitch (Inno Setup 6.6+: нужна тёмная тема и WizardBackColor).
; CI: iscc installer\WgSwitch.iss /DAppVersion=1.2.0 /DSourceExe=out\setup\WgSwitch.exe /Oout
; Внутри — сборка со встроенным .NET, так что runtime ставить не нужно. Её публикуем без сжатия:
; LZMA установщика жмёт её лучше (~45 МБ против ~59), и программа стартует без распаковки в память.
; WireGuard не перепаковываем: по галочке скачиваем официальный подписанный установщик с wireguard.com.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
; версия файла в Windows — только числа: 1.0.0-dev.57 → 1.0.0.57, 1.2.0 → 1.2.0.0
#ifndef FileVersion
  #define FileVersion "1.0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\out\setup\WgSwitch.exe"
#endif

[Setup]
AppId={{8F3B6C1E-5D2A-4C7B-9E21-6A4D3F0B7C55}
AppName=WgSwitch
AppVersion={#AppVersion}
AppVerName=WgSwitch {#AppVersion}
AppPublisher=LKXA
AppPublisherURL=https://lkxa.ru
AppSupportURL=https://github.com/lolkekxa/WgSwitch
AppUpdatesURL=https://github.com/lolkekxa/WgSwitch/releases
VersionInfoVersion={#FileVersion}
DefaultDirName={autopf}\WgSwitch
DefaultGroupName=WgSwitch
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=WgSwitch-Setup
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\WgSwitch.exe
UninstallDisplayName=WgSwitch
; страницы лицензии нет: MIT не требует согласия, а файл LICENSE всё равно кладётся рядом с программой
Compression=lzma2/ultra64
SolidCompression=yes
; тёмный, как сама программа: встроенная тёмная тема Inno Setup 6.6+, фон #0A0A0A, без разделительных линий
WizardStyle=modern dark hidebevels
WizardBackColor=#0A0A0A
WizardImageBackColor=#0A0A0A
; боковая картинка с логотипом и иконка-тоннель в шапке страниц (обычная и для экранов ×2)
WizardImageFile=wizard.bmp,wizard@2x.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small@2x.bmp
; запущенный WgSwitch держит этот mutex — установщик попросит закрыть программу
AppMutex=Local\WgSwitch.Instance
CloseApplications=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; Flags: unchecked
Name: "wireguard"; Description: "Установить WireGuard для Windows (официальный установщик с wireguard.com)"; Check: not WireGuardInstalled

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Assets\Fonts\OFL.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\WgSwitch"; Filename: "{app}\WgSwitch.exe"
Name: "{autodesktop}\WgSwitch"; Filename: "{app}\WgSwitch.exe"; Tasks: desktopicon

[Run]
; без skipifsilent: после тихого обновления из программы новая версия запускается сама
Filename: "{app}\WgSwitch.exe"; Description: "Запустить WgSwitch"; Flags: nowait postinstall

[UninstallRun]
; задача автозапуска из планировщика; конфиги в ProgramData и настройки в AppData не трогаем
Filename: "schtasks.exe"; Parameters: "/delete /tn ""WgSwitch"" /f"; Flags: runhidden; RunOnceId: "DeleteAutostartTask"

[Code]
function WireGuardInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\WireGuard\wireguard.exe'));
end;

procedure InstallWireGuard;
var
  Installer: string;
  ResultCode: Integer;
begin
  try
    DownloadTemporaryFile('https://download.wireguard.com/windows-client/wireguard-installer.exe', 'wireguard-installer.exe', '', nil);
    Installer := ExpandConstant('{tmp}\wireguard-installer.exe');
    // официальный загрузчик сам скачивает свежий MSI и проверяет его подпись
    if not Exec(Installer, '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      MsgBox('Не удалось запустить установщик WireGuard.' + #13#10 + 'Установите его вручную: https://www.wireguard.com/install/', mbError, MB_OK);
  except
    MsgBox('Не удалось скачать WireGuard: ' + GetExceptionMessage + #13#10 + 'Установите его вручную: https://www.wireguard.com/install/', mbError, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('wireguard') then
    InstallWireGuard;
end;
