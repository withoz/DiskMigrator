<#
.SYNOPSIS
    가상 디스크(VHD)로 이미지 백업/복원 경로 전체를 end-to-end 검증합니다.

.DESCRIPTION
    실제 물리 디스크는 건드리지 않습니다. 흐름:
      1. 원본 VHD 생성 → GPT + NTFS + 테스트 파일(+해시)
      2. --backup 원본 → image.vhdx (동적 VHDX)
      3. 빈 대상 VHD 생성(원본과 같은 크기)
      4. --restore image.vhdx → 대상 VHD
      5. 대상에서 파일 해시 대조 + 레이블 확인
    복원 도구는 안전을 위해 가상 디스크(File Backed Virtual)에만 씁니다.

.NOTES
    관리자 권한이 필요합니다.
#>
[CmdletBinding()]
param(
    [string]$WorkDir = "$env:TEMP\DiskMigratorImageTest",
    [switch]$KeepVhds,
    [string]$ExePath = ""
)

$ErrorActionPreference = 'Stop'

$script:SourceVhd = Join-Path $WorkDir 'source.vhd'
$script:TargetVhd = Join-Path $WorkDir 'target.vhd'
$script:Image     = Join-Path $WorkDir 'backup.vhdx'

function Write-Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Write-Fail($m) { Write-Host "  [실패] $m" -ForegroundColor Red }
function Write-Info($m) { Write-Host "  $m" -ForegroundColor Gray }

function Invoke-Diskpart([string]$Script) {
    $file = [IO.Path]::GetTempFileName()
    try {
        Set-Content -Path $file -Value $Script -Encoding Ascii
        $out = & diskpart.exe /s $file 2>&1
        if ($LASTEXITCODE -ne 0) { throw "diskpart 실패 (exit $LASTEXITCODE):`n$($out -join "`n")" }
        return $out
    } finally { Remove-Item $file -ErrorAction SilentlyContinue }
}

function Get-VhdDiskNumber([string]$VhdPath) {
    $disk = Get-Disk | Where-Object { $_.Location -eq $VhdPath }
    if (-not $disk) { throw "VHD $VhdPath 에 해당하는 디스크를 찾지 못했습니다." }
    return $disk.Number
}

function Dismount-AllTestVhds {
    foreach ($vhd in @($script:SourceVhd, $script:TargetVhd, $script:Image)) {
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

    $exe = if ($ExePath) { $ExePath } else { Join-Path $PSScriptRoot 'DiskMigrator.VhdTest\bin\Release\net8.0-windows\DiskMigrator.VhdTest.exe' }
    if (-not (Test-Path $exe)) { throw "빌드된 도구를 찾지 못했습니다: $exe" }
    Write-Info "도구: $exe"

    # --- 원본 준비 -------------------------------------------------------
    Write-Step "원본 VHD 생성 (1GB) + GPT + NTFS + 테스트 파일"
    Invoke-Diskpart @"
create vdisk file="$script:SourceVhd" maximum=1024 type=expandable
select vdisk file="$script:SourceVhd"
attach vdisk
"@ | Out-Null
    Start-Sleep -Milliseconds 500
    $sourceNum = Get-VhdDiskNumber $script:SourceVhd
    if ((Get-Disk -Number $sourceNum).BusType -ne 'File Backed Virtual') { throw "원본이 가상 디스크가 아닙니다." }

    Initialize-Disk -Number $sourceNum -PartitionStyle GPT -Confirm:$false
    $part = New-Partition -DiskNumber $sourceNum -UseMaximumSize -AssignDriveLetter
    Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel "IMGSRC" -Confirm:$false | Out-Null
    $srcLetter = $part.DriveLetter

    $testDir = "${srcLetter}:\testdata"
    New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    $expected = @{}
    foreach ($i in 1..5) {
        $f = Join-Path $testDir "file$i.bin"
        $bytes = New-Object byte[] (256KB * $i)
        (New-Object Random($i)).NextBytes($bytes)
        [IO.File]::WriteAllBytes($f, $bytes)
        $expected["testdata\file$i.bin"] = (Get-FileHash $f -Algorithm SHA256).Hash
    }
    Set-Content -Path "${srcLetter}:\hello.txt" -Value "이미지 백업 테스트 $(Get-Date -Format o)" -Encoding UTF8
    $expected["hello.txt"] = (Get-FileHash "${srcLetter}:\hello.txt" -Algorithm SHA256).Hash
    Write-VolumeCache -DriveLetter $srcLetter
    $sourceGuid = (Get-Disk -Number $sourceNum).Guid
    Write-Ok "원본 준비 완료: ${srcLetter}: (디스크 $sourceNum, 파일 $($expected.Count)개, GUID $sourceGuid)"

    # --- 백업 (VSS 스냅샷 + 스마트 클론) ---------------------------------
    Write-Step "백업: 디스크 $sourceNum → $($script:Image)  (--snapshot --skip-unused)"
    & $exe --backup $sourceNum $script:Image --snapshot --skip-unused
    if ($LASTEXITCODE -ne 0) { Write-Fail "백업 도구 exit $LASTEXITCODE"; $failures++ }
    else { Write-Ok "백업 완료 (스냅샷+스마트 클론)" }

    if (Test-Path $script:Image) {
        $imgMb = [math]::Round((Get-Item $script:Image).Length / 1MB, 1)
        Write-Info "이미지 파일 크기(실제 할당): $imgMb MB  (동적 VHDX — 1GB 디스크지만 쓴 블록만)"
    } else { Write-Fail "이미지 파일이 생성되지 않았습니다."; $failures++ }

    # 원본은 이제 필요 없으니 분리(GUID 충돌 방지).
    Invoke-Diskpart "select vdisk file=`"$script:SourceVhd`"`ndetach vdisk" | Out-Null
    Start-Sleep -Milliseconds 800

    # --- 빈 대상 준비 (2GB — 이미지보다 크게 해서 GPT 보정을 실증) --------
    Write-Step "빈 대상 VHD 생성 (2GB, 이미지보다 큼)"
    Invoke-Diskpart @"
create vdisk file="$script:TargetVhd" maximum=2048 type=expandable
select vdisk file="$script:TargetVhd"
attach vdisk
"@ | Out-Null
    Start-Sleep -Milliseconds 500
    $targetNum = Get-VhdDiskNumber $script:TargetVhd
    if ((Get-Disk -Number $targetNum).BusType -ne 'File Backed Virtual') { throw "대상이 가상 디스크가 아닙니다." }
    Write-Ok "빈 대상 준비: 디스크 $targetNum (2GB)"

    # --- 복원 ------------------------------------------------------------
    Write-Step "복원: $($script:Image) → 디스크 $targetNum"
    & $exe --restore $script:Image $targetNum
    if ($LASTEXITCODE -ne 0) { Write-Fail "복원 도구 exit $LASTEXITCODE"; $failures++ }
    else { Write-Ok "복원 완료" }

    # --- 검증 ------------------------------------------------------------
    Write-Step "복원 결과 검증"
    # 서명 충돌 잔재 정리를 위해 대상을 뗐다 다시 붙입니다.
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
    $td = Get-Disk -Number $targetNum

    if ($td.PartitionStyle -eq 'GPT') { Write-Ok "대상이 GPT로 인식됨" }
    else { Write-Fail "대상 파티션 형식이 $($td.PartitionStyle) 입니다 (GPT 기대)"; $failures++ }

    if ($td.Guid -eq $sourceGuid) { Write-Ok "디스크 GUID가 원본과 일치 (섹터 단위 복제 증거)" }
    else { Write-Fail "디스크 GUID 불일치: 원본 $sourceGuid / 대상 $($td.Guid)"; $failures++ }

    # GPT 보정: 대상(2GB)이 이미지(1GB)보다 크므로 백업 헤더가 끝으로 옮겨져 ~1GB가 미할당이어야 함.
    $unalloc = $td.LargestFreeExtent
    Write-Info "대상 미할당 공간: $([math]::Round($unalloc / 1GB, 2)) GB"
    if ($unalloc -gt 900MB) { Write-Ok "미할당 ~1GB 인식 → GPT 백업 헤더가 디스크 끝으로 옮겨짐(보정 성공)" }
    else { Write-Fail "미할당이 $([math]::Round($unalloc / 1MB)) MB 뿐 — GPT 보정이 안 된 것으로 보입니다"; $failures++ }

    # NTFS 볼륨 찾기
    $vol = $null; $vpart = $null
    for ($a = 0; $a -lt 5 -and -not $vol; $a++) {
        foreach ($p in (Get-Partition -DiskNumber $targetNum -ErrorAction SilentlyContinue | Sort-Object Size -Descending)) {
            $v = Get-Volume -Partition $p -ErrorAction SilentlyContinue
            if ($v -and $v.FileSystem -eq 'NTFS') { $vol = $v; $vpart = $p; break }
            if (-not $v -or -not $v.FileSystem) { $p | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction SilentlyContinue | Out-Null }
        }
        if (-not $vol) { Start-Sleep -Milliseconds 1000 }
    }

    if (-not $vol) { Write-Fail "복원된 디스크에서 NTFS 볼륨을 찾지 못했습니다."; $failures++ }
    else {
        $dl = $vol.DriveLetter
        if (-not $dl) { $vpart | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 800; $dl = (Get-Volume -Partition (Get-Partition -DiskNumber $targetNum -PartitionNumber $vpart.PartitionNumber)).DriveLetter }
        Write-Info "복원 볼륨: ${dl}: (레이블 '$($vol.FileSystemLabel)')"
        if ($vol.FileSystemLabel -eq 'IMGSRC') { Write-Ok "볼륨 레이블 일치 (IMGSRC)" }
        else { Write-Fail "볼륨 레이블이 '$($vol.FileSystemLabel)' 입니다"; $failures++ }

        $mis = 0
        foreach ($rel in $expected.Keys) {
            $path = "${dl}:\$rel"
            if (-not (Test-Path $path)) { Write-Fail "파일 없음: $rel"; $mis++; continue }
            if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $expected[$rel]) { Write-Fail "해시 불일치: $rel"; $mis++ }
        }
        if ($mis -eq 0) { Write-Ok "테스트 파일 $($expected.Count)개 모두 해시 일치" } else { $failures += $mis }
    }

    Write-Step "최종 결과"
    if ($failures -eq 0) { Write-Host "  *** 백업/복원 모든 검증 통과 ***" -ForegroundColor Green }
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
    if (-not $KeepVhds) { Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue; Write-Info "작업 폴더 삭제" }
    else { Write-Info "VHD 유지: $WorkDir" }
}

exit $(if ($failures -eq 0) { 0 } else { 1 })
