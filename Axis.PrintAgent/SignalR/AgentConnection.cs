using Axis.PrintAgent.Configuration;
using Axis.PrintAgent.Drivers;
using Microsoft.AspNetCore.SignalR.Client;

namespace Axis.PrintAgent.SignalR;

/// <summary>
/// Conecta ao hub /hubs/print-agent do servidor Axis usando JWT do pareamento.
/// Reage a:
///   - ListarImpressoras() => string[]
///   - Imprimir(jobId, impressora, dadosBase64) => OK/erro
/// Reconecta automaticamente.
/// </summary>
public sealed class AgentConnection : IAsyncDisposable
{
    private readonly AgentConfig _config;
    private readonly IPrinterDriver _driver;
    private readonly ILogger<AgentConnection> _log;
    private HubConnection? _hub;

    public AgentConnection(AgentConfig config, IPrinterDriver driver, ILogger<AgentConnection> log)
    {
        _config = config; _driver = driver; _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // O hub valida o token via query string ?access_token=... (e não pelo header
        // Authorization), então enviamos das duas formas para garantir compatibilidade.
        var baseUrl = _config.ServerUrl.TrimEnd('/');
        var token = Uri.EscapeDataString(_config.Token ?? string.Empty);
        var hubUrl = $"{baseUrl}/hubs/print-agent?access_token={token}";
        _log.LogInformation(
            "Conectando ao hub: {Url}/hubs/print-agent (tenant={Tenant})",
            baseUrl,
            string.IsNullOrWhiteSpace(_config.Tenant) ? "(token)" : _config.Tenant);

        _hub = new HubConnectionBuilder()
            .WithUrl(hubUrl, opt =>
            {
                opt.AccessTokenProvider = () => Task.FromResult<string?>(_config.Token);
                if (!string.IsNullOrWhiteSpace(_config.Tenant))
                    opt.Headers["X-Tenant"] = _config.Tenant;
                // Aceita certificado self-signed em dev/local.
                opt.HttpMessageHandlerFactory = inner =>
                {
                    if (inner is HttpClientHandler h)
                        h.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
                    return inner;
                };
                opt.WebSocketConfiguration = ws =>
                {
                    ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                };
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)
            })
            .Build();

        _hub.Reconnecting += err => { _log.LogWarning(err, "Reconectando..."); return Task.CompletedTask; };
        _hub.Reconnected += id => { _log.LogInformation("Reconectado. ConnectionId={Id}", id); return RegistrarAsync(); };
        _hub.Closed += err => { _log.LogWarning(err, "Conexao fechada."); return Task.CompletedTask; };

        // Handler que RESPONDE ao servidor (InvokeAsync) — usar overload Func<Task<TResult>>.
        _hub.On("ListarImpressoras", () =>
        {
            try { return Task.FromResult(_driver.ListarImpressoras().ToArray()); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha ao listar impressoras.");
                return Task.FromResult(Array.Empty<string>());
            }
        });

        _hub.On("Imprimir", async (string jobId, string impressora, string dadosBase64) =>
        {
            try
            {
                var bytes = Convert.FromBase64String(dadosBase64);
                await _driver.ImprimirRawAsync(impressora, bytes, ct);
                _log.LogInformation("Job {Id} impresso em {Imp} ({Bytes}b)", jobId, impressora, bytes.Length);
                return new JobResult(jobId, true, null);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha no job {Id}", jobId);
                return new JobResult(jobId, false, ex.Message);
            }
        });

        await _hub.StartAsync(ct);
        _log.LogInformation("Conectado. ConnectionId={Id}", _hub.ConnectionId);
        await RegistrarAsync();

        // Mantém o serviço vivo enquanto o token de cancelamento não for sinalizado.
        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task RegistrarAsync()
    {
        if (_hub is null) return;
        try
        {
            var impressoras = _driver.ListarImpressoras().ToArray();
            await _hub.InvokeAsync("RegistrarAgente", new AgentInfo(
                _config.AgentId,
                _config.AgentName,
                _driver.Tipo,
                impressoras));
            _log.LogInformation("Agente registrado: {Id} ({Qtd} impressoras)", _config.AgentId, impressoras.Length);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao registrar agente no servidor.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }

    private sealed record AgentInfo(string AgenteId, string Nome, string Tipo, string[] Impressoras);
    private sealed record JobResult(string JobId, bool Sucesso, string? Erro);
}
