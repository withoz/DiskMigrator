using System.Text.Json.Serialization;

namespace DiskMigrator.Mcp.Dto;

/// <summary>
/// 모든 도구 응답의 공통 껍데기. 성공이면 <paramref name="Data"/>, 실패면 <paramref name="Error"/>가 찹니다.
/// </summary>
/// <remarks>
/// 예외를 그대로 던지지 않고 이 형태로 감싸는 이유는 두 가지입니다.
/// Claude가 실패를 <b>이해하고 다음 행동을 정할 수 있어야</b> 하고(그래서 <c>hint</c>가 있습니다),
/// MCP 계층의 오류가 진행 중인 디스크 작업에 영향을 주면 안 되기 때문입니다(계획서 §9).
/// </remarks>
public sealed record ToolResult<T>(
    bool Ok,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] T? Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolError? Error)
{
    public static ToolResult<T> Success(T data) => new(true, data, null);

    public static ToolResult<T> Fail(string code, string message, string? hint = null) =>
        new(false, default, new ToolError(code, message, hint));
}

/// <summary>실패 사유.</summary>
/// <param name="Code">기계가 분기할 수 있는 코드. 아래 상수를 씁니다.</param>
/// <param name="Message">무엇이 잘못됐는지.</param>
/// <param name="Hint">사용자나 Claude가 <b>다음에 할 수 있는 일</b>. 없으면 null.</param>
public sealed record ToolError(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint);

/// <summary>도구가 돌려주는 오류 코드.</summary>
public static class ToolErrorCodes
{
    /// <summary>지정한 디스크가 없습니다(뽑혔거나 번호가 틀렸습니다).</summary>
    public const string DiskNotFound = "DISK_NOT_FOUND";

    /// <summary>관리자 권한이 없어 읽을 수 없습니다.</summary>
    public const string NotElevated = "NOT_ELEVATED";

    /// <summary>앱이 다른 작업(클론·백업 등)을 하는 중입니다.</summary>
    public const string Busy = "BUSY";

    /// <summary>파일이 없거나 열 수 없습니다.</summary>
    public const string FileNotFound = "FILE_NOT_FOUND";

    /// <summary>인자가 잘못됐습니다.</summary>
    public const string InvalidArgument = "INVALID_ARGUMENT";

    /// <summary>읽는 중 예기치 못한 오류가 났습니다.</summary>
    public const string Internal = "INTERNAL";
}
