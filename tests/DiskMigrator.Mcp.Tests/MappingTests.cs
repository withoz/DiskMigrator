using DiskMigrator.Core.Models;
using DiskMigrator.Mcp;
using Xunit;

namespace DiskMigrator.Mcp.Tests;

/// <summary>
/// 엔진 → DTO 변환과 민감 정보 마스킹 — 계획서 §5.2·§7.
/// </summary>
/// <remarks>
/// 진단 결과는 대화 로그에 남습니다. 시리얼·볼륨 레이블이 기본값으로 새어 나가면
/// 사용자가 의도치 않게 공유하게 되므로, 마스킹이 <b>기본</b>임을 테스트로 고정합니다.
/// </remarks>
public class MappingTests
{
    private static DiskInfo SampleDisk(string? serial = "S1234567890") => new()
    {
        DeviceNumber = 3,
        Model = "Samsung SSD 990 PRO 1TB",
        SerialNumber = serial,
        SizeBytes = 1_000_204_886_016,
        LogicalSectorSize = 512,
        BusType = DiskBusType.Nvme,
        PartitionStyle = PartitionStyle.Gpt,
        DiskGuid = Guid.Parse("fce997d8-8d45-11f1-89bf-b42e99858048"),
        Partitions =
        [
            new PartitionInfo
            {
                Number = 1,
                StartingOffset = 1_048_576,
                LengthBytes = 104_857_600,
                IsEfiSystemPartition = true,
                FileSystem = "FAT32",
                VolumeLabel = "SYSTEM",
            },
            new PartitionInfo
            {
                Number = 2,
                StartingOffset = 240_123_904,
                LengthBytes = 999_000_000_000,
                FileSystem = "NTFS",
                VolumeLabel = "Windows",
                DriveLetter = "C",
                FreeSpaceBytes = 400_000_000_000,
                GptPartitionType = Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"),
            },
        ],
    };

    [Fact]
    public void 기본은_시리얼을_가린다()
    {
        var dto = new Mapping().ToDto(SampleDisk());

        Assert.NotEqual("S1234567890", dto.SerialNumber);
        Assert.StartsWith("S1", dto.SerialNumber);       // 대조는 가능해야 합니다
        Assert.Contains('*', dto.SerialNumber!);
        Assert.Equal("S1234567890".Length, dto.SerialNumber!.Length);
    }

    [Fact]
    public void 상세_공유를_켜면_시리얼이_그대로_나온다()
    {
        var dto = new Mapping(includeSensitive: true).ToDto(SampleDisk());
        Assert.Equal("S1234567890", dto.SerialNumber);
    }

    [Fact]
    public void 볼륨_레이블도_기본으로_가린다()
    {
        var detail = new Mapping().ToDetailDto(SampleDisk());
        var windows = detail.Partitions.Single(p => p.Number == 2);

        Assert.NotEqual("Windows", windows.Label);
        Assert.StartsWith("Wi", windows.Label);
    }

    [Fact]
    public void 시리얼이_없으면_null_그대로()
    {
        var dto = new Mapping().ToDto(SampleDisk(serial: null));
        Assert.Null(dto.SerialNumber);
    }

    [Fact]
    public void 파티션_종류를_사람이_읽을_수_있게_분류한다()
    {
        var detail = new Mapping().ToDetailDto(SampleDisk());

        Assert.Equal("EfiSystem", detail.Partitions.Single(p => p.Number == 1).Kind);
        Assert.Equal("BasicData", detail.Partitions.Single(p => p.Number == 2).Kind);
    }

    [Fact]
    public void 사용량은_여유_공간에서_역산하고_알_수_없으면_null()
    {
        var detail = new Mapping().ToDetailDto(SampleDisk());

        var windows = detail.Partitions.Single(p => p.Number == 2);
        Assert.Equal(999_000_000_000 - 400_000_000_000, windows.UsedBytes);

        // ESP는 여유 공간을 모르므로 사용량도 알 수 없습니다 — 0으로 단정하지 않습니다.
        Assert.Null(detail.Partitions.Single(p => p.Number == 1).UsedBytes);
    }

    [Fact]
    public void 크기는_바이트와_읽는_문자열을_함께_준다()
    {
        var dto = new Mapping().ToDto(SampleDisk());

        Assert.Equal(1_000_204_886_016, dto.SizeBytes);
        Assert.False(string.IsNullOrWhiteSpace(dto.SizeText));
    }

    [Fact]
    public void GPT_GUID와_MBR_서명은_배타적으로_채워진다()
    {
        var gpt = new Mapping().ToDto(SampleDisk());
        Assert.NotNull(gpt.DiskGuid);
        Assert.Null(gpt.MbrSignature);

        var mbr = new Mapping().ToDto(new DiskInfo
        {
            DeviceNumber = 4,
            Model = "Old HDD",
            SizeBytes = 500_000_000_000,
            LogicalSectorSize = 512,
            PartitionStyle = PartitionStyle.Mbr,
            MbrSignature = 0xA1B2C3D4,
        });
        Assert.Null(mbr.DiskGuid);
        Assert.Equal("0xA1B2C3D4", mbr.MbrSignature);
    }
}
