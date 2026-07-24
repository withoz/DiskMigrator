; DiskMigrator 설치 프로그램 (Inno Setup 6)
;
; 빌드: installer\build.ps1 을 실행하면 최신 앱을 publish → 라이선스 생성 → 이 스크립트를
;       ISCC로 컴파일해 installer\output\DiskMigrator-Setup-v<버전>.exe 를 만듭니다.
;
; 이 앱은 단일 자체 포함 exe(런타임 미설치 PC에서도 실행)라 설치 구성이 단순합니다:
; exe 한 개를 Program Files에 넣고, 시작 메뉴 바로가기와 제거 프로그램을 만듭니다.

#define AppName "DiskMigrator"
#define AppVersion "1.0.1"
#define AppPublisher "DiskMigrator"
#define AppExeName "DiskMigrator.exe"
#define PublishDir "..\src\DiskMigrator.App\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
; AppId는 업그레이드·제거를 같은 제품으로 묶는 고정 키입니다. 절대 바꾸지 마십시오.
AppId={{3937A695-EE14-4187-B133-0FA5A3631038}
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
OutputBaseFilename=DiskMigrator-Setup-v{#AppVersion}
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
Source: "{#PublishDir}\DiskMigrator.App.exe"; DestDir: "{app}"; DestName: "{#AppExeName}"; Flags: ignoreversion
; 사용설명서(단일 HTML) — 시작 메뉴에서 바로 열 수 있게 함께 설치합니다.
Source: "..\docs\manual.html"; DestDir: "{app}"; DestName: "manual.html"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} 사용설명서"; Filename: "{app}\manual.html"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
