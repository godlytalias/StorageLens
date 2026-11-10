using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Windows.AppLifecycle;

namespace StorageLens.Services;

internal sealed class AppUpdateCoordinator : IDisposable
{
    internal const string LegacyMainInstanceKey = "StorageLens.MainInstance";
    internal const string MainInstanceKeyPrefix =
        "StorageLens.MainInstance.";

    private static readonly TimeSpan GracefulShutdownTimeout =
        TimeSpan.FromSeconds(2);
    private EventWaitHandle? _updateRequest;
    private RegisteredWaitHandle? _registeredUpdateWait;
    private bool _disposed;

    public string CurrentInstanceKey { get; private set; } = string.Empty;

    public async Task<bool> TryBecomePrimaryAsync(
        AppInstance currentInstance,
        Version packageVersion,
        string packageName,
        string publisherId,
        Action requestExit,
        CancellationToken cancellationToken = default)
    {
        CurrentInstanceKey = BuildMainInstanceKey(packageVersion);
        var activation = currentInstance.GetActivatedEventArgs();
        var registered =
            AppInstance.FindOrRegisterForKey(CurrentInstanceKey);
        if (!registered.IsCurrent)
        {
            await registered.RedirectActivationToAsync(activation);
            return false;
        }

        StartUpdateRequestListener(CurrentInstanceKey, requestExit);
        var newerInstance = AppInstance.GetInstances()
            .Where(instance =>
                !instance.IsCurrent &&
                TryGetInstanceVersion(instance.Key, out var version) &&
                version > packageVersion)
            .OrderByDescending(instance =>
                TryGetInstanceVersion(instance.Key, out var version)
                    ? version
                    : new Version(0, 0))
            .FirstOrDefault();
        if (newerInstance is not null)
        {
            currentInstance.UnregisterKey();
            await newerInstance.RedirectActivationToAsync(activation);
            return false;
        }

        var olderInstances = AppInstance.GetInstances()
            .Where(instance =>
                !instance.IsCurrent &&
                TryGetInstanceVersion(instance.Key, out var version) &&
                version < packageVersion)
            .ToArray();
        var retiredProcessIds = new HashSet<int>
        {
            Environment.ProcessId,
        };
        foreach (var instance in olderInstances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (instance.ProcessId <= int.MaxValue)
            {
                retiredProcessIds.Add((int)instance.ProcessId);
            }

            await RetireOlderInstanceAsync(
                instance,
                cancellationToken);
        }

        await RetireOlderPackageProcessesAsync(
            packageVersion,
            packageName,
            publisherId,
            retiredProcessIds,
            cancellationToken);
        return true;
    }

    internal static string BuildMainInstanceKey(Version packageVersion) =>
        $"{MainInstanceKeyPrefix}{packageVersion.Major}." +
        $"{packageVersion.Minor}.{packageVersion.Build}.{packageVersion.Revision}";

    internal static bool TryGetInstanceVersion(
        string? instanceKey,
        out Version version)
    {
        if (string.Equals(
                instanceKey,
                LegacyMainInstanceKey,
                StringComparison.Ordinal))
        {
            version = new Version(0, 0, 0, 0);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(instanceKey) &&
            instanceKey.StartsWith(
                MainInstanceKeyPrefix,
                StringComparison.Ordinal) &&
            Version.TryParse(
                instanceKey[MainInstanceKeyPrefix.Length..],
                out var parsed) &&
            parsed.Major >= 0 &&
            parsed.Minor >= 0 &&
            parsed.Build >= 0 &&
            parsed.Revision >= 0)
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    private void StartUpdateRequestListener(
        string instanceKey,
        Action requestExit)
    {
        _updateRequest = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            BuildUpdateEventName(instanceKey),
            out _);
        _registeredUpdateWait =
            ThreadPool.RegisterWaitForSingleObject(
                _updateRequest,
                (_, _) => requestExit(),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
    }

    private static Task RetireOlderInstanceAsync(
        AppInstance instance,
        CancellationToken cancellationToken)
    {
        if (instance.ProcessId > int.MaxValue)
        {
            return Task.CompletedTask;
        }

        return RetireOlderProcessAsync(
            unchecked((int)instance.ProcessId),
            [instance.Key],
            cancellationToken);
    }

    private static async Task RetireOlderPackageProcessesAsync(
        Version packageVersion,
        string packageName,
        string publisherId,
        ISet<int> retiredProcessIds,
        CancellationToken cancellationToken)
    {
        foreach (var process in Process.GetProcessesByName("StorageLens"))
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (retiredProcessIds.Contains(process.Id))
                {
                    continue;
                }

                if (!TryGetProcessPackageVersion(
                        process,
                        packageName,
                        publisherId,
                        out var processVersion) ||
                    processVersion >= packageVersion)
                {
                    continue;
                }

                await RetireOlderProcessAsync(
                    process.Id,
                    [
                        BuildMainInstanceKey(processVersion),
                        LegacyMainInstanceKey,
                    ],
                    cancellationToken);
            }
        }
    }

    private static async Task RetireOlderProcessAsync(
        int processId,
        IReadOnlyList<string?> instanceKeys,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (instanceKeys.Any(TryRequestUpdateExit))
            {
                using var timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                timeout.CancelAfter(GracefulShutdownTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    return;
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested)
                {
                }
            }

            if (!process.HasExited)
            {
                Trace.TraceWarning(
                    $"Terminating older StorageLens process {processId} after package update.");
                process.Kill(entireProcessTree: false);
                using var timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                timeout.CancelAfter(GracefulShutdownTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested)
                {
                    Trace.TraceWarning(
                        $"Older StorageLens process {processId} did not exit after termination.");
                }
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Trace.TraceWarning(
                $"Could not terminate older StorageLens process {processId}: {exception.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal static bool TryGetPackageVersionFromPath(
        string executablePath,
        string packageName,
        string publisherId,
        out Version version)
    {
        version = new Version(0, 0);
        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            return false;
        }

        var packageDirectory =
            Directory.GetParent(executableDirectory)?.Name;
        var prefix = packageName + "_";
        var suffix = "__" + publisherId;
        if (string.IsNullOrWhiteSpace(packageDirectory) ||
            packageDirectory.Length <= prefix.Length + suffix.Length ||
            !packageDirectory.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase) ||
            !packageDirectory.EndsWith(
                suffix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var packageDetails = packageDirectory.Substring(
            prefix.Length,
            packageDirectory.Length - prefix.Length - suffix.Length);
        var architectureSeparator = packageDetails.IndexOf('_');
        if (architectureSeparator <= 0 ||
            !Version.TryParse(
                packageDetails[..architectureSeparator],
                out var parsed) ||
            parsed.Revision < 0)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    private static bool TryGetProcessPackageVersion(
        Process process,
        string packageName,
        string publisherId,
        out Version version)
    {
        try
        {
            return TryGetPackageVersionFromPath(
                process.MainModule?.FileName ?? string.Empty,
                packageName,
                publisherId,
                out version);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            version = new Version(0, 0);
            return false;
        }
        catch (InvalidOperationException)
        {
            version = new Version(0, 0);
            return false;
        }
        catch (NotSupportedException)
        {
            version = new Version(0, 0);
            return false;
        }
    }

    private static bool TryRequestUpdateExit(string? instanceKey)
    {
        if (string.IsNullOrWhiteSpace(instanceKey))
        {
            return false;
        }

        try
        {
            using var updateRequest =
                EventWaitHandle.OpenExisting(
                    BuildUpdateEventName(instanceKey));
            updateRequest.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string BuildUpdateEventName(string instanceKey)
    {
        var keyHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(instanceKey)));
        return $@"Local\StorageLens.Update.{keyHash}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _registeredUpdateWait?.Unregister(_updateRequest);
        _updateRequest?.Dispose();
        _registeredUpdateWait = null;
        _updateRequest = null;
        _disposed = true;
    }
}
