using PanelFleet.Application.Cronjobs;

namespace PanelFleet.Client.UnitTests.Cronjobs;

public sealed class CronjobSpecTests
{
    [Theory]
    [InlineData(CronjobSpecType.PerMonth, 0, 5, 2, 30, 0, "30 2 5 * *")]
    [InlineData(CronjobSpecType.PerWeek, 1, 0, 1, 30, 0, "30 1 * * 1")]
    [InlineData(CronjobSpecType.PerDay, 0, 0, 2, 30, 0, "30 2 * * *")]
    [InlineData(CronjobSpecType.PerHour, 0, 0, 0, 15, 0, "15 * * * *")]
    [InlineData(CronjobSpecType.PerNDay, 0, 3, 4, 5, 0, "5 4 */3 * *")]
    [InlineData(CronjobSpecType.PerNHour, 0, 0, 6, 10, 0, "10 */6 * * *")]
    [InlineData(CronjobSpecType.PerNMinute, 0, 0, 0, 5, 0, "@every 5m")]
    [InlineData(CronjobSpecType.PerNSecond, 0, 0, 0, 0, 45, "@every 45s")]
    public void Build_ProducesExpectedExpression(
        CronjobSpecType type,
        int week,
        int day,
        int hour,
        int minute,
        int second,
        string expected)
    {
        var spec = CronjobSpec.Build(new CronjobSpecParts(type, week, day, hour, minute, second));

        Assert.Equal(expected, spec);
        Assert.True(CronjobSpec.IsValid(spec));
    }

    [Fact]
    public void Build_OutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CronjobSpec.Build(new CronjobSpecParts(CronjobSpecType.PerDay, Hour: 24)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CronjobSpec.Build(new CronjobSpecParts(CronjobSpecType.PerNMinute, Minute: 0)));
    }

    [Theory]
    [InlineData("30 1 * * 1", true)]
    [InlineData("*/5 * * * *", true)]
    [InlineData("@every 5m", true)]
    [InlineData("@every 30s", true)]
    [InlineData("30 1 * * 1 && 0 2 * * *", true)]
    [InlineData("30 1 * *", false)]
    [InlineData("60 1 * * *", false)]
    [InlineData("30 1 * * 7", false)]
    [InlineData("@every 0m", false)]
    [InlineData("not-a-spec", false)]
    [InlineData("", false)]
    public void IsValid_ChecksCronAndEverySyntax(string spec, bool expected) =>
        Assert.Equal(expected, CronjobSpec.IsValid(spec));

    [Fact]
    public void TryParse_RoundTripsStructuredSpecs()
    {
        var original = new CronjobSpecParts(CronjobSpecType.PerWeek, Week: 3, Hour: 4, Minute: 5);
        var spec = CronjobSpec.Build(original);

        Assert.True(CronjobSpec.TryParse(spec, out var parsed));
        Assert.Equal(CronjobSpecType.PerWeek, parsed.Type);
        Assert.Equal(3, parsed.Week);
        Assert.Equal(4, parsed.Hour);
        Assert.Equal(5, parsed.Minute);

        Assert.True(CronjobSpec.TryParse("@every 7m", out var everyMinute));
        Assert.Equal(CronjobSpecType.PerNMinute, everyMinute.Type);
        Assert.Equal(7, everyMinute.Minute);

        Assert.True(CronjobSpec.TryParse("@every 9s", out var everySecond));
        Assert.Equal(CronjobSpecType.PerNSecond, everySecond.Type);
        Assert.Equal(9, everySecond.Second);
    }

    [Theory]
    [InlineData("30 1 * * 1", "每周一 01:30")]
    [InlineData("30 2 * * *", "每天 02:30")]
    [InlineData("15 * * * *", "每小时第 15 分")]
    [InlineData("5 4 3 * *", "每月 3 日 04:05")]
    [InlineData("5 4 */3 * *", "每 3 天 04:05")]
    [InlineData("10 */6 * * *", "每 6 小时第 10 分")]
    [InlineData("@every 5m", "每 5 分钟")]
    [InlineData("@every 45s", "每 45 秒")]
    public void Describe_ProducesChineseText(string spec, string expected) =>
        Assert.Equal(expected, CronjobSpec.Describe(spec));

    [Fact]
    public void Describe_UnknownSpec_ReturnsRawText()
    {
        Assert.Equal("30 1 * * 1 && 0 2 * * *", CronjobSpec.Describe("30 1 * * 1 && 0 2 * * *"));
        Assert.Equal("weird", CronjobSpec.Describe("weird"));
        Assert.Equal("-", CronjobSpec.Describe(null));
    }

    [Fact]
    public void WeekdayName_MapsZeroToSunday()
    {
        Assert.Equal("周日", CronjobSpec.WeekdayName(0));
        Assert.Equal("周一", CronjobSpec.WeekdayName(1));
        Assert.Equal("99", CronjobSpec.WeekdayName(99));
    }
}
