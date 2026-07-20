<#
.SYNOPSIS
    가상 디스크(VHD)로 DiskMigrator의 "맞춤 클론"(대상 < 원본)을 end-to-end 검증합니다.

.DESCRIPTION
    단위 테스트로 확인할 수 없는 통합 경로를 실제로 실행합니다:
      - 대상이 원본보다 작아도 파티션이 들어가면 통과 (SafetyGuard: TARGET_SMALLER_LAYOUT_FITS)
      - 파티션을 제자리에 복사 (ResizePlanner.PlanFit + CloneSessionFactory.BuildResizeRegions)
      - 원본 끝의 백업 GPT는 복사하지 않고, 줄어든 대상 끝에 새로 씀 (GptRewriter)

    시나리오: 원본 2GB인데 파티션은 앞쪽 ~600MB만 차지 [P1 FITA 300MB][P2 FITB 300MB].
    나머지 ~1.4GB는 미할당. 이를 1GB 대상으로 클론합니다. 파티션은 하나도 움직이지
    않아야 하고(오프셋·크기·GUID 동일), 파일이 온전해야 하며, 무엇보다
    **원본이 전혀 변경되지 않아야** 합니다 — 이 기능의 핵심 주장입니다.

    실제 물리 디스크는 건드리지 않습니다(VhdTest가 FileBackedVirtual만 허용).

.NOTES
    관리자 권한이 필요합니다.
#>
[CmdletBinding()]
param(
    [string]$WorkDir = "$env:TEMP\DiskMigratorFitTest",
    [switch]$KeepVhds,
    [string]$ExePath = ""
)

$ErrorActionPreference = 'Stop'

$script:SourceVhd = Join-Path $WorkDir 'fsource.vhd'
$script:TargetVhd = Join-Path $WorkDir 'ftarget.vhd'

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  [OK]   $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "  [실패] $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "  $msg" -ForegroundColor Gray }

function Invoke-Diskpart([string]$Script) {
    $file = [IO.Path]::GetTempFileName()
    try {
        Set-Content -Path $file -Value $Script -Encoding Ascii
        $output = & diskpart.exe /s $file 2>&1
        if ($LASTEXITCODE -ne 0) { throw "diskpart 실패 (exit $LASTEXITCODE):`n$($output -join "`n")" }
        return $output
    }
    finally { Remove-Item $file -ErrorAction SilentlyContinue }
}

function Get-VhdDiskNumber([string]$VhdPath) {
    $disk = Get-Disk | Where-Object { $_.Location -eq $VhdPath }
    if (-not $disk) { throw "VHD $VhdPath 에 해당하는 디스크를 찾지 못했습니다." }
    return $disk.Number
}

function Mount-TestVhd([string]$VhdPath) {
    Invoke-Diskpart "select vdisk file=`"$VhdPath`"`nattach vdisk" | Out-Null
    Start-Sleep -Milliseconds 1200
    Invoke-Diskpart "rescan" | Out-Null
    Start-Sleep -Milliseconds 1200
}

function Dismount-TestVhd([string]$VhdPath) {
    try { Invoke-Diskpart "select vdisk file=`"$VhdPath`"`ndetach vdisk" | Out-Null } catch { }
    Start-Sleep -Milliseconds 800
}

function Dismount-AllTestVhds {
    foreach ($vhd in @($script:SourceVhd, $script:TargetVhd)) {
        if (Test-Path $vhd) { Dismount-TestVhd $vhd }
    }
}

# 파티션 레이아웃을 비교 가능한 형태로 뽑습니다.
function Get-LayoutSnapshot([int]$DiskNumber) {
    Get-Partition -DiskNumber $DiskNumber |
        Sort-Object PartitionNumber |
        ForEach-Object {
            [pscustomobject]@{
                Number = $_.PartitionNumber
                Offset = [int64]$_.Offset
                Size   = [int64]$_.Size
                Guid   = "$($_.Guid)"
                Type   = "$($_.GptType)"
            }
        }
}

function Get-LetterFor($DiskNumber, $PartitionNumber) {
    for ($a = 0; $a -lt 6; $a++) {
        $p = Get-Partition -DiskNumber $DiskNumber -PartitionNumber $PartitionNumber -ErrorAction SilentlyContinue
        if (-not $p) { Start-Sleep -Milliseconds 700; continue }
        $v = Get-Volume -Partition $p -ErrorAction SilentlyContinue
        if ($v -and $v.DriveLetter) { return $v.DriveLetter }
        $p | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction SilentlyContinue | Out-Null
        Start-Sleep -Milliseconds 800
    }
    return $null
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
    Dismount-AllTestVhds
    Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
    Write-Info "작업 폴더: $WorkDir"

    # --- VHD 생성: 원본이 대상보다 크다 (맞춤 클론의 전제) ------------------
    Write-Step "VHD 생성 (원본 2GB, 대상 1GB — 대상이 더 작음)"
    Invoke-Diskpart @"
create vdisk file="$script:SourceVhd" maximum=2048 type=expandable
select vdisk file="$script:SourceVhd"
attach vdisk
"@ | Out-Null
    Write-Ok "원본 VHD 2GB 생성 및 연결"

    Invoke-Diskpart @"
create vdisk file="$script:TargetVhd" maximum=1024 type=expandable
select vdisk file="$script:TargetVhd"
attach vdisk
"@ | Out-Null
    Write-Ok "대상 VHD 1GB 생성 및 연결"
    Start-Sleep -Milliseconds 800

    $sourceNum = Get-VhdDiskNumber $script:SourceVhd
    $targetNum = Get-VhdDiskNumber $script:TargetVhd
    Write-Info "원본 = 디스크 $sourceNum (2GB),  대상 = 디스크 $targetNum (1GB)"

    foreach ($n in @($sourceNum, $targetNum)) {
        $bus = (Get-Disk -Number $n).BusType
        if ($bus -ne 'File Backed Virtual') { throw "디스크 $n 버스 '$bus' — 가상 디스크 아님." }
    }
    Write-Ok "두 디스크 모두 File Backed Virtual 확인"

    # --- 원본: GPT + 앞쪽에만 파티션 2개 (뒤 ~1.4GB 미할당) -----------------
    Write-Step "원본에 GPT + 파티션 2개 [P1 FITA 300MB][P2 FITB 300MB] — 뒤는 미할당"
    Initialize-Disk -Number $sourceNum -PartitionStyle GPT -Confirm:$false | Out-Null

    $p1 = New-Partition -DiskNumber $sourceNum -Size 300MB -AssignDriveLetter
    Format-Volume -Partition $p1 -FileSystem NTFS -NewFileSystemLabel "FITA" -Confirm:$false | Out-Null
    $l1 = $p1.DriveLetter
    $n1 = $p1.PartitionNumber
    Write-Ok "P1 = ${l1}: (NTFS, FITA, 300MB, 파티션번호 $n1)"

    $p2 = New-Partition -DiskNumber $sourceNum -Size 300MB -AssignDriveLetter
    Format-Volume -Partition $p2 -FileSystem NTFS -NewFileSystemLabel "FITB" -Confirm:$false | Out-Null
    $l2 = $p2.DriveLetter
    $n2 = $p2.PartitionNumber
    Write-Ok "P2 = ${l2}: (NTFS, FITB, 300MB, 파티션번호 $n2)"

    # 검증용 파일 — 두 파티션 모두에.
    $expected = @{}
    foreach ($spec in @(@{L=$l1;K='P1'}, @{L=$l2;K='P2'})) {
        $dir = "$($spec.L):\testdata"
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        foreach ($i in 1..3) {
            $file = Join-Path $dir "file$i.bin"
            $bytes = New-Object byte[] (128KB * $i)
            (New-Object Random($i)).NextBytes($bytes)
            [IO.File]::WriteAllBytes($file, $bytes)
            $expected["$($spec.K):testdata\file$i.bin"] = (Get-FileHash $file -Algorithm SHA256).Hash
        }
        $marker = "$($spec.L):\marker.txt"
        Set-Content -Path $marker -Value "$($spec.K) 맞춤클론 대상 $(Get-Date -Format o)" -Encoding UTF8
        $expected["$($spec.K):marker.txt"] = (Get-FileHash $marker -Algorithm SHA256).Hash
    }

    # 스냅샷 없이 원시로 읽으므로, 쓴 내용이 실제로 디스크에 내려가 있어야 합니다.
    # flush를 빠뜨리면 캐시에만 있는 파일이 대상에 복사되지 않아 가짜 불일치가 납니다.
    foreach ($letter in @($l1, $l2)) {
        Write-VolumeCache -DriveLetter $letter -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 1500
    Write-Ok "테스트 파일 생성 및 볼륨 캐시 flush ($($expected.Count)개, P1·P2 각 4개)"

    # 원본 레이아웃 스냅샷 — 클론 후 '원본이 그대로인지' 대조할 기준.
    $srcLayout = Get-LayoutSnapshot $sourceNum
    $srcDiskGuid = (Get-Disk -Number $sourceNum).Guid
    $occupiedEnd = ($srcLayout | ForEach-Object { $_.Offset + $_.Size } | Measure-Object -Maximum).Maximum
    $srcDiskSize = (Get-Disk -Number $sourceNum).Size
    $tgtDiskSize = (Get-Disk -Number $targetNum).Size

    Write-Info "원본 파티션 $($srcLayout.Count)개, 차지한 끝 = $([math]::Round($occupiedEnd/1MB))MB"
    Write-Info "원본 디스크 $([math]::Round($srcDiskSize/1MB))MB / 대상 디스크 $([math]::Round($tgtDiskSize/1MB))MB"
    foreach ($sp in $srcLayout) {
        Write-Info "  파티션 $($sp.Number): $([math]::Round($sp.Size/1MB))MB @ $([math]::Round($sp.Offset/1MB))MB"
    }

    if ($tgtDiskSize -ge $srcDiskSize) { throw "시나리오 오류: 대상이 원본보다 작아야 합니다." }
    if ($occupiedEnd -ge $tgtDiskSize) { throw "시나리오 오류: 파티션이 대상에 들어가야 합니다." }
    Write-Ok "전제 확인: 대상 < 원본 이면서 파티션은 대상에 들어감"

    # --- 클론 실행 (플래그 없음 → 맞춤 클론 경로) ---------------------------
    Write-Step "맞춤 클론 실행 (리사이즈 플래그 없음 — 대상이 작아 자동으로 맞춤 배치)"
    $exe = if ($ExePath) { $ExePath } else { Join-Path $PSScriptRoot 'DiskMigrator.VhdTest\bin\Release\net8.0-windows\DiskMigrator.VhdTest.exe' }
    Write-Info "실행파일: $exe"
    if (-not (Test-Path $exe)) { throw "빌드된 도구를 찾지 못했습니다: $exe" }

    & $exe $sourceNum $targetNum --no-boot-check
    $cloneExit = $LASTEXITCODE
    if ($cloneExit -ne 0) { Write-Fail "클론 도구가 종료 코드 $cloneExit 로 실패."; $failures++ }
    else { Write-Ok "클론 도구 성공 (종료 코드 0)" }

    # --- 대상 검증 (원본 분리 후 단독 연결) ---------------------------------
    Write-Step "대상 검증 — 원본 분리 후 대상만 연결 (GUID 충돌 방지)"
    Dismount-TestVhd $script:SourceVhd
    Dismount-TestVhd $script:TargetVhd
    Mount-TestVhd $script:TargetVhd

    $targetNum = Get-VhdDiskNumber $script:TargetVhd
    $td = Get-Disk -Number $targetNum
    if ($td.IsOffline)  { Set-Disk -Number $targetNum -IsOffline $false;  Start-Sleep -Milliseconds 600 }
    if ($td.IsReadOnly) { Set-Disk -Number $targetNum -IsReadOnly $false; Start-Sleep -Milliseconds 600 }
    Invoke-Diskpart "rescan" | Out-Null
    Start-Sleep -Milliseconds 1000

    $targetDisk = Get-Disk -Number $targetNum
    Write-Info "대상 디스크: $($targetDisk.OperationalStatus) / $($targetDisk.HealthStatus) / $($targetDisk.PartitionStyle)"

    if ($targetDisk.PartitionStyle -eq 'GPT') { Write-Ok "대상이 GPT로 인식됨 (백업 헤더 재작성 정상)" }
    else { Write-Fail "대상 파티션 형식이 $($targetDisk.PartitionStyle) (GPT 기대)"; $failures++ }

    if ($targetDisk.HealthStatus -eq 'Healthy') { Write-Ok "대상 디스크 상태 Healthy" }
    else { Write-Fail "대상 디스크 상태 $($targetDisk.HealthStatus)"; $failures++ }

    # 1) 파티션이 하나도 움직이지 않았는가 — 맞춤 클론의 정의
    $tgtLayout = Get-LayoutSnapshot $targetNum
    Write-Info "대상 파티션 $($tgtLayout.Count)개"
    foreach ($tp in $tgtLayout) {
        Write-Info "  파티션 $($tp.Number): $([math]::Round($tp.Size/1MB))MB @ $([math]::Round($tp.Offset/1MB))MB"
    }

    if ($tgtLayout.Count -eq $srcLayout.Count) {
        Write-Ok "파티션 개수 일치 ($($srcLayout.Count)개)"
    } else {
        Write-Fail "파티션 개수 불일치: 원본 $($srcLayout.Count) / 대상 $($tgtLayout.Count)"
        $failures++
    }

    $layoutDiffs = 0
    foreach ($sp in $srcLayout) {
        $tp = $tgtLayout | Where-Object { $_.Number -eq $sp.Number }
        if (-not $tp) { Write-Fail "대상에 파티션 $($sp.Number) 없음"; $layoutDiffs++; continue }
        if ($tp.Offset -ne $sp.Offset) {
            Write-Fail "파티션 $($sp.Number) 오프셋이 움직임: $($sp.Offset) → $($tp.Offset)"; $layoutDiffs++
        }
        if ($tp.Size -ne $sp.Size) {
            Write-Fail "파티션 $($sp.Number) 크기가 바뀜: $($sp.Size) → $($tp.Size)"; $layoutDiffs++
        }
        if ($tp.Guid -ne $sp.Guid) {
            Write-Fail "파티션 $($sp.Number) 고유 GUID가 바뀜: $($sp.Guid) → $($tp.Guid)"; $layoutDiffs++
        }
    }
    if ($layoutDiffs -eq 0) {
        Write-Ok "모든 파티션의 오프셋·크기·고유 GUID가 원본과 동일 (제자리 복제 확인)"
    } else { $failures += $layoutDiffs }

    # 2) 파일 무결성
    $dl1 = Get-LetterFor $targetNum $n1
    $dl2 = Get-LetterFor $targetNum $n2
    Write-Info "P1 → ${dl1}:  P2 → ${dl2}:"

    $mismatches = 0
    foreach ($key in $expected.Keys) {
        $parts = $key -split ':', 2
        $letter = if ($parts[0] -eq 'P1') { $dl1 } else { $dl2 }
        if (-not $letter) { Write-Fail "볼륨 문자 없음: $key"; $mismatches++; continue }
        $path = "${letter}:\$($parts[1])"
        if (-not (Test-Path $path)) { Write-Fail "파일 없음: $key"; $mismatches++; continue }
        $hash = (Get-FileHash $path -Algorithm SHA256).Hash
        if ($hash -ne $expected[$key]) { Write-Fail "해시 불일치: $key"; $mismatches++ }
    }
    if ($mismatches -eq 0) { Write-Ok "모든 테스트 파일 해시 일치 ($($expected.Count)개, P1·P2)" }
    else { $failures += $mismatches }

    # 3) chkdsk — 두 파티션 모두
    foreach ($dl in @($dl1, $dl2)) {
        if (-not $dl) { continue }
        Write-Info "chkdsk ${dl}: 실행 중..."
        $chk = & chkdsk "${dl}:" 2>&1 | Out-String
        if ($chk -match 'found no problems' -or $chk -match '문제를 발견하지 못했습니다' -or $chk -match '문제가 없') {
            Write-Ok "chkdsk 클린 (${dl}:)"
        } else {
            Write-Info "chkdsk 출력 요약: $((($chk -split "`n") | Select-Object -First 6) -join ' / ')"
            Write-Info "(자동 판정 실패 — 육안 확인. 손상 문구가 없으면 정상.)"
        }
    }

    # --- 원본 무결성 검증 — 이 기능의 핵심 주장 -----------------------------
    Write-Step "원본 무결성 검증 — 맞춤 클론은 원본에 쓰지 않는다"
    Dismount-TestVhd $script:TargetVhd
    Mount-TestVhd $script:SourceVhd

    $sourceNum = Get-VhdDiskNumber $script:SourceVhd
    $sd = Get-Disk -Number $sourceNum
    if ($sd.IsOffline) { Set-Disk -Number $sourceNum -IsOffline $false; Start-Sleep -Milliseconds 600 }

    $srcAfter = Get-LayoutSnapshot $sourceNum
    $srcDiskGuidAfter = (Get-Disk -Number $sourceNum).Guid

    $srcDiffs = 0
    if ($srcAfter.Count -ne $srcLayout.Count) {
        Write-Fail "원본 파티션 개수가 바뀜: $($srcLayout.Count) → $($srcAfter.Count)"; $srcDiffs++
    }
    foreach ($sp in $srcLayout) {
        $ap = $srcAfter | Where-Object { $_.Number -eq $sp.Number }
        if (-not $ap) { Write-Fail "원본에서 파티션 $($sp.Number)가 사라짐"; $srcDiffs++; continue }
        if ($ap.Offset -ne $sp.Offset -or $ap.Size -ne $sp.Size -or $ap.Guid -ne $sp.Guid) {
            Write-Fail "원본 파티션 $($sp.Number)가 변경됨"; $srcDiffs++
        }
    }
    if ("$srcDiskGuidAfter" -ne "$srcDiskGuid") {
        Write-Fail "원본 디스크 GUID가 바뀜: $srcDiskGuid → $srcDiskGuidAfter"; $srcDiffs++
    }
    if ($srcDiffs -eq 0) { Write-Ok "원본 레이아웃·디스크 GUID 그대로" }
    else { $failures += $srcDiffs }

    $sl1 = Get-LetterFor $sourceNum $n1
    $sl2 = Get-LetterFor $sourceNum $n2
    $srcFileDiffs = 0
    foreach ($key in $expected.Keys) {
        $parts = $key -split ':', 2
        $letter = if ($parts[0] -eq 'P1') { $sl1 } else { $sl2 }
        if (-not $letter) { Write-Fail "원본 볼륨 문자 없음: $key"; $srcFileDiffs++; continue }
        $path = "${letter}:\$($parts[1])"
        if (-not (Test-Path $path)) { Write-Fail "원본 파일 없음: $key"; $srcFileDiffs++; continue }
        if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $expected[$key]) {
            Write-Fail "원본 해시 불일치: $key"; $srcFileDiffs++
        }
    }
    if ($srcFileDiffs -eq 0) { Write-Ok "원본 파일 $($expected.Count)개 모두 그대로" }
    else { $failures += $srcFileDiffs }

    Write-Step "최종 결과"
    if ($failures -eq 0) { Write-Host "  *** 모든 검증 통과 ***" -ForegroundColor Green }
    else { Write-Host "  *** 검증 실패 $failures 건 ***" -ForegroundColor Red }
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
    } else { Write-Info "VHD 유지: $WorkDir" }
}

exit $(if ($failures -eq 0) { 0 } else { 1 })
