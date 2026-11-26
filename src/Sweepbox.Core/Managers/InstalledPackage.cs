namespace Sweepbox.Core.Managers;

public enum PackageKind { Cask, Formula, Package }

/// <summary>One package a manager reports as installed.</summary>
public sealed record InstalledPackage(string ManagerId, string Name, string? Version, PackageKind Kind);

/// <summary>What one manager has installed, or why listing it failed.</summary>
public sealed record ManagerInventory(string ManagerId, string DisplayName, IReadOnlyList<InstalledPackage> Packages, string? Error);
