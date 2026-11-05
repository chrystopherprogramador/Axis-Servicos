using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Axis.PrintAgent.Configuration;

namespace Axis.PrintAgent;

/// <summary>
/// CLI de pareamento. Recebe um código curto gerado no navegador (ex.: AX-7K3M),
/// chama o endpoint POST {server}/api/print-agent/pair/confirm e grava o token
/// retornado no arquivo de configuração local.
/// </summary>
internal static class PairingCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Uso: axis-print-agent pair <url-do-tenant> <codigo>");
            Console.Error.WriteLine("Exemplo: axis-print-agent pair https://acme.axis.com.br AX-7K3M");
            return 2;
        }
        var serverUrl = args[1].TrimEnd('/');
        var codigo = args[2].Trim().ToUpperInvariant();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var url = $"{serverUrl}/api/print-agent/pair/confirm";
        var payload = new ConfirmPairRequest
        {
            Codigo = codigo,
            NomeMaquina = Environment.MachineName,
            Sistema = OperatingSystem.IsWindows() ? "Windows" :
                      OperatingSystem.IsLinux() ? "Linux" :
                      OperatingSystem.IsMacOS() ? "macOS" : "Outro"
        };
        try
        {
            using var resp = await http.PostAsJsonAsync(url, payload);
            if (!resp.IsSuccessStatusCode)
            {
                var msg = await resp.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"Falha {(int)resp.StatusCode}: {msg}");
                return 1;
            }
            var data = await resp.Content.ReadFromJsonAsync<ConfirmPairResponse>();
            if (data is null || string.IsNullOrEmpty(data.Token))
            {
                Console.Error.WriteLine("Resposta invalida do servidor.");
                return 1;
            }

            var cfg = new AgentConfig
            {
                ServerUrl = serverUrl,
                Token = data.Token,
                AgentId = data.AgenteId,
                AgentName = Environment.MachineName
            };
            cfg.Save();
            Console.WriteLine("Pareado com sucesso!");
            Console.WriteLine($" Servidor : {serverUrl}");
            Console.WriteLine($" Agente   : {data.AgenteId}");
            Console.WriteLine($" Config   : {AgentConfig.GetConfigPath()}");
            Console.WriteLine();
            Console.WriteLine("Reinicie o servico para conectar.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Erro: {ex.Message}");
            return 1;
        }
    }

    public static int PrintStatus()
    {
        var cfg = AgentConfig.Load();
        Console.WriteLine($"Config : {AgentConfig.GetConfigPath()}");
        Console.WriteLine($"Servidor: {(string.IsNullOrEmpty(cfg.ServerUrl) ? "(nao pareado)" : cfg.ServerUrl)}");
        Console.WriteLine($"AgenteId: {(string.IsNullOrEmpty(cfg.AgentId) ? "(nao pareado)" : cfg.AgentId)}");
        Console.WriteLine($"Maquina : {cfg.AgentName}");
        return 0;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Axis Print Agent");
        Console.WriteLine();
        Console.WriteLine("Comandos:");
        Console.WriteLine("  axis-print-agent                     Roda o agente (modo padrao).");
        Console.WriteLine("  axis-print-agent pair <url> <cod>    Pareia este agente com um tenant.");
        Console.WriteLine("  axis-print-agent status              Exibe a configuracao atual.");
        Console.WriteLine("  axis-print-agent help                Exibe esta ajuda.");
    }

    private sealed class ConfirmPairRequest
    {
        [JsonPropertyName("codigo")] public string Codigo { get; set; } = "";
        [JsonPropertyName("nomeMaquina")] public string NomeMaquina { get; set; } = "";
        [JsonPropertyName("sistema")] public string Sistema { get; set; } = "";
    }

    private sealed class ConfirmPairResponse
    {
        [JsonPropertyName("token")] public string Token { get; set; } = "";
        [JsonPropertyName("agenteId")] public string AgenteId { get; set; } = "";
    }
}
