#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef ArtifactDir
  #error ArtifactDir is required
#endif

[Setup]
AppId=br.com.sergiobaiao.SeiMonitor
AppName=SEI Monitor
AppVersion={#AppVersion}
AppPublisher=SEI Monitor
AppPublisherURL=https://github.com/sergiobaiao/sei-monitor
DefaultDirName={localappdata}\Programs\SEI Monitor
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\SeiMonitor.exe
SetupIconFile=..\src\SeiMonitor\Assets\SeiMonitor.ico
OutputDir={#ArtifactDir}
OutputBaseFilename=SEI-Monitor-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\SEI Monitor"; Filename: "{app}\SeiMonitor.exe"; WorkingDir: "{app}"
