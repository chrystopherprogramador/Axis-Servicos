using System.Text.Json;
using System.Text.Json.Serialization;
using Axis.PrintAgent.Configuration;

namespace Axis.PrintAgent;

/// <summary>
/// Lê <c>agent.defaults.json</c> ao lado do executável e copia <c>serverUrl</c> e
/// <c>tenant</c> para o <see cref="AgentConfig"/> persistente — apenas quando o agente
/// ainda não está pareado (ou seja, primeira execução). Não sobrescreve token.
///
/// O servidor injeta esse arquivo no ZIP gerado on-demand, já preenchido com a URL
/// do tenant que está baixando, para que o formulário de pareamento abra com tudo pronto.
/// </summary>
internal static class DefaultsLoader
{
    public static void AplicarSeNecessario()
    {
        try
        {
            var existente = AgentConfig.Load();
            if (existente.Pareado) return; // já pareado: não mexe

            var path = LocalizarArquivo();
            if (path is null) return;

            var json = File.ReadAllText(path);
            var defs = JsonSerializer.Deserialize<AgentDefaults>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (defs is null) return;

            var alterado = false;
            if (string.IsNullOrWhiteSpace(existente.ServerUrl) && !string.IsNullOrWhiteSpace(defs.ServerUrl))
            { existente.ServerUrl = defs.ServerUrl.TrimEnd('/'); alterado = true; }
            if (string.IsNullOrWhiteSpace(existente.Tenant) && !string.IsNullOrWhiteSpace(defs.Tenant))
            { existente.Tenant = defs.Tenant; alterado = true; }

            if (alterado) existente.Save();
        }
        catch
        {
            // qualquer erro aqui é silencioso: defaults são opcionais.
        }
    }

    private static string? LocalizarArquivo()
    {
        var exeDir = AppContext.BaseDirectory;
        var p1 = Path.Combine(exeDir, "agent.defaults.json");
        if (File.Exists(p1)) return p1;

        // Em PublishSingleFile o BaseDirectory pode apontar para o cache de extração.
        var procDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
        if (!string.IsNullOrEmpty(procDir))
        {
            var p2 = Path.Combine(procDir, "agent.defaults.json");
            if (File.Exists(p2)) return p2;
        }
        return null;
    }

    private sealed class AgentDefaults
    {
        [JsonPropertyName("serverUrl")] public string? ServerUrl { get; set; }
        [JsonPropertyName("tenant")] public string? Tenant { get; set; }
    }
}
