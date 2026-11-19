using System.Globalization;
using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Core.Repair;

public enum CpuArchitecture
{
    Unknown = 0,
    X86 = 1,
    X64 = 2,
    Arm64 = 3,
}

/// <summary>
/// What an in-place repair has to match: version, edition, system UI language and architecture.
/// </summary>
/// <param name="Build"><c>CurrentBuild</c>, e.g. 26300. On 24H2/25H2/26H2 this is the enabled version's build, not the 26100 base.</param>
/// <param name="Ubr">Update build revision (the monthly update), e.g. 9711.</param>
/// <param name="EditionId"><c>EditionID</c>, e.g. <c>Professional</c>, <c>Core</c>, <c>Enterprise</c>.</param>
/// <param name="SystemUiLanguage">System default UI language as a culture name, e.g. <c>en-US</c>. Setup requires the media to match it.</param>
/// <param name="IsInsider">The PC is enrolled in the Windows Insider Program.</param>
public sealed record WindowsIdentity(
    int Build,
    int Ubr,
    string? DisplayVersion,
    string EditionId,
    string? InstallationType,
    string SystemUiLanguage,
    CpuArchitecture Architecture,
    bool IsInsider)
{
    /// <summary>Windows 11 starts at build 22000. (The registry's ProductName still says "Windows 10" on Windows 11.)</summary>
    public bool IsWindows11 => Build >= 22000;

    public bool IsServer => InstallationType?.StartsWith("Server", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Long-Term Servicing Channel editions: EnterpriseS, EnterpriseSN, IoTEnterpriseS…</summary>
    public bool IsLtsc => EditionId.StartsWith("EnterpriseS", StringComparison.OrdinalIgnoreCase)
        || EditionId.StartsWith("IoTEnterpriseS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Enterprise editions (not LTSC): only on volume-licensing (business editions) media.</summary>
    public bool IsEnterprise => !IsLtsc && (EditionId.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase)
        || EditionId.StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase)
        || EditionId.Equals("ServerRdsh", StringComparison.OrdinalIgnoreCase));

    public string Family => IsWindows11 ? "Windows 11" : "Windows 10";

    /// <summary>"Windows 11 Pro 26H2 (build 26300.9711)".</summary>
    public string Describe() =>
        $"{Family} {Editions.DisplayName(EditionId)}" + (string.IsNullOrEmpty(DisplayVersion) ? "" : $" {DisplayVersion}") +
        $" (build {Build}.{Ubr})";
}

/// <summary>Friendly names for <c>EditionID</c> values.</summary>
public static class Editions
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Core"] = "Home",
        ["CoreN"] = "Home N",
        ["CoreSingleLanguage"] = "Home Single Language",
        ["CoreCountrySpecific"] = "Home China",
        ["Professional"] = "Pro",
        ["ProfessionalN"] = "Pro N",
        ["ProfessionalEducation"] = "Pro Education",
        ["ProfessionalEducationN"] = "Pro Education N",
        ["ProfessionalWorkstation"] = "Pro for Workstations",
        ["ProfessionalWorkstationN"] = "Pro N for Workstations",
        ["Education"] = "Education",
        ["EducationN"] = "Education N",
        ["Enterprise"] = "Enterprise",
        ["EnterpriseN"] = "Enterprise N",
        ["EnterpriseEval"] = "Enterprise Evaluation",
        ["EnterpriseS"] = "Enterprise LTSC",
        ["EnterpriseSN"] = "Enterprise N LTSC",
        ["IoTEnterprise"] = "IoT Enterprise",
        ["IoTEnterpriseS"] = "IoT Enterprise LTSC",
        ["ServerRdsh"] = "Enterprise multi-session",
    };

    public static string DisplayName(string editionId) => Names.GetValueOrDefault(editionId, editionId);
}

/// <summary>Reads <see cref="WindowsIdentity"/> from the registry. Registry values only: nothing localized is parsed.</summary>
public static class WindowsIdentityReader
{
    public const string CurrentVersion = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    public const string InsiderApplicability = @"HKLM\SOFTWARE\Microsoft\WindowsSelfHost\Applicability";

    /// <param name="systemUiLanguage">From <c>GetSystemDefaultUILanguage</c>, as a culture name.</param>
    /// <param name="architecture">The OS architecture.</param>
    public static WindowsIdentity Read(IRegistryReader registry, string systemUiLanguage, CpuArchitecture architecture)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var build = ParseInt(registry.GetValue(CurrentVersion, "CurrentBuild"))
            ?? ParseInt(registry.GetValue(CurrentVersion, "CurrentBuildNumber"))
            ?? 0;
        var ubr = ParseInt(registry.GetValue(CurrentVersion, "UBR")) ?? 0;

        return new WindowsIdentity(
            build,
            ubr,
            registry.GetValue(CurrentVersion, "DisplayVersion") as string,
            registry.GetValue(CurrentVersion, "EditionID") as string ?? "Unknown",
            registry.GetValue(CurrentVersion, "InstallationType") as string,
            systemUiLanguage,
            architecture,
            IsInsider(registry));
    }

    /// <summary>
    /// Enrolled in the Insider Program: a channel (<c>BranchName</c>) is set, or preview builds are enabled. A PC
    /// that left the program has neither; the leftover <c>WindowsSelfHost</c> keys for feature flighting don't count.
    /// </summary>
    public static bool IsInsider(IRegistryReader registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (registry.GetValue(InsiderApplicability, "BranchName") is string branch && !string.IsNullOrWhiteSpace(branch))
        {
            return true;
        }

        return ParseInt(registry.GetValue(InsiderApplicability, "EnablePreviewBuilds")) is > 0;
    }

    private static int? ParseInt(object? value) => value switch
    {
        int i => i,
        string s when int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var i) => i,
        _ => null,
    };
}
