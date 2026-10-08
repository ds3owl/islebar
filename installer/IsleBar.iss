; IsleBar installer (Inno Setup 6). Build with scripts\build-installer.ps1, which publishes the app and CLI first.
; Per-user install — no admin rights: the app lives in %LOCALAPPDATA%\IsleBar\app, the CLI that hooks call in ...\IsleBar\bin.
; Settings, logs and state in %LOCALAPPDATA%\IsleBar are kept on uninstall (reinstalling picks them up again).

#ifndef AppVersion
  #define AppVersion "0.1.2"
#endif
#ifndef PublishDir
  #define PublishDir "..\build\app"
#endif
#ifndef CliDir
  #define CliDir "..\build\bin"
#endif

[Setup]
LicenseFile=license-page.txt
AppId={{6F1C2B7E-5A3D-4E8B-9C21-1B7E0D4A9F63}
AppName=IsleBar
AppVersion={#AppVersion}
AppPublisher=IsleBar
AppComments=Unofficial taskbar island for Windows 11. Not affiliated with Anthropic or OpenAI.
DefaultDirName={localappdata}\IsleBar\app
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=IsleBar-Setup-{#AppVersion}
SetupIconFile=..\src\IsleBar.App\Assets\islebar.ico
UninstallDisplayIcon={app}\IsleBar.App.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
CloseApplications=force
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
en.TaskHooks=Connect Claude Code / Codex notifications (working · done · needs an answer)
ko.TaskHooks=Claude Code / Codex 알림 연결 (작업 중 · 완료 · 확인 필요)
ja.TaskHooks=Claude Code / Codex の通知を接続（作業中・完了・確認が必要）
fr.TaskHooks=Connecter les notifications Claude Code / Codex
de.TaskHooks=Claude Code / Codex-Benachrichtigungen verbinden
it.TaskHooks=Collega le notifiche di Claude Code / Codex
pt.TaskHooks=Conectar notificações do Claude Code / Codex
es.TaskHooks=Conectar notificaciones de Claude Code / Codex
en.TaskStartup=Start IsleBar when I sign in
ko.TaskStartup=로그인할 때 IsleBar 시작
ja.TaskStartup=サインイン時に IsleBar を起動
fr.TaskStartup=Démarrer IsleBar à l'ouverture de session
de.TaskStartup=IsleBar bei der Anmeldung starten
it.TaskStartup=Avvia IsleBar all'accesso
pt.TaskStartup=Iniciar o IsleBar ao entrar
es.TaskStartup=Iniciar IsleBar al iniciar sesión
en.TaskCrash=Send crash reports (the error, app and Windows version, basic device info — no files, titles or questions)
ko.TaskCrash=오류 보고 보내기 (오류 내용·앱/Windows 버전·기본 기기 정보만 — 파일·창 제목·질문은 보내지 않음)
ja.TaskCrash=クラッシュレポートを送信(エラー・アプリと Windows のバージョン・基本的な機器情報のみ — ファイル・タイトル・質問は送りません)
fr.TaskCrash=Envoyer les rapports de plantage (erreur, versions de l’app et de Windows, infos de base sur l’appareil — ni fichiers, ni titres, ni questions)
de.TaskCrash=Absturzberichte senden (Fehler, App- und Windows-Version, Basisdaten zum Gerät – keine Dateien, Titel oder Fragen)
it.TaskCrash=Invia segnalazioni di arresto anomalo (errore, versione app e Windows, dati base del dispositivo — niente file, titoli o domande)
pt.TaskCrash=Enviar relatórios de falha (erro, versão do app e do Windows, dados básicos do aparelho — sem arquivos, títulos ou perguntas)
es.TaskCrash=Enviar informes de errores (error, versión de la app y de Windows, datos básicos del equipo — sin archivos, títulos ni preguntas)

[Tasks]
Name: "startup"; Description: "{cm:TaskStartup}"
; not offered after the person disconnected in IsleBar's settings (reconnect there) — a reinstall used to undo that (review 10-03)
Name: "hooks"; Description: "{cm:TaskHooks}"; Check: not HooksTurnedOff
; opt-in, unticked by default — the same switch is in IsleBar's settings (10-01)
Name: "crashreports"; Description: "{cm:TaskCrash}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#CliDir}\islebar.exe"; DestDir: "{localappdata}\IsleBar\bin"; Flags: ignoreversion
Source: "hooks.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "restore-banners.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[InstallDelete]
; files earlier builds shipped and this one no longer does (the OpenAI logo was dropped 10-01 — trademark)
Type: files; Name: "{app}\Assets\codex-blossom-*.svg"

[Registry]
; Written only on the first install: Inno re-applies the remembered tasks on every update (the one-click update runs setup
; silently), which used to switch crash reports back on after the user had turned them off in settings (code review 10-02).
Root: HKCU; Subkey: "Software\IsleBar"; ValueType: dword; ValueName: "CrashReports"; ValueData: 1; Flags: uninsdeletekey createvalueifdoesntexist; Tasks: crashreports
Root: HKCU; Subkey: "Software\IsleBar"; ValueType: dword; ValueName: "CrashReports"; ValueData: 0; Flags: uninsdeletekey createvalueifdoesntexist; Tasks: not crashreports
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "IsleBar"; \
  ValueData: """{app}\IsleBar.App.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; a one-click update (silent, /RELAUNCH=1) must not reconnect hooks the user disconnected in settings (code review 10-02)
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\setup\hooks.ps1"" -Apply"; \
  Flags: runhidden waituntilterminated; Tasks: hooks; Check: not IsRelaunch
Filename: "{app}\IsleBar.App.exe"; Flags: nowait postinstall skipifsilent; Description: "{cm:LaunchProgram,IsleBar}"
; one-click update runs setup silently with /RELAUNCH=1 — start the bar again afterwards (10-01)
Filename: "{app}\IsleBar.App.exe"; Flags: nowait; Check: IsRelaunch

[UninstallRun]
; stop the bar first (the supervisor and the bar both run as IsleBar.App.exe), then undo what it changed outside its folder
Filename: "taskkill.exe"; Parameters: "/F /IM IsleBar.App.exe"; Flags: runhidden; RunOnceId: "StopBar"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\setup\restore-banners.ps1"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "RestoreBanners"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\setup\hooks.ps1"" -Revert"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveHooks"

[UninstallDelete]
Type: files; Name: "{localappdata}\IsleBar\bin\islebar.exe"

[Code]
// Upgrades: stop the running bar first. Its supervisor would otherwise relaunch the bar from half-copied files while setup
// replaces them (Restart Manager alone can't hold it back) — code review 10-01.
function HooksTurnedOff: Boolean;
var
  Value: Cardinal;
begin
  Result := RegQueryDWordValue(HKCU, 'Software\IsleBar', 'HooksOff', Value) and (Value = 1);
end;

function IsRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM IsleBar.App.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
