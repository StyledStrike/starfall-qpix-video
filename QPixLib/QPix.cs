using System.Text.Json.Serialization;

namespace QPixLib;

enum QPixFrameMode
{
    SKIP = 0,
    DELTA = 1,
    FULL = 2,
}

[Serializable]
public struct QPixMetadata
{
    [JsonPropertyName("version")] public uint Version { get; set; }
    [JsonPropertyName("width")] public uint Width { get; set; }
    [JsonPropertyName("height")] public uint Height { get; set; }
    [JsonPropertyName("fps")] public uint FPS { get; set; }
    [JsonPropertyName("frameCount")] public uint FrameCount { get; set; }
    [JsonPropertyName("colorFormat")] public string ColorFormat { get; set; }
    [JsonPropertyName("mappingInterval")] public uint MappingInterval { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(QPixMetadata))]
internal partial class QPixMetadataContext : JsonSerializerContext
{
}
