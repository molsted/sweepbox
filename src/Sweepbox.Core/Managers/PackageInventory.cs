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
}
