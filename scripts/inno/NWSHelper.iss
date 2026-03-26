#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

#ifndef SourceDir
  #define SourceDir "."
#endif

#ifndef OutputDir
  #define OutputDir "."
#endif

#ifndef SetupIconFile
  #define SetupIconFile ""
#endif

#define AppName "NWS Helper"
#define AppExeName "NWSHelper.Gui.exe"

[Setup]
AppId={{31D36D25-1A20-4DC8-9BA6-D4552C5C94D9}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=NWS Helper
DefaultDirName={autopf}\NWS Helper
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=NWSHelper-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#SetupIconFile}
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NWS Helper"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\NWS Helper"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch NWS Helper"; Flags: nowait postinstall skipifsilent
