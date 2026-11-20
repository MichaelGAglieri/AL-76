; Classic installer of AL-76 for who does not use the Store (Inno Setup 6, https://jrsoftware.org).
; Per user, no administrator rights: %LOCALAPPDATA%\Programs\AL-76. Build on Windows with:
;   iscc /DRid=win-x64 /DArch=x64 AL-76.iss        (or /DRid=win-arm64 /DArch=arm64), or simply scripts\build.ps1
; If vc_redist.<arch>.exe (Microsoft's official Visual C++ Redistributable) is put next to this file, the installer
; runs it when the PC does not have it; otherwise AL-76 shows the download link at the first start.
#ifndef Rid
  #define Rid "win-x64"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#define AppVersion "0.1.0"
; Your own build (scripts\build.ps1 -AppName -Publisher -AppId): another name and id install next to the official
; AL-76 instead of replacing it. AppId is a GUID without braces; keep the same one for your updates.
#ifndef AppName
  #define AppName "AL-76"
#endif
#ifndef Publisher
  #define Publisher "AL-76"
#endif
#ifndef AppId
  #define AppId "65F9183B-38BD-4780-AD28-A1AA4087C451"
#endif

[Setup]
AppId={{{#AppId}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=..\out
OutputBaseFilename={#StringChange(AppName, " ", "-")}-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\src\AL76.App\AL-76.ico
UninstallDisplayIcon={app}\AL-76.exe
Compression=lzma2/ultra64
SolidCompression=yes
; x64compatible: ordinary 64-bit PCs, and Arm64 PCs through Windows' x64 emulation (plain "x64" refuses those).
#if Arch == "x64"
  #define Archs "x64compatible"
#else
  #define Archs Arch
#endif
ArchitecturesAllowed={#Archs}
ArchitecturesInstallIn64BitMode={#Archs}
WizardStyle=modern

[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\out\{#Rid}\*"; DestDir: "{app}"; Excludes: "dev.flag,prove\*,AL76.Bench.exe"; Flags: recursesubdirs ignoreversion
Source: "vc_redist.{#Arch}.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\AL-76.exe"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\AL-76.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Run]
; The Visual C++ runtime asks for administrator rights itself; skipped when present or not bundled.
Filename: "{tmp}\vc_redist.{#Arch}.exe"; Parameters: "/install /passive /norestart"; Check: NeedsVcRuntime; Flags: skipifdoesntexist waituntilterminated
Filename: "{app}\AL-76.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings, memory and models stay (in %APPDATA%\AL-76 and %LOCALAPPDATA%\AL-76): removing them is the user's choice.
Type: filesandordirs; Name: "{app}"

[Code]
function NeedsVcRuntime: Boolean;
var
  installed: Cardinal;
begin
  Result := not (RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{#Arch}', 'Installed', installed) and (installed = 1));
end;
