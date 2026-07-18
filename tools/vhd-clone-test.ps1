<#
.SYNOPSIS
    가상 디스크(VHD)로 DiskMigrator의 클론 경로 전체를 end-to-end 검증합니다.

.DESCRIPTION
    단위 테스트로는 확인할 수 없는 경로를 실제로 실행합니다:
      - 원시 디스크 쓰기 (RawDiskDevice.OpenWrite)
      - 볼륨 잠금/마운트 해제 (FSCTL_LOCK_VOLUME / FSCTL_DISMOUNT_VOLUME)
      - VSS 스냅샷 생성과 스냅샷 장치 읽기
      - GPT 백업 헤더 보정 (대상이 원본보다 큼)

    실제 물리 디스크는 건드리지 않습니다. VhdTest 도구도 버스 종류가
    FileBackedVirtual이 아니면 쓰기를 거부합니다.

.NOTES
    관리자 권한이 필요합니다.
#>
[CmdletBinding()]
param(
    [string]$WorkDir = "$env:TEMP\DiskMigratorVhdTest",
    [switch]$UseSnapshot,
    [switch]$KeepVhds
)

$ErrorActionPreference = 'Stop'

$script:SourceVhd = Join-Path $WorkDir 'source.vhd'
$script:TargetVhd = Join-Path $WorkDir 'target.vhd'

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  [OK]   $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "  [실패] $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "  $msg" -ForegroundColor Gray }

function Invoke-Diskpart([string]$Script) {
    $file = [IO.Path]::GetTempFileName()
    try {
        # diskpart는 ANSI 스크립트 파일을 기대합니다.
        Set-Content -Path $file -Value $Script -Encoding Ascii
        $output = & diskpart.exe /s $file 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "diskpart 실패 (exit $LASTEXITCODE):`n$($output -join "`n")"
        }
        return $output
    }
    finally { Remove-Item $file -ErrorAction SilentlyContinue }
}

function Get-VhdDiskNumber([string]$VhdPath) {
    # Get-Disk의 Location은 VHD 파일의 전체 경로를 담습니다.
    $disk = Get-Disk | Where-Object { $_.Location -eq $VhdPath }
    if (-not $disk) { throw "VHD $VhdPath 에 해당하는 디스크를 찾지 못했습니다." }
    return $disk.Number
}

function Dismount-AllTestVhds {
    foreach ($vhd in @($script:SourceVhd, $script:TargetVhd)) {
        if (Test-Path $vhd) {
            try { Invoke-Diskpart "select vdisk file=`"$vhd`"`ndetach vdisk" | Out-Null } catch { }
        }
    }
}

# ============================================================================

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
      ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Fail "관리자 권한이 필요합니다."
    exit 3
}

$failures = 0

try {
    Write-Step "준비"

    # 이전 실행이 중간에 죽었을 수 있으므로 먼저 정리합니다.
    Dismount-AllTestVhds
    Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
    Write-Info "작업 폴더: $WorkDir"

    # --- VHD 생성 --------------------------------------------------------
    Write-Step "VHD 생성 (원본 1GB, 대상 2GB)"

    Invoke-Diskpart @"
create vdisk file="$script:SourceVhd" maximum=1024 type=expandable
select vdisk file="$script:SourceVhd"
attach vdisk
"@ | Out-Null
    Write-Ok "원본 VHD 생성 및 연결"

    Invoke-Diskpart @"
create vdisk file="$script:TargetVhd" maximum=2048 type=expandable
select vdisk file="$script:TargetVhd"
attach vdisk
"@ | Out-Null
    Write-Ok "대상 VHD 생성 및 연결"

    Start-Sleep -Milliseconds 500

    $sourceNum = Get-VhdDiskNumber $script:SourceVhd
    $targetNum = Get-VhdDiskNumber $script:TargetVhd
    Write-Info "원본 = 디스크 $sourceNum,  대상 = 디스크 $targetNum"

    # 안전 확인: 두 디스크 모두 정말 가상 디스크인가?
    foreach ($n in @($sourceNum, $targetNum)) {
        $bus = (Get-Disk -Number $n).BusType
        if ($bus -ne 'File Backed Virtual') {
            throw "디스크 $n 의 버스가 '$bus' 입니다 — 가상 디스크가 아니므로 중단합니다."
        }
    }
    Write-Ok "두 디스크 모두 File Backed Virtual 확인"

    # --- 원본 내용 준비 --------------------------------------------------
    Write-Step "원본 디스크에 GPT + NTFS + 테스트 파일 준비"

    Initialize-Disk -Number $sourceNum -PartitionStyle GPT -Confirm:$false
    $part = New-Partition -DiskNumber $sourceNum -UseMaximumSize -AssignDriveLetter
    $vol = Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel "CLONESRC" -Confirm:$false
    $srcLetter = $part.DriveLetter
    Write-Ok "원본 포맷 완료: ${srcLetter}: (NTFS, 레이블 CLONESRC)"

    # 검증용 파일 — 클론 후 내용이 그대로인지 해시로 확인합니다.
    $testDir = "${srcLetter}:\testdata"
    New-Item -ItemType Directory -Path $testDir -Force | Out-Null

    $expected = @{}
    foreach ($i in 1..5) {
        $file = Join-Path $testDir "file$i.bin"
        $bytes = New-Object byte[] (256KB * $i)
        (New-Object Random($i)).NextBytes($bytes)
        [IO.File]::WriteAllBytes($file, $bytes)
        $expected["testdata\file$i.bin"] = (Get-FileHash $file -Algorithm SHA256).Hash
    }

    $textFile = "${srcLetter}:\hello.txt"
    Set-Content -Path $textFile -Value "DiskMigrator VHD 클론 테스트 $(Get-Date -Format o)" -Encoding UTF8
    $expected["hello.txt"] = (Get-FileHash $textFile -Algorithm SHA256).Hash

    Write-Ok "테스트 파일 $($expected.Count)개 생성 (총 $([int]((Get-ChildItem $testDir | Measure-Object Length -Sum).Sum / 1MB)) MB+)"

    # 파일 시스템 캐시를 디스크로 내려보냅니다.
    Write-VolumeCache -DriveLetter $srcLetter
    Write-Ok "원본 볼륨 캐시 flush"

    $sourceDiskGuid = (Get-Disk -Number $sourceNum).Guid
    Write-Info "원본 디스크 GUID: $sourceDiskGuid"

    # --- 대상 내용 준비 --------------------------------------------------
    # 대상을 빈 디스크로 두면 잠글 볼륨이 없어 VolumeLock 경로가 아예 실행되지 않습니다.
    # 실제 사용자는 대개 "쓰던 디스크를 재활용"하므로, 그 상황을 그대로 재현합니다.
    Write-Step "대상 디스크에 기존 데이터 배치 (VolumeLock 경로 검증용)"

    Initialize-Disk -Number $targetNum -PartitionStyle MBR -Confirm:$false
    $tPart = New-Partition -DiskNumber $targetNum -UseMaximumSize -AssignDriveLetter
    Format-Volume -Partition $tPart -FileSystem NTFS -NewFileSystemLabel "OLDDATA" -Confirm:$false | Out-Null
    $preLetter = $tPart.DriveLetter

    Set-Content -Path "${preLetter}:\must-be-erased.txt" -Value "이 파일은 클론으로 사라져야 합니다." -Encoding UTF8
    Write-VolumeCache -DriveLetter $preLetter
    Write-Ok "대상에 기존 볼륨 배치: ${preLetter}: (MBR, NTFS, 레이블 OLDDATA)"

    # 볼륨을 실제로 붙잡고 있는 핸들을 하나 열어 둡니다 — 잠금이 진짜로 동작하는지 보려면
    # 파일 시스템이 살아 있는 상태여야 합니다.
    $probeFile = [IO.File]::Open("${preLetter}:\must-be-erased.txt", 'Open', 'Read', 'ReadWrite')
    Write-Info "대상 볼륨의 파일 핸들을 연 채로 클론을 시작합니다"

    # --- 클론 실행 -------------------------------------------------------
    Write-Step "클론 실행 (DiskMigrator.VhdTest)"

    $exe = Join-Path $PSScriptRoot 'DiskMigrator.VhdTest\bin\Release\net8.0-windows\DiskMigrator.VhdTest.exe'
    if (-not (Test-Path $exe)) { throw "빌드된 도구를 찾지 못했습니다: $exe" }

    $cloneArgs = @($sourceNum, $targetNum)
    if ($UseSnapshot) { $cloneArgs += '--snapshot' }

    # 열어 둔 핸들을 닫습니다. 열린 채로 두면 FSCTL_LOCK_VOLUME이 실패해야 정상이고,
    # 그건 별도 시나리오이므로 여기서는 정상 경로를 봅니다.
    $probeFile.Dispose()

    & $exe @cloneArgs
    $cloneExit = $LASTEXITCODE

    if ($cloneExit -ne 0) {
        Write-Fail "클론 도구가 종료 코드 $cloneExit 로 실패했습니다."
        $failures++
    } else {
        Write-Ok "클론 도구 성공 (종료 코드 0)"
    }

    # --- 검증 ------------------------------------------------------------
    Write-Step "클론 결과 검증"

    # 원본과 대상은 이제 디스크 GUID·파티션 GUID가 같습니다. 둘 다 붙어 있으면
    # Windows가 한쪽을 오프라인으로 내리므로, 원본을 떼고 대상만 확인합니다.
    Invoke-Diskpart "select vdisk file=`"$script:SourceVhd`"`ndetach vdisk" | Out-Null
    Write-Info "원본 VHD 분리 (GUID 충돌 방지)"
    Start-Sleep -Milliseconds 1000

    $targetNum = Get-VhdDiskNumber $script:TargetVhd
    $targetDisk = Get-Disk -Number $targetNum

    Write-Info "대상 디스크 상태: $($targetDisk.OperationalStatus) / $($targetDisk.HealthStatus) / $($targetDisk.PartitionStyle)"

    # 1) 파티션 형식이 GPT로 넘어왔는가
    if ($targetDisk.PartitionStyle -eq 'GPT') { Write-Ok "대상이 GPT로 인식됨" }
    else { Write-Fail "대상 파티션 형식이 $($targetDisk.PartitionStyle) 입니다 (GPT 기대)"; $failures++ }

    # 2) 디스크 GUID가 원본과 같은가 — 섹터 단위 복제가 됐다는 증거
    if ($targetDisk.Guid -eq $sourceDiskGuid) { Write-Ok "디스크 GUID가 원본과 일치: $($targetDisk.Guid)" }
    else { Write-Fail "디스크 GUID 불일치: 원본 $sourceDiskGuid / 대상 $($targetDisk.Guid)"; $failures++ }

    # 3) 디스크가 정상인가 — GPT 백업 헤더가 잘못된 위치면 여기서 Warning이 뜹니다
    if ($targetDisk.HealthStatus -eq 'Healthy') { Write-Ok "대상 디스크 상태 Healthy" }
    else { Write-Fail "대상 디스크 상태가 $($targetDisk.HealthStatus) 입니다"; $failures++ }

    # 4) GPT 보정 확인 — 대상이 2배 크므로 약 1GB가 미할당으로 남아 있어야 합니다.
    #    백업 헤더를 옮기지 않았다면 이 공간을 쓸 수 없습니다.
    $unallocated = $targetDisk.LargestFreeExtent
    Write-Info "대상 미할당 공간: $([math]::Round($unallocated / 1GB, 2)) GB"

    if ($unallocated -gt 900MB) {
        Write-Ok "미할당 공간이 정상적으로 인식됨 → GPT 백업 헤더가 디스크 끝으로 옮겨졌습니다"
    } else {
        Write-Fail "미할당 공간이 $([math]::Round($unallocated / 1MB)) MB 뿐입니다 — GPT 보정이 안 된 것으로 보입니다"
        $failures++
    }

    # 5) 볼륨이 마운트되고 파일이 읽히는가
    $targetPart = Get-Partition -DiskNumber $targetNum | Where-Object { $_.Type -ne 'Reserved' } | Select-Object -First 1
    if (-not $targetPart.DriveLetter -or $targetPart.DriveLetter -eq "`0") {
        $targetPart = $targetPart | Add-PartitionAccessPath -AssignDriveLetter -PassThru
        Start-Sleep -Milliseconds 500
        $targetPart = Get-Partition -DiskNumber $targetNum -PartitionNumber $targetPart.PartitionNumber
    }

    $dstLetter = $targetPart.DriveLetter
    Write-Info "대상 볼륨: ${dstLetter}:"

    $targetVol = Get-Volume -DriveLetter $dstLetter
    if ($targetVol.FileSystemLabel -eq 'CLONESRC') { Write-Ok "볼륨 레이블이 원본과 일치 (CLONESRC)" }
    else { Write-Fail "볼륨 레이블이 '$($targetVol.FileSystemLabel)' 입니다"; $failures++ }

    # 6) 파일 내용 해시 대조 — 클론이 실제로 데이터를 옮겼는지 최종 확인
    $mismatches = 0
    foreach ($relative in $expected.Keys) {
        $path = "${dstLetter}:\$relative"
        if (-not (Test-Path $path)) {
            Write-Fail "파일 없음: $relative"
            $mismatches++
            continue
        }

        $hash = (Get-FileHash $path -Algorithm SHA256).Hash
        if ($hash -ne $expected[$relative]) {
            Write-Fail "해시 불일치: $relative"
            $mismatches++
        }
    }

    if ($mismatches -eq 0) { Write-Ok "테스트 파일 $($expected.Count)개 모두 해시 일치" }
    else { $failures += $mismatches }

    # 7) 대상의 기존 데이터가 실제로 사라졌는가
    if (Test-Path "${dstLetter}:\must-be-erased.txt") {
        Write-Fail "대상의 기존 파일이 남아 있습니다 — 클론이 덮어쓰지 않았습니다"
        $failures++
    } else {
        Write-Ok "대상의 기존 데이터가 정상적으로 덮어써짐"
    }

    # --- 결과 ------------------------------------------------------------
    Write-Step "최종 결과"

    if ($failures -eq 0) {
        Write-Host "  *** 모든 검증 통과 ***" -ForegroundColor Green
    } else {
        Write-Host "  *** 검증 실패 $failures 건 ***" -ForegroundColor Red
    }
}
catch {
    Write-Fail "예외: $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    $failures++
}
finally {
    Write-Step "정리"
    Dismount-AllTestVhds
    Write-Info "VHD 분리 완료"

    if (-not $KeepVhds) {
        Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Info "작업 폴더 삭제"
    } else {
        Write-Info "VHD 유지: $WorkDir"
    }
}

exit $(if ($failures -eq 0) { 0 } else { 1 })
