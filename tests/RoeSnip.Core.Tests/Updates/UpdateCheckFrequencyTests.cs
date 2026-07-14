using System;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

public class UpdateCheckFrequencyTests
{
    [Theory]
    [InlineData(UpdateCheckFrequency.StartupOnly)]
    [InlineData(UpdateCheckFrequency.EveryMinute)]
    [InlineData(UpdateCheckFrequency.Every30Minutes)]
    [InlineData(UpdateCheckFrequency.Hourly)]
    [InlineData(UpdateCheckFrequency.Every6Hours)]
    [InlineData(UpdateCheckFrequency.Daily)]
    public void Parse_RoundTripsEveryMember_CaseInsensitively(UpdateCheckFrequency frequency)
    {
        string persisted = frequency.ToString();
        Assert.Equal(frequency, UpdateCheckFrequencies.Parse(persisted));
        Assert.Equal(frequency, UpdateCheckFrequencies.Parse(persisted.ToUpperInvariant()));
        Assert.Equal(frequency, UpdateCheckFrequencies.Parse(persisted.ToLowerInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("hourly ")] // trailing space is not a valid enum name
    [InlineData("7")] // Enum.TryParse accepts any integer string for a non-Flags enum - must not slip past IsDefined
    [InlineData("-1")]
    [InlineData("2")] // happens to be the underlying int of a DEFINED member (Every30Minutes) - still not a valid name, must fail safe
    public void Parse_UnrecognizedOrMissing_FailsSafeToHourly(string? value)
    {
        Assert.Equal(UpdateCheckFrequency.Hourly, UpdateCheckFrequencies.Parse(value));
    }

    [Fact]
    public void Interval_MapsEachMemberToItsDocumentedSpan()
    {
        Assert.Null(UpdateCheckFrequencies.Interval(UpdateCheckFrequency.StartupOnly));
        Assert.Equal(TimeSpan.FromMinutes(1), UpdateCheckFrequencies.Interval(UpdateCheckFrequency.EveryMinute));
        Assert.Equal(TimeSpan.FromMinutes(30), UpdateCheckFrequencies.Interval(UpdateCheckFrequency.Every30Minutes));
        Assert.Equal(TimeSpan.FromHours(1), UpdateCheckFrequencies.Interval(UpdateCheckFrequency.Hourly));
        Assert.Equal(TimeSpan.FromHours(6), UpdateCheckFrequencies.Interval(UpdateCheckFrequency.Every6Hours));
        Assert.Equal(TimeSpan.FromHours(24), UpdateCheckFrequencies.Interval(UpdateCheckFrequency.Daily));
    }

    [Theory]
    [InlineData(UpdateCheckFrequency.StartupOnly, "At startup only")]
    [InlineData(UpdateCheckFrequency.EveryMinute, "Every minute")]
    [InlineData(UpdateCheckFrequency.Every30Minutes, "Every 30 minutes")]
    [InlineData(UpdateCheckFrequency.Hourly, "Every hour")]
    [InlineData(UpdateCheckFrequency.Every6Hours, "Every 6 hours")]
    [InlineData(UpdateCheckFrequency.Daily, "Once a day")]
    public void DisplayLabel_MatchesDocumentedText_AndHasNoEmDash(UpdateCheckFrequency frequency, string expected)
    {
        string label = UpdateCheckFrequencies.DisplayLabel(frequency);
        Assert.Equal(expected, label);
        Assert.DoesNotContain('—', label);
    }

    [Fact]
    public void UiChoices_ExcludesEveryMinute_AndListsTheOtherFiveInOrder()
    {
        Assert.Equal(
            new[]
            {
                UpdateCheckFrequency.StartupOnly,
                UpdateCheckFrequency.Every30Minutes,
                UpdateCheckFrequency.Hourly,
                UpdateCheckFrequency.Every6Hours,
                UpdateCheckFrequency.Daily,
            },
            UpdateCheckFrequencies.UiChoices);
        Assert.DoesNotContain(UpdateCheckFrequency.EveryMinute, UpdateCheckFrequencies.UiChoices);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Jitter_StaysWithinZeroToCappedQuarterInterval(double random01)
    {
        foreach (TimeSpan interval in new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30), TimeSpan.FromHours(24) })
        {
            TimeSpan jitter = UpdateCheckFrequencies.Jitter(interval, random01);
            TimeSpan expectedMax = interval / 4 < TimeSpan.FromMinutes(5) ? interval / 4 : TimeSpan.FromMinutes(5);

            Assert.True(jitter >= TimeSpan.Zero, $"jitter {jitter} was negative for interval {interval}, r={random01}");
            Assert.True(jitter <= expectedMax, $"jitter {jitter} exceeded cap {expectedMax} for interval {interval}, r={random01}");
        }
    }
}
