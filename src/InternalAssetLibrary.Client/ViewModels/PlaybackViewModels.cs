using System.ComponentModel;
using System.Runtime.CompilerServices;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client.ViewModels;

public sealed class PlayerQueueItemViewModel : INotifyPropertyChanged
{
    private bool _isCurrent;

    private readonly string _kindLabel;

    public PlayerQueueItemViewModel(Guid id, string source, string name, string kindLabel)
    {
        Id = id;
        Source = source;
        Name = name;
        _kindLabel = kindLabel;
        UiLocalization.Register(this, static value =>
            value.PropertyChanged?.Invoke(value, new PropertyChangedEventArgs(nameof(KindLabel))));
    }

    public Guid Id { get; }

    public string Source { get; }

    public string Name { get; }

    public string KindLabel => UiLocalization.Text(_kindLabel);

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AudioTrackOptionViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private readonly Func<string> _labelFactory;

    public AudioTrackOptionViewModel(long id, Func<string> labelFactory, bool isSelected)
    {
        Id = id;
        _labelFactory = labelFactory;
        _isSelected = isSelected;
        UiLocalization.Register(this, static value =>
            value.PropertyChanged?.Invoke(value, new PropertyChangedEventArgs(nameof(Label))));
    }

    public long Id { get; }

    public string Label => _labelFactory();

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

public sealed class LutOptionViewModel : INotifyPropertyChanged
{
    public LutOptionViewModel(
        string key,
        string name,
        string sourceLabel,
        string? localPath,
        Guid? cloudId,
        int? cloudVersion,
        bool isAvailable)
    {
        Key = key;
        Name = name;
        SourceLabel = sourceLabel;
        LocalPath = localPath;
        CloudId = cloudId;
        CloudVersion = cloudVersion;
        IsAvailable = isAvailable;
        UiLocalization.Register(this, static value =>
            value.PropertyChanged?.Invoke(value, new PropertyChangedEventArgs(nameof(DisplayLabel))));
    }

    public string Key { get; }
    public string Name { get; }
    public string SourceLabel { get; }
    public string? LocalPath { get; }
    public Guid? CloudId { get; }
    public int? CloudVersion { get; }
    public bool IsAvailable { get; }

    public string DisplayLabel => IsAvailable
        ? $"{Name} · {UiLocalization.Text(SourceLabel)}"
        : UiLocalization.Format("{0} · {1}（不可用）", Name, UiLocalization.Text(SourceLabel));

    public override string ToString() => DisplayLabel;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record PlayerMarkerSetOptionViewModel(Guid Id, string DisplayLabel)
{
    public override string ToString() => DisplayLabel;
}

public sealed record PlayerMarkerItemViewModel(
    Guid Id,
    TimeSpan Time,
    string? Name,
    string? Note)
{
    public string TimeLabel
    {
        get
        {
            var hours = (long)Math.Floor(Time.TotalHours);
            return $"{hours:00}:{Time.Minutes:00}:{Time.Seconds:00}";
        }
    }

    public string NameLabel => string.IsNullOrWhiteSpace(Name)
        ? UiLocalization.Text("未命名标记")
        : Name;

    public string NoteLabel => string.IsNullOrWhiteSpace(Note)
        ? UiLocalization.Text("无备注")
        : Note;
}
