; ============================================================
;  ResumePoint 断点续播伴生工具 —— Inno Setup 安装脚本
;
;  构建：
;    dotnet publish src\ResumePoint -c Release -r win-x64 --self-contained false -o publish
;    iscc installer\ResumePoint.iss
;  设计取舍：
;   * PrivilegesRequired=lowest  → 按用户安装到 %LOCALAPPDATA%\Programs
;     （SMTC 必须跑在交互式桌面会话里，本来就是每用户工具，不需要管理员）
;   * 保留"安装位置可自定义"，但默认走用户目录，免 UAC
;   * 不动系统的默认文件关联（Windows 的 UserChoice 有哈希保护，程序改不了），
;     只把自己注册进"打开方式"列表，由用户在 设置→应用→默认应用 里选一次
; ============================================================

#define AppName "ResumePoint"
#define AppNameCN "ResumePoint 断点续播"
#define AppVersion "0.1.0"
#define AppPublisher "Local"
#define AppExeName "ResumePoint.exe"

[Setup]
AppId={{8B3C1D42-9A6E-4F51-B0C7-2D5E8F1A3B94}
AppName={#AppNameCN}
AppVersion={#AppVersion}
AppVerName={#AppNameCN} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppNameCN}
DisableProgramGroupPage=yes
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
OutputDir=output
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppNameCN}
SetupIconFile=..\src\ResumePoint\ResumePoint.ico
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.CreateDesktopIcon=创建桌面快捷方式
chinesesimplified.AutoStart=登录时自动启动（后台托盘常驻）
; 注意：CustomMessages 的值里不能用 ASCII 双引号 —— {cm:} 展开后会破坏 Description 参数的引号配对
chinesesimplified.RegisterOpenWith=注册到系统的「打开方式」列表
chinesesimplified.AssocHint=注册后可在 设置→应用→默认应用 里把视频文件默认交给 ResumePoint

[Tasks]
; 注意：Inno Setup 6.7 的任务 flag 里没有 "checked" —— 默认就是勾选；
;       要默认不勾选才写 "unchecked"。（写 checked 会报 unknown flag）
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "openwith";   Description: "{cm:RegisterOpenWith}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; installer\ 的上一级就是项目根，publish\ 与之同级
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userdesktop}\{#AppNameCN}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppNameCN}"; Filename: "{app}\{#AppExeName}"; Tasks: autostart

[Registry]
; --- 注册进系统"打开方式"列表（不抢默认关联） ---
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video"; ValueType: string; ValueName: ""; ValueData: "ResumePoint 断点续播打开"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" --open ""%1"""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\SupportedTypes"; ValueType: string; ValueName: ".wmv"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\SupportedTypes"; ValueType: string; ValueName: ".flv"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\SupportedTypes"; ValueType: string; ValueName: ".mp3"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\SupportedTypes"; ValueType: string; ValueName: ".m4a"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\SupportedTypes"; ValueType: string; ValueName: ".flac"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\ResumePoint.Video\SupportedTypes"; ValueType: string; ValueName: ".wav"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith

; --- 让 Windows 的"打开方式"对话框能列出我们 ---
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: ""; ValueData: "{#AppNameCN}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" --open ""%1"""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".wmv"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".flv"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith

; --- 记录默认安装时勾选的组件，供卸载清理 ---
Root: HKA; Subkey: "Software\{#AppName}"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\{#AppName}"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppNameCN}"; Flags: nowait postinstall skipifsilent
