using Npgsql;

namespace Axis.Conector;

/// <summary>
/// Worker que importa pedidos da Shopify para o Axis como Pedidos de Venda.
/// </summary>
public class ShopifyOrderWorker(ILogger<ShopifyOrderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ShopifyOrderWorker iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cs = ConfigHelper.ObterConnectionString();
                if (string.IsNullOrEmpty(cs) || !ConfigHelper.ObterParametroBool("SHOPIFY_ATIVO")
                    || !ConfigHelper.ObterParametroBool("SHOPIFY_SYNC_PEDIDOS"))
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var loja = ConfigHelper.ObterParametro("SHOPIFY_LOJA");
                var token = ConfigHelper.ObterParametro("SHOPIFY_ACCESS_TOKEN");
                var empresaId = ConfigHelper.ObterParametroLong("SHOPIFY_EMPRESA_ID");
                var dbLogger = new IntegrationLogger(cs, empresaId);

                if (string.IsNullOrEmpty(loja) || string.IsNullOrEmpty(token))
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                using var shopify = new ShopifyApiClient(loja, token);

                // Buscar última sincronização
                var ultimaSync = ObterUltimaSync(cs, empresaId, "PEDIDOS");
                var pedidos = await shopify.ListarPedidosAsync("any", ultimaSync);

                foreach (var pedido in pedidos)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    if (PedidoJaImportado(cs, empresaId, pedido.Id)) continue;

                    ImportarPedido(cs, empresaId, pedido, dbLogger);
                }

                if (pedidos.Count > 0)
                {
                    AtualizarUltimaSync(cs, empresaId, "PEDIDOS");
                    dbLogger.Info("PEDIDOS", $"{pedidos.Count} pedido(s) da Shopify processados.");
                }

                ConfigHelper.LimparCache();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no ShopifyOrderWorker");
            }

            var intervalo = ConfigHelper.ObterParametroDouble("SHOPIFY_INTERVALO_SYNC", 5);
            await Task.Delay(TimeSpan.FromMinutes(intervalo), stoppingToken);
        }
    }

    private static bool PedidoJaImportado(string cs, long empresaId, long shopifyOrderId)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pedidos WHERE empresa_id=@e AND shopify_order_id=@s";
        cmd.Parameters.AddWithValue("@e", empresaId);
        cmd.Parameters.AddWithValue("@s", shopifyOrderId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static void ImportarPedido(string cs, long empresaId, ShopifyOrder pedido, IntegrationLogger dbLogger)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var tran = conn.BeginTransaction();

            // Buscar ou criar cliente
            long pessoaId = 0;
            if (pedido.Customer != null)
            {
                using var cmdP = conn.CreateCommand();
                cmdP.Transaction = tran;
                cmdP.CommandText = "SELECT codigo FROM pessoas WHERE shopify_customer_id=@s AND empresa_id=@e LIMIT 1";
                cmdP.Parameters.AddWithValue("@s", pedido.Customer.Id);
                cmdP.Parameters.AddWithValue("@e", empresaId);
                var result = cmdP.ExecuteScalar();
                if (result != null) pessoaId = Convert.ToInt64(result);
            }

            // Inserir pedido
            using var cmdPed = conn.CreateCommand();
            cmdPed.Transaction = tran;
            cmdPed.CommandText = @"INSERT INTO pedidos 
                (empresa_id, pessoa_id, data_pedido, valor_total, status, shopify_order_id, observacoes)
                VALUES (@e, @p, @d, @v, 'C', @s, @obs) RETURNING codigo";
            cmdPed.Parameters.AddWithValue("@e", empresaId);
            cmdPed.Parameters.AddWithValue("@p", pessoaId > 0 ? pessoaId : DBNull.Value);
            cmdPed.Parameters.AddWithValue("@d", pedido.CreatedAt);
            cmdPed.Parameters.AddWithValue("@v", pedido.TotalPrice);
            cmdPed.Parameters.AddWithValue("@s", pedido.Id);
            cmdPed.Parameters.AddWithValue("@obs", $"Shopify #{pedido.Name}");
            var pedidoId = Convert.ToInt64(cmdPed.ExecuteScalar());

            // Inserir itens
            if (pedido.LineItems != null)
            {
                foreach (var item in pedido.LineItems)
                {
                    using var cmdItem = conn.CreateCommand();
                    cmdItem.Transaction = tran;
                    cmdItem.CommandText = @"INSERT INTO pedidos_itens 
                        (pedido_id, variacao_id, quantidade, valor_unitario, valor_total)
                        VALUES (@ped, (SELECT codigo FROM variacoes WHERE sku=@sku LIMIT 1), @qtd, @vu, @vt)";
                    cmdItem.Parameters.AddWithValue("@ped", pedidoId);
                    cmdItem.Parameters.AddWithValue("@sku", item.Sku ?? "");
                    cmdItem.Parameters.AddWithValue("@qtd", (decimal)item.Quantity);
                    cmdItem.Parameters.AddWithValue("@vu", item.Price);
                    cmdItem.Parameters.AddWithValue("@vt", item.Price * item.Quantity);
                    cmdItem.ExecuteNonQuery();
                }
            }

            tran.Commit();
            dbLogger.Info("PEDIDOS", $"Pedido Shopify #{pedido.Name} importado como pedido #{pedidoId}");

            // Criar sugestão de compra se dropshipping estiver ativo
            if (ConfigHelper.ObterParametroBool("SHOPIFY_DROPSHIPPING_ATIVO"))
            {
                CriarSugestaoCompraDropshipping(cs, empresaId, pedidoId, pedido.Id, dbLogger);
            }
        }
        catch (Exception ex)
        {
            dbLogger.Error("PEDIDOS", $"Erro ao importar pedido Shopify #{pedido.Name}: {ex.Message}", ex.ToString());
        }
    }

    private static void CriarSugestaoCompraDropshipping(string cs, long empresaId, long pedidoId, long shopifyOrderId, IntegrationLogger dbLogger)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();

            // Buscar itens do pedido com seus fornecedores
            var itensPorFornecedor = new Dictionary<long, List<(long variacaoId, decimal quantidade, decimal precoEstimado)>>();
            var fornecedorPadrao = ConfigHelper.ObterParametroLong("SHOPIFY_DROPSHIPPING_FORNECEDOR_PADRAO_ID");
            var tabelaPrecoId = ConfigHelper.ObterParametroLong("SHOPIFY_DROPSHIPPING_TABELA_PRECO_ID");

            using var cmdItens = conn.CreateCommand();
            cmdItens.CommandText = @"
                SELECT pi.variacao_id, pi.quantidade, 
                    COALESCE(fp.fornecedor_id, @fornPadrao) AS fornecedor_id,
                    COALESCE(fp.ultimo_preco, 0) AS preco_estimado
                FROM pedidos_itens pi
                LEFT JOIN fornecedores_produtos fp ON fp.variacao_id = pi.variacao_id AND fp.ativo = TRUE
                WHERE pi.pedido_id = @pedId AND pi.variacao_id > 0
                ORDER BY COALESCE(fp.fornecedor_id, @fornPadrao)";
            cmdItens.Parameters.AddWithValue("@pedId", pedidoId);
            cmdItens.Parameters.AddWithValue("@fornPadrao", fornecedorPadrao > 0 ? fornecedorPadrao : (object)DBNull.Value);

            using var r = cmdItens.ExecuteReader();
            while (r.Read())
            {
                var variacaoId = r.GetInt64(0);
                var quantidade = r.GetDecimal(1);
                var fornecedorId = r.IsDBNull(2) ? 0L : r.GetInt64(2);
                var precoEstimado = r.GetDecimal(3);

                if (fornecedorId == 0)
                {
                    dbLogger.Warn("DROPSHIPPING", $"Variação {variacaoId} sem fornecedor - pulando");
                    continue;
                }

                if (!itensPorFornecedor.ContainsKey(fornecedorId))
                    itensPorFornecedor[fornecedorId] = new List<(long, decimal, decimal)>();

                itensPorFornecedor[fornecedorId].Add((variacaoId, quantidade, precoEstimado));
            }
            r.Close();

            if (itensPorFornecedor.Count == 0)
            {
                dbLogger.Info("DROPSHIPPING", $"Pedido #{pedidoId} sem itens com fornecedor - nenhuma sugestão criada");
                return;
            }

            // Criar uma sugestão de compra para cada fornecedor
            foreach (var (fornecedorId, itens) in itensPorFornecedor)
            {
                using var tran = conn.BeginTransaction();
                try
                {
                    // Inserir sugestão
                    using var cmdSug = conn.CreateCommand();
                    cmdSug.Transaction = tran;
                    cmdSug.CommandText = @"INSERT INTO sugestoes_compra 
                        (empresa_id, fornecedor_id, pedido_id, shopify_order_id, data_sugestao, status, origem, observacao)
                        VALUES (@emp, @forn, @ped, @shop, NOW(), 'P', 'DROPSHIPPING', @obs) RETURNING codigo";
                    cmdSug.Parameters.AddWithValue("@emp", empresaId);
                    cmdSug.Parameters.AddWithValue("@forn", fornecedorId);
                    cmdSug.Parameters.AddWithValue("@ped", pedidoId);
                    cmdSug.Parameters.AddWithValue("@shop", shopifyOrderId);
                    cmdSug.Parameters.AddWithValue("@obs", $"Sugestão automática - Pedido Shopify #{shopifyOrderId}");
                    var sugestaoId = Convert.ToInt64(cmdSug.ExecuteScalar());

                    // Inserir itens
                    foreach (var (variacaoId, quantidade, precoEstimado) in itens)
                    {
                        using var cmdItem = conn.CreateCommand();
                        cmdItem.Transaction = tran;
                        cmdItem.CommandText = @"INSERT INTO sugestoes_compra_itens 
                            (sugestao_id, variacao_id, quantidade, preco_estimado)
                            VALUES (@sug, @var, @qtd, @preco)";
                        cmdItem.Parameters.AddWithValue("@sug", sugestaoId);
                        cmdItem.Parameters.AddWithValue("@var", variacaoId);
                        cmdItem.Parameters.AddWithValue("@qtd", quantidade);
                        cmdItem.Parameters.AddWithValue("@preco", precoEstimado);
                        cmdItem.ExecuteNonQuery();
                    }

                    tran.Commit();
                    dbLogger.Info("DROPSHIPPING", $"Sugestão de compra #{sugestaoId} criada para fornecedor #{fornecedorId} ({itens.Count} item(ns))");
                }
                catch (Exception ex)
                {
                    tran.Rollback();
                    dbLogger.Error("DROPSHIPPING", $"Erro ao criar sugestão para fornecedor #{fornecedorId}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            dbLogger.Error("DROPSHIPPING", $"Erro geral ao criar sugestões dropshipping: {ex.Message}", ex.ToString());
        }
    }

    private static DateTime? ObterUltimaSync(string cs, long empresaId, string tipo)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(data_hora) FROM integracoes_log WHERE empresa_id=@e AND categoria=@t AND nivel='INFO'";
            cmd.Parameters.AddWithValue("@e", empresaId);
            cmd.Parameters.AddWithValue("@t", tipo);
            var result = cmd.ExecuteScalar();
            return result is DateTime dt ? dt : null;
        }
        catch { return null; }
    }

    private static void AtualizarUltimaSync(string cs, long empresaId, string tipo)
    {
        // A gravação do log pelo IntegrationLogger já serve como marcador de última sync
    }
}
