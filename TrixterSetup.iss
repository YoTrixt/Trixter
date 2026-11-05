#define MyAppName "Trixter"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "Trixter"
#define MyAppExeName "Trixter.exe"

[Setup]
AppId={{25A59C34-1364-4F35-97C3-4F122A3CCCE9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Trixter
DefaultGroupName=Trixter
DisableProgramGroupPage=yes
MinVersion=10.0.22000
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayName=Trixter
UninstallDisplayIcon={app}\Trixter.exe
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoVersion=1.1.0.0
OutputDir=installer-output
OutputBaseFilename=TrixterSetup-1.1.0-x64
SetupLogging=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; The publish folder currently contains local WebView2 profile folders from development runs.
; Exclude them so browser data is never shipped or installed into Program Files.
Source: "publish\*"; DestDir: "{app}"; Excludes: "\Trixter.exe.WebView2\*,\WpfApp1.exe.WebView2\*"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\Trixter"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Trixter"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Code]
const
  WebView2RuntimeClientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2RuntimeDownloadUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124701';
  WebView2RuntimeInstallerName = 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe';

var
  WebView2DownloadPage: TDownloadWizardPage;

function HasValidRuntimeVersion(const RootKey: Integer; const SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version);
  if Result then
    Result := (Version <> '') and (Version <> '0.0.0.0');
end;

function IsWebView2RuntimeInstalled: Boolean;
var
  ClientKey: String;
begin
  ClientKey := 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2RuntimeClientId;
  Result :=
    HasValidRuntimeVersion(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2RuntimeClientId) or
    HasValidRuntimeVersion(HKLM, ClientKey) or
    HasValidRuntimeVersion(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2RuntimeClientId) or
    HasValidRuntimeVersion(HKCU, 'Software\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2RuntimeClientId);
end;

function AskToContinueWithoutRuntime(const FailureDetails: String): Boolean;
begin
  Result := MsgBox(
    'Trixter could not install the Microsoft Edge WebView2 Evergreen Runtime.' + #13#10 + #13#10 +
    FailureDetails + #13#10 + #13#10 +
    'You can continue installing Trixter, but web pages may not work until the Runtime is installed. ' +
    'You can download it later from:' + #13#10 + WebView2RuntimeDownloadUrl + #13#10 + #13#10 +
    'Continue installing Trixter anyway?',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstallerPath: String;
  RuntimeFailure: String;
  ResultCode: Integer;
begin
  Result := '';
  if IsWebView2RuntimeInstalled then
    Exit;

  InstallerPath := ExpandConstant('{tmp}\' + WebView2RuntimeInstallerName);
  RuntimeFailure := '';

  WebView2DownloadPage.Clear;
  WebView2DownloadPage.Add(WebView2RuntimeDownloadUrl, WebView2RuntimeInstallerName, '');
  WebView2DownloadPage.Show;
  try
    try
      WebView2DownloadPage.Download;
    except
      RuntimeFailure := 'The download failed: ' + GetExceptionMessage;
    end;
  finally
    WebView2DownloadPage.Hide;
  end;

  if (RuntimeFailure = '') and not FileExists(InstallerPath) then
    RuntimeFailure := 'The Microsoft installer was not downloaded.';

  if RuntimeFailure = '' then
  begin
    if not Exec(InstallerPath, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RuntimeFailure := 'Windows could not start the Microsoft Runtime installer.'
    else if (ResultCode = 3010) or (ResultCode = 1641) then
    begin
      { 3010 means a restart is required; 1641 means one was initiated. Continue setup either way. }
      NeedsRestart := True;
      DeleteFile(InstallerPath);
      Exit;
    end
    else if ResultCode <> 0 then
      RuntimeFailure := 'The Microsoft Runtime installer returned exit code ' + IntToStr(ResultCode) + '.'
    else if not IsWebView2RuntimeInstalled then
      RuntimeFailure := 'The Microsoft installer finished, but Windows does not yet report the Runtime as installed.';
  end;

  DeleteFile(InstallerPath);

  if RuntimeFailure <> '' then
  begin
    if WizardSilent or not AskToContinueWithoutRuntime(RuntimeFailure) then
      Result := 'Trixter setup was canceled because the WebView2 Runtime could not be installed.';
  end;
end;

procedure InitializeWizard;
begin
  WebView2DownloadPage := CreateDownloadPage(
    'Microsoft Edge WebView2 Runtime',
    'The WebView2 Evergreen Runtime is required by Trixter. Downloading the official Microsoft x64 standalone installer...',
    nil);
  WebView2DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;
