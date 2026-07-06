using System.IO;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class RumourSettingsTests
{
    [Fact]
    public void Defaults_RumourHelperEnabledAndInterval()
    {
        var cfg = new AppConfig();
        Assert.True(cfg.RumourHelperEnabled);
        Assert.Equal(1800, cfg.RumourScanIntervalMs);
    }

    [Fact]
    public void WorldRegion_DefaultsToAutoDetect_NoManualRegion()
    {
        var cfg = new AppConfig();
        Assert.True(cfg.RumourWorldAutoDetect);
        Assert.False(cfg.HasManualWorldRegion);
    }

    [Fact]
    public void WorldRegion_RoundTrips_AndFlagsManual()
    {
        using var dir = new TempDir();
        ConfigStore.Save(new AppConfig
        {
            RumourWorldAutoDetect = false,
            RumourWorldRect = new System.Drawing.Rectangle(770, 138, 380, 54),
        }, dir.Path);

        var loaded = ConfigStore.Load(dir.Path);
        Assert.False(loaded.RumourWorldAutoDetect);
        Assert.True(loaded.HasManualWorldRegion);
        Assert.Equal(new System.Drawing.Rectangle(770, 138, 380, 54), loaded.RumourWorldRect);
    }

    [Fact]
    public void OlderConfig_MissingKeys_KeepsDefaults()
    {
        // A config.json written before the rumour fields existed must load with the defaults applied,
        // not false/0 (Newtonsoft only overwrites keys present in the file).
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "config.json"), """{ "LeagueName": "Runes of Aldur" }""");
        var cfg = ConfigStore.Load(dir.Path);
        Assert.True(cfg.RumourHelperEnabled);
        Assert.Equal(1800, cfg.RumourScanIntervalMs);
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        using var dir = new TempDir();
        ConfigStore.Save(new AppConfig { RumourHelperEnabled = false, RumourScanIntervalMs = 3000 }, dir.Path);
        var loaded = ConfigStore.Load(dir.Path);
        Assert.False(loaded.RumourHelperEnabled);
        Assert.Equal(3000, loaded.RumourScanIntervalMs);
    }

    [Theory]
    [InlineData(1200, 1200)]
    [InlineData(1800, 1800)]
    [InlineData(3000, 3000)]
    [InlineData(50, 500)]        // below the floor → clamped up
    [InlineData(0, 500)]
    [InlineData(60000, 10000)]   // above the ceiling → clamped down
    public void ClampInterval_KeepsWithinSaneBounds(int input, int expected)
    {
        Assert.Equal(expected, RumourScanEngine.ClampInterval(input));
    }
}
