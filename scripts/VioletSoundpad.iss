#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{47D9E29A-3E13-415F-AACC-DBFFFC32CC9D}
AppName=Violet Soundpad
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppMutex=VioletSoundpad-47D9E29A-3E13-415F-AACC-DBFFFC32CC9D
MinVersion=10.0.19041
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Violet Soundpad
DefaultGroupName=Violet Soundpad
DisableProgramGroupPage=yes
AllowNoIcons=yes
UninstallDisplayIcon={app}\VioletSoundpad.exe
SetupIconFile=..\src\Soundpad\app.ico
OutputDir=..\artifacts
OutputBaseFilename=VioletSoundpad-Setup-{#AppVersion}
WizardStyle=modern dynamic
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\artifacts\app\VioletSoundpad.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\app\VioletSoundpad.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\app\NAudio.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\app\NAudio.Wasapi.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Violet Soundpad"; Filename: "{app}\VioletSoundpad.exe"
Name: "{autodesktop}\Violet Soundpad"; Filename: "{app}\VioletSoundpad.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VioletSoundpad.exe"; Description: "{cm:LaunchProgram,Violet Soundpad}"; Flags: nowait postinstall skipifsilent
