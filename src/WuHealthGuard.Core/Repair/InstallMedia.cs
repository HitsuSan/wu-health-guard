namespace WuHealthGuard.Core.Repair;

/// <summary>One image in <c>sources\install.wim</c> / <c>install.esd</c>, as DISM reports it.</summary>
/// <param name="Index">Image index (1-based), for Setup's <c>/ImageIndex</c>.</param>
/// <param name="Build">Build number, e.g. 26300.</param>
/// <param name="Ubr">"ServicePack Build": the monthly update revision baked into the image, e.g. 9550. 0 if unknown.</param>
/// <param name="Languages">Culture names, e.g. <c>en-US</c>. Empty if DISM didn't report any.</param>
public sealed record InstallImage(
    int Index,
    string Name,
    string EditionId,
    int Build,
    int Ubr,
    CpuArchitecture Architecture,
    IReadOnlyList<string> Languages,
    string? DefaultLanguage);

/// <summary>Installation media: an ISO's contents, a USB stick or an extracted folder.</summary>
/// <param name="Root">Folder that holds <c>setup.exe</c>, e.g. <c>E:\</c>.</param>
/// <param name="ImageFile">Full path of <c>sources\install.wim</c>, <c>install.esd</c> or <c>install.swm</c>.</param>
/// <param name="IsoPath">The ISO it came from, if any.</param>
public sealed record InstallMedia(string Root, string ImageFile, IReadOnlyList<InstallImage> Images, string? IsoPath = null)
{
    public string SetupPath => Path.Join(Root, "setup.exe");

    /// <summary>"E:\ (Win11_26H2_English_x64.iso)".</summary>
    public string Describe() => IsoPath is null ? Root : $"{Root} ({Path.GetFileName(IsoPath)})";
}

public enum MediaNoteLevel
{
    /// <summary>Fine, or worth knowing.</summary>
    Info = 0,

    /// <summary>Setup will most likely refuse to keep apps.</summary>
    Problem = 1,
}

public sealed record MediaNote(MediaNoteLevel Level, string Text);

/// <summary>How install media compares with this PC.</summary>
/// <param name="Image">The image Setup would use for this PC (same architecture and edition), or null if there is none.</param>
public sealed record MediaVerdict(InstallImage? Image, IReadOnlyList<MediaNote> Notes)
{
    /// <summary>Nothing found that stops Setup keeping apps. Setup's own scan still has the final word.</summary>
    public bool LooksRight => Image is not null && Notes.All(n => n.Level != MediaNoteLevel.Problem);
}

/// <summary>
/// The rules for "Keep personal files and apps", applied to install media before Setup runs.
/// <list type="bullet">
/// <item>Same architecture.</item>
/// <item>Same edition: changing to a lower edition removes apps (Microsoft: Windows upgrade paths).</item>
/// <item>Same system UI language: required since Windows 11 22H2 (Microsoft: Setup command-line options, /Auto Upgrade).</item>
/// <item>Same or newer build: an older build is a downgrade. Not documented by Microsoft, consistently reported.</item>
/// </list>
/// These are a preview with reasons. The decision is Setup's own compatibility scan (<see cref="SetupCommand.Scan"/>).
/// </summary>
public static class RepairMediaRules
{
    public static MediaVerdict Evaluate(WindowsIdentity pc, InstallMedia media)
    {
        ArgumentNullException.ThrowIfNull(pc);
        ArgumentNullException.ThrowIfNull(media);

        var notes = new List<MediaNote>();
        if (media.Images.Count == 0)
        {
            notes.Add(new(MediaNoteLevel.Problem, "No Windows images were found on this media."));
            return new MediaVerdict(null, notes);
        }

        var sameArch = media.Images.Where(i => i.Architecture == pc.Architecture).ToList();
        if (sameArch.Count == 0)
        {
            var archs = string.Join(", ", media.Images.Select(i => Architectures.Name(i.Architecture)).Distinct());
            notes.Add(new(MediaNoteLevel.Problem,
                $"This media is for {archs}; this PC is {Architectures.Name(pc.Architecture)}. Get the {Architectures.Name(pc.Architecture)} ISO."));
            return new MediaVerdict(null, notes);
        }

        var image = sameArch.FirstOrDefault(i => string.Equals(i.EditionId, pc.EditionId, StringComparison.OrdinalIgnoreCase));
        if (image is null)
        {
            var editions = string.Join(", ", sameArch.Select(i => Editions.DisplayName(i.EditionId)).Distinct());
            var hint = pc.IsEnterprise
                ? " Enterprise is on the business-editions (volume licensing) ISO, not the one on Microsoft's public download page."
                : pc.IsLtsc
                    ? " LTSC needs LTSC media of the same version, from volume licensing."
                    : "";
            notes.Add(new(MediaNoteLevel.Problem,
                $"This media doesn't contain {Editions.DisplayName(pc.EditionId)}, the edition on this PC. It contains: {editions}. " +
                "Installing a different edition doesn't keep apps." + hint));
            return new MediaVerdict(null, notes);
        }

        notes.Add(new(MediaNoteLevel.Info, $"Edition: {Editions.DisplayName(image.EditionId)} (image {image.Index}), {Architectures.Name(image.Architecture)}. Matches."));
        notes.Add(LanguageNote(pc, image));
        notes.Add(BuildNote(pc, image));

        if (pc.IsInsider)
        {
            notes.Add(new(MediaNoteLevel.Info,
                "This PC is in the Windows Insider Program. Media only keeps apps if it's the same Insider build or newer."));
        }

        return new MediaVerdict(image, notes);
    }

    private static MediaNote LanguageNote(WindowsIdentity pc, InstallImage image)
    {
        if (image.Languages.Count == 0)
        {
            return new(MediaNoteLevel.Info, "Couldn't read the media's language. Setup's check will compare it.");
        }

        if (image.Languages.Contains(pc.SystemUiLanguage, StringComparer.OrdinalIgnoreCase))
        {
            return new(MediaNoteLevel.Info, $"Language: {Languages.Describe(pc.SystemUiLanguage)}. Matches.");
        }

        var media = string.Join(", ", image.Languages.Select(Languages.Describe));
        return new(MediaNoteLevel.Problem,
            $"Language: the media is {media}, but this PC's system language is {Languages.Describe(pc.SystemUiLanguage)}. " +
            "Since Windows 11 22H2, Setup only keeps apps when they're exactly the same (even en-US and en-GB count as different). " +
            "Get the ISO in the PC's language.");
    }

    private static MediaNote BuildNote(WindowsIdentity pc, InstallImage image)
    {
        var mediaVersion = image.Ubr > 0 ? $"{image.Build}.{image.Ubr}" : $"{image.Build}";
        var pcVersion = $"{pc.Build}.{pc.Ubr}";

        if (image.Build < pc.Build)
        {
            return new(MediaNoteLevel.Problem,
                $"Version: the media is build {mediaVersion}, older than this PC ({pcVersion}). Going back to an older " +
                "version doesn't keep apps. Get the ISO for this PC's version or newer.");
        }

        if (image.Build > pc.Build)
        {
            return new(MediaNoteLevel.Info,
                $"Version: the media is build {mediaVersion}, newer than this PC ({pcVersion}). Setup upgrades Windows to " +
                "the newer version and keeps apps; that's a feature update as well as a repair.");
        }

        if (image.Ubr > 0 && image.Ubr < pc.Ubr)
        {
            return new(MediaNoteLevel.Info,
                $"Version: build {mediaVersion}, same version as this PC ({pcVersion}) with an older monthly update. That's " +
                "normal for a repair; Windows Update installs the newer monthly update again afterwards.");
        }

        return new(MediaNoteLevel.Info, $"Version: build {mediaVersion}, same version as this PC ({pcVersion}).");
    }
}

public static class Architectures
{
    public static string Name(CpuArchitecture architecture) => architecture switch
    {
        CpuArchitecture.X64 => "x64",
        CpuArchitecture.Arm64 => "Arm64",
        CpuArchitecture.X86 => "32-bit (x86)",
        _ => "an unknown architecture",
    };
}

public static class Languages
{
    /// <summary>Where Microsoft's download page uses a different name than the language's English name.</summary>
    private static readonly Dictionary<string, string> DownloadPageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en-GB"] = "English International",
        ["fr-CA"] = "French Canadian",
        ["es-MX"] = "Spanish (Mexico)",
        ["pt-BR"] = "Brazilian Portuguese",
    };

    /// <summary>"English (United States) [en-US]".</summary>
    public static string Describe(string cultureName) => $"{EnglishName(cultureName)} [{cultureName}]";

    public static string EnglishName(string cultureName)
    {
        try
        {
            return System.Globalization.CultureInfo.GetCultureInfo(cultureName).EnglishName;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return cultureName;
        }
    }

    /// <summary>The name to pick on Microsoft's download page, when it's not the plain English name.</summary>
    public static string? DownloadPageName(string cultureName) => DownloadPageNames.GetValueOrDefault(cultureName);
}
