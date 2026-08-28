using FileExplorer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileExplorer.Views;

/// Picks the tab template: the fixed "Home" workspace (drive picker + system drive, non-closable)
/// gets its own layout; every other workspace uses the standard dual-pane template.
public sealed partial class TabItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Normal { get; set; }
    public DataTemplate? Home { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is TabViewModel { IsHome: true } ? Home : Normal;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
