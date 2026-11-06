using System.Diagnostics;

namespace StorageLens.Services;

public sealed record ScanOwner(int ProcessId, long ProcessStartedUtc)
{
    public static ScanOwner Current
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return new ScanOwner(
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime())
                    .ToUnixTimeMilliseconds());
        }
    }

    public static bool IsAlive(int processId, long processStartedUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var actualStartedUtc =
                new DateTimeOffset(process.StartTime.ToUniversalTime())
                    .ToUnixTimeMilliseconds();
            return Math.Abs(actualStartedUtc - processStartedUtc) < 2_000 &&
                   !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
