; DiskMigrator 설치 프로그램 (Inno Setup 6)
;
; 빌드: installer\build.ps1 을 실행하면 최신 앱을 publish → 라이선스 생성 → 이 스크립트를
;       ISCC로 컴파일해 installer\output\DiskMigrator-Setup-v<버전>.exe 를 만듭니다.
;
; 이 앱은 단일 자체 포함 exe(런타임 미설치 PC에서도 실행)라 설치 구성이 단순합니다:
; exe 한 개를 Program Files에 넣고, 시작 메뉴 바로가기와 제거 프로그램을 만듭니다.

; ⚠ 이 설치본은 수동 버전 DiskMigrator와 **함께** 설치됩니다 — 대체하지 않습니다.
;   제품명·설치 경로·exe 이름·AppId를 전부 달리해야 두 앱이 공존합니다.

#define AppName "DiskMigrator-X"
#define AppVersion "1.1.0"
#define AppPublisher "DiskMigrator"
#define AppExeName "DiskMigratorX.exe"
#define PublishDir "..\src\DiskMigrator.App\bin\Release\net8.0-windows\win-x64\publish"
#define BridgeDir "..\src\DiskMigrator.Bridge\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
; AppId는 업그레이드·제거를 같은 제품으로 묶는 고정 키입니다. 절대 바꾸지 마십시오.
;
; ⚠ 수동 버전(3937A695-…)과 **반드시 달라야** 합니다. 같으면 Windows가 두 제품을 하나로 보고,
;   DiskMigrator-X를 설치하는 순간 사용자의 DiskMigrator v1.4.x가 업그레이드로 덮여 사라집니다.
;   "기존 버전을 계속 쓸 수 있어야 한다"는 이 프로젝트의 전제가 그 한 줄에서 무너집니다.
AppId={{0816A1C6-539A-459E-8937-26CD06F713DC}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; 설치 마법사에서 EULA 동의 단계를 띄웁니다(build.ps1이 UTF-8 BOM으로 생성).
LicenseFile=EULA-license.txt
OutputDir=output
OutputBaseFilename={#AppName}-Setup-v{#AppVersion}
SetupIconFile=..\src\DiskMigrator.App\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
; Program Files에 쓰고 앱 자체도 관리자 권한이 필요하므로 설치도 관리자 권한.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\DiskMigratorX.exe"; DestDir: "{app}"; DestName: "{#AppExeName}"; Flags: ignoreversion
; Claude 데스크톱 앱과 이어 주는 중계기.
;
; ⚠ 앱 exe 옆에, 이 이름 그대로 있어야 합니다. 앱의 [Claude에 연결하기] 버튼이 자기
;   실행 파일이 있는 폴더에서 이 이름을 찾아 Claude 설정에 그 경로를 적어 두기 때문입니다.
;   빠지면 그 버튼이 아예 나타나지 않고, 사용자는 다시 명령 창을 열어야 합니다.
Source: "{#BridgeDir}\DiskMigratorX.Bridge.exe"; DestDir: "{app}"; Flags: ignoreversion
; 사용설명서(단일 HTML, 영어·한국어) — 시작 메뉴에서 바로 열 수 있게 함께 설치합니다.
; 두 파일은 서로 언어 링크로 연결되므로 같은 폴더에 나란히 있어야 합니다.
Source: "..\docs\manual.html"; DestDir: "{app}"; DestName: "manual.html"; Flags: ignoreversion
Source: "..\docs\manual-en.html"; DestDir: "{app}"; DestName: "manual-en.html"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} User Manual"; Filename: "{app}\manual-en.html"
Name: "{group}\{#AppName} 사용설명서"; Filename: "{app}\manual.html"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
