using CodexQuota.Core.Models;
using CodexQuota.Core.Services;

namespace CodexQuota.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    [Fact]
    public async Task NewPreferencesSurviveLegacyCompatibleLoadAndSave()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"),
            """{"version":1,"opacity":0.8,"theme":"Dark","refreshIntervalSeconds":45,"ballLeft":12,"ballTop":34,"ballSizePercent":125,"placement":"TopLeft","showRemaining":false,"showResetTime":false,"showTodayUsage":false,"lowQuotaThreshold":25,"notificationsEnabled":true}""");
        SettingsStore store = CreateStore();
        WidgetSettings loaded = await store.LoadAsync();
        await store.SaveAsync(loaded);
        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(store.SettingsPath));
        Assert.True(json.RootElement.TryGetProperty("BallSizePercent", out var size));
        Assert.Equal(125, size.GetInt32());
        Assert.Equal("TopLeft", json.RootElement.GetProperty("Placement").GetString());
        Assert.False(json.RootElement.GetProperty("ShowRemaining").GetBoolean());
        Assert.True(json.RootElement.GetProperty("NotificationsEnabled").GetBoolean());
        Assert.False(json.RootElement.GetProperty("ShowResetTime").GetBoolean());
        Assert.False(json.RootElement.GetProperty("ShowTodayUsage").GetBoolean());
        Assert.Equal(25, json.RootElement.GetProperty("LowQuotaThreshold").GetInt32());
        Assert.Equal(loaded, await CreateStore().LoadAsync());
        Assert.Equal(45, loaded.RefreshIntervalSeconds);
        Assert.Equal(12, loaded.BallLeft);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexquota-settings-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OldSettingsKeepExistingValuesAndReceiveNewDefaults()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"),
            """{"version":1,"opacity":0.65,"theme":"Dark","refreshIntervalSeconds":45,"ballLeft":12,"ballTop":34,"codexCliPath":"C:\\Tools\\codex.exe"}""");
        WidgetSettings loaded = await CreateStore().LoadAsync();
        Assert.Equal(0.65, loaded.Opacity);
        Assert.Equal(ThemeMode.Dark, loaded.Theme);
        Assert.Equal(45, loaded.RefreshIntervalSeconds);
        Assert.Equal(@"C:\Tools\codex.exe", loaded.CodexCliPath);
        Assert.Equal(BallPlacement.Custom, loaded.Placement);
        Assert.Equal(100, loaded.BallSizePercent);
        Assert.Equal(20, loaded.LowQuotaThreshold);
        Assert.True(loaded.ShowRemaining && loaded.ShowResetTime && loaded.ShowTodayUsage);
        Assert.False(loaded.NotificationsEnabled);
    }

    [Theory]
    [InlineData(0, 0, 75, 1)]
    [InlineData(200, 200, 150, 100)]
    [InlineData(125, 25, 125, 25)]
    public async Task NewNumericBoundsAreNormalizedAndRoundTrip(int size, int threshold, int expectedSize, int expectedThreshold)
    {
        var store = CreateStore();
        await store.SaveAsync(WidgetSettings.Default with { BallSizePercent = size, LowQuotaThreshold = threshold, Placement = (BallPlacement)999 });
        WidgetSettings loaded = await CreateStore().LoadAsync();
        Assert.Equal(expectedSize, loaded.BallSizePercent);
        Assert.Equal(expectedThreshold, loaded.LowQuotaThreshold);
        Assert.Equal(BallPlacement.Custom, loaded.Placement);
    }

    private SettingsStore CreateStore() => new(_root);

    [Fact]
    public async Task BriefWindowsReadLockDoesNotLoseASave()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = CreateStore();
        await store.SaveAsync(WidgetSettings.Default);
        var readLock = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task saving = store.SaveAsync(WidgetSettings.Default with { BallSizePercent = 125 });
        await Task.Delay(50);
        readLock.Dispose();
        await saving;
        Assert.Equal(125, (await store.LoadAsync()).BallSizePercent);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task PermanentWindowsReadLockStillFailsWithoutChangingSettings()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = CreateStore();
        await store.SaveAsync(WidgetSettings.Default);
        using var readLock = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Exception? error = await Record.ExceptionAsync(() => store.SaveAsync(WidgetSettings.Default with { BallSizePercent = 150 }));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(100, (await store.LoadAsync()).BallSizePercent);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task CancellationDuringWindowsReadLockReleasesTheSaveGate()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = CreateStore();
        await store.SaveAsync(WidgetSettings.Default);
        using var cancellation = new CancellationTokenSource();
        using (var readLock = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Task saving = store.SaveAsync(WidgetSettings.Default with { BallSizePercent = 150 }, cancellation.Token);
            cancellation.CancelAfter(20);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
        }
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        await store.SaveAsync(WidgetSettings.Default with { BallSizePercent = 125 });
        Assert.Equal(125, (await store.LoadAsync()).BallSizePercent);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFileReturnsDefaults()
    {
        SettingsStore store = CreateStore();

        WidgetSettings settings = await store.LoadAsync();

        Assert.Equal(WidgetSettings.Default, settings);
        Assert.Null(store.LastWarning);
    }

    [Fact]
    public async Task SaveAndLoadRoundTrips()
    {
        SettingsStore store = CreateStore();
        var expected = new WidgetSettings(1, 0.6, ThemeMode.Dark, 300, 120.5, 240.25, @"C:\path with space\codex.exe");

        await store.SaveAsync(expected);
        WidgetSettings actual = await CreateStore().LoadAsync();

        Assert.Equal(expected, actual);
        Assert.False(File.Exists(store.SettingsPath + ".tmp"), "the temporary file must be replaced atomically");
    }

    [Fact]
    public async Task MalformedJsonFallsBackToDefaultsWithAWarning()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "{ not json ");

        SettingsStore store = CreateStore();
        WidgetSettings settings = await store.LoadAsync();

        Assert.Equal(WidgetSettings.Default, settings);
        Assert.False(string.IsNullOrEmpty(store.LastWarning));
    }

    [Fact]
    public async Task UnsupportedVersionFallsBackToDefaultsWithAWarning()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "settings.json"),
            """{"version":99,"opacity":0.5,"theme":"Dark","refreshIntervalSeconds":60}""");

        SettingsStore store = CreateStore();
        WidgetSettings settings = await store.LoadAsync();

        Assert.Equal(WidgetSettings.Default, settings);
        Assert.Contains("99", store.LastWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidValuesAreClampedOrDefaulted()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "settings.json"),
            """{"version":1,"opacity":5.0,"theme":"Neon","refreshIntervalSeconds":5,"ballLeft":123.5,"ballTop":null}""");

        WidgetSettings settings = await CreateStore().LoadAsync();

        Assert.Equal(WidgetSettings.MaxOpacity, settings.Opacity);
        Assert.Equal(ThemeMode.System, settings.Theme);
        Assert.Equal(WidgetSettings.MinIntervalSeconds, settings.RefreshIntervalSeconds);
        Assert.Null(settings.BallLeft);
        Assert.Null(settings.BallTop);
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(1.0)]
    public async Task OpacityBoundariesArePreserved(double opacity)
    {
        SettingsStore store = CreateStore();
        await store.SaveAsync(WidgetSettings.Default with { Opacity = opacity });

        WidgetSettings loaded = await CreateStore().LoadAsync();

        Assert.Equal(opacity, loaded.Opacity, 5);
    }

    [Fact]
    public void NonFiniteOpacityFallsBackToDefault()
    {
        WidgetSettings normalized = SettingsStore.Normalize(WidgetSettings.Default with { Opacity = double.NaN });

        Assert.Equal(WidgetSettings.Default.Opacity, normalized.Opacity);
    }

    [Fact]
    public void HalfSpecifiedPositionIsDiscarded()
    {
        WidgetSettings normalized = SettingsStore.Normalize(WidgetSettings.Default with { BallLeft = 100, BallTop = null });

        Assert.Null(normalized.BallLeft);
        Assert.Null(normalized.BallTop);
    }

    [Fact]
    public void IntervalIsClampedIntoRange()
    {
        Assert.Equal(WidgetSettings.MinIntervalSeconds, SettingsStore.Normalize(WidgetSettings.Default with { RefreshIntervalSeconds = 1 }).RefreshIntervalSeconds);
        Assert.Equal(WidgetSettings.MaxIntervalSeconds, SettingsStore.Normalize(WidgetSettings.Default with { RefreshIntervalSeconds = 100000 }).RefreshIntervalSeconds);
        Assert.Equal(600, SettingsStore.Normalize(WidgetSettings.Default with { RefreshIntervalSeconds = 600 }).RefreshIntervalSeconds);
    }

    [Fact]
    public async Task EmptyFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), string.Empty);

        WidgetSettings settings = await CreateStore().LoadAsync();

        Assert.Equal(WidgetSettings.Default, settings);
    }

    [Fact]
    public void DefaultsHaveStartupDisabledByAbsence()
    {
        // Start-at-login is never persisted; only the real registry value is authoritative.
        Assert.DoesNotContain(
            typeof(WidgetSettings).GetProperties(),
            property => property.Name.Contains("Startup", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("AutoStart", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DockSideRoundTripsAndOldFilesDefaultToUndocked()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"),
            """{"version":1,"opacity":0.9,"theme":"Light","refreshIntervalSeconds":60,"ballLeft":100,"ballTop":200,"placement":"Custom","dockSide":"Left"}""");
        WidgetSettings loaded = await CreateStore().LoadAsync();
        Assert.Equal(BallDockSide.Left, loaded.DockSide);

        await CreateStore().SaveAsync(loaded with { DockSide = BallDockSide.Right });
        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(CreateStore().SettingsPath));
        Assert.Equal("Right", json.RootElement.GetProperty("DockSide").GetString());
        Assert.Equal(BallDockSide.Right, await CreateStore().LoadAsync() is { } reloaded ? reloaded.DockSide : default);

        // Old schema1 files without the field stay undocked.
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"),
            """{"version":1,"opacity":0.9,"theme":"Light","refreshIntervalSeconds":60}""");
        Assert.Equal(BallDockSide.None, (await CreateStore().LoadAsync()).DockSide);
    }

    [Theory]
    [InlineData("Top")]
    [InlineData("Bottom")]
    [InlineData("nonsense")]
    public async Task InvalidDockSideNormalizesToUndocked(string side)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"),
            "{ \"version\": 1, \"opacity\": 0.9, \"theme\": \"Light\", \"refreshIntervalSeconds\": 60, \"dockSide\": \"" + side + "\" }");

        WidgetSettings loaded = await CreateStore().LoadAsync();

        Assert.Equal(BallDockSide.None, loaded.DockSide);
    }

    [Fact]
    public void NormalizeClampsUnknownEnumDockSide()
    {
        WidgetSettings normalized = SettingsStore.Normalize(WidgetSettings.Default with { DockSide = (BallDockSide)999 });

        Assert.Equal(BallDockSide.None, normalized.DockSide);
    }

    [Theory]
    [InlineData(BallPlacement.Custom)]
    [InlineData(BallPlacement.TopRight)]
    public void IncompleteOrCornerDockMetadataDefaultsToOrdinaryBall(BallPlacement placement)
    {
        WidgetSettings incomplete = WidgetSettings.Default with { DockSide = BallDockSide.Left, Placement = placement };
        Assert.Equal(BallDockSide.None, SettingsStore.Normalize(incomplete).DockSide);
        if (placement != BallPlacement.Custom)
        {
            Assert.Equal(BallDockSide.None, SettingsStore.Normalize(incomplete with { BallLeft = 20, BallTop = 30 }).DockSide);
        }
    }
}
