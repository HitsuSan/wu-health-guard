using System.Reflection;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Tests;

public class FixGuideTests
{
    /// <summary>Stands in for any fix by id; the guide only looks at Id, Description and Risk.</summary>
    private sealed class StubFix(string id, FixRisk risk = FixRisk.Safe) : IFix
    {
        public string Id => id;

        public string Title => id;

        public string Description => "Technical description of " + id;

        public FixRisk Risk => risk;

        public string? CannotApplyReason(Finding? finding, HealthReport? report) => null;

        public IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) => [];

        public Task<FixResult> ApplyAsync(FixContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public static TheoryData<string> AllFixIds()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(FixIds).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            data.Add((string)field.GetValue(null)!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllFixIds))]
    public void Every_fix_has_a_plain_explanation_with_facts(string id)
    {
        var guide = FixGuides.For(new StubFix(id));

        Assert.DoesNotContain("Technical description", guide.Summary, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(guide.Summary));
        Assert.Contains(guide.Facts, f => f.Kind == FixFactKind.Duration);
        Assert.Contains(guide.Facts, f => f.Kind == FixFactKind.Restart || f.Kind == FixFactKind.Usable);
    }

    [Fact]
    public void Risky_fixes_say_they_ask_first_and_what_protects_the_pc()
    {
        var harness = new Harness();
        IFix[] risky = [new DismRestoreHealthFix(harness.Processes), new ClearStaleRebootFlagsFix(harness.Registry), new RemoveStuckPackageFix(harness.Processes)];

        foreach (var fix in risky)
        {
            Assert.Equal(FixRisk.Risky, fix.Risk);
            Assert.Contains(FixGuides.For(fix).Facts, f => f.Kind == FixFactKind.Protected && f.Text.StartsWith("Asks first", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Pause_explanation_uses_the_configured_number_of_days()
    {
        var fix = new PauseUpdatesFix(new FakePause(), new FakeTime(T.Now), () => 14);

        Assert.Contains("for 14 days", FixGuides.For(fix).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_fix_falls_back_to_its_description_and_flags_risk()
    {
        var guide = FixGuides.For(new StubFix("something-new", FixRisk.Risky));

        Assert.Equal("Technical description of something-new", guide.Summary);
        Assert.Contains(guide.Facts, f => f.Kind == FixFactKind.Protected);
    }
}
