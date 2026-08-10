# DiskMigrator 설치 프로그램 빌드 스크립트
#
# 하는 일:
#   1. 최신 앱을 단일 exe로 publish
#   2. 앱 exe에 코드 서명 (인증서가 설정된 경우 — sign.ps1, 없으면 건너뜀)
#   3. EULA 본문을 UTF-8 BOM 텍스트로 변환(설치 마법사 라이선스 단계가 한글을 올바로 표시)
#   4. Inno Setup(ISCC.exe)으로 컴파일 → installer\output\DiskMigrator-X-Setup-v<버전>.exe
#   5. 설치 exe에 코드 서명 (인증서가 설정된 경우)
#
# 코드 서명은 인증서 환경변수(DM_SIGN_PFX 또는 DM_SIGN_THUMBPRINT)가 있을 때만 동작하고,
# 없으면 경고만 남기고 서명 없이 빌드됩니다. 자세한 설정은 docs\CODE-SIGNING.md 참고.
#
# 사용: 관리자 권한이 필요 없습니다. installer 폴더에서  ./build.ps1  실행.

$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$root = Split-Path -Parent $installerDir

$appExe = "$root\src\DiskMigrator.App\bin\Release\net8.0-windows\win-x64\publish\DiskMigratorX.exe"
$bridgeExe = "$root\src\DiskMigrator.Bridge\bin\Release\net8.0-windows\win-x64\publish\DiskMigratorX.Bridge.exe"

Write-Host "[1/5] 앱 publish (단일 exe)..." -ForegroundColor Cyan
dotnet publish "$root\src\DiskMigrator.App\DiskMigrator.App.csproj" -c Release -r win-x64 --nologo -v m
if ($LASTEXITCODE -ne 0) { throw "publish 실패 (exit $LASTEXITCODE)" }

# Claude 데스크톱 앱은 표준입출력으로 켜지는 프로그램만 등록할 수 있는데, 앱 본체는
# 관리자 권한을 요구해 그 방식으로 켤 수 없습니다. 그래서 권한을 올리지 않는 중계기를
# 함께 만들어 앱 옆에 둡니다 — 빠지면 [Claude에 연결하기] 버튼이 사라집니다.
dotnet publish "$root\src\DiskMigrator.Bridge\DiskMigrator.Bridge.csproj" -c Release -r win-x64 --nologo -v m
if ($LASTEXITCODE -ne 0) { throw "중계기 publish 실패 (exit $LASTEXITCODE)" }

Write-Host "[2/5] 앱 exe 코드 서명..." -ForegroundColor Cyan
# 앱 exe를 설치 프로그램에 넣기 전에 서명합니다 — 그래야 설치된 실행 파일도 서명됩니다.
$appSigned = & "$installerDir\sign.ps1" -Files @($appExe, $bridgeExe)

Write-Host "[3/5] 라이선스 파일 생성 (UTF-8 BOM)..." -ForegroundColor Cyan
$eula = Get-Content "$root\src\DiskMigrator.App\Resources\EULA.txt" -Raw -Encoding UTF8
$utf8bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText("$installerDir\EULA-license.txt", $eula, $utf8bom)

Write-Host "[4/5] Inno Setup 컴파일..." -ForegroundColor Cyan
# ISCC.exe는 설치 범위(전체/사용자)에 따라 위치가 다릅니다. 알려진 곳을 차례로 확인합니다.
$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup(ISCC.exe)을 찾지 못했습니다. https://jrsoftware.org/isdl.php 에서 설치하거나 'winget install JRSoftware.InnoSetup' 을 실행하세요."
}
& $iscc "$installerDir\DiskMigrator.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC 컴파일 실패 (exit $LASTEXITCODE)" }

$out = Join-Path $installerDir "output"
$setupExe = Get-ChildItem "$out\*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

Write-Host "[5/5] 설치 exe 코드 서명..." -ForegroundColor Cyan
$setupSigned = & "$installerDir\sign.ps1" -Files @($setupExe.FullName)

Write-Host "`n완료. 설치 프로그램:" -ForegroundColor Green
Get-ChildItem "$out\*.exe" | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}, LastWriteTime | Format-Table -AutoSize
if (-not $appSigned -or -not $setupSigned) {
    Write-Warning "서명되지 않은 빌드입니다. 배포 전 코드 서명을 하십시오 (docs\CODE-SIGNING.md)."
}
