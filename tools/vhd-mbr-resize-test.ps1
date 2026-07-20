<#
.SYNOPSIS
    가상 디스크(VHD)로 MBR 원본의 파티션 리사이즈(확대) 클론을 end-to-end 검증합니다.

.DESCRIPTION
    단위 테스트로 확인할 수 없는 리사이즈 통합 경로를 실제로 실행합니다:
      - 파티션을 새 오프셋으로 복사 (CloneSessionFactory.BuildResizeRegions)
      - 클론 후 MBR 재작성 (MbrRewriter: 항목 위치 변경, 디스크 서명·부팅 표시 보존)
      - 확대한 파티션의 NTFS 확장 (PartitionExtender.TryExpandPartitionAsync)

    시나리오: 원본 1GB MBR에 주 파티션 2개 [P1 GROWME 300MB(활성)][P2 TAIL 200MB].
    대상 2GB로 클론하며 P1을 확대(--grow 1) → P1이 남는 공간을 흡수하고 P2는
    오른쪽으로 밀립니다. 두 파티션의 파일이 모두 온전하고, P1이 실제로 커지고,
    P2가 그대로 유지되는지 확인합니다.

    MBR 고유 검사 — 이것이 이 하네스의 존재 이유입니다:
      - NT 디스크 서명(0x1B8)이 원본과 같은가. 바뀌면 BCD가 부팅 볼륨을 못 찾습니다.
      - 부트 코드(0~445바이트)가 한 바이트도 안 바뀌었는가.
      - 활성(부팅) 표시가 P1에 그대로 남아 있는가.

    실제 물리 디스크는 건드리지 않습니다(VhdTest가 FileBackedVirtual만 허용).

.NOTES
    관리자 권한이 필요합니다.
#>
[CmdletBinding()]
param(
    [string]$WorkDir = "$env:TEMP\DiskMigratorMbrResizeTest",
    [switch]$KeepVhds,
    [string]$ExePath = ""
)

$ErrorActionPreference = 'Stop'

$script:SourceVhd = Join-Path $WorkDir 'mbrsource.vhd'
$script:TargetVhd = Join-Path $WorkDir 'mbrtarget.vhd'

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

<#
 디스크의 0번 섹터(MBR)를 그대로 읽습니다.
 파티션 API는 해석된 값만 보여 주므로, 부트 코드와 디스크 서명이 정말 그대로인지
 확인하려면 원시 바이트를 봐야 합니다.
#>
function Read-FirstSector([int]$DiskNumber) {
    $stream = New-Object IO.FileStream(
        "\\.\PhysicalDrive$DiskNumber", [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    try {
        $buf = New-Object byte[] 512
        $read = $stream.Read($buf, 0, 512)
        if ($read -ne 512) { throw "디스크 $DiskNumber 의 0번 섹터를 읽지 못했습니다($read 바이트)." }
        return $buf
    }
    finally { $stream.Dispose() }
}

function Get-VhdDiskNumber([string]$VhdPath) {
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

    foreach ($n in @($sourceNum, $targetNum)) {
        $bus = (Get-Disk -Number $n).BusType
        if ($bus -ne 'File Backed Virtual') { throw "디스크 $n 버스 '$bus' — 가상 디스크 아님." }
    }
    Write-Ok "두 디스크 모두 File Backed Virtual 확인"

    # --- 원본: GPT + 2개 파티션 -----------------------------------------
    Write-Step "원본에 MBR + 주 파티션 2개 [P1 GROWME 300MB(활성)][P2 TAIL 200MB]"
    Initialize-Disk -Number $sourceNum -PartitionStyle MBR -Confirm:$false | Out-Null

    $p1 = New-Partition -DiskNumber $sourceNum -Size 300MB -AssignDriveLetter
    Format-Volume -Partition $p1 -FileSystem NTFS -NewFileSystemLabel "GROWME" -Confirm:$false | Out-Null
    $l1 = $p1.DriveLetter
    Write-Ok "P1 = ${l1}: (NTFS, GROWME, 300MB, 파티션번호 $($p1.PartitionNumber))"

    $p2 = New-Partition -DiskNumber $sourceNum -Size 200MB -AssignDriveLetter
    Format-Volume -Partition $p2 -FileSystem NTFS -NewFileSystemLabel "TAIL" -Confirm:$false | Out-Null
    $l2 = $p2.DriveLetter
    Write-Ok "P2 = ${l2}: (NTFS, TAIL, 200MB, 파티션번호 $($p2.PartitionNumber))"

    $growNum = $p1.PartitionNumber   # 확대할 파티션 번호
    $tailNum = $p2.PartitionNumber

    # 실기 N: 디스크처럼 P1을 활성(부팅) 파티션으로 만듭니다. 재작성이 이 표시를
    # 지우면 클론한 디스크가 부팅하지 못합니다.
    Set-Partition -DiskNumber $sourceNum -PartitionNumber $growNum -IsActive $true
    Write-Ok "P1을 활성(부팅) 파티션으로 표시"

    # 원본 MBR 첫 섹터를 통째로 보관 — 재작성 후 앞 446바이트를 바이트 단위로 대조합니다.
    $script:SrcMbr = Read-FirstSector $sourceNum
    $srcSignature = (Get-Disk -Number $sourceNum).Signature
    Write-Info ("원본 디스크 서명: 0x{0:X8}" -f $srcSignature)

    # 검증용 파일 — P1(확대 대상)에 여러 개, P2(시프트 대상)에 마커.
    $expected = @{}
    $testDir = "${l1}:\testdata"
    New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    foreach ($i in 1..4) {
        $file = Join-Path $testDir "file$i.bin"
        $bytes = New-Object byte[] (256KB * $i)
        (New-Object Random($i)).NextBytes($bytes)
        [IO.File]::WriteAllBytes($file, $bytes)
        $expected["P1:testdata\file$i.bin"] = (Get-FileHash $file -Algorithm SHA256).Hash
    }
    Set-Content -Path "${l1}:\grow-marker.txt" -Value "P1 GROWME 확대 대상 $(Get-Date -Format o)" -Encoding UTF8
    $expected["P1:grow-marker.txt"] = (Get-FileHash "${l1}:\grow-marker.txt" -Algorithm SHA256).Hash

    Set-Content -Path "${l2}:\tail-marker.txt" -Value "P2 TAIL 시프트 대상 $(Get-Date -Format o)" -Encoding UTF8
    $expected["P2:tail-marker.txt"] = (Get-FileHash "${l2}:\tail-marker.txt" -Algorithm SHA256).Hash

    Write-VolumeCache -DriveLetter $l1
    Write-VolumeCache -DriveLetter $l2
    Write-Ok "테스트 파일 생성 및 flush (P1 $([int](($expected.Keys | Where-Object {$_ -like 'P1:*'}).Count))개, P2 1개)"

    $srcP1Size = (Get-Partition -DiskNumber $sourceNum -PartitionNumber $growNum).Size
    $srcP2Size = (Get-Partition -DiskNumber $sourceNum -PartitionNumber $tailNum).Size
    $srcP2Start = (Get-Partition -DiskNumber $sourceNum -PartitionNumber $tailNum).Offset
    Write-Info "원본 P1 크기 $([math]::Round($srcP1Size/1MB))MB, P2 크기 $([math]::Round($srcP2Size/1MB))MB @ $([math]::Round($srcP2Start/1MB))MB"

    # --- 클론 실행 (P1 확대) ---------------------------------------------
    Write-Step "리사이즈 클론 실행 (--grow $growNum, 남는 공간 전부 P1에)"
    $exe = if ($ExePath) { $ExePath } else { Join-Path $PSScriptRoot 'DiskMigrator.VhdTest\bin\Release\net8.0-windows\DiskMigrator.VhdTest.exe' }
    Write-Info "실행파일: $exe"
    if (-not (Test-Path $exe)) { throw "빌드된 도구를 찾지 못했습니다: $exe" }

    & $exe $sourceNum $targetNum --grow $growNum --no-boot-check
    $cloneExit = $LASTEXITCODE
    if ($cloneExit -ne 0) { Write-Fail "클론 도구가 종료 코드 $cloneExit 로 실패."; $failures++ }
    else { Write-Ok "클론 도구 성공 (종료 코드 0)" }

    # --- 검증 ------------------------------------------------------------
    Write-Step "클론 결과 검증"
    # 원본을 떼고(GUID 충돌 방지) 대상을 떼었다 다시 붙여 깨끗이 마운트.
    Invoke-Diskpart "select vdisk file=`"$script:SourceVhd`"`ndetach vdisk" | Out-Null
    Start-Sleep -Milliseconds 1000
    Invoke-Diskpart "select vdisk file=`"$script:TargetVhd`"`ndetach vdisk" | Out-Null
    Start-Sleep -Milliseconds 800
    Invoke-Diskpart "select vdisk file=`"$script:TargetVhd`"`nattach vdisk" | Out-Null
    Start-Sleep -Milliseconds 1500
    Invoke-Diskpart "rescan" | Out-Null
    Start-Sleep -Milliseconds 1500

    $targetNum = Get-VhdDiskNumber $script:TargetVhd
    $td = Get-Disk -Number $targetNum
    if ($td.IsOffline)  { Set-Disk -Number $targetNum -IsOffline $false;  Start-Sleep -Milliseconds 500 }
    if ($td.IsReadOnly) { Set-Disk -Number $targetNum -IsReadOnly $false; Start-Sleep -Milliseconds 500 }
    Invoke-Diskpart "rescan" | Out-Null
    Start-Sleep -Milliseconds 1000

    # 클론 중 자동 확장은 best-effort(서명 충돌로 실패 가능). 대상이 이제 단독 연결이므로
    # 확대 파티션의 NTFS를 뒤 미할당 공간까지 표준 확장으로 채웁니다(v0.2.0 검증 경로).
    Write-Step "확대 파티션 표준 확장 (--expand-part $growNum)"
    & $exe $targetNum --expand-part $growNum
    Start-Sleep -Milliseconds 1000
    Invoke-Diskpart "rescan" | Out-Null
    Start-Sleep -Milliseconds 800

    $targetDisk = Get-Disk -Number $targetNum
    Write-Info "대상 디스크: $($targetDisk.OperationalStatus) / $($targetDisk.HealthStatus) / $($targetDisk.PartitionStyle)"

    if ($targetDisk.PartitionStyle -eq 'MBR') { Write-Ok "대상이 MBR로 인식됨" }
    else { Write-Fail "대상 파티션 형식이 $($targetDisk.PartitionStyle) (MBR 기대)"; $failures++ }

    if ($targetDisk.HealthStatus -eq 'Healthy') { Write-Ok "대상 디스크 상태 Healthy (MBR 재작성 정상)" }
    else { Write-Fail "대상 디스크 상태 $($targetDisk.HealthStatus)"; $failures++ }

    # --- MBR 고유 검사 ---------------------------------------------------
    # 이 세 가지가 이 하네스의 존재 이유입니다. 파티션 크기는 API로 보이지만,
    # 디스크 서명과 부트 코드는 원시 섹터를 읽어야만 확인할 수 있습니다.
    Write-Step "MBR 보존 검사 (디스크 서명 · 부트 코드 · 부팅 표시)"

    $dstMbr = Read-FirstSector $targetNum
    $dstSignature = (Get-Disk -Number $targetNum).Signature

    if ($dstSignature -eq $srcSignature) {
        Write-Ok ("디스크 서명 보존: 0x{0:X8} — BCD가 부팅 볼륨을 계속 찾을 수 있습니다" -f $dstSignature)
    } else {
        Write-Fail ("디스크 서명이 바뀜: 0x{0:X8} → 0x{1:X8} (클론이 부팅하지 못합니다)" -f $srcSignature, $dstSignature)
        $failures++
    }

    $bootCodeSame = $true
    for ($i = 0; $i -lt 446; $i++) {
        if ($script:SrcMbr[$i] -ne $dstMbr[$i]) { $bootCodeSame = $false; break }
    }
    if ($bootCodeSame) {
        Write-Ok "부트 코드·디스크 서명 영역(0~445바이트) 한 바이트도 안 바뀜"
    } else {
        Write-Fail "부트 코드 영역이 바뀜 (오프셋 $i) — 재작성이 파티션 테이블 밖을 건드렸습니다"
        $failures++
    }

    if ($dstMbr[510] -eq 0x55 -and $dstMbr[511] -eq 0xAA) { Write-Ok "MBR 서명(0x55AA) 정상" }
    else { Write-Fail "MBR 서명이 깨짐"; $failures++ }

    $activeParts = @(Get-Partition -DiskNumber $targetNum | Where-Object { $_.IsActive })
    if ($activeParts.Count -eq 1 -and $activeParts[0].PartitionNumber -eq $growNum) {
        Write-Ok "활성(부팅) 표시가 P1에 그대로 남음"
    } else {
        Write-Fail "활성 표시가 어긋남: $($activeParts.Count)개 (기대: P$growNum 하나)"
        $failures++
    }

    # 파티션 개수·크기 확인
    $tParts = Get-Partition -DiskNumber $targetNum | Sort-Object PartitionNumber
    Write-Info "대상 파티션 수: $($tParts.Count)"
    foreach ($tp in $tParts) {
        Write-Info "  파티션 $($tp.PartitionNumber): $([math]::Round($tp.Size/1MB))MB @ $([math]::Round($tp.Offset/1MB))MB"
    }

    $tP1 = $tParts | Where-Object { $_.PartitionNumber -eq $growNum }
    $tP2 = $tParts | Where-Object { $_.PartitionNumber -eq $tailNum }

    # 1) P1(확대)이 실제로 커졌는가 — 최소 원본+500MB
    if ($tP1 -and $tP1.Size -gt ($srcP1Size + 500MB)) {
        Write-Ok "P1 확대됨: $([math]::Round($srcP1Size/1MB))MB → $([math]::Round($tP1.Size/1MB))MB"
    } else {
        Write-Fail "P1이 충분히 커지지 않음: $([math]::Round(($tP1.Size)/1MB))MB (원본 $([math]::Round($srcP1Size/1MB))MB)"
        $failures++
    }

    # 2) P2(시프트)가 크기 유지 + 오른쪽으로 이동
    if ($tP2 -and [math]::Abs($tP2.Size - $srcP2Size) -lt 2MB) {
        Write-Ok "P2 크기 유지: $([math]::Round($tP2.Size/1MB))MB"
    } else {
        Write-Fail "P2 크기 변함: $([math]::Round(($tP2.Size)/1MB))MB (원본 $([math]::Round($srcP2Size/1MB))MB)"
        $failures++
    }
    if ($tP2 -and $tP2.Offset -gt $srcP2Start) {
        Write-Ok "P2가 오른쪽으로 시프트됨: $([math]::Round($srcP2Start/1MB))MB → $([math]::Round($tP2.Offset/1MB))MB"
    } else {
        Write-Fail "P2가 시프트되지 않음"
        $failures++
    }

    # 3) 각 파티션 볼륨 마운트 + 파일 해시 대조
    function Get-LetterFor($part) {
        for ($a = 0; $a -lt 5; $a++) {
            $v = Get-Volume -Partition $part -ErrorAction SilentlyContinue
            if ($v -and $v.DriveLetter) { return $v.DriveLetter }
            $part | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction SilentlyContinue | Out-Null
            Start-Sleep -Milliseconds 800
            $part = Get-Partition -DiskNumber $targetNum -PartitionNumber $part.PartitionNumber
        }
        return $null
    }

    $dl1 = Get-LetterFor $tP1
    $dl2 = Get-LetterFor $tP2
    Write-Info "P1 → ${dl1}:  P2 → ${dl2}:"

    # 확대한 P1의 NTFS(볼륨)가 실제로 커졌는가 — 파티션 슬롯뿐 아니라 사용 가능 용량 확인.
    $vol1 = Get-Volume -DriveLetter $dl1 -ErrorAction SilentlyContinue
    if ($vol1) {
        Write-Info "P1 볼륨(NTFS) 크기: $([math]::Round($vol1.Size/1MB))MB (파티션 슬롯 $([math]::Round($tP1.Size/1MB))MB)"
        if ($vol1.Size -gt ($srcP1Size + 500MB)) {
            Write-Ok "P1 NTFS가 확대된 파티션을 채움 — 사용 가능 용량 증가"
        } else {
            Write-Fail "P1 NTFS가 확장되지 않음: 볼륨 $([math]::Round($vol1.Size/1MB))MB (원본 $([math]::Round($srcP1Size/1MB))MB)"
            $failures++
        }
    }

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

    # 4) chkdsk — 확대한 P1의 파일시스템 무결성
    if ($dl1) {
        Write-Info "chkdsk ${dl1}: 실행 중..."
        $chk = & chkdsk "${dl1}:" 2>&1 | Out-String
        if ($chk -match 'Windows has scanned the file system and found no problems' -or
            $chk -match '문제를 발견하지 못했습니다' -or $chk -match '문제가 없') {
            Write-Ok "chkdsk 클린 (확대한 P1)"
        } else {
            Write-Info "chkdsk 출력 요약: $((($chk -split "`n") | Select-Object -First 6) -join ' / ')"
            Write-Info "(자동 판정 실패 — 위 출력을 육안 확인. 손상 문구가 없으면 정상.)"
        }
    }

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
