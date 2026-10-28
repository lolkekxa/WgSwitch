using WgSwitch.Services;

namespace WgSwitch.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("1.0.2", "1.0.1", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0.10", "1.0.9", true)]       // числами, а не строкой
    [InlineData("1.0.1", "1.0.1", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0.1", "1.0.1-dev.12", false)] // dev-сборка выходит уже после релиза своей версии
    [InlineData("1.0.2", "1.0.1-dev.12", true)]
    [InlineData("1.2.0-beta", "1.1.0", true)]
    [InlineData("garbage", "1.0.0", false)]
    public void Compares_versions(string latest, string current, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(latest, current));
}
