using Noctaxis.Core.Time;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>A resolved time zone's short name at a given moment, and its identifier for the detail.</summary>
public sealed class TimeZoneDisplayTests
{
    private static readonly Instant Winter = Instant.FromUtc(2026, 1, 15, 12, 0);
    private static readonly Instant Summer = Instant.FromUtc(2026, 7, 15, 12, 0);

    [Fact]
    public void AZoneIsNamedByItsShortNameThere()
    {
        var london = DateTimeZoneProviders.Tzdb["Europe/London"];
        Assert.Equal(("GMT", "Europe/London"), Short(TimeZoneDisplay.Describe(london, Winter)));
        Assert.Equal(("BST", "Europe/London"), Short(TimeZoneDisplay.Describe(london, Summer)));
        Assert.Equal("CEST", TimeZoneDisplay.Describe(DateTimeZoneProviders.Tzdb["Europe/Paris"], Summer).ShortName);
    }

    [Fact]
    public void AWindowsZoneIsNamedAsItsIanaEquivalent()
    {
        var windows = DateTimeZoneProviders.Bcl.GetZoneOrNull("GMT Standard Time");
        if (windows is null) return; // Not a Windows machine.
        Assert.Equal(("BST", "Europe/London"), Short(TimeZoneDisplay.Describe(windows, Summer)));
    }

    [Theory]
    [InlineData("GMT Standard Time", "Europe/London")]
    [InlineData("Tokyo Standard Time", "Asia/Tokyo")]
    [InlineData("Pacific Standard Time", "America/Los_Angeles")]
    [InlineData("Europe/London", "Europe/London")]
    public void AZoneSavedOnAnotherSystemResolvesWithoutTheMachinesZoneData(string saved, string expected)
    {
        // NX-34: a Windows identifier in a saved location must not depend on the host's ICU or zone database.
        Assert.Equal(expected, TimeZoneResolver.ResolveWithoutSystemZones(saved)?.Id);
        Assert.Null(TimeZoneResolver.ResolveWithoutSystemZones("Not A Zone"));
    }

    [Fact]
    public void AZoneWithoutAShortNameIsNamedByItsOffset()
    {
        Assert.Equal("UTC+4", TimeZoneDisplay.Describe(DateTimeZoneProviders.Tzdb["Asia/Dubai"], Summer).ShortName);
        Assert.Equal("UTC+5:45", TimeZoneDisplay.Describe(DateTimeZoneProviders.Tzdb["Asia/Kathmandu"], Summer).ShortName);
        Assert.Equal("UTC", TimeZoneDisplay.FormatOffset(Offset.Zero));
        Assert.Equal("UTC−3:30", TimeZoneDisplay.FormatOffset(Offset.FromHoursAndMinutes(-3, -30)));
    }

    private static (string, string) Short((string ShortName, string Identifier, Offset Offset) zone) => (zone.ShortName, zone.Identifier);
}
