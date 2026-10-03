using System.Xml.Linq;

internal static class Release120WiringSelfTests
{
    // 布局接线回归；不冒充原生窗口实际鼠标验收。
    public static void CollapsedFolderListHasNoOverlay()
    {
        var root = RepositoryRoot();
        var xaml = XDocument.Load(Path.Combine(root, "src/InternalAssetLibrary.Client/MainWindow.axaml"));
        XNamespace name = "http://schemas.microsoft.com/winfx/2006/xaml";
        var expanded = xaml.Descendants().Single(x => (string?)x.Attribute(name + "Name") == "ExpandedLocalFolderContent");
        var compact = xaml.Descendants().Single(x => (string?)x.Attribute(name + "Name") == "CompactLocalFolderList");
        Require(expanded.Parent == compact.Parent, "两种文件夹视图必须共享同一布局容器");
        Require(expanded.Descendants().Any(x => (string?)x.Attribute(name + "Name") == "LocalFolderTree"), "必须隐藏整个展开视图而非仅隐藏树");
        var source = File.ReadAllText(Path.Combine(root, "src/InternalAssetLibrary.Client/MainWindow.axaml.cs"));
        Require(source.Contains("ExpandedLocalFolderContent.IsVisible = expanded;"), "展开滚动容器必须随展开状态隐藏");
        Require(source.Contains("CompactLocalFolderList.IsVisible = !expanded;"), "紧凑列表必须与展开视图互斥");
        Require(source.Contains("LocalFolderSidebar.Padding = new Thickness(expanded ? 8 : 0);"), "窄栏必须给48像素图标留完整点击宽度");
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InternalAssetLibrary.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
