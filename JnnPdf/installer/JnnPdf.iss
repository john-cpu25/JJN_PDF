; ==========================================================
;  JNN PDF – Inno Setup script
;  Build with Build_Installer.bat (publishes the app first, then compiles this file).
;  Output: installer\Output\JNN_PDF_Setup_<version>.exe
; ==========================================================

#define MyAppName      "JNN PDF"
#define MyAppExeName   "JnnPdf.exe"
#define MyAppPublisher "JNN"
#define PublishDir     "..\publish\win-x64"
#define MyAppVersion   GetVersionNumbersString(PublishDir + "\" + MyAppExeName)

[Setup]
; Fixed GUID = same app on upgrade/uninstall. Do not change.
AppId={{6C3B7E2A-4F1D-4B8E-9A57-2D0F1C9E8B41}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
DefaultDirName={autopf}\JNN PDF
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Per-user install by default (no admin needed); user can pick "all users" in the dialog
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=JNN_PDF_Setup_{#MyAppVersion}
SetupIconFile=..\Logo_JNN.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "assocreview"; Description: "Open .jnnreview project files with JNN PDF"; GroupDescription: "File associations:"
Name: "assocpdf";    Description: "Add JNN PDF to ""Open with"" for PDF files"; GroupDescription: "File associations:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; ProgID used by both associations
Root: HKA; Subkey: "Software\Classes\JnnPdf.Document"; ValueType: string; ValueName: ""; ValueData: "JNN PDF Document"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\JnnPdf.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\JnnPdf.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
; .jnnreview -> JNN PDF (default handler)
Root: HKA; Subkey: "Software\Classes\.jnnreview"; ValueType: string; ValueName: ""; ValueData: "JnnPdf.Document"; Flags: uninsdeletevalue; Tasks: assocreview
; .pdf -> only adds JNN PDF to "Open with" (does not take over the default PDF viewer)
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "JnnPdf.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".jnnreview"; ValueData: ""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
