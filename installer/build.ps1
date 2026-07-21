# DiskMigrator 설치 프로그램 빌드 스크립트
#
# 하는 일:
#   1. 최신 앱을 단일 exe로 publish
#   2. EULA 본문을 UTF-8 BOM 텍스트로 변환(설치 마법사 라이선스 단계가 한글을 올바로 표시)
#   3. Inno Setup(ISCC.exe)으로 컴파일 → installer\output\DiskMigrator-Setup-v<버전>.exe
#
# 사용: 관리자 권한이 필요 없습니다. installer 폴더에서  ./build.ps1  실행.

$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$root = Split-Path -Parent $installerDir

Write-Host "[1/3] 앱 publish (단일 exe)..." -ForegroundColor Cyan
dotnet publish "$root\src\DiskMigrator.App\DiskMigrator.App.csproj" -c Release -r win-x64 --nologo -v m
if ($LASTEXITCODE -ne 0) { throw "publish 실패 (exit $LASTEXITCODE)" }

Write-Host "[2/3] 라이선스 파일 생성 (UTF-8 BOM)..." -ForegroundColor Cyan
$eula = Get-Content "$root\src\DiskMigrator.App\Resources\EULA.txt" -Raw -Encoding UTF8
$utf8bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText("$installerDir\EULA-license.txt", $eula, $utf8bom)

Write-Host "[3/3] Inno Setup 컴파일..." -ForegroundColor Cyan
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
Write-Host "`n완료. 설치 프로그램:" -ForegroundColor Green
Get-ChildItem "$out\*.exe" | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}, LastWriteTime | Format-Table -AutoSize
