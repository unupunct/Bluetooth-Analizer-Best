using System.Collections.ObjectModel;

namespace BluetoothFleetMonitor.Desktop.Services;

/// <summary>
/// Rolling one-second samples for the dashboard charts: how many devices were detected and how many advertisements
/// per second the scanner heard. Kept in memory only (10 minutes); the database holds the real history.
/// </summary>
public sealed class DashboardMetrics
{
    public const int Capacity = 600;

    public ObservableCollection<int> Detected { get; } = new();
    public ObservableCollection<double> AdvertisementsPerSecond { get; } = new();

    public void Record(int detected, double advPerSecond)
    {
        Detected.Add(detected);
        AdvertisementsPerSecond.Add(Math.Round(advPerSecond, 1));
        while (Detected.Count > Capacity) Detected.RemoveAt(0);
        while (AdvertisementsPerSecond.Count > Capacity) AdvertisementsPerSecond.RemoveAt(0);
    }

    /// <summary>Fills the history with a plausible, clearly artificial curve. Used only by the labelled demo screenshots.</summary>
    public void SeedDemo(int seed = 7)
    {
        Detected.Clear(); AdvertisementsPerSecond.Clear();
        var rng = new Random(seed);
        double level = 138, rate = 310;
        for (var i = 0; i < Capacity; i++)
        {
            level = Math.Clamp(level + (rng.NextDouble() - 0.5) * 6 + Math.Sin(i / 55.0) * 0.8, 105, 175);
            rate = Math.Clamp(rate + (rng.NextDouble() - 0.5) * 40 + Math.Sin(i / 31.0) * 6, 150, 520);
            Detected.Add((int)level);
            AdvertisementsPerSecond.Add(Math.Round(rate, 1));
        }
    }
}
