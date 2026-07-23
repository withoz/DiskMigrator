using DiskMigrator.Core.Registry;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// <see cref="UniversalRestore.Apply"/>의 최대 절전/빠른 시작 무력화를 실제 하이브 바이트로 검증합니다.
/// (저장소 드라이버 부팅 시작 설정은 SetDword 경로가 RegistryHive 테스트에서 이미 검증됩니다.)
/// </summary>
public sealed class UniversalRestoreTests
{
    /// <summary>
    /// ControlSet001\Control 아래에 빠른 시작(HiberbootEnabled)·최대 절전(HibernateEnabled)
    /// 값을 1로 둔 최소 SYSTEM 하이브를 만듭니다.
    /// </summary>
    private static byte[] BuildHiveWithHibernationOn()
    {
        var b = new HiveBuilder();

        int hiberboot = b.Dword("HiberbootEnabled", 1);
        int smPower = b.AddKey("Power", [hiberboot], []);
        int sessionMgr = b.AddKey("Session Manager", [], [smPower]);

        int hibernate = b.Dword("HibernateEnabled", 1);
        int controlPower = b.AddKey("Power", [hibernate], []);

        int control = b.AddKey("Control", [], [sessionMgr, controlPower]);
        int cs001 = b.AddKey("ControlSet001", [], [control]);
        int root = b.AddKey("", [], [cs001]);
        return b.Finish(root);
    }

    [Fact]
    public void Apply_최대절전과_빠른시작을_끕니다()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dm-ur-{Guid.NewGuid():N}.hive");
        File.WriteAllBytes(path, BuildHiveWithHibernationOn());
        try
        {
            var before = RegistryHive.Load(path);
            Assert.Equal(1u, before.GetDword("ControlSet001\\Control\\Session Manager\\Power", "HiberbootEnabled"));
            Assert.Equal(1u, before.GetDword("ControlSet001\\Control\\Power", "HibernateEnabled"));

            var result = UniversalRestore.Apply(path);

            Assert.True(result.HibernationDisabled);
            Assert.True(result.AnyChanged);

            // 디스크에 저장된 하이브를 다시 읽어 실제로 0으로 꺼졌는지 확인.
            var after = RegistryHive.Load(path);
            Assert.Equal(0u, after.GetDword("ControlSet001\\Control\\Session Manager\\Power", "HiberbootEnabled"));
            Assert.Equal(0u, after.GetDword("ControlSet001\\Control\\Power", "HibernateEnabled"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Apply_최대절전_값이_없으면_HibernationDisabled는_false()
    {
        // Power 키에 값이 없는 하이브 — SetDword가 false를 반환하고 넘어가야 합니다.
        var b = new HiveBuilder();
        int smPower = b.AddKey("Power", [], []);
        int sessionMgr = b.AddKey("Session Manager", [], [smPower]);
        int controlPower = b.AddKey("Power", [], []);
        int control = b.AddKey("Control", [], [sessionMgr, controlPower]);
        int cs001 = b.AddKey("ControlSet001", [], [control]);
        int root = b.AddKey("", [], [cs001]);

        string path = Path.Combine(Path.GetTempPath(), $"dm-ur-{Guid.NewGuid():N}.hive");
        File.WriteAllBytes(path, b.Finish(root));
        try
        {
            var result = UniversalRestore.Apply(path);
            Assert.False(result.HibernationDisabled);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
