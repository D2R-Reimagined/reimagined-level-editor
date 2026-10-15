using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using D2RLevel.Core;
namespace D2RLevel.App;

/// <summary>
/// The Selected tab of the scene entity list: every selected model on its own row, so one can be
/// dropped from a group selection with a click, plus the models within reach of the selection.
/// </summary>
public partial class MainWindow
{
    private sealed record NearbyRow(PresetEntity Entity, string Name, string Distance);
    private static readonly double[] NearbyTiles = [0.5, 1, 2, 4];
    private const int NearbyLimit = 60;
    private bool showingSelection;

    private void InitializeSelectionView()
    {
        foreach (var tiles in NearbyTiles) NearbyRange.Items.Add(tiles == 1 ? L.T("1 tile") : L.T("{0} tiles", tiles));
        NearbyRange.SelectedIndex = 1;
    }
    private void AllEntitiesTab_Click(object sender, RoutedEventArgs e) => ShowSelectionView(false);
    private void SelectionTab_Click(object sender, RoutedEventArgs e) => ShowSelectionView(true);
    private void ShowSelectionView(bool show)
    {
        showingSelection = show;
        AllEntitiesView.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SelectionView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var active = new SolidColorBrush(Color.FromRgb(40, 104, 108));
        var inactive = new SolidColorBrush(Color.FromRgb(53, 68, 86));
        SelectionTab.Background = show ? active : inactive;
        AllEntitiesTab.Background = show ? inactive : active;
        Scene.Hover(null);
        RefreshSelectionView();
    }
    private void RefreshSelectionView()
    {
        if (SelectionTab is null) return;
        var members = SelectedEntities;
        SelectionTab.Content = members.Length > 0 ? L.T("Selected ({0})", members.Length) : L.T("Selected");
        if (!showingSelection) return;
        SelectedMembers.ItemsSource = members;
        SelectionSummary.Text = members.Length == 0 ? L.T("Nothing selected. Click a model in the scene or the All list.") :
            Selected is { } primary && assetGroups?.Find(primary) is { } group ? L.T("Group: {0} · click a model to deselect it", group.Name) :
            L.T("Click a model to deselect it");
        NearbyModels.ItemsSource = FindNearby(members);
    }
    private NearbyRow[] FindNearby(PresetEntity[] members)
    {
        if (document is null || members.Length == 0) return [];
        double tile = Ds1Preview.UnitsPerTile is > 0 and var units && double.IsFinite(units) ? units : GridCalibration.FallbackUnitsPerTile;
        var selection = members.Select(Scene.GetExtent).OfType<ModelFootprint>().ToArray();
        var chosen = members.ToHashSet();
        var candidates = document.Entities.Concat(npcItems.Select(i => i.Entity))
            .Where(e => !chosen.Contains(e) && !Scene.IsLocked(e))
            .Select(e => (Entity: e, Footprint: Scene.GetExtent(e)))
            .Where(c => c.Footprint is not null).Select(c => (c.Entity, c.Footprint!));
        return SelectionNeighbors.Find(selection, candidates, NearbyTiles[Math.Max(0, NearbyRange.SelectedIndex)] * tile, NearbyLimit)
            .Select(h => new NearbyRow(h.Item, h.Item.Name, h.Gap < 1e-3 ? L.T("touching") : L.T("{0:F1} tiles", h.Gap / tile))).ToArray();
    }
    private void NearbyRange_Changed(object sender, SelectionChangedEventArgs e) => RefreshSelectionView();

    private void Deselect(PresetEntity entity) { Scene.CancelDrag(); SetSelection(SelectedEntities.Where(e => e != entity)); }
    private void AddToSelection(PresetEntity entity)
    {
        Scene.CancelDrag();
        // Match a viewport click: picking a grouped model brings its group along.
        var members = assetGroups?.Find(entity) is { } group ? assetGroups.Resolve(group) : [entity];
        var previous = SelectedEntities;
        Search.Text = "";
        SetSelection(previous.Union(members));
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) { Scene.CancelDrag(); SetSelection([]); }

    private static object? RowAt(ListBox list, RoutedEventArgs e) =>
        ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is ListBoxItem row ? row.Content : null;
    private void SelectedMembers_Click(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(SelectedMembers, e) is PresetEntity entity) { Deselect(entity); HoverRowUnderMouse(SelectedMembers); e.Handled = true; }
    }
    private void NearbyModels_Click(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(NearbyModels, e) is NearbyRow row) { AddToSelection(row.Entity); HoverRowUnderMouse(NearbyModels); e.Handled = true; }
    }
    // Rows in any entity list outline their model in gold, so it can be found in the scene before clicking.
    private void EntityList_MouseMove(object sender, MouseEventArgs e) => HoverRow((ListBox)sender, e.OriginalSource as DependencyObject);
    private void EntityList_MouseLeave(object sender, MouseEventArgs e) => Scene.Hover(null);
    private void HoverRow(ListBox list, DependencyObject? source) =>
        Scene.Hover((source is null ? null : ItemsControl.ContainerFromElement(list, source) as ListBoxItem)?.Content switch
        {
            PresetEntity entity => entity,
            NearbyRow nearby => nearby.Entity,
            _ => null,
        });
    /// <summary>A click can remove the hovered row, sliding another under the pointer without a mouse move.</summary>
    private void HoverRowUnderMouse(ListBox list)
    {
        list.UpdateLayout();
        HoverRow(list, list.IsMouseOver ? list.InputHitTest(Mouse.GetPosition(list)) as DependencyObject : null);
    }
    private void SelectionRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        if ((RowAt(list, e) is NearbyRow row ? row.Entity : RowAt(list, e) as PresetEntity) is { } entity) { Scene.FrameEntity(entity); e.Handled = true; }
    }
    private void SelectedMembers_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Space or Key.Enter) || Keyboard.FocusedElement is not ListBoxItem { Content: PresetEntity entity } row) return;
        int index = SelectedMembers.ItemContainerGenerator.IndexFromContainer(row);
        Deselect(entity); FocusRow(SelectedMembers, index); e.Handled = true;
    }
    private void NearbyModels_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter) || Keyboard.FocusedElement is not ListBoxItem { Content: NearbyRow nearby } row) return;
        int index = NearbyModels.ItemContainerGenerator.IndexFromContainer(row);
        AddToSelection(nearby.Entity); FocusRow(NearbyModels, index); e.Handled = true;
    }
    /// <summary>Keeps keyboard focus on the row that took the removed one's place.</summary>
    private static void FocusRow(ListBox list, int index)
    {
        if (list.Items.Count == 0) { list.Focus(); return; }
        index = Math.Clamp(index, 0, list.Items.Count - 1);
        list.ScrollIntoView(list.Items[index]); list.UpdateLayout();
        (list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus();
    }
}
