using DiskMigrator.Core.Registry;
using Xunit;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 부팅 관리자 서명 발급자 분류 — 구형 보드 호환성 조언이 여기서 갈립니다.
/// </summary>
/// <remarks>
/// 2023 CA로 서명된 부팅 관리자는 아직 흔치 않아 실물을 구하기 어렵지만, 이 판정이 틀리면
/// "이 보드에서 부팅되는가"라는 조언 전체가 어긋납니다. 실제 발급자 문자열로 고정합니다.
/// </remarks>
public class SigningAuthorityTests
{
    /// <summary>2026-08-04 조사에서 실제로 읽은 발급자 문자열입니다.</summary>
    private const string RealPca2011 =
        "CN=Microsoft Windows Production PCA 2011, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    [Fact]
    public void 실제_PCA2011_문자열을_알아본다()
    {
        Assert.Equal(SigningAuthority.Pca2011, SigningAuthority.Classify(RealPca2011));
    }

    [Theory]
    [InlineData("CN=Windows UEFI CA 2023, O=Microsoft Corporation, C=US")]
    [InlineData("CN=Microsoft Windows UEFI CA 2023, O=Microsoft Corporation")]
    public void CA2023은_따로_분류한다(string issuer)
    {
        // 이 분류가 "구형 보드에서 검증 실패할 수 있다"는 경고로 이어집니다.
        Assert.Equal(SigningAuthority.Ca2023, SigningAuthority.Classify(issuer));
    }

    [Fact]
    public void 다른_Microsoft_서명은_별도_분류()
    {
        Assert.Equal(SigningAuthority.OtherMicrosoft,
            SigningAuthority.Classify("CN=Microsoft Code Signing PCA, O=Microsoft Corporation"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CN=Some Other Vendor CA")]
    public void 알_수_없으면_단정하지_않는다(string? issuer)
    {
        // 모르는 것을 아는 척하면 잘못된 조언이 나갑니다.
        Assert.Equal(SigningAuthority.Unknown, SigningAuthority.Classify(issuer));
    }

    [Fact]
    public void 대소문자는_구분하지_않는다()
    {
        Assert.Equal(SigningAuthority.Pca2011,
            SigningAuthority.Classify("cn=microsoft windows production pca 2011, o=microsoft"));
    }
}
