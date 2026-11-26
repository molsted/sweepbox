namespace Sweepbox.Core.Managers;

/// <summary>Disk space a package occupies; <paramref name="IsEstimate"/> when only part of it could be attributed.</summary>
public sealed record PackageSize(long Bytes, bool IsEstimate);

/// <summary>Identifies a package across inventories; companions and version are deliberately not part of it.</summary>
public sealed record PackageKey(string ManagerId, string Name, PackageKind Kind)
{
    public static PackageKey Of(InstalledPackage p) => new(p.ManagerId, p.Name, p.Kind);
}
