using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Desktop.ViewModels;

public sealed partial class InventoryViewModel : ObservableObject
{
    private readonly LiveRegistry _registry;
    private readonly DeviceAdminService _admin;
    private readonly IDbContextFactory<FleetDbContext> _factory;
    private readonly SelectionService _selection;

    public ICollectionView View { get; }
    public LiveRegistry Registry => _registry;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private LiveDevice? _selected;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editCategory = "";
    [ObservableProperty] private string _editRoom = "";
    [ObservableProperty] private string _editWorkstation = "";
    [ObservableProperty] private string _editNotes = "";
    [ObservableProperty] private LiveDevice? _mergeTarget;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _hasSelection;

    public InventoryViewModel(LiveRegistry registry, DeviceAdminService admin, IDbContextFactory<FleetDbContext> factory, SelectionService selection)
    {
        _registry = registry; _admin = admin; _factory = factory; _selection = selection;
        View = new ListCollectionView(registry.Devices);
        View.Filter = o =>
        {
            if (Search.Length == 0) return true;
            var d = (LiveDevice)o;
            return d.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || (d.Room?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.Workstation?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)
                || d.Category.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || (d.Manufacturer?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.Notes?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false);
        };
        View.SortDescriptions.Add(new SortDescription(nameof(LiveDevice.Name), ListSortDirection.Ascending));
    }

    partial void OnSearchChanged(string value) => View.Refresh();

    partial void OnSelectedChanged(LiveDevice? value)
    {
        HasSelection = value is not null;
        EditName = value?.CustomName ?? "";
        EditCategory = value?.Category ?? "";
        EditRoom = value?.Room ?? "";
        EditWorkstation = value?.Workstation ?? "";
        EditNotes = value?.Notes ?? "";
        MergeTarget = null;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (Selected is null) return;
        try
        {
            await _admin.UpdateDetailsAsync(Selected.Id, new DeviceDetails(EditName, EditCategory, EditRoom, EditWorkstation, EditNotes));
            await _registry.RefreshDeviceAsync(_factory, Selected.Id);
            Message = $"Saved {Selected.Name}.";
        }
        catch (Exception ex) { Message = "Could not save: " + ex.Message; }
    }

    [RelayCommand]
    private void OpenHistory()
    {
        if (Selected is not null) _selection.OpenHistory(Selected.Id);
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        var path = Dialogs.SaveFile("Export device inventory", $"devices-{DateTime.Now:yyyyMMdd}.csv", "CSV (*.csv)|*.csv");
        if (path is null) return;
        try { await File.WriteAllTextAsync(path, await _admin.ExportCsvAsync()); Message = $"Exported to {path}"; }
        catch (Exception ex) { Message = "Export failed: " + ex.Message; }
    }

    [RelayCommand]
    private async Task ImportCsv()
    {
        var path = Dialogs.OpenFile("Import device details", "CSV (*.csv)|*.csv");
        if (path is null) return;
        try
        {
            var r = await _admin.ImportCsvAsync(await File.ReadAllTextAsync(path));
            foreach (var d in _registry.Devices.ToList()) await _registry.RefreshDeviceAsync(_factory, d.Id);
            Message = r.Errors.Count > 0
                ? "Import problem: " + string.Join(" ", r.Errors)
                : $"Imported: {r.Updated} updated, {r.Unmatched} rows matched no device.";
        }
        catch (Exception ex) { Message = "Import failed: " + ex.Message; }
    }

    [RelayCommand]
    private async Task Merge()
    {
        if (Selected is null || MergeTarget is null) { Message = "Pick the device to keep in the 'Merge into' box first."; return; }
        if (Selected.Id == MergeTarget.Id) { Message = "Choose a different device to merge into."; return; }
        var source = Selected; var target = MergeTarget;
        var ok = MessageBox.Show(
            $"Merge '{source.Name}' into '{target.Name}'?\n\nAll its history and identifiers move to '{target.Name}', and '{source.Name}' disappears from the inventory. " +
            "The merge is recorded in the Event Log, and a split can undo it.\n\nOnly do this if you know they are the same physical device.",
            "Confirm merge", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!ok) return;
        try
        {
            await _admin.MergeAsync(source.Id, target.Id, "Manual merge from the Inventory screen");
            _registry.Remove(source.Id);
            await _registry.RefreshDeviceAsync(_factory, target.Id);
            Selected = target;
            Message = $"Merged '{source.Name}' into '{target.Name}'.";
        }
        catch (Exception ex) { Message = "Merge failed: " + ex.Message; }
    }
}
