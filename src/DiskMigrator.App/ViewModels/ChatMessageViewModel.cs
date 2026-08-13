using CommunityToolkit.Mvvm.ComponentModel;

namespace DiskMigrator.App.ViewModels;

/// <summary>대화 한 줄 — 사용자가 물은 것이거나 Claude가 답한 것.</summary>
/// <remarks>
/// 누가 한 말인지 화면에서 구분되지 않으면, 사용자는 자기가 쓴 질문과 Claude의 답을
/// 섞어 읽게 됩니다. 디스크를 지우는 도구에서 그 혼동은 값이 비쌉니다.
///
/// <para><b>글은 자라납니다.</b> Claude의 답은 다 만들어진 뒤에 오지 않고 글자가 생기는 대로
/// 흘러옵니다. 그래서 <see cref="Text"/>는 바뀌는 값이어야 합니다 — 예전에는 고정 값이라
/// 다 끝날 때까지 화면이 비어 있었고, 사용자는 56초 동안 멈춘 화면을 봤습니다.</para>
/// </remarks>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(bool IsUser, string text)
    {
        this.IsUser = IsUser;
        _text = text;
    }

    /// <summary>사용자가 쓴 것인지. 화면에서 서로 다르게 그립니다.</summary>
    public bool IsUser { get; }

    /// <summary>내용. 답이 흘러오는 동안 계속 자랍니다.</summary>
    [ObservableProperty] private string _text;

    /// <summary>흘러온 조각을 뒤에 붙입니다.</summary>
    public void Append(string chunk) => Text += chunk;

    /// <summary>화면에 붙는 이름표.</summary>
    public string Who => IsUser ? Localization.Strings.Get("ChatYou") : "Claude";
}
