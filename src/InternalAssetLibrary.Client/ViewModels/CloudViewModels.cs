using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client.ViewModels;

public sealed class CloudAssetCardViewModel : INotifyPropertyChanged, IDisposable
{
    private Bitmap? _thumbnail;
    private bool _isSelected;
    private bool _isDetailSelected;
    private double _cardWidth = 218;
    private bool _disposed;

    public CloudAssetCardViewModel(ApiAsset asset)
    {
        Asset = asset;
        UiLocalization.Register(this, static value => value.RefreshLocalization());
    }

    public ApiAsset Asset { get; }

    public Guid Id => Asset.Id;

    public string Name => Asset.Name;

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    public string Extension => Asset.Extension.TrimStart('.').ToUpperInvariant();

    public string CategoryLabel => UiLocalization.Text(Asset.Category switch
    {
        ApiAssetCategory.Bgm => "BGM",
        ApiAssetCategory.SoundEffect => "音效",
        ApiAssetCategory.Image => "图片",
        ApiAssetCategory.Video => "视频",
        _ => "素材"
    });

    public string SizeLabel => FormatSize(Asset.SizeBytes);

    public string UploaderLabel
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Asset.UploadedBy.DisplayName)
                ? Asset.UploadedBy.Username
                : Asset.UploadedBy.DisplayName;
            return $"{name}  ·  {Asset.UploadedAt.ToLocalTime():yyyy-MM-dd}";
        }
    }

    public string TagsLabel => Asset.Tags.Count == 0
        ? UiLocalization.Text("未添加标签")
        : string.Join("  ·  ", Asset.Tags);

    public string StatusLabel => UiLocalization.Text(Asset.HasOriginal ? "可下载" : "等待上传");

    public Bitmap? Thumbnail => _thumbnail;

    public bool HasThumbnail => _thumbnail is not null;

    public bool ShowPlaceholder => _thumbnail is null;

    public bool IsTimedMedia => Asset.Category is ApiAssetCategory.Bgm or
        ApiAssetCategory.SoundEffect or ApiAssetCategory.Video;

    public Stretch ListThumbnailStretch => Asset.Category is ApiAssetCategory.Bgm or ApiAssetCategory.SoundEffect
        ? Stretch.Fill
        : Stretch.UniformToFill;

    public string DurationLabel => FormatDuration(Asset.DurationSeconds);

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDetailSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
        }
    }

    public bool IsHighlighted => IsSelected || IsDetailSelected;

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardWidth)));
        }
    }

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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThumbnail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPlaceholder)));
        DeferredUiResourceDisposer.Dispose(previous);
    }

    public static string FormatSize(long bytes)
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

    private static string FormatDuration(double? durationSeconds)
    {
        if (durationSeconds is not { } seconds || !double.IsFinite(seconds) || seconds < 0)
        {
            return "--:--";
        }

        var duration = TimeSpan.FromSeconds(Math.Floor(seconds));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThumbnail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPlaceholder)));
        DeferredUiResourceDisposer.Dispose(thumbnail);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RefreshLocalization()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CategoryLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagsLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
    }
}

public sealed class UserCardViewModel : INotifyPropertyChanged, IDisposable
{
    private Bitmap? _avatar;
    private bool _disposed;

    public UserCardViewModel(ApiPublicUserProfile profile, Guid? currentUserId = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        CurrentUserId = currentUserId;
        UiLocalization.Register(this, static value => value.RefreshLocalization());
    }

    public ApiPublicUserProfile Profile { get; }

    public Guid? CurrentUserId { get; }

    public Guid Id => Profile.Id;

    public string DisplayLabel => string.IsNullOrWhiteSpace(Profile.DisplayName)
        ? $"@{Profile.Username}"
        : UiLocalization.Format("{0}（{1}）", Profile.DisplayName, $"@{Profile.Username}");

    public string Initial => string.IsNullOrWhiteSpace(Profile.DisplayName)
        ? Profile.Username[..1].ToUpperInvariant()
        : Profile.DisplayName[..1].ToUpperInvariant();

    public Bitmap? Avatar => _avatar;

    public bool HasAvatar => _avatar is not null;

    public bool ShowPlaceholder => _avatar is null;

    public bool IsCurrent => CurrentUserId == Profile.Id;

    public string StatusLabel => UiLocalization.Text(IsCurrent
        ? "当前账号"
        : Profile.IsEnabled
            ? "可用"
            : "已停用");

    public string AssetCountLabel
    {
        get
        {
            var total = Profile.AssetCounts.Values.Sum();
            return UiLocalization.Format("上传 {0:N0} 个素材", total);
        }
    }

    public void SetAvatar(Bitmap avatar)
    {
        ArgumentNullException.ThrowIfNull(avatar);
        if (_disposed)
        {
            avatar.Dispose();
            return;
        }

        if (ReferenceEquals(_avatar, avatar))
        {
            return;
        }

        var previous = _avatar;
        _avatar = avatar;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Avatar)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAvatar)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPlaceholder)));
        DeferredUiResourceDisposer.Dispose(previous);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var avatar = _avatar;
        _avatar = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Avatar)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAvatar)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPlaceholder)));
        DeferredUiResourceDisposer.Dispose(avatar);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RefreshLocalization()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AssetCountLabel)));
    }
}

public sealed class RecycleBinAssetViewModel : INotifyPropertyChanged
{
    public RecycleBinAssetViewModel(ApiAsset asset)
    {
        Asset = asset;
        UiLocalization.Register(this, static value => value.RefreshLocalization());
    }

    public ApiAsset Asset { get; }

    public Guid Id => Asset.Id;

    public string Name => Asset.Name;

    public string Extension => Asset.Extension.TrimStart('.').ToUpperInvariant();

    public string CategoryLabel => UiLocalization.Text(Asset.Category switch
    {
        ApiAssetCategory.Bgm => "BGM",
        ApiAssetCategory.SoundEffect => "音效",
        ApiAssetCategory.Image => "图片",
        ApiAssetCategory.Video => "视频",
        _ => "素材"
    });

    public string FileLabel => $"{Asset.OriginalFileName}  ·  {CloudAssetCardViewModel.FormatSize(Asset.SizeBytes)}";

    public string TagsLabel => Asset.Tags.Count == 0
        ? UiLocalization.Text("未添加标签")
        : string.Join("  ·  ", Asset.Tags);

    public string RecycledLabel => Asset.RecycledAt is { } recycledAt
        ? UiLocalization.Format("移入时间 {0:yyyy-MM-dd HH:mm}", recycledAt.ToLocalTime())
        : UiLocalization.Text("已移入回收站");

    public string PurgeLabel => Asset.PurgeAfter is { } purgeAfter
        ? UiLocalization.Format("预计清理 {0:yyyy-MM-dd HH:mm}", purgeAfter.ToLocalTime())
        : UiLocalization.Text("等待清理");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RefreshLocalization()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CategoryLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagsLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecycledLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PurgeLabel)));
    }
}

public sealed class CloudTagFilterItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public CloudTagFilterItemViewModel(string name, bool isAll, bool isSelected)
    {
        _name = name;
        IsAll = isAll;
        _isSelected = isSelected;
        UiLocalization.Register(this, static value =>
            value.PropertyChanged?.Invoke(value, new PropertyChangedEventArgs(nameof(Name))));
    }

    private readonly string _name;

    public string Name => IsAll ? UiLocalization.Text(_name) : _name;

    public bool IsAll { get; }

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

public sealed class TransferItemViewModel : INotifyPropertyChanged
{
    public InternalAssetLibrary.Client.Core.Transfers.TransferTask? Control { get; set; }
    public Guid TaskId { get; set; }
    public bool IsUpload => _direction == "上传";
    public bool CanPause => Control?.State is InternalAssetLibrary.Client.Core.Transfers.TransferTaskState.Queued or InternalAssetLibrary.Client.Core.Transfers.TransferTaskState.Running;
    public bool CanResume => Control?.State is InternalAssetLibrary.Client.Core.Transfers.TransferTaskState.Paused or InternalAssetLibrary.Client.Core.Transfers.TransferTaskState.Failed;
    public bool CanCancel => Control is not null && Control.State is not (InternalAssetLibrary.Client.Core.Transfers.TransferTaskState.Canceled or InternalAssetLibrary.Client.Core.Transfers.TransferTaskState.Completed or InternalAssetLibrary.Client.Core.Transfers.TransferTaskState.Canceling);
    public void RefreshControl()
    {
        foreach (var property in new[] { nameof(CanPause), nameof(CanResume), nameof(CanCancel) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    private double _progress;
    private string _status = "等待中";
    private readonly string _direction;

    public TransferItemViewModel(string name, string direction)
    {
        Name = name;
        _direction = direction;
        UiLocalization.Register(this, static value => value.RefreshLocalization());
    }

    public string Name { get; }

    public string Direction => UiLocalization.Text(_direction);

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, Math.Clamp(value, 0, 100));
    }

    public string ProgressLabel => $"{Progress:0}%";

    public string Status
    {
        get => LocalizeStatus(_status);
        set => SetField(ref _status, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string LocalizeStatus(string status)
    {
        const string failurePrefix = "失败：";
        return status.StartsWith(failurePrefix, StringComparison.Ordinal)
            ? UiLocalization.Format("失败：{0}", status[failurePrefix.Length..])
            : UiLocalization.Text(status);
    }

    private void RefreshLocalization()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Direction)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(Progress))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressLabel)));
        }
    }
}
