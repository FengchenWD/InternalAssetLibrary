using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed partial class PlayerQueueAddWindow : Window
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly List<PlayerQueueAssetOption> _allAssets = [];
    private PlayerQueueFolderOption? _selectedFolder;
    private bool? _usesNarrowLayout;

    public PlayerQueueAddWindow()
    {
        InitializeComponent();
        DataContext = this;
        UiLocalization.Apply(this, UiLocalization.CurrentLanguage);
        SizeChanged += PlayerQueueAddWindow_OnSizeChanged;
        UpdateResponsiveLayout(Width);
    }

    public PlayerQueueAddWindow(IEnumerable<LocalAsset> assets)
        : this()
    {
        _allAssets.AddRange(assets
            .Select(asset => new PlayerQueueAssetOption(asset))
            .OrderBy(asset => asset.FullPath, PathComparer));
        BuildFolderTree();
        ApplyFilters();
        UpdateSelectionSummary();
    }

    public ObservableCollection<PlayerQueueAssetOption> Assets { get; } = [];

    public ObservableCollection<PlayerQueueFolderOption> FolderNodes { get; } = [];

    private void ChooseComputerFiles_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        Close(new PlayerQueueAddSelection(UseFilePicker: true, []));

    private void AllFolders_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _selectedFolder = null;
        FolderTree.SelectedItem = null;
        AllFoldersButton.Classes.Set("selected", true);
        ApplyFilters();
    }

    private void FolderTree_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        _selectedFolder = FolderTree.SelectedItem as PlayerQueueFolderOption;
        AllFoldersButton.Classes.Set("selected", _selectedFolder is null);
        ApplyFilters();
    }

    private void SearchBox_OnTextChanged(object? sender, TextChangedEventArgs eventArgs) => ApplyFilters();

    private void AssetOption_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is ToggleButton { DataContext: PlayerQueueAssetOption option } toggle)
        {
            option.IsSelected = toggle.IsChecked == true;
        }

        UpdateSelectionSummary();
    }

    private void AddSelected_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var ids = _allAssets
            .Where(item => item.IsSelected)
            .Select(item => item.Id)
            .ToArray();
        if (ids.Length > 0)
        {
            Close(new PlayerQueueAddSelection(UseFilePicker: false, ids));
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private void BuildFolderTree()
    {
        FolderNodes.Clear();
        AllFolderCountText.Text = _allAssets.Count.ToString("N0");

        foreach (var folderAssets in _allAssets
                     .GroupBy(asset => asset.FolderId)
                     .OrderBy(group => InferRootPath(group.First()), PathComparer))
        {
            var assets = folderAssets.ToArray();
            var rootPath = InferRootPath(assets[0]);
            var root = new PlayerQueueFolderOption(
                assets[0].FolderId,
                string.Empty,
                rootPath,
                GetFolderDisplayName(rootPath),
                assets.Length);
            FolderNodes.Add(root);

            var relativeDirectories = assets
                .Select(asset => asset.RelativeDirectoryPath)
                .Where(path => path.Length > 0)
                .SelectMany(EnumerateDirectoryAndParents)
                .Distinct(PathComparer)
                .OrderBy(RelativePathDepth)
                .ThenBy(path => path, PathComparer)
                .ToArray();
            var nodesByPath = new Dictionary<string, PlayerQueueFolderOption>(PathComparer);

            foreach (var relativePath in relativeDirectories)
            {
                var node = new PlayerQueueFolderOption(
                    assets[0].FolderId,
                    relativePath,
                    Path.GetFullPath(Path.Combine(rootPath, relativePath)),
                    Path.GetFileName(relativePath),
                    assets.Count(asset => IsAssetWithinDirectory(asset, relativePath)));
                nodesByPath.Add(relativePath, node);

                var parentPath = NormalizeRelativePath(Path.GetDirectoryName(relativePath));
                if (parentPath.Length > 0 && nodesByPath.TryGetValue(parentPath, out var parent))
                {
                    parent.Children.Add(node);
                }
                else
                {
                    root.Children.Add(node);
                }
            }
        }
    }

    private void ApplyFilters()
    {
        var query = SearchBox.Text?.Trim();
        var visible = _allAssets.Where(asset =>
            IsWithinSelectedFolder(asset) &&
            (string.IsNullOrEmpty(query) || asset.Matches(query)));

        Assets.Clear();
        foreach (var asset in visible)
        {
            Assets.Add(asset);
        }

        UiLocalization.SetText(ResultSummaryText, "显示 {0:N0} 项", Assets.Count);
        EmptyStateText.IsVisible = Assets.Count == 0;
        UiLocalization.SetText(
            EmptyStateText,
            _allAssets.Count == 0
                ? "本地素材库中没有可加入队列的素材"
                : "没有符合当前文件夹或搜索条件的素材");
    }

    private bool IsWithinSelectedFolder(PlayerQueueAssetOption asset) =>
        _selectedFolder is null ||
        asset.FolderId == _selectedFolder.FolderId &&
        (_selectedFolder.RelativePath.Length == 0 ||
         IsAssetWithinDirectory(asset, _selectedFolder.RelativePath));

    private void UpdateSelectionSummary()
    {
        var selectedCount = _allAssets.Count(item => item.IsSelected);
        AddSelectedButton.IsEnabled = selectedCount > 0;
        UiLocalization.SetText(
            SelectionSummaryText,
            selectedCount == 0 ? "未选择素材" : "已选 {0:N0} 项",
            selectedCount);
    }

    private void PlayerQueueAddWindow_OnSizeChanged(object? sender, SizeChangedEventArgs eventArgs) =>
        UpdateResponsiveLayout(eventArgs.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        var useNarrowLayout = width < 760;
        if (_usesNarrowLayout == useNarrowLayout)
        {
            return;
        }

        _usesNarrowLayout = useNarrowLayout;
        LibraryBodyGrid.ColumnDefinitions.Clear();
        LibraryBodyGrid.RowDefinitions.Clear();

        if (useNarrowLayout)
        {
            LibraryBodyGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            LibraryBodyGrid.RowDefinitions.Add(new RowDefinition(new GridLength(150)));
            LibraryBodyGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Grid.SetColumn(LibraryFolderPane, 0);
            Grid.SetRow(LibraryFolderPane, 0);
            Grid.SetColumn(LibraryAssetPane, 0);
            Grid.SetRow(LibraryAssetPane, 1);
            LibraryFolderPane.Margin = new Thickness(0, 0, 0, 10);
            return;
        }

        LibraryBodyGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(220)));
        LibraryBodyGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        LibraryBodyGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        Grid.SetColumn(LibraryFolderPane, 0);
        Grid.SetRow(LibraryFolderPane, 0);
        Grid.SetColumn(LibraryAssetPane, 1);
        Grid.SetRow(LibraryAssetPane, 0);
        LibraryFolderPane.Margin = new Thickness(0, 0, 10, 0);
    }

    private static IEnumerable<string> EnumerateDirectoryAndParents(string relativePath)
    {
        var parts = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        foreach (var part in parts)
        {
            current = current.Length == 0 ? part : Path.Combine(current, part);
            yield return current;
        }
    }

    private static bool IsAssetWithinDirectory(PlayerQueueAssetOption asset, string relativeDirectoryPath) =>
        PathComparer.Equals(asset.RelativeDirectoryPath, relativeDirectoryPath) ||
        asset.RelativeDirectoryPath.StartsWith(
            relativeDirectoryPath + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string InferRootPath(PlayerQueueAssetOption asset)
    {
        var current = Path.GetFullPath(asset.FullPath);
        var relativeParts = asset.RelativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var _ in relativeParts)
        {
            current = Path.GetDirectoryName(current) ?? current;
        }

        return current;
    }

    private static string NormalizeRelativePath(string? relativePath) =>
        string.IsNullOrWhiteSpace(relativePath) || relativePath == "."
            ? string.Empty
            : relativePath
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Trim(Path.DirectorySeparatorChar);

    private static int RelativePathDepth(string relativePath) =>
        NormalizeRelativePath(relativePath).Count(character => character == Path.DirectorySeparatorChar);

    private static string GetFolderDisplayName(string folderPath)
    {
        var trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? folderPath : name;
    }
}

public sealed record PlayerQueueAddSelection(bool UseFilePicker, IReadOnlyList<Guid> LocalAssetIds);

public sealed class PlayerQueueAssetOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public PlayerQueueAssetOption(LocalAsset asset)
    {
        Id = asset.Id;
        FolderId = asset.FolderId;
        Name = Path.GetFileNameWithoutExtension(asset.FileName);
        FileName = asset.FileName;
        FullPath = asset.FullPath;
        RelativePath = NormalizeRelativePath(asset.RelativePath);
        RelativeDirectoryPath = NormalizeRelativePath(Path.GetDirectoryName(RelativePath));
        Extension = asset.Extension;
        TypeLabel = UiLocalization.Text(asset.MediaType switch
        {
            LocalMediaType.Audio => "音频",
            LocalMediaType.Video => "视频",
            LocalMediaType.Image => "图片",
            _ => "文件"
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }

    public Guid FolderId { get; }

    public string Name { get; }

    public string FileName { get; }

    public string FullPath { get; }

    public string RelativePath { get; }

    public string RelativeDirectoryPath { get; }

    public string Extension { get; }

    public string TypeLabel { get; }

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

    public bool Matches(string query) =>
        FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        FullPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Extension.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string? relativePath) =>
        string.IsNullOrWhiteSpace(relativePath) || relativePath == "."
            ? string.Empty
            : relativePath
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Trim(Path.DirectorySeparatorChar);
}

public sealed class PlayerQueueFolderOption(
    Guid folderId,
    string relativePath,
    string fullPath,
    string displayName,
    int assetCount)
{
    public Guid FolderId { get; } = folderId;

    public string RelativePath { get; } = relativePath;

    public string FullPath { get; } = fullPath;

    public string DisplayName { get; } = displayName;

    public string AssetCountLabel { get; } = assetCount.ToString("N0");

    public ObservableCollection<PlayerQueueFolderOption> Children { get; } = [];
}
