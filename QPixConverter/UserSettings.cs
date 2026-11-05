using System.Text.Json.Serialization;

namespace QPixConverter;

[Serializable]
public struct UserSettings
{
    [JsonPropertyName("lastInputFilePath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastInputFilePath { get; set; }

    [JsonPropertyName("lastOutputDirPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastOutputDirPath { get; set; }

    [JsonPropertyName("maxWidth")] public int MaxWidth { get; set; }
    [JsonPropertyName("maxHeight")] public int MaxHeight { get; set; }
    [JsonPropertyName("maxFPS")] public int MaxFps { get; set; }
    [JsonPropertyName("colorFormat")] public QPixLib.QPixFormat ColorFormat { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UserSettings))]
internal partial class UserSettingsJsonContext : JsonSerializerContext
{
}
