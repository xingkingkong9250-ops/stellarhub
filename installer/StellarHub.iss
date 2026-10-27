; 千星汇图（StellarHub）安装包脚本 —— Inno Setup 6
; 编译：& ISCC.exe /DAppVersion=1.0.0 installer\StellarHub.iss
; 源目录依赖门禁产物：artifacts\staging\StellarHub_v{AppVersion}\
; 发布形态：self-contained win-x64（内置 .NET 运行时），目标机免环境

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "千星汇图"
#define AppNameEn "StellarHub"
#define AppPublisher "StellarHub"
#define AppExeName "StellarHub.exe"
#define SourceDir "..\artifacts\staging\StellarHub_v" + AppVersion

[Setup]
AppId={{5E7C1A42-9B3D-4F68-A251-0D8E6C4B7F90}
AppName={#AppName} ({#AppNameEn})
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppNameEn}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
OutputDir=..\artifacts\installer
OutputBaseFilename={#AppNameEn}_Setup_v{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
; 品牌图标：安装向导图标 + exe/快捷方式/卸载项图标
SetupIconFile=..\src\QL.World\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
