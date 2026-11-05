using Axis.Conector;

var builder = Host.CreateApplicationBuilder(args);

// Suporte a rodar como Windows Service
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Axis.Conector";
});

// Log de inicialização para diagnóstico (aparece no Event Viewer / console)
var startupLogger = LoggerFactory.Create(b => b.AddConsole().AddEventLog(s => s.SourceName = "Axis.Conector"))
    .CreateLogger("Axis.Conector.Startup");

var dbPath = ConfigHelper.DbPath;
var cs = ConfigHelper.ObterConnectionString();

if (string.IsNullOrEmpty(dbPath))
{
    startupLogger.LogError("axis_config.db NÃO foi encontrado. Configure AXIS_CONFIG_DB ou execute o Axis.Config.");
}
else
{
    startupLogger.LogInformation("axis_config.db encontrado em: {Path}", dbPath);
}

if (string.IsNullOrEmpty(cs))
{
    startupLogger.LogError("Connection string vazia. O serviço NÃO conseguirá conectar no PostgreSQL.");
}
else
{
    // Mascarar senha no log
    var safe = System.Text.RegularExpressions.Regex.Replace(cs, @"Password=[^;]*", "Password=***");
    startupLogger.LogInformation("Connection string carregada: {Cs}", safe);
}

// APOSENTADO: os 4 workers Shopify agora rodam dentro do Axis.WebApp como BackgroundServices
// multi-tenant (Axis.WebApp/HostedServices/Shopify*HostedService.cs).
// Este executável foi mantido apenas como referência/compatibilidade — não registra mais hosted services.
startupLogger.LogWarning("Axis.Conector está APOSENTADO. Os workers Shopify rodam agora no Axis.WebApp.");
//builder.Services.AddHostedService<ShopifyWorker>();
//builder.Services.AddHostedService<ShopifyOrderWorker>();
//builder.Services.AddHostedService<ShopifyFulfillmentWorker>();
//builder.Services.AddHostedService<ShopifyIncrementalWorker>();

var host = builder.Build();
host.Run();
