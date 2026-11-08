using System.Text.Json.Serialization;

namespace StorageLens.Models;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(AppPreferences))]
[JsonSerializable(typeof(AutomationSettings))]
[JsonSerializable(typeof(Dictionary<string, long>))]
internal sealed partial class StorageLensJsonContext : JsonSerializerContext
{
}
