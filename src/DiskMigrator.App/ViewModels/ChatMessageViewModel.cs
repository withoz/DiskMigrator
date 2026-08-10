namespace DiskMigrator.App.ViewModels;

/// <summary>대화 한 줄 — 사용자가 물은 것이거나 Claude가 답한 것.</summary>
/// <param name="IsUser">사용자가 쓴 것인지. 화면에서 서로 다르게 그립니다.</param>
/// <param name="Text">내용.</param>
/// <remarks>
/// 누가 한 말인지 화면에서 구분되지 않으면, 사용자는 자기가 쓴 질문과 Claude의 답을
/// 섞어 읽게 됩니다. 디스크를 지우는 도구에서 그 혼동은 값이 비쌉니다.
/// </remarks>
public sealed record ChatMessageViewModel(bool IsUser, string Text)
{
    /// <summary>화면에 붙는 이름표.</summary>
    public string Who => IsUser ? Localization.Strings.Get("ChatYou") : "Claude";
}
