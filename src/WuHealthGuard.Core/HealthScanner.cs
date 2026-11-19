using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core;

/// <summary>Runs all checks and builds a <see cref="HealthReport"/>. A failing check doesn't stop the others.</summary>
public sealed class HealthScanner(IEnumerable<IHealthCheck> checks, ISystemInfo system, TimeProvider time)
{
    private readonly IReadOnlyList<IHealthCheck> _checks = checks.ToList();

    public IReadOnlyList<IHealthCheck> Checks => _checks;

    public async Task<HealthReport> ScanAsync(CancellationToken cancellationToken = default)
    {
        var findings = new List<Finding>();
        var errors = new List<CheckError>();

        foreach (var check in _checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                findings.AddRange(await check.RunAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // One broken check must not hide the results of the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                errors.Add(new CheckError(check.Id, check.Name, ex.Message));
            }
        }

        return new HealthReport(time.GetUtcNow(), system.LastBootTime, findings, errors);
    }
}
