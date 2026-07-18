using System.Runtime.CompilerServices;

// 진단 도구가 네이티브 구조체 크기를 검증할 수 있도록 internal을 공개합니다.
// 구조체 레이아웃이 Win32 헤더와 어긋나면 파티션 정보를 조용히 잘못 읽게 되므로,
// 실제 하드웨어에서 이 값을 확인할 수 있어야 합니다.
[assembly: InternalsVisibleTo("DiskMigrator.Probe")]
[assembly: InternalsVisibleTo("DiskMigrator.Windows.Tests")]
