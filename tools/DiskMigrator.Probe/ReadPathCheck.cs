using System.Diagnostics;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;
using DiskMigrator.Windows.Devices;

namespace DiskMigrator.Probe;

/// <summary>
/// 원시 디스크 <b>읽기</b> 경로를 실제 하드웨어에서 검증합니다. 쓰기는 하지 않습니다.
/// </summary>
/// <remarks>
/// 이 검사가 중요한 이유: RawDiskDevice는 FILE_FLAG_NO_BUFFERING으로 장치를 열고
/// RandomAccess로 읽습니다. 이 조합은 오프셋·길이·<b>버퍼 주소</b>가 모두 섹터 정렬일 때만
/// 동작하며, 하나라도 어긋나면 ERROR_INVALID_PARAMETER가 납니다. 단위 테스트의
/// 가짜 장치로는 이 제약을 재현할 수 없으므로 실기에서 확인해야 합니다.
/// </remarks>
internal static class ReadPathCheck
{
    public static void Run(IReadOnlyList<DiskInfo> disks)
    {
        Console.WriteLine("=== 원시 디스크 읽기 경로 검증 (읽기 전용) ===\n");
        Console.WriteLine("FILE_FLAG_NO_BUFFERING + 정렬 버퍼 조합이 실제로 동작하는지 확인합니다.\n");

        foreach (var disk in disks)
        {
            Console.WriteLine($"[{disk.DeviceNumber}] {disk.Model}");

            try
            {
                CheckDisk(disk);
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("     건너뜀 — 관리자 권한이 필요합니다.\n");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"     실패 — {ex.GetType().Name}: {ex.Message}\n");
            }
        }
    }

    private static void CheckDisk(DiskInfo disk)
    {
        using var device = RawDiskDevice.OpenRead(disk.DevicePath);

        Console.WriteLine($"     열기      : OK (크기 {SizeFormatter.Format(device.Length)}, " +
                          $"섹터 {device.SectorSize}B)");

        if (device.Length != disk.SizeBytes)
        {
            Console.WriteLine($"     경고      : 열거 크기({disk.SizeBytes:N0})와 장치 크기가 다릅니다!");
        }

        // 1) 섹터 하나 읽기 — 부트 섹터 시그니처로 실제 데이터가 왔는지 확인합니다.
        using (var buffer = new AlignedBuffer(RoundUp(device.SectorSize)))
        {
            var span = buffer.SpanOf(device.SectorSize);
            int read = device.Read(0, span);

            bool hasSignature = read >= 512 && span[510] == 0x55 && span[511] == 0xAA;

            Console.WriteLine($"     섹터 0    : {read}바이트 읽음, 부트 시그니처 " +
                              $"{(hasSignature ? "확인 (55 AA)" : "없음")}");
        }

        // 2) 대용량 정렬 읽기 — 클론 엔진이 실제로 쓰는 경로입니다.
        const int bufferSize = 4 * 1024 * 1024;
        using (var buffer = new AlignedBuffer(bufferSize))
        {
            var sw = Stopwatch.StartNew();
            long total = 0;
            int blocks = 16; // 64MB

            for (int i = 0; i < blocks && total + bufferSize <= device.Length; i++)
            {
                total += device.Read(total, buffer.Span);
            }

            sw.Stop();

            double speed = total / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"     순차 읽기 : {SizeFormatter.Format(total)} @ " +
                              $"{SizeFormatter.FormatSpeed(speed)}");
        }

        // 3) 디스크 끝 근처 읽기 — GPT 백업 헤더가 있는 영역이며, 경계 처리 오류가 잘 나는 곳입니다.
        long lastSectorOffset = (device.Length / device.SectorSize - 1) * device.SectorSize;
        using (var buffer = new AlignedBuffer(RoundUp(device.SectorSize)))
        {
            int read = device.Read(lastSectorOffset, buffer.SpanOf(device.SectorSize));
            Console.WriteLine($"     마지막 섹터: 오프셋 {lastSectorOffset:N0}에서 {read}바이트 읽음");
        }

        // 4) 끝을 넘어선 읽기 — 예외가 아니라 0을 돌려줘야 합니다.
        using (var buffer = new AlignedBuffer(RoundUp(device.SectorSize)))
        {
            int read = device.Read(device.Length, buffer.SpanOf(device.SectorSize));
            Console.WriteLine($"     끝 너머   : {read}바이트 (0이어야 정상)");
        }

        Console.WriteLine();
    }

    private static int RoundUp(int size) => Math.Max(4096, (size + 4095) / 4096 * 4096);
}
