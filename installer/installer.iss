; Inno Setup script for WeChatViewer.Desktop
; Built in CI: ISCC /DAppVersion=<from csproj> /DPublishDir=... installer\installer.iss

#ifndef AppVersion
#define AppVersion "2.5.0"
#endif

#ifndef PublishDir
#define PublishDir "WeChatViewer.Desktop\bin\publish\win-x64"
#endif

#define MyAppExe "WeChatViewer.Desktop.exe"

[Setup]
AppId={{9F1C2A34-6E5B-4C8D-8A21-3E7F0B9C4D52}
AppName=WeChatViewer Desktop
AppVersion={#AppVersion}
AppVerName=WeChatViewer Desktop {#AppVersion}
AppPublisher=Ming-QWQ520
DefaultDirName={autopf}\WeChatViewer.Desktop
PrivilegesRequiredOverridesAllowed=dialog commandline
UninstallDisplayName=WeChatViewer Desktop (微信聊天记录查看器)
UninstallDisplayIcon={app}\{#MyAppExe}
DefaultGroupName=WeChatViewer Desktop
OutputDir=..\dist
OutputBaseFilename=WeChatViewer-Setup-{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "logs,logs\*,*.pdb"

[Icons]
Name: "{group}\WeChatViewer Desktop"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\卸载 WeChatViewer Desktop"; Filename: "{uninstallexe}"
Name: "{autodesktop}\WeChatViewer Desktop"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "立即运行 WeChatViewer Desktop"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; remove runtime leftovers (logs, extracted keys, cached thumbs)
Type: filesandordirs; Name: "{app}\logs"
