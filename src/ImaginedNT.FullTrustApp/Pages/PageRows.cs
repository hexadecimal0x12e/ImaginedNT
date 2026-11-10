using Microsoft.UI.Xaml.Controls;

namespace ImaginedNT.FullTrustApp.Pages;

internal static class PageRows
{
    public static void Set(ListView list, IEnumerable<string> rows)
    {
        var items = rows.ToArray();
        if (list.ItemsSource is string[] old && old.SequenceEqual(items)) return;
        var selected = list.SelectedIndex;
        list.ItemsSource = items;
        if (selected >= 0 && selected < items.Length) list.SelectedIndex = selected;
    }
}
