using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Axis.PrintAgent.Configuration;

namespace Axis.PrintAgent.LocalHttp;

/// <summary>
/// Mini servidor HTTP local (loopback) usado pelo navegador para detectar
/// o agente e disparar o pareamento sem que o usuário precise abrir terminal.
///
/// Endpoints:
///   GET  /ping       → { ok, paired, agentName, sistema, version }
///   POST /pair       → { serverUrl, codigo } → confirma pareamento e grava agent.json
///   OPTIONS *        → preflight CORS
///
/// Escuta apenas em 127.0.0.1 (loopback) para evitar exposição em rede.
/// Browsers modernos tratam http://127.0.0.1 como secure context, então a
/// página HTTPS do Axis consegue chamar sem aviso de mixed content.
/// </summary>
public sealed class LocalControlServer : BackgroundService
{
    public const int DefaultPort = 39111;

    private readonly AgentConfig _config;
    private readonly ILogger<LocalControlServer> _log;
    private readonly HttpListener _listener = new();

    public LocalControlServer(AgentConfig config, ILogger<LocalControlServer> log)
    {
        _config = config;
        _log = log;
        _listener.Prefixes.Add($"http://127.0.0.1:{DefaultPort}/");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { _listener.Start(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Não foi possível iniciar o servidor local na porta {Port}. O pareamento via navegador ficará indisponível.", DefaultPort);
            return;
        }

        _log.LogInformation("Servidor local de controle escutando em http://127.0.0.1:{Port}", DefaultPort);

        using var stopReg = stoppingToken.Register(() => { try { _listener.Stop(); } catch { } });

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "GetContextAsync falhou.");
                continue;
            }

            _ = Task.Run(() => HandleAsync(ctx, stoppingToken), stoppingToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            AddCors(ctx.Response, ctx.Request);

            if (string.Equals(ctx.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? string.Empty;
            switch (path)
            {
                case "/ping":
                    await JsonAsync(ctx, new PingResponse
                    {
                        Ok = true,
                        Paired = _config.Pareado,
                        AgentName = string.IsNullOrEmpty(_config.AgentName) ? Environment.MachineName : _config.AgentName,
                        AgentId = _config.AgentId,
                        Sistema = Sistema(),
                        Version = typeof(LocalControlServer).Assembly.GetName().Version?.ToString() ?? "0"
                    });
                    return;

                case "/pair":
                    if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Response.StatusCode = 405; ctx.Response.Close(); return;
                    }
                    await HandlePairAsync(ctx, ct);
                    return;

                default:
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha ao tratar requisição local.");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private async Task HandlePairAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        PairRequest? req;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            req = JsonSerializer.Deserialize<PairRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            await JsonAsync(ctx, new PairResponse { Ok = false, Erro = "JSON inválido." }, 400);
            return;
        }

        if (req is null || string.IsNullOrWhiteSpace(req.ServerUrl) || string.IsNullOrWhiteSpace(req.Codigo))
        {
            await JsonAsync(ctx, new PairResponse { Ok = false, Erro = "serverUrl e codigo são obrigatórios." }, 400);
            return;
        }

        var serverUrl = req.ServerUrl.TrimEnd('/');
        var codigo = req.Codigo.Trim().ToUpperInvariant();
        var tenant = (req.Tenant ?? string.Empty).Trim();

        var handler = new HttpClientHandler
        {
            // Aceita certificado de localhost/dev sem reclamação.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        try
        {
            using var pairReq = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/api/print-agent/pair/confirm")
            {
                Content = JsonContent.Create(new
                {
                    codigo,
                    nome = Environment.MachineName,
                    sistema = Sistema()
                })
            };
            if (!string.IsNullOrEmpty(tenant))
                pairReq.Headers.TryAddWithoutValidation("X-Tenant", tenant);

            using var resp = await http.SendAsync(pairReq, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var msg = await resp.Content.ReadAsStringAsync(ct);
                await JsonAsync(ctx, new PairResponse { Ok = false, Erro = $"{(int)resp.StatusCode}: {Truncate(msg, 200)}" }, 400);
                return;
            }

            var data = await resp.Content.ReadFromJsonAsync<ConfirmPairResponse>(cancellationToken: ct);
            if (data is null || string.IsNullOrEmpty(data.Token))
            {
                await JsonAsync(ctx, new PairResponse { Ok = false, Erro = "Resposta inválida do servidor." }, 502);
                return;
            }

            _config.ServerUrl = serverUrl;
            _config.Token = data.Token;
            _config.AgentId = data.AgenteId;
            _config.AgentName = Environment.MachineName;
            _config.Tenant = tenant;
            _config.Save();

            _log.LogInformation("Pareado via navegador. Agente {Id} no servidor {Url}.", data.AgenteId, serverUrl);

            await JsonAsync(ctx, new PairResponse
            {
                Ok = true,
                AgenteId = data.AgenteId,
                AgentName = _config.AgentName
            });
        }
        catch (Exception ex)
        {
            await JsonAsync(ctx, new PairResponse { Ok = false, Erro = ex.Message }, 500);
        }
    }

    private static void AddCors(HttpListenerResponse resp, HttpListenerRequest req)
    {
        var origin = req.Headers["Origin"];
        resp.Headers["Access-Control-Allow-Origin"] = string.IsNullOrEmpty(origin) ? "*" : origin;
        resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        resp.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        resp.Headers["Vary"] = "Origin";
        resp.Headers["Cache-Control"] = "no-store";
    }

    private static async Task JsonAsync<T>(HttpListenerContext ctx, T payload, int status = 200)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        ctx.Response.ContentLength64 = json.Length;
        await ctx.Response.OutputStream.WriteAsync(json);
        ctx.Response.Close();
    }

    private static string Sistema() =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsLinux() ? "Linux" :
        OperatingSystem.IsMacOS() ? "macOS" : "Outro";

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n);

    private sealed class PingResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("paired")] public bool Paired { get; set; }
        [JsonPropertyName("agentName")] public string AgentName { get; set; } = "";
        [JsonPropertyName("agentId")] public string AgentId { get; set; } = "";
        [JsonPropertyName("sistema")] public string Sistema { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
    }

    private sealed class PairRequest
    {
        [JsonPropertyName("serverUrl")] public string ServerUrl { get; set; } = "";
        [JsonPropertyName("codigo")] public string Codigo { get; set; } = "";
        [JsonPropertyName("tenant")] public string? Tenant { get; set; }
    }

    private sealed class PairResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("agenteId")] public string? AgenteId { get; set; }
        [JsonPropertyName("agentName")] public string? AgentName { get; set; }
        [JsonPropertyName("erro")] public string? Erro { get; set; }
    }

    private sealed class ConfirmPairResponse
    {
        [JsonPropertyName("token")] public string Token { get; set; } = "";
        [JsonPropertyName("agenteId")] public string AgenteId { get; set; } = "";
    }
}
