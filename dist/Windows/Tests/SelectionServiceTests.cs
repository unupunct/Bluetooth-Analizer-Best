using BluetoothFleetMonitor.Desktop.Services;

namespace BluetoothFleetMonitor.Tests;

public class SelectionServiceTests
{
    [Fact]
    public void OpenAnalysis_raises_the_event_with_the_device_id_and_remembers_the_selection()
    {
        var s = new SelectionService(); Guid? got = null; var id = Guid.NewGuid();
        s.OpenAnalysisRequested += g => got = g;
        s.OpenAnalysis(id);
        Assert.Equal(id, got);
        Assert.Equal(id, s.SelectedDeviceId);
    }
}
