using Axis.PrintAgent;
using Axis.PrintAgent.Configuration;
using Axis.PrintAgent.Drivers;
using Axis.PrintAgent.LocalHttp;
using Axis.PrintAgent.Platform;
using Axis.PrintAgent.SignalR;

// Subcomandos via CLI: "pair <url> <codigo>" e "status".
// (Mantidos para troubleshooting / automação; a UX padrão é o formulário de pareamento.)
if (args.Length > 0)
{
    var cmd = args[0].ToLowerInvariant();
    if (cmd == "pair")
    {
        return await PairingCli.RunAsync(args);
    }
    if (cmd == "status")
    {
        return PairingCli.PrintStatus();
    }
    if (cmd is "--help" or "-h" or "help")
    {
        PairingCli.PrintHelp();
        return 0;
    }
}

var modoConsole = args.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase));
var modoTray    = args.Any(a => string.Equals(a, "--tray",    StringComparison.OrdinalIgnoreCase));
var modoPair    = args.Any(a => string.Equals(a, "--pair",    StringComparison.OrdinalIgnoreCase));

// Aplica defaults vindos de "agent.defaults.json" (que o servidor empacota junto do ZIP),
// sem sobrescrever pareamento existente.
DefaultsLoader.AplicarSeNecessario();

var configInicial = AgentConfig.Load();

if (OperatingSystem.IsWindows() && !modoConsole)
{
    // Em uso normal, escondemos o console para o .exe parecer um serviço da bandeja.
    ConsoleVisibility.HideOnWindows();
    WindowsAutostart.EnsureRegistered();

    // Abre o formulário de pareamento se ainda não estiver pareado OU se o usuário pedir explicitamente.
    if (modoPair || !configInicial.Pareado)
    {
#if AXIS_WINFORMS
        var ok = Axis.PrintAgent.UI.PairingUi.MostrarFormPareamento(configInicial);
        if (!ok && !configInicial.Pareado)
        {
            // Usuário fechou o formulário sem parear — encerra educadamente.
            return 0;
        }
#endif
    }
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<AgentConfig>(_ => AgentConfig.Load());
builder.Services.AddSingleton<IPrinterDriver>(_ => PrinterDriverFactory.Create());
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AgentConnection>();
builder.Services.AddHostedService<PrintAgentWorker>();
builder.Services.AddHostedService<LocalControlServer>();

// No Linux/macOS gravar PID em /tmp pode ajudar diagnóstico. Sem efeito no Windows.
if (!OperatingSystem.IsWindows())
{
    try { File.WriteAllText("/tmp/axis-print-agent.pid", Environment.ProcessId.ToString()); }
    catch { /* ignora */ }
}

var host = builder.Build();
await host.RunAsync();
return 0;
