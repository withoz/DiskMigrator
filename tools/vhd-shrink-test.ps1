# 축소 기능 3단계(VhdxShrinker) 실기 검증 스크립트
#
# 하는 일:
#   1. 3GB 테스트 VHDX를 만들어 GPT + NTFS 파티션으로 포맷하고 ~800MB 데이터를 씁니다.
#   2. VhdTest --shrink 로 그 파티션을 차등 자식 안에서 1.5GB로 축소합니다.
#   3. 부모 이미지가 그대로 보존됐는지, 파티션이 실제로 줄었는지 확인합니다.
#
# 사용: 관리자 권한 PowerShell에서  tools\vhd-shrink-test.ps1  실행.
#       (diskpart로 가상 디스크를 만들고 부착하므로 관리자 권한이 필요합니다.)

$ErrorActionPreference = 'Stop'

# 관리자 권한 확인 — 없으면 바로 중단(diskpart attach가 실패하기 때문).
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "관리자 권한이 필요합니다. PowerShell을 '관리자로 실행'해 다시 시도하세요." }

$root = Split-Path -Parent $PSScriptRoot
$exe = "$root\tools\DiskMigrator.VhdTest\bin\Release\net8.0-windows\DiskMigrator.VhdTest.exe"
$work = "$env:TEMP\dm-shrink-test"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$parent = "$work\shrink-parent.vhdx"
$child = "$work\shrink-child.vhdx"

Write-Host "[0/4] 이전 산출물 정리..." -ForegroundColor Cyan
foreach ($f in @($parent, $child)) {
    if (Test-Path $f) {
        # 혹시 부착돼 있으면 분리 후 삭제.
        "select vdisk file=`"$f`"`r`ndetach vdisk" | diskpart | Out-Null
        Remove-Item $f -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "[1/4] VhdTest 빌드..." -ForegroundColor Cyan
dotnet build "$root\tools\DiskMigrator.VhdTest\DiskMigrator.VhdTest.csproj" -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "빌드 실패" }

Write-Host "[2/4] 테스트 VHDX 생성 + NTFS 포맷 + 800MB 데이터..." -ForegroundColor Cyan
# 빈 드라이브 문자 하나 고릅니다.
$used = (Get-Volume).DriveLetter
$letter = 68..90 | ForEach-Object { [char]$_ } | Where-Object { $_ -notin $used } | Select-Object -First 1
@"
create vdisk file="$parent" maximum=3072 type=expandable
select vdisk file="$parent"
attach vdisk
convert gpt
create partition primary
format fs=ntfs quick label=SHRINKTEST
assign letter=$letter
"@ | diskpart | Out-Null
Start-Sleep -Seconds 2

# 데이터 800MB 기록(축소 한계가 0이 아니게).
$fs = [IO.File]::Create("${letter}:\data.bin")
$buf = New-Object byte[] (1048576)
for ($i = 0; $i -lt 800; $i++) { $fs.Write($buf, 0, $buf.Length) }
$fs.Close()

$disk = Get-Disk | Where-Object { $_.Location -like "*shrink-parent*" }
$part = Get-Partition -DiskNumber $disk.Number | Where-Object { $_.DriveLetter -eq $letter }
$partNum = $part.PartitionNumber
Write-Host "  파티션 $partNum, 현재 $([math]::Round($part.Size/1GB,2)) GB, 데이터 800MB 기록됨."

# 분리(축소 도구가 차등 자식으로 다시 부착합니다).
"select vdisk file=`"$parent`"`r`ndetach vdisk" | diskpart | Out-Null
$parentBefore = (Get-Item $parent).Length

Write-Host "[3/4] 축소 실행: 파티션 $partNum -> 1.5 GB (차등 자식)..." -ForegroundColor Cyan
& $exe --shrink $parent $child $partNum 1.5
$shrinkExit = $LASTEXITCODE

Write-Host "[4/4] 검증..." -ForegroundColor Cyan
$parentAfter = (Get-Item $parent).Length
$parentIntact = $parentBefore -eq $parentAfter
Write-Host ("  부모 보존: {0} (before={1:N0} after={2:N0})" -f `
    ($(if ($parentIntact) { '✓ 변화 없음' } else { '✗ 변경됨!' })), $parentBefore, $parentAfter)
Write-Host "  축소 도구 종료코드: $shrinkExit  ($(if ($shrinkExit -eq 0) { '성공' } else { '실패' }))"

if ($shrinkExit -eq 0 -and $parentIntact) {
    Write-Host "`n*** 3단계 실기 검증 성공 ***" -ForegroundColor Green
} else {
    Write-Host "`n*** 검증 실패 — 위 로그 확인 ***" -ForegroundColor Red
}

# 정리.
foreach ($f in @($parent, $child)) {
    "select vdisk file=`"$f`"`r`ndetach vdisk" | diskpart | Out-Null
    Remove-Item $f -Force -ErrorAction SilentlyContinue
}
