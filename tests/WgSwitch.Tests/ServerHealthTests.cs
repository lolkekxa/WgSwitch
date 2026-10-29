using WgSwitch.Services;

namespace WgSwitch.Tests;

public class ServerHealthTests
{
    [Fact]
    public void Parses_probe_output()
    {
        var h = ServerHealthProbe.Parse("up=6185259.12\nload=0.63\ncpus=2\nmem=2147483648/4294967296\ndisk=21474836480/40479535104\n");
        Assert.NotNull(h);
        Assert.Equal(71, (int)h!.Uptime.TotalDays);
        Assert.Equal(0.63, h.Load, 3);
        Assert.Equal(2, h.Cpus);
        Assert.Equal(0.5, h.MemShare, 3);
        Assert.InRange(h.DiskShare, 0.52, 0.54);
    }

    [Fact]
    public void Garbage_is_not_health() => Assert.Null(ServerHealthProbe.Parse("Permission denied (publickey)."));

    [Fact]
    public void Command_only_reads()
    {
        // ничего не пишет и не меняет на сервере
        foreach (var bad in new[] { ">", "rm ", "tee", "sudo", "systemctl" })
            Assert.DoesNotContain(bad, ServerHealthProbe.Command);
    }
}
