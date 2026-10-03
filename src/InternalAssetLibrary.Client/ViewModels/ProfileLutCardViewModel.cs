using System.ComponentModel;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client.ViewModels;

public sealed class ProfileLutCardViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public ProfileLutCardViewModel(TeamLutSummary lut)
    {
        Lut = lut ?? throw new ArgumentNullException(nameof(lut));
        UiLocalization.Register(this, static value => value.RefreshLocalization());
    }

    public TeamLutSummary Lut { get; }

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

    public string Name => Lut.Name;

    public string FileLabel =>
        $"{Lut.OriginalFileName}  ·  {CloudAssetCardViewModel.FormatSize(Lut.SizeBytes)}  ·  v{Lut.Version}";

    public string NoteLabel => string.IsNullOrWhiteSpace(Lut.Note)
        ? UiLocalization.Text("未填写备注")
        : Lut.Note;

    public string UploaderLabel => $"{Lut.UploadedBy.DisplayName}  ·  @{Lut.UploadedBy.Username}";

    public string UpdatedLabel => UiLocalization.Format(
        "更新于 {0:yyyy-MM-dd HH:mm}",
        Lut.UpdatedAt.ToLocalTime());

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RefreshLocalization()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoteLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdatedLabel)));
    }
}
