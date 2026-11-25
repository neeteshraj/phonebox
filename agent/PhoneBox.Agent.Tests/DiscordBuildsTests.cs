using PhoneBox.Agent;
using Xunit;

/// <summary>"Choose on PC" brings forward the Discord build the agent is connected to, where the picker opened.</summary>
public sealed class DiscordBuildsTests
{
    [Theory]
    [InlineData(null, "Discord")]
    [InlineData("Discord", "Discord")]
    [InlineData("DiscordPTB", "DiscordPTB")]
    [InlineData("discordcanary", "DiscordCanary")]
    [InlineData("SomethingElse", "Discord")]
    public void The_connected_build_comes_first(string? connected, string first)
    {
        var order = DiscordMonitor.BuildsFor(connected).Select(b => b.Process).ToList();
        Assert.Equal(first, order[0]);
        Assert.Equal(3, order.Distinct().Count()); // the others are still tried
    }
}
