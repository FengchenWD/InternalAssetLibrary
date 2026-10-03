using Avalonia.Controls;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Core.Tags;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed record UploadBatchOptions(ApiAssetCategory AudioCategory, string? Notes, IReadOnlyList<string> Tags);

public sealed partial class UploadOptionsWindow : Window
{
    private readonly List<string> _availableTags = [];
    private readonly List<string> _selectedTags = [];
    private Func<string, Task<string>>? _createTagAsync;

    public UploadOptionsWindow()
    {
        InitializeComponent();
        Services.UiLocalization.Apply(this, Services.UiLocalization.CurrentLanguage);
    }

    public UploadOptionsWindow(
        IReadOnlyList<string> filePaths,
        IReadOnlyList<string>? initialTags = null,
        IReadOnlyList<string>? availableTags = null,
        Func<string, Task<string>>? createTagAsync = null)
        : this()
    {
        var audioCount = filePaths.Count(path =>
            MediaExtensionClassifier.TryClassify(path, out var type) && type == LocalMediaType.Audio);
        AudioCategoryPicker.IsEnabled = audioCount > 0;
        FileSummaryText.Text = audioCount == 0
            ? UiLocalization.Format("已选择 {0:N0} 个文件；名称默认使用原文件名。", filePaths.Count)
            : UiLocalization.Format(
                "已选择 {0:N0} 个文件，其中 {1:N0} 个音频；所有音频统一选择 BGM 或音效。",
                filePaths.Count,
                audioCount);
        _selectedTags.AddRange(TagRules.NormalizeLibrary(initialTags));
        _availableTags.AddRange(TagRules.NormalizeLibrary(
            (availableTags ?? []).Concat(_selectedTags)));
        _createTagAsync = createTagAsync;
        UpdateTagSelectionText();
        if (initialTags is not null)
        {
            TagsLabel.Text = UiLocalization.Text("本地标签（随素材上传）");
        }
    }

    private async void SelectTags_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var picker = new TagPickerWindow(
            UiLocalization.Text("选择上传标签"),
            UiLocalization.Text("先添加标签，再从已有标签中勾选；本次选择会统一应用到所有上传素材。"),
            _availableTags,
            _selectedTags,
            _createTagAsync);
        var result = await picker.ShowDialog<TagPickerResult?>(this);
        if (result is null)
        {
            return;
        }

        _selectedTags.Clear();
        _selectedTags.AddRange(result.Tags);
        foreach (var tag in result.Tags)
        {
            if (!_availableTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                _availableTags.Add(tag);
            }
        }

        UpdateTagSelectionText();
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        string[] tags;
        try
        {
            tags = TagRules.NormalizeSelection(_selectedTags);
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = exception.Message;
            return;
        }

        Close(new UploadBatchOptions(
            AudioCategoryPicker.SelectedIndex == 1 ? ApiAssetCategory.SoundEffect : ApiAssetCategory.Bgm,
            string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim(),
            tags));
    }

    private void UpdateTagSelectionText()
    {
        TagSelectionText.Text = _selectedTags.Count == 0
            ? UiLocalization.Text("选择已有标签")
            : _selectedTags.Count <= 3
                ? string.Join("  ·  ", _selectedTags)
                : UiLocalization.Format("已选 {0:N0} 个标签", _selectedTags.Count);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close(null);
}
