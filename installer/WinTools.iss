; WinTools 安装脚本（Inno Setup 6）。
; 不要直接编译：用 scripts\Release.ps1，它会先改版本号、发布，再带上 /DAppVersion=x.y.z 调用 ISCC。
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "WinTools"
#define AppExe "WinTools.exe"

[Setup]
; AppId 一旦发布就不能改：它决定了「覆盖安装」与「卸载」认的是不是同一个程序。
AppId={{6B1F4D0A-7C25-4E3B-9A58-3D9E27C0B1F4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ZijianChenArt
AppPublisherURL=https://github.com/ZijianChenArt/WinTools
AppSupportURL=https://github.com/ZijianChenArt/WinTools/issues
AppUpdatesURL=https://github.com/ZijianChenArt/WinTools/releases
VersionInfoVersion={#AppVersion}
; 装到当前用户目录，不需要管理员权限；在线更新才能静默覆盖安装。
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
SetupIconFile=..\src\WinTools\Assets\AppIcon.ico
OutputDir=..\artifacts\dist
OutputBaseFilename=WinTools-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 覆盖安装 / 在线更新时自动关掉正在运行的 WinTools。
CloseApplications=force
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\artifacts\release\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; 卸载时清掉「登录时启动」；用户配置（%LocalAppData%\WinTools）与桌面收纳的文件一律保留。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "WinTools"; Flags: uninsdeletevalue dontcreatekey

[Run]
; 手动安装：向导最后一页的「运行 WinTools」；静默更新：装完直接重新启动。
Filename: "{app}\{#AppExe}"; Description: "运行 {#AppName}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent
