using System.Text.Json;
using System.Text.Json.Serialization;

namespace Axis.PrintAgent.Configuration;

/// <summary>
/// Configuração persistente do agente. Após o pareamento bem-sucedido, o
/// arquivo agent.json é gravado em <see cref="GetConfigDirectory"/> com a URL
/// do tenant e o token de autenticação. Não é versionado.
/// </summary>
public sealed class AgentConfig
{
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("agentName")]
    public string AgentName { get; set; } = Environment.MachineName;

    [JsonPropertyName("tenant")]
    public string Tenant { get; set; } = string.Empty;

    [JsonIgnore]
    public bool Pareado => !string.IsNullOrWhiteSpace(ServerUrl)
                          && !string.IsNullOrWhiteSpace(Token);

    public static string GetConfigDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var dir = Path.Combine(baseDir, "AxisPrintAgent");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetConfigPath() => Path.Combine(GetConfigDirectory(), "agent.json");

    public static AgentConfig Load()
    {
        var path = GetConfigPath();
        if (!File.Exists(path)) return new AgentConfig();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AgentConfig>(json) ?? new AgentConfig();
        }
        catch
        {
            return new AgentConfig();
        }
    }

    public void Save()
    {
        var path = GetConfigPath();
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
