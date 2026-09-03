using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskMigrator.Core.Localization;

namespace DiskMigrator.App.Views;

/// <summary>
/// WinPE(부팅 USB)용 자체 파일 선택 창.
/// </summary>
/// <remarks>
/// WPF의 <c>OpenFileDialog</c>/<c>SaveFileDialog</c>는 내부적으로 탐색기 셸의 COM 구성요소
/// (IFileOpenDialog)를 쓰는데, WinPE에는 그 클래스가 등록돼 있지 않아
/// <c>0x80040111 (CLASS_E_CLASSNOTAVAILABLE)</c>로 실패합니다. 이 창은 셸 없이
/// <see cref="DriveInfo"/>·<see cref="Directory"/>만으로 드라이브→폴더→파일을 탐색하므로
/// PE에서도 동작합니다. 일반 Windows에서는 표준 대화상자를 계속 씁니다(<see cref="FileDialogs"/>).
/// </remarks>
public sealed class PeFileDialog : Window
{
    private enum Mode { Open, Save }

    private sealed record Entry(string Display, string? Path, bool IsDir, bool IsDrive);

    private readonly Mode _mode;
    private readonly string _ext;                 // ".vhdx" — 목록에 보여줄 파일 확장자
    private readonly bool _overwritePrompt;       // 저장 모드: 기존 파일 선택 시 덮어쓰기 확인 여부
    private string? _currentDir;                  // null = 드라이브 목록
    private readonly TextBlock _pathText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ListView _list = new();
    private readonly TextBox _nameBox = new() { FontSize = 13, Padding = new Thickness(4, 3, 4, 3) };
    private readonly Button _okButton;

    /// <summary>확정된 전체 경로. DialogResult가 true일 때만 유효합니다.</summary>
    public string? SelectedPath { get; private set; }

    /// <summary>열기(기존 .vhdx 선택) 대화상자를 띄웁니다. 취소하면 null.</summary>
    public static string? ShowOpen(Window? owner, string title, string ext)
        => Show(owner, title, ext, Mode.Open, null, overwritePrompt: false);

    /// <summary>저장(폴더 이동 + 파일명 입력) 대화상자를 띄웁니다. 취소하면 null.</summary>
    public static string? ShowSave(Window? owner, string title, string ext, string defaultName,
        bool overwritePrompt = true)
        => Show(owner, title, ext, Mode.Save, defaultName, overwritePrompt);

    private static string? Show(Window? owner, string title, string ext, Mode mode, string? defaultName,
        bool overwritePrompt)
    {
        var dlg = new PeFileDialog(title, ext, mode, defaultName, overwritePrompt);
        if (owner is not null) { dlg.Owner = owner; dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg.SelectedPath : null;
    }

    private PeFileDialog(string title, string ext, Mode mode, string? defaultName, bool overwritePrompt)
    {
        _mode = mode;
        _ext = ext;
        _overwritePrompt = overwritePrompt;
        Title = title;
        Width = 620; Height = 460; MinWidth = 480; MinHeight = 340;
        FontSize = 13;
        // ⚠ 색을 여기 적어 두면 <b>어두운 화면에서 이 창만 밝게</b> 남습니다. 그런데 글자는
        //   앱의 팔레트를 따라 밝아지므로, 흰 바탕에 흰 글자가 되어 <b>아무것도 안 보입니다.</b>
        //   하필 이 창은 부팅 USB(WinPE)에서 파일을 고르는 자리라, 부팅이 안 되는 컴퓨터를
        //   살리러 온 사람 앞에서 처음 드러납니다. 팔레트에서 꺼내 씁니다.
        Background = ThemeBrush.Get("SurfaceAlt");
        Foreground = ThemeBrush.Get("TextBrush");
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 경로 줄
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 목록
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 파일명(저장)
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 버튼

        // --- 경로 줄: [⬆ 위로] X:\현재\경로 ---
        var pathRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var upButton = new Button
        {
            Content = L.T("⬆ 위로", "⬆ Up"),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 0),
        };
        upButton.Click += (_, _) => NavigateUp();
        DockPanel.SetDock(upButton, Dock.Left);
        pathRow.Children.Add(upButton);
        pathRow.Children.Add(_pathText);
        Grid.SetRow(pathRow, 0);
        root.Children.Add(pathRow);

        // --- 목록 ---
        var gridView = new GridView();
        gridView.Columns.Add(new GridViewColumn
        {
            Header = L.T("이름", "Name"),
            Width = 430,
            DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Entry.Display)),
        });
        _list.View = gridView;
        // ListView도 Windows가 자기 색(흰 바탕)으로 그립니다 — 창과 함께 맞춥니다.
        _list.Background = ThemeBrush.Get("Surface");
        _list.Foreground = ThemeBrush.Get("TextBrush");
        _list.BorderBrush = ThemeBrush.Get("BorderBrushSoft");
        _list.MouseDoubleClick += (_, _) => ActivateSelection();
        _list.SelectionChanged += (_, _) => OnSelectionChanged();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ActivateSelection(); e.Handled = true; } };
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        // --- 파일명 입력(저장 모드 전용) ---
        if (_mode == Mode.Save)
        {
            var nameRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var label = new TextBlock
            {
                Text = L.T("파일 이름:", "File name:"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            DockPanel.SetDock(label, Dock.Left);
            nameRow.Children.Add(label);
            _nameBox.Text = defaultName ?? "";
            _nameBox.TextChanged += (_, _) => UpdateOkEnabled();
            nameRow.Children.Add(_nameBox);
            Grid.SetRow(nameRow, 2);
            root.Children.Add(nameRow);
        }

        // --- 버튼 ---
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        _okButton = new Button
        {
            Content = _mode == Mode.Open ? L.T("선택", "Select") : L.T("저장", "Save"),
            Padding = new Thickness(18, 5, 18, 5),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
            IsEnabled = false,
        };
        _okButton.Click += (_, _) => Confirm();
        var cancel = new Button
        {
            Content = L.T("취소", "Cancel"),
            Padding = new Thickness(18, 5, 18, 5),
            IsCancel = true,
        };
        buttons.Children.Add(_okButton);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Content = root;
        LoadDrives();
    }

    // --- 탐색 ---------------------------------------------------------------

    private void LoadDrives()
    {
        _currentDir = null;
        _pathText.Text = L.T("드라이브를 선택하세요", "Choose a drive");
        var items = new List<Entry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            // PE의 X:(램디스크)도 그대로 보여줍니다 — 백업 이미지는 보통 다른 드라이브에 있지만
            // 어디에 뭐가 있는지는 사용자가 제일 잘 압니다.
            string detail;
            try
            {
                detail = d.IsReady
                    ? $"{(string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : d.VolumeLabel + ", ")}" +
                      $"{L.T("남음", "free")} {FormatSize(d.AvailableFreeSpace)} / {FormatSize(d.TotalSize)}"
                    : L.T("준비 안 됨", "not ready");
            }
            catch { detail = L.T("준비 안 됨", "not ready"); }
            items.Add(new Entry($"💽 {d.Name}  ({detail})", d.RootDirectory.FullName, IsDir: true, IsDrive: true));
        }
        _list.ItemsSource = items;
        UpdateOkEnabled();
    }

    private void NavigateTo(string dir)
    {
        List<Entry> items;
        try
        {
            var dirs = Directory.GetDirectories(dir)
                .Where(p => (File.GetAttributes(p) & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => new Entry($"📁 {Path.GetFileName(p)}", p, IsDir: true, IsDrive: false));
            var files = Directory.GetFiles(dir, "*" + _ext)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => new Entry($"💾 {Path.GetFileName(p)}  ({FormatSize(new FileInfo(p).Length)})",
                                       p, IsDir: false, IsDrive: false));
            items = [.. dirs, .. files];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                L.T($"폴더를 열지 못했습니다: {ex.Message}", $"Could not open the folder: {ex.Message}"),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _currentDir = dir;
        _pathText.Text = dir;
        _list.ItemsSource = items;
        UpdateOkEnabled();
    }

    private void NavigateUp()
    {
        if (_currentDir is null) return;
        var parent = Directory.GetParent(_currentDir);
        if (parent is null) LoadDrives();
        else NavigateTo(parent.FullName);
    }

    private void ActivateSelection()
    {
        if (_list.SelectedItem is not Entry e || e.Path is null) return;
        if (e.IsDir) NavigateTo(e.Path);
        else if (_mode == Mode.Open) { SelectedPath = e.Path; DialogResult = true; }
    }

    private void OnSelectionChanged()
    {
        // 저장 모드에서 기존 파일을 클릭하면 그 이름을 파일명 칸에 넣어줍니다(덮어쓰기 의도).
        if (_mode == Mode.Save && _list.SelectedItem is Entry { IsDir: false, Path: { } p })
            _nameBox.Text = Path.GetFileName(p);
        UpdateOkEnabled();
    }

    private void UpdateOkEnabled()
    {
        _okButton.IsEnabled = _mode switch
        {
            Mode.Open => _list.SelectedItem is Entry { IsDir: false },
            Mode.Save => _currentDir is not null && !string.IsNullOrWhiteSpace(_nameBox.Text)
                         && _nameBox.Text.IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
            _ => false,
        };
    }

    private void Confirm()
    {
        if (_mode == Mode.Open)
        {
            ActivateSelection();
            return;
        }

        if (_currentDir is null) return;
        string name = _nameBox.Text.Trim();
        if (!name.EndsWith(_ext, StringComparison.OrdinalIgnoreCase)) name += _ext;
        string full = Path.Combine(_currentDir, name);

        // 표준 SaveFileDialog의 OverwritePrompt에 해당하는 확인.
        if (_overwritePrompt && File.Exists(full))
        {
            var answer = MessageBox.Show(this,
                L.T($"{name} 파일이 이미 있습니다. 덮어쓸까요?",
                    $"{name} already exists. Overwrite it?"),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        SelectedPath = full;
        DialogResult = true;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.##} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        _ => $"{bytes / 1024.0:0.#} KB",
    };
}

/// <summary>
/// 파일 선택 진입점 — 일반 Windows에서는 표준 대화상자, WinPE(셸 없음)에서는
/// <see cref="PeFileDialog"/>를 씁니다. 표준 대화상자가 COM 오류로 실패해도 자체 창으로
/// 폴백하므로, 셸이 손상된 환경에서도 파일 선택이 막히지 않습니다.
/// </summary>
public static class FileDialogs
{
    // 판별은 한곳에서만(WinPeEnvironment) — 같은 조건이 세 곳에 흩어져 있었습니다.
    private static bool IsWinPe => DiskMigrator.Windows.Pe.WinPeEnvironment.IsWinPe;

    /// <summary>기존 파일 선택. 취소하면 null.</summary>
    public static string? PickOpen(string title, string filter, string ext)
    {
        if (!IsWinPe)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            }
            catch (COMException) { /* 셸 구성요소 없음 → 자체 창으로 폴백 */ }
        }
        return PeFileDialog.ShowOpen(Application.Current?.MainWindow, title, ext);
    }

    /// <summary>
    /// 저장 위치·이름 선택. 취소하면 null.
    /// <paramref name="overwritePrompt"/>를 끄면 기존 파일 선택 시 덮어쓰기 확인을 띄우지
    /// 않습니다 — 호출자가 덮어쓰지 않을 때(증분 백업으로 이어 쓰기) 사용합니다.
    /// </summary>
    public static string? PickSave(string title, string filter, string ext, string defaultName,
        bool overwritePrompt = true)
    {
        if (!IsWinPe)
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = title, Filter = filter, DefaultExt = ext,
                    FileName = defaultName, OverwritePrompt = overwritePrompt,
                };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            }
            catch (COMException) { /* 셸 구성요소 없음 → 자체 창으로 폴백 */ }
        }
        return PeFileDialog.ShowSave(Application.Current?.MainWindow, title, ext, defaultName, overwritePrompt);
    }
}
