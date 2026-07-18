; 屏幕标注白板 — Inno Setup 安装脚本（CR-009）
; 技术说明：Inno Setup 6；每用户安装到 LocalAppData，免管理员；自包含发布产物。
; 构建：先 dotnet publish，再 ISCC installer\ScreenAnnotator.iss

#define MyAppName "屏幕标注白板"
#define MyAppNameEn "ScreenAnnotator"
#define MyAppVersion "1.0.7"
#define MyAppPublisher "ScreenAnnotator"
#define MyAppExeName "ScreenAnnotator.exe"
; 稳定 AppId：升级/覆盖同一入口，勿随意更改
#define MyAppId "{{A7C3E9F2-4B1D-4E8A-9C6F-2D5A8B0E1F34}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppNameEn}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 每用户安装，无需管理员
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=ScreenAnnotator-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 中文向导
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 关闭时若程序在运行则提示关闭
CloseApplications=yes
RestartApplications=no
; 覆盖升级：同 AppId 先卸再装由 Inno 自动处理
UsePreviousAppDir=yes
DisableDirPage=no
InfoBeforeFile=uninstall-note-zh.txt

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked

[Files]
; 自包含发布目录全部文件（与 publish/ScreenAnnotator 一致）
Source: "..\publish\ScreenAnnotator\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 仅清理安装目录内可能残留的空目录；不触碰 %APPDATA%\ScreenAnnotator
Type: dirifempty; Name: "{app}"

; 注意：切勿在此删除 {userappdata}\ScreenAnnotator —— 用户配置与设置须保留
