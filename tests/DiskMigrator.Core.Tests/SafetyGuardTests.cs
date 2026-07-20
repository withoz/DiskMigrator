using DiskMigrator.Core.Models;
using DiskMigrator.Core.Safety;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// SafetyGuard는 이 프로그램의 마지막 방어선이므로, 각 차단 규칙마다 테스트가 있어야 합니다.
/// 여기서 놓친 규칙은 사용자의 데이터로 대가를 치릅니다.
/// </summary>
public class SafetyGuardTests
{
    private const long OneHundredGb = 100L * 1024 * 1024 * 1024;

    private static DiskInfo Disk(
        int number,
        long size = OneHundredGb,
        string model = "Test Disk Model",
        string? serial = "SERIAL123",
        bool isSystem = false,
        bool isBoot = false,
        bool hasPageFile = false,
        bool isReadOnly = false,
        int sectorSize = 512,
        PartitionStyle style = PartitionStyle.Raw,
        IReadOnlyList<PartitionInfo>? partitions = null) =>
        new()
        {
            DeviceNumber = number,
            Model = model,
            SerialNumber = serial,
            SizeBytes = size,
            LogicalSectorSize = sectorSize,
            IsSystemDisk = isSystem,
            IsBootDisk = isBoot,
            HasPageFile = hasPageFile,
            IsReadOnly = isReadOnly,
            PartitionStyle = style,
            Partitions = partitions ?? [],
        };

    private static PartitionInfo Partition(int number = 1, long length = OneHundredGb) => new()
    {
        Number = number,
        StartingOffset = 1024 * 1024,
        LengthBytes = length,
        FileSystem = "NTFS",
    };

    [Fact]
    public void 정상적인_원본과_대상은_통과한다()
    {
        var report = SafetyGuard.Evaluate(Disk(0), Disk(1, serial: "OTHER456"), isElevated: true);

        Assert.True(report.CanProceed);
        Assert.Empty(report.Blockers);
    }

    [Fact]
    public void 관리자_권한이_없으면_차단된다()
    {
        var report = SafetyGuard.Evaluate(Disk(0), Disk(1, serial: "OTHER456"), isElevated: false);

        Assert.False(report.CanProceed);
        Assert.Contains(report.Blockers, i => i.Code == SafetyGuard.CodeNotElevated);
    }

    [Fact]
    public void 같은_디스크로의_복제는_차단된다()
    {
        var disk = Disk(2);
        var report = SafetyGuard.Evaluate(disk, disk, isElevated: true);

        Assert.False(report.CanProceed);
        Assert.Contains(report.Blockers, i => i.Code == SafetyGuard.CodeSameDisk);
    }

    [Fact]
    public void 대상이_시스템_디스크면_차단된다()
    {
        var report = SafetyGuard.Evaluate(
            Disk(1, serial: "A1"),
            Disk(0, serial: "B2", isSystem: true),
            isElevated: true);

        Assert.False(report.CanProceed);
        Assert.Contains(report.Blockers, i => i.Code == SafetyGuard.CodeTargetIsSystemDisk);
    }

    [Fact]
    public void 대상이_부팅_디스크면_차단된다()
    {
        var report = SafetyGuard.Evaluate(
            Disk(1, serial: "A1"),
            Disk(0, serial: "B2", isBoot: true),
            isElevated: true);

        Assert.Contains(report.Blockers, i => i.Code == SafetyGuard.CodeTargetIsBootDisk);
    }

    [Fact]
    public void 대상에_페이지파일이_있으면_차단된다()
    {
        var report = SafetyGuard.Evaluate(
            Disk(1, serial: "A1"),
            Disk(0, serial: "B2", hasPageFile: true),
            isElevated: true);

        Assert.Contains(report.Blockers, i => i.Code == SafetyGuard.CodeTargetHasPageFile);
    }

    [Fact]
    public void 대상이_읽기_전용이면_차단된다()
    {
        var report = SafetyGuard.Evaluate(
            Disk(0, serial: "A1"),
            Disk(1, serial: "B2", isReadOnly: true),
            isElevated: true);

        Assert.Contains(report.Blockers, i => i.Code == SafetyGuard.CodeTargetReadOnly);
    }

    [Fact]
    public void 대상이_원본보다_작으면_차단된다()
    {
        var report = SafetyGuard.Evaluate(
            Disk(0, size: OneHundredGb, serial: "A1"),
            Disk(1, size: OneHundredGb - 512, serial: "B2"),
            isElevated: true);

        Assert.False(report.CanProceed);
        Assert.Contains(report.Blockers, i => i.Code == SafetyGuard.CodeTargetTooSmall);
    }

    [Fact]
    public void 대상이_작아도_GPT_파티션이_모두_들어가면_확인만_받는다()
    {
        // 1TB 원본이지만 파티션은 앞쪽 ~10GB만 차지 → 100GB 대상에 그대로 들어간다.
        var partitions = new List<PartitionInfo>
        {
            new() { Number = 1, StartingOffset = 1024 * 1024, LengthBytes = 10L * 1024 * 1024 * 1024 },
        };

        var report = SafetyGuard.Evaluate(
            Disk(0, size: 1024L * 1024 * 1024 * 1024, serial: "A1",
                 style: PartitionStyle.Gpt, partitions: partitions),
            Disk(1, size: OneHundredGb, serial: "B2"),
            isElevated: true);

        Assert.DoesNotContain(report.Blockers, i => i.Code == SafetyGuard.CodeTargetTooSmall);
        Assert.Contains(report.Issues, i => i.Code == SafetyGuard.CodeTargetSmallerLayoutFits);
        Assert.True(report.CanProceed);
    }

    [Fact]
    public void 대상이_작고_파티션이_안_들어가면_여전히_차단된다()
    {
        // 파티션이 대상보다 크다 — 파일시스템을 줄이지 않고는 불가능하므로 차단.
        var partitions = new List<PartitionInfo>
        {
            new() { Number = 1, StartingOffset = 1024 * 1024, LengthBytes = 200L * 1024 * 1024 * 1024 },
        };

        var report = SafetyGuard.Evaluate(
            Disk(0, size: 1024L * 1024 * 1024 * 1024, serial: "A1",
                 style: PartitionStyle.Gpt, partitions: partitions),
            Disk(1, size: OneHundredGb, serial: "B2"),
            isElevated: true);

        Assert.False(report.CanProceed);
        Assert.Contains(report.Blockers, i => i.Code == SafetyGuard.CodeTargetTooSmall);
    }

    [Fact]
    public void MBR_원본은_대상이_작으면_들어가더라도_차단된다()
    {
        // 맞춤 클론은 GPT 백업 헤더 재작성에 기대므로 GPT 전용이다.
        var partitions = new List<PartitionInfo>
        {
            new() { Number = 1, StartingOffset = 1024 * 1024, LengthBytes = 10L * 1024 * 1024 * 1024 },
        };

        var report = SafetyGuard.Evaluate(
            Disk(0, size: 1024L * 1024 * 1024 * 1024, serial: "A1",
                 style: PartitionStyle.Mbr, partitions: partitions),
            Disk(1, size: OneHundredGb, serial: "B2"),
            isElevated: true);

        Assert.False(report.CanProceed);
        Assert.Contains(report.Blockers, i => i.Code == SafetyGuard.CodeTargetTooSmall);
    }

    [Fact]
    public void 대상이_원본과_같은_크기면_통과한다()
    {
        var report = SafetyGuard.Evaluate(
            Disk(0, size: OneHundredGb, serial: "A1"),
            Disk(1, size: OneHundredGb, serial: "B2"),
            isElevated: true);

        Assert.True(report.CanProceed);
    }

    [Fact]
    public void 섹터_크기가_다르면_차단된다()
    {
        var report = SafetyGuard.Evaluate(
            Disk(0, serial: "A1", sectorSize: 512),
            Disk(1, serial: "B2", sectorSize: 4096),
            isElevated: true);

        Assert.Contains(report.Blockers, i => i.Code == SafetyGuard.CodeSectorSizeMismatch);
    }

    [Fact]
    public void 대상에_데이터가_있으면_사용자_확인을_요구한다()
    {
        var target = Disk(1, serial: "B2", style: PartitionStyle.Gpt, partitions: [Partition()]);
        var report = SafetyGuard.Evaluate(Disk(0, serial: "A1"), target, isElevated: true);

        Assert.True(report.CanProceed);
        Assert.True(report.NeedsTypedConfirmation);
        Assert.Contains(report.Confirmations, i => i.Code == SafetyGuard.CodeTargetHasData);
    }

    [Fact]
    public void 빈_대상은_확인을_요구하지_않는다()
    {
        var report = SafetyGuard.Evaluate(
            Disk(0, serial: "A1"),
            Disk(1, serial: "B2", style: PartitionStyle.Raw),
            isElevated: true);

        Assert.False(report.NeedsTypedConfirmation);
    }

    [Fact]
    public void 실행중_시스템_디스크를_스냅샷_없이_복제하면_차단한다()
    {
        // 실기에서 이 조합(VSS 미로드 → 스냅샷 없이 라이브 시스템 복제)이 불일치 2097건의
        // 깨진 클론을 만들었습니다. 예측 가능하게 나쁜 결과이므로 경고가 아니라 차단합니다.
        var report = SafetyGuard.Evaluate(
            Disk(0, serial: "A1", isSystem: true),
            Disk(1, serial: "B2"),
            isElevated: true,
            useSnapshot: false);

        Assert.False(report.CanProceed);
        Assert.Contains(report.Blockers, i => i.Code == SafetyGuard.CodeSourceIsLiveSystem);
    }

    [Fact]
    public void 스냅샷을_쓰면_실행중_시스템_디스크도_진행할_수_있다()
    {
        var report = SafetyGuard.Evaluate(
            Disk(0, serial: "A1", isSystem: true),
            Disk(1, serial: "B2"),
            isElevated: true,
            useSnapshot: true);

        Assert.DoesNotContain(report.Blockers, i => i.Code == SafetyGuard.CodeSourceIsLiveSystem);
        Assert.True(report.CanProceed);
    }

    [Fact]
    public void 스냅샷을_쓰면_실행중_시스템_디스크_경고가_사라진다()
    {
        var report = SafetyGuard.Evaluate(
            Disk(0, serial: "A1", isSystem: true),
            Disk(1, serial: "B2"),
            isElevated: true,
            useSnapshot: true);

        Assert.DoesNotContain(report.Issues, i => i.Code == SafetyGuard.CodeSourceIsLiveSystem);
    }

    // --- 확인 문구 검증 ---------------------------------------------------

    [Theory]
    [InlineData("Test Disk Model", true)]
    [InlineData("test disk model", true)]      // 대소문자 무시
    [InlineData("  Test Disk Model  ", true)]  // 앞뒤 공백 무시
    [InlineData("Test Disk", false)]           // 부분 일치는 불가
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 확인_문구는_모델명과_정확히_일치해야_한다(string? typed, bool expected)
    {
        var target = Disk(1, model: "Test Disk Model");

        Assert.Equal(expected, SafetyGuard.IsConfirmationValid(target, typed));
    }

    // --- 시리얼 신뢰성 ----------------------------------------------------
    //
    // 실기 테스트에서 발견된 문제: 저가 USB 인클로저는 서로 다른 디스크에
    // 똑같은 더미 시리얼을 보고합니다.

    [Theory]
    [InlineData("0025_3849_5142_2F2F.", true)]
    [InlineData("DD56419883A62", true)]
    [InlineData("00000000NABDLJPC", true)]
    [InlineData("00000000000000000000", false)]  // 실기에서 관측된 더미 값
    [InlineData("FFFFFFFF", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("ABC", false)]                   // 너무 짧음
    public void 자리표시자_시리얼은_신원_확인에_쓰지_않는다(string? serial, bool expected)
    {
        Assert.Equal(expected, SafetyGuard.IsMeaningfulSerial(serial));
    }

    [Fact]
    public void 더미_시리얼이_같아도_크기가_다르면_다른_디스크로_본다()
    {
        // 실기에서 관측된 상황: ThinkWay 인클로저 2개가 같은 더미 시리얼을 보고하지만
        // 실제로는 2.73TB와 3.64TB의 서로 다른 디스크입니다.
        var a = Disk(3, size: 3_000_592_982_016, serial: "00000000000000000000");
        var b = Disk(4, size: 4_000_787_030_016, serial: "00000000000000000000");

        Assert.False(SafetyGuard.IsSameDisk(a, b));
    }

    [Fact]
    public void 더미_시리얼이_같고_크기도_같아도_다른_디스크로_본다()
    {
        // 같은 모델의 인클로저 2개를 꽂으면 시리얼도 크기도 같을 수 있습니다.
        // 이때 시리얼을 근거로 동일하다고 판정하면 정상적인 클론이 막힙니다.
        var a = Disk(3, size: 3_000_592_982_016, serial: "00000000000000000000");
        var b = Disk(4, size: 3_000_592_982_016, serial: "00000000000000000000");

        Assert.False(SafetyGuard.IsSameDisk(a, b));
    }

    [Fact]
    public void 의미있는_시리얼이_같으면_번호가_달라도_같은_디스크로_본다()
    {
        // 장치 번호는 USB를 뽑았다 꽂으면 재배정됩니다. 시리얼이 신뢰할 만하면
        // 번호가 달라도 같은 디스크임을 알아내야 합니다.
        var a = Disk(1, model: "Samsung SSD 990 EVO", serial: "DD56419883A62");
        var b = Disk(5, model: "Samsung SSD 990 EVO", serial: "DD56419883A62");

        Assert.True(SafetyGuard.IsSameDisk(a, b));
    }

    [Fact]
    public void 인클로저가_시리얼을_공유해도_모델이_다르면_다른_디스크로_본다()
    {
        // 실기에서 관측: 같은 모델의 USB 인클로저 두 개가 안에 든 드라이브와 무관하게
        // 똑같은 시리얼 "DD56419883A62"를 보고했습니다. 자리표시자처럼 생기지 않아
        // IsMeaningfulSerial을 통과하고, 두 디스크 모두 1TB라 크기로도 구분되지 않습니다.
        // 모델까지 보지 않으면 서로 다른 디스크 간 클론이 SAME_DISK로 잘못 차단됩니다.
        var samsung = Disk(1, model: "Samsung SSD 990 EVO SCSI Disk Device",
                           serial: "DD56419883A62", size: 1_000_204_886_016);
        var crucial = Disk(5, model: "CT1000P3 10SSD8 SCSI Disk Device",
                           serial: "DD56419883A62", size: 1_000_204_886_016);

        Assert.False(SafetyGuard.IsSameDisk(samsung, crucial));

        var report = SafetyGuard.Evaluate(samsung, crucial, isElevated: true);
        Assert.DoesNotContain(report.Blockers, i => i.Code == SafetyGuard.CodeSameDisk);
    }

    [Fact]
    public void 시리얼이_같고_모델도_같지만_크기가_다르면_다른_디스크로_본다()
    {
        var a = Disk(1, model: "Generic USB Disk", serial: "DD56419883A62", size: OneHundredGb);
        var b = Disk(5, model: "Generic USB Disk", serial: "DD56419883A62", size: OneHundredGb * 2);

        Assert.False(SafetyGuard.IsSameDisk(a, b));
    }

    // --- 쓰기 직전 최종 관문 ----------------------------------------------

    [Fact]
    public void 대상_디스크가_사라졌으면_중단한다()
    {
        var confirmed = Disk(1);

        var ex = Assert.Throws<SafetyViolationException>(
            () => SafetyGuard.AssertTargetUnchanged(confirmed, null));

        Assert.Contains("더 이상 존재하지 않습니다", ex.Message);
    }

    [Fact]
    public void 같은_번호에_다른_디스크가_꽂혀_있으면_중단한다()
    {
        var confirmed = Disk(1, model: "Old Disk", serial: "AAA111", size: OneHundredGb);
        var fresh = Disk(1, model: "Different Disk", serial: "BBB222", size: OneHundredGb * 2);

        var ex = Assert.Throws<SafetyViolationException>(
            () => SafetyGuard.AssertTargetUnchanged(confirmed, fresh));

        Assert.Contains("달라졌습니다", ex.Message);
    }

    [Fact]
    public void 대상이_시스템_디스크로_재판정되면_중단한다()
    {
        var confirmed = Disk(1);
        var fresh = Disk(1, isSystem: true);

        Assert.Throws<SafetyViolationException>(
            () => SafetyGuard.AssertTargetUnchanged(confirmed, fresh));
    }

    [Fact]
    public void 대상이_그대로면_통과한다()
    {
        var confirmed = Disk(1);
        var fresh = Disk(1);

        SafetyGuard.AssertTargetUnchanged(confirmed, fresh); // 예외가 없어야 합니다.
    }

    // --- 복제는 되지만 부팅이 안 될 조합 ------------------------------------
    //
    // 실기에서 MBR 원본을 NVMe로 옮기고 나서야 부팅이 불가능함을 알았습니다.
    // 원본만 봐도 시작 전에 말할 수 있는 것들입니다.

    private static PartitionInfo Part(
        int number, bool active = false, bool esp = false, long start = 1L << 20) =>
        new()
        {
            Number = number,
            StartingOffset = start,
            LengthBytes = 50L * 1024 * 1024 * 1024,
            IsActive = active,
            IsEfiSystemPartition = esp,
            FileSystem = "NTFS",
        };

    [Fact]
    public void MBR_활성파티션_원본은_레거시_전용임을_알린다()
    {
        var source = Disk(0, style: PartitionStyle.Mbr, partitions: [Part(1, active: true)]);

        var report = SafetyGuard.Evaluate(source, Disk(1, serial: "OTHER"), isElevated: true);

        var issue = report.Issues.Single(i => i.Code == SafetyGuard.CodeSourceBiosOnly);
        Assert.Equal(SafetySeverity.Warning, issue.Severity);
        Assert.Contains("NVMe", issue.Message);
    }

    [Fact]
    public void ESP가_있으면_레거시_전용_안내를_하지_않는다()
    {
        // UEFI로 부팅되는 원본은 대상이 NVMe여도 문제없습니다.
        var source = Disk(0, style: PartitionStyle.Gpt,
            partitions: [Part(1, esp: true), Part(2, start: 2L << 30)]);

        var report = SafetyGuard.Evaluate(source, Disk(1, serial: "OTHER"), isElevated: true);

        Assert.DoesNotContain(report.Issues, i => i.Code == SafetyGuard.CodeSourceBiosOnly);
    }

    [Fact]
    public void 활성_파티션이_없는_MBR_데이터_디스크는_알리지_않는다()
    {
        // 부팅용이 아닌 데이터 디스크에 부팅 경고를 붙이면 소음이 됩니다.
        var source = Disk(0, style: PartitionStyle.Mbr, partitions: [Part(1)]);

        var report = SafetyGuard.Evaluate(source, Disk(1, serial: "OTHER"), isElevated: true);

        Assert.DoesNotContain(report.Issues, i => i.Code == SafetyGuard.CodeSourceBiosOnly);
    }

    [Fact]
    public void 최대_절전_이미지가_있으면_검은_화면을_예고한다()
    {
        var report = SafetyGuard.Evaluate(
            Disk(0), Disk(1, serial: "OTHER"), isElevated: true, sourceHibernated: true);

        var issue = report.Issues.Single(i => i.Code == SafetyGuard.CodeSourceHibernated);
        Assert.Equal(SafetySeverity.Warning, issue.Severity);
        Assert.Contains("검은 화면", issue.Message);
    }

    [Fact]
    public void 최대_절전_이미지가_없으면_알리지_않는다()
    {
        var report = SafetyGuard.Evaluate(Disk(0), Disk(1, serial: "OTHER"), isElevated: true);

        Assert.DoesNotContain(report.Issues, i => i.Code == SafetyGuard.CodeSourceHibernated);
    }

    [Fact]
    public void BIOS_전용_판정은_경고와_변환_제안이_함께_쓴다()
    {
        // 시작 전 경고(SafetyGuard)와 복제 후 UEFI 변환 제안(UefiConverter)이 같은 근거를
        // 써야 "경고는 하는데 변환 버튼은 없는" 상태가 생기지 않습니다.
        var biosOnly = Disk(0, style: PartitionStyle.Mbr, partitions: [Part(1, active: true)]);
        var uefi = Disk(1, style: PartitionStyle.Gpt, partitions: [Part(1, esp: true)]);
        var dataOnly = Disk(2, style: PartitionStyle.Mbr, partitions: [Part(1)]);

        Assert.True(biosOnly.IsBiosOnlyBootLayout);
        Assert.False(uefi.IsBiosOnlyBootLayout);
        Assert.False(dataOnly.IsBiosOnlyBootLayout);
    }
}
