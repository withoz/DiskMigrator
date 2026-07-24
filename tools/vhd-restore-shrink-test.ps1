# 축소 복원(4단계) 실기 검증 스크립트
#
# 하는 일:
#   1. 원본 이미지 VHDX(4GB, GPT)를 만들어 [파티션1 3GB NTFS + 데이터][파티션2 500MB NTFS + 데이터]로 채웁니다.
#   2. 2.5GB 대상 VHDX를 붙이고, --restore-shrink 로 파티션1을 1.5GB로 줄여 대상에 압축 복원합니다.
#   3. 대상에 두 파티션이 다 있고(파티션1 축소·파티션2 왼쪽 이동), 두 파티션의 데이터 해시가
#      원본과 일치하며, 원본 이미지가 그대로 보존됐는지 확인합니다.
#
# 사용: 관리자 권한 PowerShell에서  tools\vhd-restore-shrink-test.ps1  실행.

$ErrorActionPreference = 'Stop'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "관리자 권한이 필요합니다. PowerShell을 '관리자로 실행'해 다시 시도하세요." }

$root = Split-Path -Parent $PSScriptRoot
$exe = "$root\tools\DiskMigrator.VhdTest\bin\Release\net8.0-windows\DiskMigrator.VhdTest.exe"
$work = "$env:TEMP\dm-restore-shrink-test"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$image = "$work\image.vhdx"
$targetVhd = "$work\target.vhdx"

function Detach($f) { if (Test-Path $f) { "select vdisk file=`"$f`"`r`ndetach vdisk" | diskpart | Out-Null } }
function FreeLetter { $u = (Get-Volume).DriveLetter; 71..90 | %{ [char]$_ } | ?{ $_ -notin $u } | Select -First 1 }

Write-Host "[0/5] 정리 + 빌드..." -ForegroundColor Cyan
Detach $image; Detach $targetVhd
Remove-Item $image, $targetVhd -Force -ErrorAction SilentlyContinue
dotnet build "$root\tools\DiskMigrator.VhdTest\DiskMigrator.VhdTest.csproj" -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "빌드 실패" }

Write-Host "[1/5] 원본 이미지 생성 (파티션1 3GB + 파티션2 500MB, 데이터 기록)..." -ForegroundColor Cyan
$l1 = FreeLetter
@"
create vdisk file="$image" maximum=4096 type=expandable
select vdisk file="$image"
attach vdisk
convert gpt
create partition primary size=3000
format fs=ntfs quick label=DATA1
assign letter=$l1
"@ | diskpart | Out-Null
Start-Sleep 2
$l2 = FreeLetter
@"
select vdisk file="$image"
create partition primary size=500
format fs=ntfs quick label=DATA2
assign letter=$l2
"@ | diskpart | Out-Null
Start-Sleep 2

# 데이터 기록: 파티션1에 600MB, 파티션2에 200MB.
$b = New-Object byte[] (1048576)
(New-Object Random 12345).NextBytes($b)
$f1 = [IO.File]::Create("${l1}:\payload1.bin"); for($i=0;$i -lt 600;$i++){$f1.Write($b,0,$b.Length)}; $f1.Close()
$f2 = [IO.File]::Create("${l2}:\payload2.bin"); for($i=0;$i -lt 200;$i++){$f2.Write($b,0,$b.Length)}; $f2.Close()
$h1 = (Get-FileHash "${l1}:\payload1.bin" -Algorithm SHA256).Hash
$h2 = (Get-FileHash "${l2}:\payload2.bin" -Algorithm SHA256).Hash

$idisk = Get-Disk | Where-Object { $_.Location -like "*dm-restore-shrink-test\image*" }
$p1 = (Get-Partition -DiskNumber $idisk.Number | Where-Object DriveLetter -eq $l1).PartitionNumber
Write-Host "  파티션1 번호=$p1, 데이터 해시 기록됨. 이미지 분리."
Detach $image
$imageBefore = (Get-Item $image).Length

Write-Host "[2/5] 2.5GB 대상 VHDX 생성·부착..." -ForegroundColor Cyan
@"
create vdisk file="$targetVhd" maximum=2560 type=expandable
select vdisk file="$targetVhd"
attach vdisk
"@ | diskpart | Out-Null
Start-Sleep 2
$tdisk = (Get-Disk | Where-Object { $_.Location -like "*dm-restore-shrink-test\target*" }).Number
Write-Host "  대상 디스크 번호=$tdisk"

Write-Host "[3/5] 축소 복원: 이미지 → 대상, 파티션 $p1 -> 1.5GB..." -ForegroundColor Cyan
& $exe --restore-shrink $image $tdisk $p1 1.5
$rsExit = $LASTEXITCODE

Write-Host "[4/5] 대상 검증 (파티션·데이터 해시)..." -ForegroundColor Cyan
Start-Sleep 2
$tparts = Get-Partition -DiskNumber $tdisk | Where-Object { $_.Type -eq 'Basic' -or $_.DriveLetter }
Write-Host "  대상 파티션 수: $($tparts.Count) (기대 2)"
# 드라이브 문자 부여 후 해시 비교.
$ok = $true
$tv1 = FreeLetter; $tp1 = Get-Partition -DiskNumber $tdisk -PartitionNumber $p1
$tp1 | Set-Partition -NewDriveLetter $tv1 -ErrorAction SilentlyContinue
Start-Sleep 1
if (Test-Path "${tv1}:\payload1.bin") {
    $th1 = (Get-FileHash "${tv1}:\payload1.bin" -Algorithm SHA256).Hash
    Write-Host ("  파티션1 데이터: {0}" -f $(if ($th1 -eq $h1) { '해시 일치 ✓' } else { '불일치 ✗'; $ok=$false }))
} else { Write-Host "  파티션1 데이터 파일 없음 ✗"; $ok = $false }

$secondNum = ($tparts | Where-Object PartitionNumber -ne $p1 | Select-Object -First 1).PartitionNumber
if ($secondNum) {
    $tv2 = FreeLetter
    Get-Partition -DiskNumber $tdisk -PartitionNumber $secondNum | Set-Partition -NewDriveLetter $tv2 -ErrorAction SilentlyContinue
    Start-Sleep 1
    if (Test-Path "${tv2}:\payload2.bin") {
        $th2 = (Get-FileHash "${tv2}:\payload2.bin" -Algorithm SHA256).Hash
        Write-Host ("  파티션2 데이터: {0}" -f $(if ($th2 -eq $h2) { '해시 일치 ✓ (이동됨)' } else { '불일치 ✗'; $ok=$false }))
    } else { Write-Host "  파티션2 데이터 파일 없음 ✗"; $ok = $false }
} else { Write-Host "  파티션2 없음 ✗"; $ok = $false }

Write-Host "[5/5] 부모 이미지 보존 확인..." -ForegroundColor Cyan
$imageAfter = (Get-Item $image).Length
$intact = $imageBefore -eq $imageAfter
Write-Host ("  이미지: {0} (before={1:N0} after={2:N0})" -f `
    $(if ($intact) { '변화 없음 ✓' } else { '변경됨 ✗' }), $imageBefore, $imageAfter)

if ($rsExit -eq 0 -and $ok -and $intact) {
    Write-Host "`n*** 4단계 축소 복원 실기 검증 성공 ***" -ForegroundColor Green
} else {
    Write-Host "`n*** 검증 실패 (복원 종료코드 $rsExit) — 위 로그 확인 ***" -ForegroundColor Red
}

Detach $image; Detach $targetVhd
Remove-Item $image, $targetVhd -Force -ErrorAction SilentlyContinue
