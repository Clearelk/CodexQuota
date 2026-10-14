using CodexQuota.Core.Services;
using CodexQuota.Tests.Support;

namespace CodexQuota.Tests;

public sealed class StartupTests
{
    [Fact]
    public void QuoteWrapsAPathContainingSpaces()
    {
        string command = StartupCommandLine.Quote(@"C:\Program Files\CodexQuota\CodexQuota.Desktop.exe");

        Assert.Equal("\"C:\\Program Files\\CodexQuota\\CodexQuota.Desktop.exe\"", command);
    }

    [Fact]
    public void QuoteKeepsExistingQuotes()
    {
        Assert.Equal("\"C:\\a\\b.exe\"", StartupCommandLine.Quote("\"C:\\a\\b.exe\""));
    }

    [Fact]
    public void QuoteHandlesUnicodeAndEmptyInput()
    {
        Assert.Equal("\"D:\\我的 工具\\CodexQuota.Desktop.exe\"", StartupCommandLine.Quote(@"D:\我的 工具\CodexQuota.Desktop.exe"));
        Assert.Equal("\"\"", StartupCommandLine.Quote("   "));
    }

    [Fact]
    public void EnableWritesAndDisableRemovesOnlyTheWidgetValue()
    {
        var registration = new FakeStartupRegistration();

        Assert.False(registration.IsEnabled());

        bool enabled = registration.Enable(StartupCommandLine.Quote(@"C:\app\CodexQuota.Desktop.exe"));

        Assert.True(enabled);
        Assert.True(registration.IsEnabled());
        Assert.Equal("\"C:\\app\\CodexQuota.Desktop.exe\"", registration.StoredCommand);
        Assert.Equal("CodexQuotaWidget", registration.ValueName);

        registration.Disable();

        Assert.False(registration.IsEnabled());
        Assert.Equal(["enable:\"C:\\app\\CodexQuota.Desktop.exe\"", "disable"], registration.Operations);
    }

    [Fact]
    public void AFailedRegistrationMustNotReportSuccess()
    {
        var registration = new FakeStartupRegistration { AllowEnable = false };

        bool enabled = registration.Enable(@"C:\app\CodexQuota.Desktop.exe");

        Assert.False(enabled);
        Assert.False(registration.IsEnabled());
    }

    [Fact]
    public void StartupIsDisabledByDefault()
    {
        var registration = new FakeStartupRegistration();

        Assert.False(registration.IsEnabled());
    }
}
