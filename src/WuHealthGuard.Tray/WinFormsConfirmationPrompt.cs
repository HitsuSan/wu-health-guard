using System.Text;
using WuHealthGuard.Core.Fixes;

namespace WuHealthGuard.Tray;

/// <summary>Confirmation dialog for risky fixes. Defaults to "No".</summary>
internal sealed class WinFormsConfirmationPrompt(Func<IWin32Window?> owner) : IConfirmationPrompt
{
    public Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken)
    {
        var text = new StringBuilder()
            .AppendLine(request.Fix.Description)
            .AppendLine();

        if (request.BackupPlan.Count > 0)
        {
            text.AppendLine("Before making changes, WU Health Guard will back up:");
            foreach (var item in request.BackupPlan)
            {
                text.Append("  • ").AppendLine(FixRunner.Describe(item));
            }

            text.AppendLine().AppendLine("If any backup fails, nothing is changed.");
        }

        text.AppendLine().Append("Everything is recorded in the action log. Continue?");

        var answer = MessageBox.Show(
            owner(),
            text.ToString(),
            $"Risky fix: {request.Fix.Title}",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return Task.FromResult(answer == DialogResult.Yes);
    }
}
