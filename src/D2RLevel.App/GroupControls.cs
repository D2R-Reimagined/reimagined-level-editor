using System.Windows;
using System.Windows.Input;
using D2RLevel.Core;
namespace D2RLevel.App;

public partial class MainWindow
{
    private AssetGroups? assetGroups;
    private bool changingSelection;
    private PresetEntity[] SelectedEntities => Hierarchy.SelectedItems.Cast<PresetEntity>().ToArray();
    private void SetSelection(IEnumerable<PresetEntity> members)
    {
        changingSelection = true;
        try
        {
            Hierarchy.SelectedItems.Clear();
            foreach (var member in members.Distinct()) if (Hierarchy.Items.Contains(member)) Hierarchy.SelectedItems.Add(member);
        }
        finally { changingSelection = false; }
        UpdateSelection();
    }
    private void SelectFromViewport(PresetEntity entity)
    {
        var previous = SelectedEntities;
        var members = assetGroups?.Find(entity) is { } group ? assetGroups.Resolve(group) : [entity];
        Search.Text = "";
        bool toggle = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool add = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var next = toggle ? (members.All(previous.Contains) ? previous.Except(members) : previous.Union(members)) :
            add ? previous.Union(members) : previous.Contains(entity) ? previous : members;
        SetSelection(next); Hierarchy.ScrollIntoView(entity);
    }
    private void UpdateSelection()
    {
        if (changingSelection) return;
        Scene.SelectMany(SelectedEntities, Selected);
        if (Selected?.GameplayUnitIndex is { } index) ds1Window?.SelectGameplayUnit(index);
        PopulateInspector(); UpdateGroupControls();
    }
    private void UpdateGroupControls()
    {
        if (GroupButton is null) return;
        var members = SelectedEntities;
        GroupStatus.Text = members.Length > 1 ? $"{members.Length} assets selected - drag any selected model to move them together." : "Ctrl-click models to select several. Shift-click adds to the selection.";
        if (Selected is { } selected && assetGroups?.Find(selected) is { } group) GroupStatus.Text += "\nGroup: " + group.Name;
        if (assetGroups?.Warning is { } warning) GroupStatus.Text += "\n" + warning;
        GroupButton.IsEnabled = loading is null && assetGroups?.Warning is null && members.Length > 1 && members.All(GroupMovement.CanGroup);
        UngroupButton.IsEnabled = loading is null && members.Any(e => assetGroups?.Find(e) is not null);
        if (members.Length > 1 && members.Any(e => !GroupMovement.CanGroup(e))) GroupStatus.Text = "Select only unparented HD models for group movement. NPCs and terrain must be selected separately.";
        if (members.Length > 1) { TransformPanel.IsEnabled = false; DeleteModelButton.IsEnabled = false; }
    }
    private void Group_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Scene.CancelDrag();
            var expanded = SelectedEntities.SelectMany(e => assetGroups?.Find(e) is { } group ? assetGroups.Resolve(group) : [e]).Distinct().ToArray();
            assetGroups?.Create(GroupName.Text, expanded);
            Search.Text = ""; SetSelection(expanded);
            Notify("Group saved", $"{GroupName.Text}: {expanded.Length} assets. Click any member to select the group. Group metadata is saved beside the preset.");
        }
        catch (Exception ex) { Error(ex); }
    }
    private void Ungroup_Click(object sender, RoutedEventArgs e)
    {
        try { Scene.CancelDrag(); assetGroups?.Remove(SelectedEntities); UpdateGroupControls(); Notify("Ungrouped", "Models and gameplay links are unchanged."); }
        catch (Exception ex) { Error(ex); }
    }
    private void RefreshGroupMove(PresetEntity[] members)
    {
        Scene.UpdateEntities(members.Where(member => document?.Entities.Contains(member) == true));
        SetSelection(members); PopulateInspector(); RefreshState();
    }
}
