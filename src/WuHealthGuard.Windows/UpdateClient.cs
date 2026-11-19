using System.Net.Http.Json;
using System.Text.Json;
using WuHealthGuard.Core.Updates;

namespace WuHealthGuard.Windows;

public sealed record UpdateCheck(UpdateDecision Decision, UpdateManifest? Manifest);

/// <summary>Fetches and verifies the signed update manifest, and downloads the update.</summary>
public sealed class UpdateClient(HttpClient http, string currentVersion, string? feedUrl, string? publicKey)
{
    /// <summary>Hard cap on download size, whatever the manifest says.</summary>
    private const long MaxDownloadBytes = 400L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Enabled => !string.IsNullOrWhiteSpace(feedUrl) && !string.IsNullOrWhiteSpace(publicKey);

    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return new UpdateCheck(new UpdateDecision(UpdateStatus.Disabled, "Updates are off in this build."), null);
        }

        UpdateManifest? manifest;
        try
        {
            manifest = await http.GetFromJsonAsync<UpdateManifest>(feedUrl, Json, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            manifest = null;
        }

        return new UpdateCheck(UpdateRules.Evaluate(manifest, currentVersion, publicKey), manifest);
    }

    /// <summary>Downloads into the locked-down updates folder and checks the SHA-256. Returns the file path.</summary>
    public async Task<string> DownloadAsync(UpdateManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Size > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"The update is unexpectedly large ({UpdateRules.FormatSize(manifest.Size)}).");
        }

        AppPaths.EnsureSecured();
        var dir = Directory.CreateDirectory(AppPaths.Updates);
        foreach (var old in dir.EnumerateFiles())
        {
            old.Delete();
        }

        var part = Path.Combine(dir.FullName, $"WuHealthGuard-{manifest.Version}.part");
        var final = Path.Combine(dir.FullName, $"WuHealthGuard-{manifest.Version}.exe");

        using (var response = await http.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                await using (target.ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > MaxDownloadBytes)
                        {
                            throw new InvalidOperationException("The update download is larger than allowed.");
                        }

                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        using (var check = File.OpenRead(part))
        {
            if (!UpdateRules.HashMatches(check, manifest.Sha256))
            {
                check.Dispose();
                File.Delete(part);
                throw new InvalidOperationException("The downloaded update doesn't match its signed hash. It was deleted.");
            }
        }

        File.Move(part, final, overwrite: true);
        return final;
    }
}
