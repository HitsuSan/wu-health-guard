using System.Text.Json;
using System.Text.Json.Serialization;

namespace WuHealthGuard.Core.Repair;

/// <summary>
/// How the image-info helper process reports back (DISM runs out of process, like the component store check).
/// One line: <c>images [json]</c> or <c>error message</c>.
/// </summary>
public static class InstallImageProtocol
{
    private const string ImagesPrefix = "images ";
    private const string ErrorPrefix = "error ";

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Images(IReadOnlyList<InstallImage> images) => ImagesPrefix + JsonSerializer.Serialize(images, Options);

    public static string Error(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return ErrorPrefix + message.ReplaceLineEndings(" ").Trim();
    }

    /// <summary>The images, or the helper's error message. Anything else in the output is ignored.</summary>
    public static (IReadOnlyList<InstallImage>? Images, string? Error) Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(ImagesPrefix, StringComparison.Ordinal))
            {
                try
                {
                    var images = JsonSerializer.Deserialize<List<InstallImage>>(line[ImagesPrefix.Length..], Options);
                    return images is null ? (null, "The image list was empty.") : (images, null);
                }
                catch (JsonException ex)
                {
                    return (null, $"Couldn't read the image list: {ex.Message}");
                }
            }

            if (line.StartsWith(ErrorPrefix, StringComparison.Ordinal))
            {
                return (null, line[ErrorPrefix.Length..]);
            }
        }

        return (null, "The image check ended without a result.");
    }
}
