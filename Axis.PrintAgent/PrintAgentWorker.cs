using Axis.PrintAgent.Configuration;
using Axis.PrintAgent.SignalR;

namespace Axis.PrintAgent;

/// <summary>
/// Loop principal: se o agente já está pareado, mantém a conexão SignalR.
/// Se não, mostra instruções de pareamento e aguarda configuração ser gravada.
/// </summary>
public sealed class PrintAgentWorker : BackgroundService
{
    private readonly AgentConfig _config;
    private readonly AgentConnection _connection;
    private readonly ILogger<PrintAgentWorker> _log;

    public PrintAgentWorker(AgentConfig config, AgentConnection connection, ILogger<PrintAgentWorker> log)
    {
        _config = config; _connection = connection; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("=== Axis Print Agent ===");
        _log.LogInformation("Maquina: {Name} | Sistema: {OS}", Environment.MachineName,
            OperatingSystem.IsWindows() ? "Windows" :
            OperatingSystem.IsLinux() ? "Linux" :
            OperatingSystem.IsMacOS() ? "macOS" : "Desconhecido");
        _log.LogInformation("Config: {Path}", AgentConfig.GetConfigPath());

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_config.Pareado)
            {
                MostrarInstrucoesPareamento();
                // Recarrega a config a cada 5s aguardando o pareamento via CLI.
                while (!stoppingToken.IsCancellationRequested && !_config.Pareado)
                {
                    var recarregado = AgentConfig.Load();
                    if (recarregado.Pareado)
                    {
                        _config.ServerUrl = recarregado.ServerUrl;
                        _config.Token = recarregado.Token;
                        _config.AgentId = recarregado.AgentId;
                        _config.AgentName = recarregado.AgentName;
                        break;
                    }
                    try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch { return; }
                }
            }

            try
            {
                await _connection.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha na conexao. Tentando novamente em 10s.");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch { return; }
            }
        }
    }

    private void MostrarInstrucoesPareamento()
    {
        Console.WriteLine();
        Console.WriteLine("==============================================================");
        Console.WriteLine(" AGENTE AGUARDANDO PAREAMENTO");
        Console.WriteLine("==============================================================");
        Console.WriteLine(" Volte ao navegador, abra:");
        Console.WriteLine("   Configurações > Agentes de Impressão");
        Console.WriteLine(" e clique em \"Parear esta máquina\".");
        Console.WriteLine();
        Console.WriteLine(" O agente está escutando em http://127.0.0.1:39111");
        Console.WriteLine($" Config: {AgentConfig.GetConfigPath()}");
        Console.WriteLine("==============================================================");
        Console.WriteLine();
    }
}
