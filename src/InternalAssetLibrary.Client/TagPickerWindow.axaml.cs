using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.Tags;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed record TagPickerResult(IReadOnlyList<string> Tags);

public sealed record TagPickerEntry(Guid? Id, string Name, int? UsageCount = null);

public sealed partial class TagPickerWindow : Window
{
    private readonly Func<string, Task<TagPickerEntry>>? _createTagAsync;
    private readonly Func<TagPickerEntry, Task>? _deleteTagAsync;
    private readonly List<TagPickerItemViewModel> _allItems = [];
    private bool _isBusy;

    public TagPickerWindow()
    {
        InitializeComponent();
        DataContext = this;
        Services.UiLocalization.Apply(this, Services.UiLocalization.CurrentLanguage);
        Closing += TagPickerWindow_OnClosing;
    }

    public TagPickerWindow(
        string title,
        string description,
        IEnumerable<string> availableTags,
        IEnumerable<string>? selectedTags,
        Func<string, Task<string>>? createTagAsync)
        : this(
            title,
            description,
            availableTags.Select(name => new TagPickerEntry(null, name)),
            selectedTags,
            createTagAsync is null
                ? null
                : async name => new TagPickerEntry(null, await createTagAsync(name)),
            deleteTagAsync: null)
    {
    }

    public TagPickerWindow(
        string title,
        string description,
        IEnumerable<TagPickerEntry> availableTags,
        IEnumerable<string>? selectedTags,
        Func<string, Task<TagPickerEntry>>? createTagAsync,
        Func<TagPickerEntry, Task>? deleteTagAsync)
        : this()
    {
        DialogTitle = UiLocalization.Text(title);
        Description = UiLocalization.Text(description);
        _createTagAsync = createTagAsync;
        _deleteTagAsync = deleteTagAsync;
        CreateTagPanel.IsVisible = createTagAsync is not null;

        var initialSelection = selectedTags?.ToArray() ?? [];
        var selected = new HashSet<string>(initialSelection, StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, TagPickerEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in availableTags)
        {
            var normalizedName = TagRules.NormalizeName(entry.Name);
            entries.TryAdd(normalizedName, entry with { Name = normalizedName });
        }

        foreach (var name in TagRules.NormalizeLibrary(initialSelection))
        {
            entries.TryAdd(name, new TagPickerEntry(null, name));
        }

        foreach (var entry in entries.Values.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            AddItem(entry, selected.Contains(entry.Name));
        }

        DataContext = null;
        DataContext = this;
        RefreshVisibleItems();
    }

    public string DialogTitle { get; private set; } = UiLocalization.Text("选择标签");

    public string Description { get; private set; } =
        UiLocalization.Format("勾选要添加到素材的标签，最多 {0} 个。", TagRules.MaximumTagsPerAsset);

    public ObservableCollection<TagPickerItemViewModel> VisibleItems { get; } = [];

    private async void CreateTag_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await CreateTagAsync();

    private async void NewTagNameBox_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter)
        {
            return;
        }

        eventArgs.Handled = true;
        await CreateTagAsync();
    }

    private async Task CreateTagAsync()
    {
        if (_createTagAsync is null || _isBusy)
        {
            return;
        }

        StatusText.Text = string.Empty;
        string name;
        try
        {
            name = TagRules.NormalizeName(NewTagNameBox.Text);
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = TagRuleMessage(exception);
            return;
        }

        if (_allItems.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = UiLocalization.Text("该标签已存在，可直接在下方勾选。");
            return;
        }

        SetBusy(true);
        try
        {
            var created = await _createTagAsync(name);
            var persistedName = TagRules.NormalizeName(created.Name);
            AddItem(created with { Name = persistedName }, isSelected: false);
            NewTagNameBox.Text = string.Empty;
            SearchBox.Text = string.Empty;
            RefreshVisibleItems();
        }
        catch (Exception exception)
        {
            StatusText.Text = UiLocalization.Text(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DeleteTag_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_deleteTagAsync is null ||
            sender is not Button { DataContext: TagPickerItemViewModel item } ||
            _isBusy ||
            item.IsDeleting)
        {
            return;
        }

        var message = item.UsageCount switch
        {
            0 => UiLocalization.Format("标签“{0}”当前未用于任何素材。确定删除吗？", item.Name),
            > 0 => UiLocalization.Format(
                "标签“{0}”目前用于 {1:N0} 个素材。删除后，该标签会从这些素材上移除。",
                item.Name,
                item.UsageCount),
            _ => UiLocalization.Format(
                "确定删除标签“{0}”吗？删除后，该标签会从所有使用它的素材上移除。",
                item.Name)
        };
        var confirmed = await new MessageDialogWindow(
            "删除标签",
            message,
            "确认删除",
            "取消").ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        StatusText.Text = string.Empty;
        item.IsDeleting = true;
        SetBusy(true);
        try
        {
            await _deleteTagAsync(item.Entry);
            item.PropertyChanged -= Item_OnPropertyChanged;
            _allItems.Remove(item);
            RefreshVisibleItems();
        }
        catch (Exception exception)
        {
            StatusText.Text = UiLocalization.Text(exception.Message);
        }
        finally
        {
            if (_allItems.Contains(item))
            {
                item.IsDeleting = false;
            }

            SetBusy(false);
        }
    }

    private void SearchBox_OnTextChanged(object? sender, TextChangedEventArgs eventArgs) =>
        RefreshVisibleItems();

    private void Confirm_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isBusy)
        {
            return;
        }

        var selected = _allItems
            .Where(item => item.IsSelected)
            .Select(item => item.Name)
            .ToArray();
        try
        {
            Close(new TagPickerResult(TagRules.NormalizeSelection(selected)));
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = TagRuleMessage(exception);
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (!_isBusy)
        {
            Close(null);
        }
    }

    private void TagPickerWindow_OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_isBusy)
        {
            eventArgs.Cancel = true;
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        DialogContent.IsEnabled = !isBusy;
    }

    private void AddItem(TagPickerEntry entry, bool isSelected)
    {
        if (_allItems.Any(item => item.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var item = new TagPickerItemViewModel(entry, isSelected, _deleteTagAsync is not null);
        item.PropertyChanged += Item_OnPropertyChanged;
        _allItems.Add(item);
        _allItems.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
    }

    private void Item_OnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(TagPickerItemViewModel.IsSelected))
        {
            UpdateSelectionSummary();
        }
    }

    private void RefreshVisibleItems()
    {
        var search = SearchBox.Text?.Trim();
        VisibleItems.Clear();
        foreach (var item in _allItems.Where(item =>
                     string.IsNullOrEmpty(search) ||
                     item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            VisibleItems.Add(item);
        }

        EmptyText.IsVisible = VisibleItems.Count == 0;
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        var selectedCount = _allItems.Count(item => item.IsSelected);
        SelectionSummaryText.Text = UiLocalization.Format(
            "已选 {0:N0} / {1:N0}",
            selectedCount,
            TagRules.MaximumTagsPerAsset);
        if (selectedCount <= TagRules.MaximumTagsPerAsset &&
            string.Equals(
                StatusText.Text,
                UiLocalization.Format("每个素材最多可添加 {0} 个标签。", TagRules.MaximumTagsPerAsset),
                StringComparison.Ordinal))
        {
            StatusText.Text = string.Empty;
        }
    }

    private static string TagRuleMessage(ArgumentException exception) => exception.ParamName switch
    {
        "name" => UiLocalization.Format("标签名称必须为 1 至 {0} 个字符。", TagRules.MaximumNameLength),
        "tags" => UiLocalization.Format("每个素材最多可添加 {0} 个标签。", TagRules.MaximumTagsPerAsset),
        _ => UiLocalization.Text(exception.Message)
    };
}

public sealed class TagPickerItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isDeleting;

    public TagPickerItemViewModel(TagPickerEntry entry, bool isSelected, bool canDelete)
    {
        Entry = entry;
        _isSelected = isSelected;
        CanDelete = canDelete;
    }

    public TagPickerEntry Entry { get; }

    public string Name => Entry.Name;

    public int? UsageCount => Entry.UsageCount;

    public bool HasUsageCount => UsageCount.HasValue;

    public string UsageText => UiLocalization.Format("{0:N0} 个素材", UsageCount.GetValueOrDefault());

    public bool CanDelete { get; }

    public bool IsInteractive => !_isDeleting;

    public bool IsDeleting
    {
        get => _isDeleting;
        set
        {
            if (_isDeleting == value)
            {
                return;
            }

            _isDeleting = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDeleting)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInteractive)));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
