using System.Text.Json;
using StorageLens.Models;
using Windows.Storage;

namespace StorageLens.Services;

public static class StorageMonitorStateService
{
    private const string StateFileName = "storage-monitor-state.json";
    private static readonly Mutex StateMutex = new(false, @"Local\StorageLens.MonitorState");

    public static Task<Dictionary<string, long>> LoadAsync()
    {
        var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, StateFileName);
        if (!RunExclusive(() => File.Exists(path)))
        {
            return Task.FromResult(new Dictionary<string, long>());
        }

        var json = RunExclusive(() => File.ReadAllText(path));
        try
        {
            return Task.FromResult(
                JsonSerializer.Deserialize(
                    json,
                    StorageLensJsonContext.Default.DictionaryStringInt64) ?? []);
        }
        catch (JsonException)
        {
            return Task.FromResult(new Dictionary<string, long>());
        }
    }

    public static Task SaveAsync(Dictionary<string, long> snapshot)
    {
        RunExclusive(() =>
        {
            var path = Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                StateFileName);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(
                        snapshot,
                        StorageLensJsonContext.Default.DictionaryStringInt64));
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        });
        return Task.CompletedTask;
    }

    private static T RunExclusive<T>(Func<T> action)
    {
        var acquired = false;
        try
        {
            try
            {
                acquired = StateMutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException("Timed out waiting for storage monitor state.");
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                StateMutex.ReleaseMutex();
            }
        }
    }

    private static void RunExclusive(Action action)
    {
        RunExclusive(() =>
        {
            action();
            return true;
        });
    }
}
