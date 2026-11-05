using Npgsql;

namespace Axis.Conector;

/// <summary>
/// Worker que envia informações de expedição (fulfillment) do Axis para a Shopify.
/// Quando um pedido importado da Shopify é expedido no Axis, este worker envia
/// o tracking de volta para a Shopify.
/// </summary>
public class ShopifyFulfillmentWorker(ILogger<ShopifyFulfillmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ShopifyFulfillmentWorker iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cs = ConfigHelper.ObterConnectionString();
                if (string.IsNullOrEmpty(cs) || !ConfigHelper.ObterParametroBool("SHOPIFY_ATIVO")
                    || !ConfigHelper.ObterParametroBool("SHOPIFY_SYNC_FULFILLMENT"))
                {
                    await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
                    continue;
                }

                var loja = ConfigHelper.ObterParametro("SHOPIFY_LOJA");
                var token = ConfigHelper.ObterParametro("SHOPIFY_ACCESS_TOKEN");
                var empresaId = ConfigHelper.ObterParametroLong("SHOPIFY_EMPRESA_ID");

                if (string.IsNullOrEmpty(loja) || string.IsNullOrEmpty(token))
                {
                    await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
                    continue;
                }

                var dbLogger = new IntegrationLogger(cs, empresaId);
                using var shopify = new ShopifyApiClient(loja, token);

                // Buscar pedidos expedidos no Axis que vieram da Shopify e ainda não tiveram fulfillment enviado
                var pendentes = ObterExpedicoesPendentes(cs, empresaId);

                foreach (var (pedidoId, shopifyOrderId, tracking, transportadora) in pendentes)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    try
                    {
                        var fulfillment = new ShopifyFulfillment
                        {
                            TrackingNumber = tracking,
                            TrackingCompany = transportadora
                        };

                        await shopify.CriarFulfillmentAsync(shopifyOrderId, fulfillment);
                        MarcarFulfillmentEnviado(cs, pedidoId);
                        dbLogger.Info("FULFILLMENT", $"Fulfillment enviado para Shopify Order #{shopifyOrderId} (Pedido #{pedidoId})");
                    }
                    catch (Exception ex)
                    {
                        dbLogger.Error("FULFILLMENT", $"Erro ao enviar fulfillment do pedido #{pedidoId}: {ex.Message}", ex.ToString());
                    }
                }

                ConfigHelper.LimparCache();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no ShopifyFulfillmentWorker");
            }

            var intervalo = ConfigHelper.ObterParametroDouble("SHOPIFY_INTERVALO_SYNC", 5);
            await Task.Delay(TimeSpan.FromMinutes(intervalo), stoppingToken);
        }
    }

    private static List<(long pedidoId, long shopifyOrderId, string tracking, string transportadora)> ObterExpedicoesPendentes(string cs, long empresaId)
    {
        var lista = new List<(long, long, string, string)>();
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT p.codigo, p.shopify_order_id, 
                COALESCE(p.tracking_code, ''), COALESCE(p.transportadora, '')
                FROM pedidos p 
                WHERE p.empresa_id=@e AND p.shopify_order_id > 0 
                AND p.status = 'E' AND COALESCE(p.shopify_fulfillment_enviado, false) = false";
            cmd.Parameters.AddWithValue("@e", empresaId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                lista.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3)));
            }
        }
        catch { }
        return lista;
    }

    private static void MarcarFulfillmentEnviado(string cs, long pedidoId)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE pedidos SET shopify_fulfillment_enviado = true WHERE codigo = @c";
            cmd.Parameters.AddWithValue("@c", pedidoId);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }
}
