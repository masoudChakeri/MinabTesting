using System.Text.Json.Serialization;

namespace MinabClassManager.Models;

public class ClassSession
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("starts_at")]
    public string StartsAt { get; set; } = string.Empty;

    [JsonPropertyName("host_url")]
    public string HostUrl { get; set; } = string.Empty;

    [JsonPropertyName("join_url")]
    public string JoinUrl { get; set; } = string.Empty;
}
