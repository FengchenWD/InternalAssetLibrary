#ifndef AppVersion
  #error AppVersion must be provided by publish-client.ps1.
#endif
#ifndef SourceDirectory
  #error SourceDirectory must be provided by publish-client.ps1.
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be provided by publish-client.ps1.
#endif

#define AppName "云汀素材管理工具"
#define AppPublisher "风尘WD(FengchenWD)"
#define AppExeName "InternalAssetLibrary.Client.exe"
#define UpdaterExeName "InternalAssetLibrary.Updater.exe"
#define RegisteredAppName "FengchenWD.InternalAssetLibrary"
#define ProductKey "Software\FengchenWD\InternalAssetLibrary"
#define CapabilitiesKey "Software\FengchenWD\InternalAssetLibrary\Capabilities"

[Setup]
AppId={{D1B775BE-9677-4C86-B128-211433AF9F38}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\FengchenWD\InternalAssetLibrary
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
ShowLanguageDialog=no
UsePreviousLanguage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir={#OutputDirectory}
OutputBaseFilename=InternalAssetLibrary.Client.Setup
SetupIconFile=..\..\src\InternalAssetLibrary.Client\Assets\internal-asset-library.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
UsePreviousAppDir=yes
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式"; Flags: unchecked

[InstallDelete]
Type: filesandordirs; Name: "{app}\runtime"
Type: filesandordirs; Name: "{app}\Licenses"

[Files]
Source: "{#SourceDirectory}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDirectory}\{#UpdaterExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDirectory}\runtime\*"; DestDir: "{app}\runtime"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDirectory}\Licenses\*"; DestDir: "{app}\Licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDirectory}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "{#ProductKey}"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "{#ProductKey}"; ValueType: string; ValueName: "ExecutablePath"; ValueData: "{app}\{#AppExeName}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#RegisteredAppName}"; ValueData: "{#CapabilitiesKey}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "{#CapabilitiesKey}"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "{#CapabilitiesKey}"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "团队素材管理与媒体播放器"
Root: HKCU; Subkey: "{#CapabilitiesKey}"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: """{app}\{#AppExeName}"",0"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: none; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: none; Flags: deletekey

Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".mp3"; ValueData: "FengchenWD.InternalAssetLibrary.Audio"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".aac"; ValueData: "FengchenWD.InternalAssetLibrary.Audio"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".ogg"; ValueData: "FengchenWD.InternalAssetLibrary.Audio"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".m4a"; ValueData: "FengchenWD.InternalAssetLibrary.Audio"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".flac"; ValueData: "FengchenWD.InternalAssetLibrary.Audio"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".ape"; ValueData: "FengchenWD.InternalAssetLibrary.Audio"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".wav"; ValueData: "FengchenWD.InternalAssetLibrary.Audio"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".alac"; ValueData: "FengchenWD.InternalAssetLibrary.Audio"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "FengchenWD.InternalAssetLibrary.Video"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "FengchenWD.InternalAssetLibrary.Video"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "FengchenWD.InternalAssetLibrary.Video"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".avi"; ValueData: "FengchenWD.InternalAssetLibrary.Video"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".wmv"; ValueData: "FengchenWD.InternalAssetLibrary.Video"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".flv"; ValueData: "FengchenWD.InternalAssetLibrary.Video"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "FengchenWD.InternalAssetLibrary.Video"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".m3u8"; ValueData: "FengchenWD.InternalAssetLibrary.Video"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".jpg"; ValueData: "FengchenWD.InternalAssetLibrary.Image"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".jpeg"; ValueData: "FengchenWD.InternalAssetLibrary.Image"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".png"; ValueData: "FengchenWD.InternalAssetLibrary.Image"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".gif"; ValueData: "FengchenWD.InternalAssetLibrary.Image"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".webp"; ValueData: "FengchenWD.InternalAssetLibrary.Image"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".bmp"; ValueData: "FengchenWD.InternalAssetLibrary.Image"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".tiff"; ValueData: "FengchenWD.InternalAssetLibrary.Image"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".tif"; ValueData: "FengchenWD.InternalAssetLibrary.Image"
Root: HKCU; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".svg"; ValueData: "FengchenWD.InternalAssetLibrary.Image"

Root: HKCU; Subkey: "Software\Classes\FengchenWD.InternalAssetLibrary.Audio"; ValueType: string; ValueData: "云汀素材管理工具音频"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\FengchenWD.InternalAssetLibrary.Audio\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\FengchenWD.InternalAssetLibrary.Audio\shell"; ValueType: string; ValueData: "open"
Root: HKCU; Subkey: "Software\Classes\FengchenWD.InternalAssetLibrary.Audio\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" --open ""%1"""
Root: HKCU; Subkey: "Software\Classes\FengchenWD.InternalAssetLibrary.Video"; ValueType: string; ValueData: "云汀素材管理工具视频"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\FengchenWD.InternalAssetLibrary.Video\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\FengchenWD.InternalAssetLibrary.Video\shell"; ValueType: string; ValueData: "open"
Root: HKCU; Subkey: "Software\Classes\FengchenWD.InternalAssetLibrary.Video\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" --open ""%1"""
Root: HKCU; Subkey: "Software\Classes\FengchenWD.InternalAssetLibrary.Image"; ValueType: string; ValueData: "云汀素材管理工具图片"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\FengchenWD.InternalAssetLibrary.Image\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\FengchenWD.InternalAssetLibrary.Image\shell"; ValueType: string; ValueData: "open"
Root: HKCU; Subkey: "Software\Classes\FengchenWD.InternalAssetLibrary.Image\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" --open ""%1"""

Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" --open ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mp3"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".aac"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".ogg"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".m4a"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".flac"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".ape"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".wav"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".alac"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".wmv"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".flv"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".m3u8"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".jpg"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".jpeg"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".png"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".gif"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".webp"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".bmp"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".tiff"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".tif"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".svg"; ValueData: ""

[UninstallDelete]
Type: filesandordirs; Name: "{app}\runtime"
Type: filesandordirs; Name: "{app}\Licenses"
Type: dirifempty; Name: "{app}"

[Code]
var
  RemoveUserData: Boolean;

function InitializeUninstall(): Boolean;
var
  Choice: Integer;
begin
  RemoveUserData := False;
  if UninstallSilent then
  begin
    Result := True;
    Exit;
  end;

  Choice := MsgBox(
    '是否保留用户设置和本机数据？' + #13#10 + #13#10 +
    '选择“是”将保留账号记录、密码凭据、服务器地址、本地目录索引、设置和缓存，方便以后重新安装。' + #13#10 +
    '选择“否”将清除这些软件管理的数据，但不会删除真实素材、已连接的文件夹或素材下载目录中的文件。',
    mbConfirmation,
    MB_YESNOCANCEL);
  if Choice = IDCANCEL then
  begin
    Result := False;
    Exit;
  end;

  RemoveUserData := Choice = IDNO;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Started: Boolean;
begin
  if (CurUninstallStep <> usUninstall) or (not RemoveUserData) then
    Exit;

  Started := Exec(
    ExpandConstant('{app}\{#AppExeName}'),
    '--purge-user-data',
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  if (not Started) or (ResultCode <> 0) then
    MsgBox(
      '用户设置未能完整清除。软件将继续卸载；如需手动清理，请删除当前 Windows 用户 LocalAppData 下的 FengchenWD\InternalAssetLibrary 文件夹和凭据管理器中的 InternalAssetLibrary 凭据。',
      mbError,
      MB_OK);
end;
