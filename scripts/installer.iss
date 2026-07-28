; EpubFabric（CLI・GUI）の Inno Setup インストーラー定義。
; ビルドは scripts\build-installer.ps1 から行う（publish 出力と各種パスを /D で受け取る）。
;
; 必須の define:
;   AppVersion     - インストーラーのバージョン（例: 1.0.0）
;   PublishDir     - publish.ps1 の CLI 出力ディレクトリ（epubfabric-cli.exe を含む。{app}\cli へ入る）
;   GuiPublishDir  - publish.ps1 の GUI 出力ディレクトリ（EpubFabric.exe を含む。{app} 直下へ入る）
;   OutputDir      - セットアップEXEの出力先ディレクトリ
;   IconFile       - セットアップEXEに使うアイコン（省略時は Inno Setup の既定）

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #error "PublishDir を /DPublishDir=... で指定してください"
#endif
#ifndef GuiPublishDir
  #error "GuiPublishDir を /DGuiPublishDir=... で指定してください"
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{8A0BB7CE-7B36-4E64-9F14-2A4B6C1E5D90}
AppName=EpubFabric
AppVersion={#AppVersion}
AppPublisher=fukuyori
AppPublisherURL=https://github.com/fukuyori/EpubFabric
DefaultDirName={autopf}\EpubFabric
DefaultGroupName=EpubFabric
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=EpubFabric-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 管理者/ユーザー単位のどちらでもインストールできるようにする。
; 管理者: Program Files + HKLM PATH / ユーザー: %LocalAppData%\Programs + HKCU PATH
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; PATH変更を反映させるため、終了時に WM_SETTINGCHANGE を送出させる。
ChangesEnvironment=yes
UninstallDisplayIcon={app}\EpubFabric.exe
#ifdef IconFile
SetupIconFile={#IconFile}
#endif

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "addtopath"; Description: "PATH 環境変数に追加する（`epubfabric` と打つだけで起動できるようにする）"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
; 本体はGUI。アプリ直下（PATHに追加される場所）に置き、`epubfabric` で起動できるようにする。
; Windowsのファイル名は大文字小文字を区別しないため、EpubFabric.exe が epubfabric で見つかる。
; CLIは補助ツール（epubfabric-cli.exe）として cli\ 配下に分ける。双方が自己完結型で
; ランタイムDLLの同名ファイルを持つため、同じフォルダーには混在させない。
Source: "{#GuiPublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "{#PublishDir}\*"; DestDir: "{app}\cli"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\EpubFabric"; Filename: "{app}\EpubFabric.exe"; WorkingDir: "{app}"
Name: "{group}\EpubFabric CLI（コマンドプロンプト）"; Filename: "{cmd}"; Parameters: "/k cd /d ""{app}\cli"" && epubfabric-cli.exe"; WorkingDir: "{app}\cli"
Name: "{autodesktop}\EpubFabric"; Filename: "{app}\EpubFabric.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Code]
function PathRegRootKey: Integer;
begin
  if IsAdminInstallMode then
    Result := HKEY_LOCAL_MACHINE
  else
    Result := HKEY_CURRENT_USER;
end;

function PathRegSubkey: string;
begin
  if IsAdminInstallMode then
    Result := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  else
    Result := 'Environment';
end;

function PathContains(const Path, Dir: string): Boolean;
begin
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') <> 0;
end;

procedure AddAppDirToPath;
var
  Path, AppDir: string;
begin
  AppDir := ExpandConstant('{app}');
  if not RegQueryStringValue(PathRegRootKey, PathRegSubkey, 'Path', Path) then
    Path := '';

  if PathContains(Path, AppDir) then
    exit;

  if (Path <> '') and (Copy(Path, Length(Path), 1) <> ';') then
    Path := Path + ';';
  Path := Path + AppDir;

  RegWriteExpandStringValue(PathRegRootKey, PathRegSubkey, 'Path', Path);
end;

procedure RemoveAppDirFromPath;
var
  Path, AppDir: string;
  P: Integer;
begin
  AppDir := ExpandConstant('{app}');
  if not RegQueryStringValue(PathRegRootKey, PathRegSubkey, 'Path', Path) then
    exit;

  P := Pos(';' + Uppercase(AppDir) + ';', ';' + Uppercase(Path) + ';');
  if P = 0 then
    exit;

  // 先頭に付けた番兵の ';' の分だけ位置を戻して削除する。
  Delete(Path, P, Length(AppDir) + 1);
  // 先頭要素だった場合に残る先頭の ';' を取り除く。
  if Copy(Path, 1, 1) = ';' then
    Delete(Path, 1, 1);

  RegWriteExpandStringValue(PathRegRootKey, PathRegSubkey, 'Path', Path);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddAppDirToPath;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveAppDirFromPath;
end;
