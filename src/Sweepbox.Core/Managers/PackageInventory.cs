using System.Diagnostics;
using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;

namespace Sweepbox.Core.Managers;

/// <summary>Lists what each package manager on this machine has installed.</summary>
public sealed class PackageInventory(
    Func<AppSettings> settings,
    Func<AppSettings, Task<ICommandRunner>> runnerFactory,
    IReadOnlyList<PackageManager> managers)
{
    /// <summary>
    /// Lists every supported manager whose executable is found, enabled or not, one after another.
    /// A manager that fails becomes an inventory with an error; cancellation propagates.
    /// </summary>
    public async Task<IReadOnlyList<ManagerInventory>> ListAsync(CancellationToken cancellationToken)
    {
        var runner = await runnerFactory(settings());
        var result = new List<ManagerInventory>();
        foreach (var m in managers)
        {
            if (!m.IsSupportedOnThisOS || runner.ResolveExecutable(m.ExecutableName) is not { } executable)
                continue;
            try
            {
                result.Add(await m.ListInstalledAsync(runner, executable, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Add(new ManagerInventory(m.Id, m.DisplayName, [], ex.Message));
            }
        }

        return result;
    }

    /// <summary>
    /// Measures each non-empty inventory with its manager, one after another, and calls
    /// <paramref name="onManagerMeasured"/> once per inventory on the measuring thread. A manager that fails or
    /// can't be run reports every package as unknown; cancellation propagates.
    /// </summary>
    public async Task MeasureAsync(IReadOnlyList<ManagerInventory> inventories,
        Action<IReadOnlyDictionary<PackageKey, PackageSize?>> onManagerMeasured, CancellationToken cancellationToken)
    {
        var runner = await runnerFactory(settings());
        foreach (var inventory in inventories)
        {
            if (inventory.Packages.Count == 0 || inventory.Error is not null)
                continue;
            if (managers.FirstOrDefault(x => x.Id == inventory.ManagerId) is not { } m)
                continue;

            IReadOnlyDictionary<PackageKey, PackageSize?> measured;
            if (runner.ResolveExecutable(m.ExecutableName) is not { } executable)
            {
                measured = Unknown(inventory.Packages);
            }
            else
            {
                try
                {
                    measured = Complete(await m.MeasureAsync(runner, executable, inventory.Packages, cancellationToken), inventory.Packages);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Measuring {m.DisplayName} sizes failed: {ex.Message}");
                    measured = Unknown(inventory.Packages);
                }
            }

            onManagerMeasured(measured);
        }
    }

    private static Dictionary<PackageKey, PackageSize?> Unknown(IEnumerable<InstalledPackage> packages) =>
        packages.ToDictionary(PackageKey.Of, _ => (PackageSize?)null);

    private static Dictionary<PackageKey, PackageSize?> Complete(IReadOnlyDictionary<PackageKey, PackageSize?> measured,
        IEnumerable<InstalledPackage> packages)
    {
        var result = Unknown(packages);
        foreach (var key in result.Keys.ToList())
            if (measured.TryGetValue(key, out var size))
                result[key] = size;
        return result;
    }
}
