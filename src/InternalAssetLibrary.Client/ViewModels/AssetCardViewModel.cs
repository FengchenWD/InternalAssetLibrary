using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client.ViewModels;

public sealed class AssetCardViewModel : INotifyPropertyChanged, IDisposable
{
    private Bitmap? _thumbnail;
    private TimeSpan? _duration;
    private bool _isBatchSelected;
    private bool _isDetailSelected;
    private bool _isBatchMode;
    private double _cardWidth = 218;
    private bool _disposed;

    private AssetCardViewModel(
        Guid id,
        string name,
        string extension,
        LocalMediaType mediaType,
        string sizeLabel,
        string addedLabel,
        LocalAssetAvailability availability,
        string fullPath,
        bool isAvailable,
        IReadOnlyList<string> tags)
    {
        Id = id;
        Name = name;
        Extension = extension;
        MediaType = mediaType;
        SizeLabel = sizeLabel;
        AddedLabel = addedLabel;
        Availability = availability;
        FullPath = fullPath;
        IsAvailable = isAvailable;
        Tags = tags;
        UiLocalization.Register(this, static value => value.RefreshLocalization());
    }

    public Guid Id { get; }

    public string Name { get; }

    public string Extension { get; }

    public LocalMediaType MediaType { get; }

    public string TypeLabel => UiLocalization.Text(MediaType switch
    {
        LocalMediaType.Audio => "音频",
        LocalMediaType.Image => "图片",
        LocalMediaType.Video => "视频",
        _ => "文件"
    });

    public string SizeLabel { get; }

    public string AddedLabel { get; }

    public LocalAssetAvailability Availability { get; }

    public string StatusLabel => UiLocalization.Text(Availability switch
    {
        LocalAssetAvailability.Available => "可用",
        LocalAssetAvailability.OfflineStorage => "磁盘离线",
        _ => "暂不可用"
    });

    public string FullPath { get; }

    public bool IsAvailable { get; }

    public IReadOnlyList<string> Tags { get; }

    public Bitmap? Thumbnail => _thumbnail;

    public bool HasThumbnail => _thumbnail is not null;

    public bool ShowPlaceholder => _thumbnail is null;

    public bool IsTimedMedia => MediaType is LocalMediaType.Audio or LocalMediaType.Video;

    public Stretch ListThumbnailStretch => MediaType == LocalMediaType.Audio
        ? Stretch.Fill
        : Stretch.UniformToFill;

    public string DurationLabel => FormatDuration(_duration);

    public bool IsBatchSelected
    {
        get => _isBatchSelected;
        set
        {
            if (_isBatchSelected == value)
            {
                return;
            }

            _isBatchSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHighlighted));
        }
    }

    public bool IsDetailSelected
    {
        get => _isDetailSelected;
        set
        {
            if (_isDetailSelected == value)
            {
                return;
            }

            _isDetailSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHighlighted));
        }
    }

    public bool IsBatchMode
    {
        get => _isBatchMode;
        set
        {
            if (_isBatchMode == value)
            {
                return;
            }

            _isBatchMode = value;
            OnPropertyChanged();
        }
    }

    public bool IsHighlighted => IsBatchSelected || IsDetailSelected;

    public double CardWidth
    {
        get => _cardWidth;
        set
        {
            if (Math.Abs(_cardWidth - value) < 0.1)
            {
                return;
            }

            _cardWidth = value;
            OnPropertyChanged();
        }
    }

    public string Initial => string.IsNullOrWhiteSpace(Name)
        ? "?"
        : Name[..1].ToUpperInvariant();

    public string TagsLabel => Tags.Count == 0
        ? UiLocalization.Text("未添加标签")
        : string.Join("  ·  ", Tags);

    public void SetThumbnail(Bitmap thumbnail)
    {
        ArgumentNullException.ThrowIfNull(thumbnail);
        if (_disposed)
        {
            thumbnail.Dispose();
            return;
        }

        var previous = _thumbnail;
        _thumbnail = thumbnail;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(ShowPlaceholder));
        DeferredUiResourceDisposer.Dispose(previous);
    }

    public void SetDuration(TimeSpan? duration)
    {
        TimeSpan? normalized = duration is { } value && value >= TimeSpan.Zero ? value : null;
        if (_duration == normalized)
        {
            return;
        }

        _duration = normalized;
        OnPropertyChanged(nameof(DurationLabel));
    }

    public static AssetCardViewModel FromLocalAsset(LocalAsset asset) => new(
        asset.Id,
        Path.GetFileNameWithoutExtension(asset.FileName),
        asset.Extension.TrimStart('.').ToUpperInvariant(),
        asset.MediaType,
        FormatSize(asset.SizeBytes),
        asset.AddedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        asset.Availability,
        asset.FullPath,
        asset.IsAvailable,
        asset.Tags);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } value)
        {
            return "--:--";
        }

        var rounded = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(value.TotalSeconds)));
        return rounded.TotalHours >= 1
            ? $"{(int)rounded.TotalHours:00}:{rounded.Minutes:00}:{rounded.Seconds:00}"
            : $"{rounded.Minutes:00}:{rounded.Seconds:00}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void RefreshLocalization()
    {
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(TagsLabel));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var thumbnail = _thumbnail;
        _thumbnail = null;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(ShowPlaceholder));
        DeferredUiResourceDisposer.Dispose(thumbnail);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
