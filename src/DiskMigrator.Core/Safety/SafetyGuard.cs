using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Partitioning;

namespace DiskMigrator.Core.Safety;

/// <summary>
/// 클론 작업을 시작하기 전에 대상 디스크가 정말로 지워도 되는 디스크인지 검증합니다.
/// </summary>
/// <remarks>
/// 이 클래스가 이 프로그램의 마지막 방어선입니다. 여기서 놓치면 사용자의 데이터가
/// 통째로 사라집니다. 그래서 규칙은 "의심스러우면 차단" 쪽으로 편향돼 있고,
/// 판정에 필요한 모든 입력은 순수 데이터(DiskInfo)라 단위 테스트로 전부 검증할 수 있습니다.
/// </remarks>
public static class SafetyGuard
{
    // 오류 코드 — UI/로그/테스트가 문자열 대신 이 상수를 참조합니다.
    public const string CodeNotElevated = "NOT_ELEVATED";
    public const string CodeSameDisk = "SAME_DISK";
    public const string CodeTargetIsSystemDisk = "TARGET_IS_SYSTEM_DISK";
    public const string CodeTargetIsBootDisk = "TARGET_IS_BOOT_DISK";
    public const string CodeTargetHasPageFile = "TARGET_HAS_PAGEFILE";
    public const string CodeTargetReadOnly = "TARGET_READ_ONLY";
    public const string CodeTargetTooSmall = "TARGET_TOO_SMALL";
    public const string CodeTargetSmallerLayoutFits = "TARGET_SMALLER_LAYOUT_FITS";
    public const string CodeTargetSmallerShrink = "TARGET_SMALLER_SHRINK";
    public const string CodeSectorSizeMismatch = "SECTOR_SIZE_MISMATCH";
    public const string CodeTargetHasData = "TARGET_HAS_DATA";
    public const string CodeTargetLarger = "TARGET_LARGER";
    public const string CodeSourceIsLiveSystem = "SOURCE_IS_LIVE_SYSTEM";
    public const string CodeSourceOverUsb = "SOURCE_OVER_USB";
    public const string CodeTargetOverUsb = "TARGET_OVER_USB";
    public const string CodeSourceBiosOnly = "SOURCE_BIOS_ONLY";
    public const string CodeSourceHibernated = "SOURCE_HIBERNATED";

    /// <summary>
    /// 원본 → 대상 전체 섹터 클론의 안전성을 평가합니다.
    /// </summary>
    /// <param name="source">읽을 디스크.</param>
    /// <param name="target">덮어쓸 디스크. 이 디스크의 모든 데이터가 파괴됩니다.</param>
    /// <param name="isElevated">현재 프로세스가 관리자 권한인지.</param>
    /// <param name="useSnapshot">원본이 실행 중 시스템 디스크일 때 VSS 스냅샷을 쓸 것인지.</param>
    public static SafetyReport Evaluate(
        DiskInfo source,
        DiskInfo target,
        bool isElevated,
        bool useSnapshot = false,
        bool sourceHibernated = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var issues = new List<SafetyIssue>();

        if (!isElevated)
        {
            issues.Add(new SafetyIssue(SafetySeverity.Blocker, CodeNotElevated,
                Strings.Get("SafeNotElevated")));
        }

        // --- 절대 차단 규칙 ---------------------------------------------------

        // 장치 번호는 재부팅이나 장치 재연결로 바뀔 수 있으므로 모델+시리얼+크기까지 함께 봅니다.
        if (IsSameDisk(source, target))
        {
            issues.Add(new SafetyIssue(SafetySeverity.Blocker, CodeSameDisk,
                Strings.Get("SafeSameDisk")));
        }

        if (target.IsSystemDisk)
        {
            issues.Add(new SafetyIssue(SafetySeverity.Blocker, CodeTargetIsSystemDisk,
                Strings.Format("SafeTargetIsSystemFmt", target.DeviceNumber, target.Model)));
        }

        if (target.IsBootDisk)
        {
            issues.Add(new SafetyIssue(SafetySeverity.Blocker, CodeTargetIsBootDisk,
                Strings.Format("SafeTargetIsBootFmt", target.DeviceNumber, target.Model)));
        }

        if (target.HasPageFile)
        {
            issues.Add(new SafetyIssue(SafetySeverity.Blocker, CodeTargetHasPageFile,
                Strings.Format("SafeTargetHasPageFileFmt", target.DeviceNumber, target.Model)));
        }

        if (target.IsReadOnly)
        {
            issues.Add(new SafetyIssue(SafetySeverity.Blocker, CodeTargetReadOnly,
                Strings.Get("SafeTargetReadOnly")));
        }

        // 대상이 원본보다 작아도, 원본의 파티션이 모두 대상 안에 들어가면 복제할 수 있습니다.
        // 뒤쪽이 비어 있는 디스크(예: 1TB에 파티션은 200GB만)가 흔한데, 디스크 전체 크기로만
        // 판정하면 실제로는 들어가는 조합까지 막게 됩니다. 이때는 파티션을 제자리에 복사하고
        // GPT 백업 헤더만 줄어든 끝으로 다시 씁니다 — 원본에는 쓰지 않습니다.
        //
        // 파티션 자체가 대상보다 큰 경우(진짜 파일시스템 축소)는 여전히 차단합니다.
        if (target.SizeBytes < source.SizeBytes)
        {
            bool layoutFits =
                source.PartitionStyle == PartitionStyle.Gpt &&
                source.Partitions.Count > 0 &&
                ResizePlanner.LayoutFitsIn(source.Partitions, target.SizeBytes);

            if (layoutFits)
            {
                var dropped = source.SizeBytes - ResizePlanner.MinimumTargetSize(source.Partitions);
                issues.Add(new SafetyIssue(SafetySeverity.RequiresConfirmation, CodeTargetSmallerLayoutFits,
                    Strings.Format("SafeTargetSmallerLayoutFitsFmt",
                        FormatSize(source.SizeBytes), FormatSize(target.SizeBytes), FormatSize(dropped))));
            }
            else
            {
                // 파티션이 제자리로는 안 들어가도, 가장 큰 NTFS 파티션을 줄이면 들어가는 경우가
                // 있습니다(예: 1TB 시스템 → 500GB SSD, 실사용 300GB). 이때는 차단하는 대신
                // 자동 축소 클론(내부적으로 백업→축소→복원, 원본 무수정)을 확인 후 진행합니다.
                // GPT 원본만 — 축소 복원의 재배치·테이블 재작성 경로가 GPT를 전제합니다.
                var shrink = source.PartitionStyle == PartitionStyle.Gpt
                    ? ShrinkClonePlanner.Evaluate(source.Partitions, target.SizeBytes, out _)
                    : null;

                if (shrink is not null)
                {
                    issues.Add(new SafetyIssue(SafetySeverity.RequiresConfirmation, CodeTargetSmallerShrink,
                        Strings.Format("SafeTargetSmallerShrinkFmt",
                            shrink.PartitionNumber,
                            FormatSize(shrink.CurrentBytes), FormatSize(shrink.NewBytes),
                            shrink.EstimatedUsedBytes >= 0 ? FormatSize(shrink.EstimatedUsedBytes) : "?")));
                }
                else
                {
                    var shortfall = source.SizeBytes - target.SizeBytes;
                    string detail = source.PartitionStyle == PartitionStyle.Gpt && source.Partitions.Count > 0
                        ? Strings.Format("SafeTooSmallDetailShrinkFmt",
                            FormatSize(ResizePlanner.MinimumTargetSize(source.Partitions)))
                        : Strings.Get("SafeTooSmallDetailSectors");

                    issues.Add(new SafetyIssue(SafetySeverity.Blocker, CodeTargetTooSmall,
                        Strings.Format("SafeTargetTooSmallFmt",
                            FormatSize(shortfall), FormatSize(source.SizeBytes), FormatSize(target.SizeBytes), detail)));
                }
            }
        }

        if (source.LogicalSectorSize != target.LogicalSectorSize)
        {
            issues.Add(new SafetyIssue(SafetySeverity.Blocker, CodeSectorSizeMismatch,
                Strings.Format("SafeSectorSizeMismatchFmt", source.LogicalSectorSize, target.LogicalSectorSize)));
        }

        // --- 사용자 확인이 필요한 사항 ----------------------------------------

        if (target.HasExistingData)
        {
            var partitionSummary = string.Join(", ", target.Partitions.Select(p => p.ToString()));
            issues.Add(new SafetyIssue(SafetySeverity.RequiresConfirmation, CodeTargetHasData,
                Strings.Format("SafeTargetHasDataFmt",
                    target.DeviceNumber, target.Model, target.Partitions.Count, partitionSummary)));
        }

        // --- 절대 차단: 실행 중 시스템 디스크를 스냅샷 없이 복제 --------------
        //
        // 실기에서 이 조합이 실제로 깨진 클론을 만들었습니다. VSS가 로드되지 않아 스냅샷
        // 없이 라이브 시스템 디스크를 34분간 복사했더니, 복사 중 계속 바뀐 부분 2097군데가
        // 검증에서 불일치로 잡혔습니다. 앞부분과 뒷부분의 시점이 어긋난 클론은 부팅되지
        // 않거나 파일시스템 오류를 냅니다.
        //
        // 이건 "경고 후 진행"으로 둘 문제가 아니라 결과가 예측 가능하게 나쁜 경우이므로
        // 차단합니다. 스냅샷 없이 시스템 디스크를 복제하려면 WinPE 등으로 부팅해 그 디스크가
        // 실행 중이 아닌 상태에서 오프라인 클론해야 합니다.
        if (source.IsSystemDisk && !useSnapshot)
        {
            issues.Add(new SafetyIssue(SafetySeverity.Blocker, CodeSourceIsLiveSystem,
                Strings.Get("SafeSourceLiveSystem")));
        }

        if (target.SizeBytes > source.SizeBytes)
        {
            var surplus = target.SizeBytes - source.SizeBytes;
            issues.Add(new SafetyIssue(SafetySeverity.Info, CodeTargetLarger,
                Strings.Format("SafeTargetLargerFmt", FormatSize(surplus))));
        }

        if (source.BusType == DiskBusType.Usb)
        {
            issues.Add(new SafetyIssue(SafetySeverity.Info, CodeSourceOverUsb,
                Strings.Get("SafeSourceOverUsb")));
        }

        if (target.BusType == DiskBusType.Usb)
        {
            issues.Add(new SafetyIssue(SafetySeverity.Info, CodeTargetOverUsb,
                Strings.Get("SafeTargetOverUsb")));
        }

        // --- 복제는 되지만 부팅이 안 될 조합 ----------------------------------
        //
        // 도구가 "복제했습니다"까지만 말하고 "부팅됩니다"는 사용자가 알아내야 했습니다.
        // 실기에서 MBR 원본을 NVMe로 옮기고 나서야 부팅이 불가능함을 알게 되는 일이
        // 있었습니다. 원본만 봐도 시작 전에 말할 수 있는 것들입니다.

        if (source.IsBiosOnlyBootLayout)
        {
            issues.Add(new SafetyIssue(SafetySeverity.Warning, CodeSourceBiosOnly,
                Strings.Get("SafeSourceBiosOnly")));
        }

        if (sourceHibernated)
        {
            issues.Add(new SafetyIssue(SafetySeverity.Warning, CodeSourceHibernated,
                Strings.Get("SafeSourceHibernated")));
        }

        return new SafetyReport { Issues = issues };
    }

    /// <summary>
    /// 두 디스크가 물리적으로 같은 디스크인지 판정합니다.
    /// </summary>
    /// <remarks>
    /// 장치 번호가 1차 기준입니다. 시리얼 비교는 보조 수단인데, 여기에 함정이 있습니다:
    /// 저가 USB 인클로저는 시리얼을 "00000000000000000000" 같은 더미 값으로 보고하며,
    /// 서로 다른 제품이 같은 더미 값을 씁니다. 실제로 이 코드를 실기에서 돌렸을 때
    /// 2.73TB 디스크와 3.64TB 디스크가 같은 더미 시리얼을 보고해 "동일 디스크"로
    /// 오판되어 정상적인 클론이 차단됐습니다.
    ///
    /// 그래서 (1) 의미 있는 시리얼일 때만 비교하고, (2) 크기와 모델까지 모두 같아야 동일로 봅니다.
    /// 같은 물리 디스크라면 크기와 모델은 반드시 같으므로, 하나라도 다르면 확실히 다른 디스크입니다.
    ///
    /// 모델까지 보는 이유도 실기에서 나왔습니다: 같은 모델의 USB 인클로저 두 개가 안에 든
    /// 드라이브와 무관하게 똑같은 시리얼 "DD56419883A62"를 보고했습니다. Samsung 990 EVO와
    /// Crucial CT1000P3라는 전혀 다른 디스크가, 용량마저 같아서(1TB) 시리얼과 크기만으로는
    /// 구분되지 않았습니다. 이 시리얼은 더미 값처럼 생기지도 않아 자리표시자 필터도 통과합니다.
    /// </remarks>
    public static bool IsSameDisk(DiskInfo a, DiskInfo b)
    {
        if (a.DeviceNumber == b.DeviceNumber) return true;

        // 크기가 다르면 같은 물리 디스크일 수 없습니다.
        if (a.SizeBytes != b.SizeBytes) return false;

        // 모델이 다르면 같은 물리 디스크일 수 없습니다.
        if (!string.Equals(a.Model.Trim(), b.Model.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

        return IsMeaningfulSerial(a.SerialNumber) &&
               IsMeaningfulSerial(b.SerialNumber) &&
               string.Equals(a.SerialNumber!.Trim(), b.SerialNumber!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 시리얼 번호를 신원 확인에 쓸 수 있는지 판정합니다.
    /// </summary>
    /// <remarks>
    /// USB-SATA 브리지 칩은 시리얼을 제대로 전달하지 않는 경우가 많고, 그럴 때
    /// 전부 0이거나 전부 같은 문자인 자리표시자를 보고합니다. 이런 값으로 두 디스크가
    /// 같은지 판단하면 서로 다른 디스크를 같다고 오판합니다.
    /// </remarks>
    public static bool IsMeaningfulSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return false;

        // 구분자를 뺀 실제 내용만 봅니다 (예: "0025_3849_5142_2F2F." → "002538495142 2F2F").
        string trimmed = new string(serial.Where(char.IsLetterOrDigit).ToArray());

        if (trimmed.Length < 4) return false;

        // 전부 같은 문자면 자리표시자입니다 ("0000...", "FFFF...").
        if (trimmed.Distinct().Count() <= 1) return false;

        return true;
    }

    /// <summary>
    /// 사용자가 입력한 확인 문구가 대상 디스크 모델명과 일치하는지 검사합니다.
    /// 사용자가 어떤 디스크를 지우는지 실제로 읽었다는 것을 강제하는 장치입니다.
    /// </summary>
    public static bool IsConfirmationValid(DiskInfo target, string? typedText)
    {
        if (string.IsNullOrWhiteSpace(typedText)) return false;
        return string.Equals(typedText.Trim(), target.Model.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 쓰기 직전에 호출하는 최종 관문. 사용자가 UI에서 확인한 그 디스크가
    /// 지금도 같은 물리 디스크인지 재확인합니다.
    /// </summary>
    /// <param name="confirmedTarget">사용자가 확인 절차를 밟은 시점의 대상 정보.</param>
    /// <param name="freshTarget">쓰기 직전에 다시 열거해서 얻은 같은 장치 번호의 디스크 정보.</param>
    /// <exception cref="SafetyViolationException">디스크가 바뀌었으면 던집니다.</exception>
    public static void AssertTargetUnchanged(DiskInfo confirmedTarget, DiskInfo? freshTarget)
    {
        if (freshTarget is null)
        {
            throw new SafetyViolationException(
                Strings.Format("SafeAssertTargetGoneFmt", confirmedTarget.DeviceNumber, confirmedTarget.Model));
        }

        if (freshTarget.SizeBytes != confirmedTarget.SizeBytes ||
            !string.Equals(freshTarget.Model.Trim(), confirmedTarget.Model.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(freshTarget.SerialNumber?.Trim() ?? "", confirmedTarget.SerialNumber?.Trim() ?? "", StringComparison.OrdinalIgnoreCase))
        {
            throw new SafetyViolationException(
                Strings.Format("SafeAssertTargetChangedFmt",
                    confirmedTarget.DeviceNumber,
                    confirmedTarget.Model, FormatSize(confirmedTarget.SizeBytes),
                    freshTarget.Model, FormatSize(freshTarget.SizeBytes)));
        }

        // 열거 시점과 쓰기 시점 사이에 시스템 디스크가 되는 일은 없어야 하지만,
        // 비용이 0에 가까운 검사이므로 한 번 더 확인합니다.
        if (freshTarget.IsSystemDisk || freshTarget.IsBootDisk || freshTarget.HasPageFile)
        {
            throw new SafetyViolationException(
                Strings.Format("SafeAssertTargetReclassifiedFmt", freshTarget.DeviceNumber, freshTarget.Model));
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}

/// <summary>안전 규칙 위반으로 작업을 중단할 때 던집니다.</summary>
public sealed class SafetyViolationException : Exception
{
    public SafetyViolationException(string message) : base(message) { }
}
