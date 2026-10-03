using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Client.ViewModels;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client;

public sealed partial class MainWindow
{
    private IReadOnlyDictionary<string, int> _profileAssetCountSnapshot =
        new Dictionary<string, int>();
    private IReadOnlyList<TeamLutSummary> _profileLutSource = [];
    private bool _profileShowsLuts;

    public ObservableCollection<ProfileLutCardViewModel> ProfileLuts { get; } = [];

    private async Task<IReadOnlyList<TeamLutSummary>> ListAllProfileLutsAsync(
        Guid uploaderId,
        CancellationToken cancellationToken = default)
    {
        if (_api is null)
        {
            return [];
        }

        const int pageSize = 200;
        var luts = new List<TeamLutSummary>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _api.ListTeamLutsAsync(new ApiTeamLutListQuery(
                Page: pageNumber,
                PageSize: pageSize,
                UploaderId: uploaderId), cancellationToken);
            luts.AddRange(page.Items.Where(lut =>
                lut.State == TeamLutState.Active &&
                lut.HasContent &&
                lut.UploadedBy.Id == uploaderId));
            if (page.Items.Count == 0 || pageNumber * pageSize >= page.Total)
            {
                return luts;
            }
        }
    }

    private void UpdateProfileUploadCountLabels()
    {
        var assetTotal = _profileAssetCountSnapshot.Values.Sum();
        UiLocalization.SetContent(ProfileAllCount, "全部  {0:N0}", assetTotal);
        ProfileBgmCount.Content = $"BGM  {Count("bgm"):N0}";
        UiLocalization.SetContent(ProfileSoundCount, "音效  {0:N0}", Count("sound-effect"));
        UiLocalization.SetContent(ProfileImageCount, "图片  {0:N0}", Count("image"));
        UiLocalization.SetContent(ProfileVideoCount, "视频  {0:N0}", Count("video"));
        ProfileLutCount.Content = $"LUT  {_profileLutSource.Count:N0}";
        return;

        int Count(string key) =>
            _profileAssetCountSnapshot.TryGetValue(key, out var count) ? count : 0;
    }

    private void RebuildProfileLutCards()
    {
        ProfileLuts.Clear();
        foreach (var lut in _profileLutSource)
        {
            ProfileLuts.Add(new ProfileLutCardViewModel(lut));
        }

        ProfileLutItems.IsVisible = _profileShowsLuts;
        ProfileLutsEmptyText.IsVisible = _profileShowsLuts && ProfileLuts.Count == 0;
        ProfileLutCount.Classes.Set("selected", _profileShowsLuts);
    }

    private async void ProfileLut_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ProfileLutCardViewModel })
        {
            return;
        }

        try
        {
            await CreateLutLibraryWindow().ShowDialog(this);
            if (_displayedProfileUserId is not null)
            {
                await RefreshDisplayedProfileAssetsAsync();
                _profileShowsLuts = true;
                _profileAssetCategory = null;
                ApplyProfileAssetFilter();
            }
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(
                ProfileStatusText,
                "LUT 列表刷新失败：{0}",
                UserMessage(exception));
        }
    }
}
