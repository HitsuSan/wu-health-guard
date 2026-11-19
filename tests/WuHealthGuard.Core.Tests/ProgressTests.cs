using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Tests;

public class ProgressTests
{
    [Theory]
    [InlineData("[==========================42.5%==========================]", 42)]
    [InlineData("[==========================100.0%==========================]", 100)]
    [InlineData("Verification 42% complete.", 42)]
    [InlineData("Überprüfung zu 12 % abgeschlossen.", 12)]
    [InlineData("Avancement : 12,5 %", 12)]
    [InlineData("from 10% to 80%", 80)]
    [InlineData("The operation completed successfully.", null)]
    [InlineData("150%", null)]
    [InlineData("", null)]
    public void Percent_is_read_from_tool_output(string line, int? expected) =>
        Assert.Equal(expected, ProgressText.LastPercent(line));

    [Fact]
    public async Task Dism_repair_passes_its_percentages_on()
    {
        var h = new Harness();
        h.Processes.Handler = (_, _) => new ProcessResult(0,
            "Deployment Image Servicing and Management tool\n[=====   10.0%   ]\n[=========   55.5%   ]\n[======= 100.0% ====]\nThe restore operation completed successfully.");
        var progress = new ListProgress<FixProgress>();

        var result = await h.Runner(true).RunAsync(FixIds.DismRestoreHealth, null, progress: progress);

        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.Equal([10, 55, 100], progress.Items.Where(p => p.Percent is not null).Select(p => p.Percent!.Value));
        Assert.All(progress.Items.Where(p => p.Percent is not null), p => Assert.StartsWith("Repairing the component store", p.Step, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Risky_fix_reports_confirmation_and_backup_steps_first()
    {
        var h = new Harness();
        var progress = new ListProgress<FixProgress>();

        await h.Runner(true).RunAsync(FixIds.DismRestoreHealth, null, progress: progress);

        var steps = progress.Items.Select(p => p.Step).ToList();
        Assert.Equal("Waiting for your confirmation", steps[0]);
        Assert.StartsWith("Creating a restore point", steps[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restarting_services_reports_each_step()
    {
        var h = new Harness();
        var progress = new ListProgress<FixProgress>();

        await h.Runner(true).RunAsync(FixIds.RestartUpdateServices, null, progress: progress);

        var steps = progress.Items.Where(p => p.Step.StartsWith("Stopping", StringComparison.Ordinal) || p.Step.StartsWith("Starting", StringComparison.Ordinal)).ToList();
        Assert.Equal(6, steps.Count);
        Assert.Equal("Stopping the Windows Update service", steps[0].Step);
        Assert.Equal([0, 16, 33, 50, 66, 83], steps.Select(s => s.Percent!.Value));
        Assert.Equal(100, progress.Items[^1].Percent);
    }
}
