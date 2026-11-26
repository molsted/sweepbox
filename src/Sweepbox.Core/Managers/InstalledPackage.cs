namespace Sweepbox.Core.Managers;

public enum PackageKind { Cask, Formula, Package }

/// <summary>One package a manager reports as installed.</summary>
/// <param name="Companions">
/// Packages removed together with this one, e.g. Chocolatey's hidden "git.install" for "git"; null when there are none.
/// </param>
public sealed record InstalledPackage(string ManagerId, string Name, string? Version, PackageKind Kind,
    IReadOnlyList<string>? Companions = null);

/// <summary>What one manager has installed, or why listing it failed.</summary>
public sealed record ManagerInventory(string ManagerId, string DisplayName, IReadOnlyList<InstalledPackage> Packages, string? Error);
