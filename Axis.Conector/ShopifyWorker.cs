using Npgsql;

namespace Axis.Conector;

/// <summary>
/// Worker Service que sincroniza Produtos, Estoque e Preços com a Shopify.
/// Roda como Windows Service.
/// </summary>
public class ShopifyWorker(ILogger<ShopifyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Axis.Conector Shopify Worker iniciado.");
        try
        {
            var csIni = ConfigHelper.ObterConnectionString();
            var empIni = ConfigHelper.ObterParametroLong("SHOPIFY_EMPRESA_ID");
            if (!string.IsNullOrEmpty(csIni))
                new IntegrationLogger(csIni, empIni).Info("SERVICO", "Serviço Axis.Conector iniciado.");
        }
        catch { }

        // Aguardar um pouco para garantir que tudo está pronto
        await Task.Delay(3000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            IntegrationLogger? dbLogger = null;
            try
            {
                var cs = ConfigHelper.ObterConnectionString();
                if (string.IsNullOrEmpty(cs))
                {
                    logger.LogWarning("Connection string não encontrada. Aguardando...");
                    await Task.Delay(30000, stoppingToken);
                    continue;
                }

                // Criar logger de banco o mais cedo possível
                var empresaId = ConfigHelper.ObterParametroLong("SHOPIFY_EMPRESA_ID");
                dbLogger = new IntegrationLogger(cs, empresaId);

                if (!ConfigHelper.ObterParametroBool("SHOPIFY_ATIVO"))
                {
                    dbLogger.Info("SERVICO", "Integração Shopify desativada. Aguardando ativação...");
                    logger.LogInformation("Integração Shopify desativada. Aguardando...");
                    await Task.Delay(60000, stoppingToken);
                    continue;
                }

                var loja = ConfigHelper.ObterParametro("SHOPIFY_LOJA");
                var token = ConfigHelper.ObterParametro("SHOPIFY_ACCESS_TOKEN");
                if (string.IsNullOrEmpty(loja) || string.IsNullOrEmpty(token))
                {
                    dbLogger.Warn("CONFIG", "Loja ou Token Shopify não configurados. Configure em Parâmetros > Shopify.");
                    logger.LogWarning("Shopify: Loja ou Token não configurados.");
                    await Task.Delay(60000, stoppingToken);
                    continue;
                }

                var depositoId = ConfigHelper.ObterParametroLong("SHOPIFY_DEPOSITO_ID");
                var tabelaPrecoId = ConfigHelper.ObterParametroLong("SHOPIFY_TABELA_PRECO_ID");
                var intervaloMin = ConfigHelper.ObterParametroDouble("SHOPIFY_INTERVALO_SYNC", 5);
                if (intervaloMin < 0.1) intervaloMin = 0.1;

                using var shopify = new ShopifyApiClient(loja, token);

                // Obter location padrão da Shopify
                long locationId = 0;
                try
                {
                    var locations = await shopify.ListarLocationsAsync();
                    locationId = locations.FirstOrDefault(l => l.Active)?.Id ?? 0;
                    dbLogger.Info("CONEXAO", $"Conectado à Shopify ({loja}). Location: {locationId}");
                }
                catch (Exception ex)
                {
                    dbLogger.Error("CONEXAO", "Erro ao conectar na Shopify: " + ex.Message, ex.ToString());
                    logger.LogError(ex, "Erro ao conectar na Shopify");
                    await Task.Delay(60000, stoppingToken);
                    continue;
                }

                dbLogger.Info("SYNC", $"Sincronização iniciada (Empresa: {empresaId}, Depósito: {depositoId})");

                // Sincronizar Produtos
                if (ConfigHelper.ObterParametroBool("SHOPIFY_SYNC_PRODUTOS"))
                {
                    await SincronizarProdutosAsync(cs, shopify, dbLogger, empresaId, stoppingToken);
                }

                // Sincronizar Preços
                if (ConfigHelper.ObterParametroBool("SHOPIFY_SYNC_PRECOS") && tabelaPrecoId > 0)
                {
                    await SincronizarPrecosAsync(cs, shopify, dbLogger, empresaId, tabelaPrecoId, stoppingToken);
                }

                // Sincronizar Estoque
                if (ConfigHelper.ObterParametroBool("SHOPIFY_SYNC_ESTOQUE") && locationId > 0)
                {
                    await SincronizarEstoqueAsync(cs, shopify, dbLogger, empresaId, depositoId, locationId, stoppingToken);
                }

                dbLogger.Info("SYNC", $"Sincronização concluída. Próxima em {intervaloMin} min.");
                logger.LogInformation("Sincronização Shopify concluída. Próxima em {min} min.", intervaloMin);

                // Limpar cache de parâmetros para recarregar na próxima iteração
                ConfigHelper.LimparCache();

                await Task.Delay(TimeSpan.FromMinutes(intervaloMin), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no ciclo de sincronização Shopify");
                try
                {
                    if (dbLogger != null)
                    {
                        dbLogger.Error("SYNC", "Erro geral no ciclo: " + ex.Message, ex.ToString());
                    }
                    else
                    {
                        var cs = ConfigHelper.ObterConnectionString();
                        var empId = ConfigHelper.ObterParametroLong("SHOPIFY_EMPRESA_ID");
                        new IntegrationLogger(cs, empId).Error("SYNC", "Erro geral no ciclo: " + ex.Message, ex.ToString());
                    }
                }
                catch { }
                await Task.Delay(60000, stoppingToken);
            }
        }

        logger.LogInformation("Axis.Conector Shopify Worker parado.");
        // Só registra parada no log da integração se o cancelamento foi solicitado
        // (serviço sendo realmente desligado). Caso contrário, o loop ainda está vivo
        // e a mensagem confundiria o usuário (apareceria como "parado" com Status=Running).
        if (stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cs = ConfigHelper.ObterConnectionString();
                var empId = ConfigHelper.ObterParametroLong("SHOPIFY_EMPRESA_ID");
                new IntegrationLogger(cs, empId).Info("SERVICO", "Serviço Axis.Conector parado.");
            }
            catch { }
        }
    }

    #region Sincronizar Produtos

    private async Task SincronizarProdutosAsync(string cs, ShopifyApiClient shopify,
        IntegrationLogger dbLogger, long empresaId, CancellationToken ct)
    {
        try
        {
            // Garantir que todos os produtos/variações ativos da empresa
            // tenham um registro em integracoes_produtos para serem enviados.
            var enfileirados = EnfileirarProdutosPendentes(cs, empresaId);
            if (enfileirados > 0)
                dbLogger.Info("PRODUTOS", $"{enfileirados} produto(s)/variação(ões) enfileirado(s) para envio.");

            // Buscar mapeamentos pendentes (sem plataforma_produto_id)
            var pendentes = ObterMapeamentosPendentes(cs, empresaId);
            if (pendentes.Count == 0) return;

            dbLogger.Info("PRODUTOS", $"Sincronizando {pendentes.Count} produto(s)...");

            // Agrupar por produto_id (cada produto pode ter várias variações)
            var grupos = pendentes.GroupBy(p => p.ProdutoId);

            foreach (var grupo in grupos)
            {
                if (ct.IsCancellationRequested) break;

                var produtoId = grupo.Key;
                var variacoes = grupo.ToList();

                try
                {
                    var infoProduto = ObterInfoProduto(cs, produtoId);
                    if (infoProduto == null) continue;

                    // Verificar se já existe na Shopify (alguma variação já tem ID)
                    var jaExiste = variacoes.FirstOrDefault(v => !string.IsNullOrEmpty(v.PlataformaProdutoId) && v.PlataformaProdutoId != "0");

                    if (jaExiste != null)
                    {
                        // Produto já existe — atualizar variantes pendentes
                        var shopifyProdId = long.Parse(jaExiste.PlataformaProdutoId!);
                        foreach (var v in variacoes.Where(x => string.IsNullOrEmpty(x.PlataformaProdutoId) || x.PlataformaProdutoId == "0"))
                        {
                            AtualizarMapeamento(cs, v.Codigo, jaExiste.PlataformaProdutoId!, "", "");
                        }

                        // Atualizar tags (classificações) do produto já existente
                        try
                        {
                            var tagsExist = ObterTagsProduto(cs, produtoId);
                            await shopify.AtualizarProdutoAsync(shopifyProdId, new ShopifyProduct { Tags = tagsExist });
                            dbLogger.Info("PRODUTOS", $"Tags atualizadas no produto Shopify #{shopifyProdId}: {tagsExist}");
                        }
                        catch (Exception exTag)
                        {
                            dbLogger.Warn("PRODUTOS", $"Falha ao atualizar tags do produto #{shopifyProdId}: {exTag.Message}");
                        }
                    }
                    else
                    {
                        // Coletar info de cada varição (com cor_id para mapear fotos depois)
                        var infoVars = variacoes
                            .Select(v => new { Mapeamento = v, Info = ObterInfoVariacao(cs, v.VariacaoId) })
                            .ToList();

                        // Tags vindas das classificações do produto
                        var tags = ObterTagsProduto(cs, produtoId);

                        // Criar produto novo na Shopify
                        var shopifyProduct = new ShopifyProduct
                        {
                            Title = infoProduto.Nome,
                            BodyHtml = infoProduto.Descricao,
                            ProductType = infoProduto.Tipo,
                            Vendor = "",
                            Status = "active",
                            Tags = tags,
                            Variants = infoVars.Select(x => new ShopifyVariant
                            {
                                Sku = x.Info?.Sku ?? "",
                                Barcode = x.Info?.Ean ?? "",
                                Price = (x.Info?.PrecoVenda ?? 0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                                Option1 = x.Info?.Cor ?? "Única",
                                Option2 = string.IsNullOrEmpty(x.Info?.Tamanho) ? null : x.Info.Tamanho,
                                InventoryManagement = "shopify",
                                RequiresShipping = true,
                                Weight = infoProduto.Peso,
                                WeightUnit = "kg"
                            }).ToList()
                        };

                        var criado = await shopify.CriarProdutoAsync(shopifyProduct);
                        if (criado?.Id != null && criado.Variants != null)
                        {
                            // Mapear variantes criadas
                            // shopifyVariantesPorCor: cor_id -> lista de variant_ids criados
                            var shopifyVariantesPorCor = new Dictionary<long, List<long>>();
                            for (int i = 0; i < infoVars.Count && i < criado.Variants.Count; i++)
                            {
                                var sv = criado.Variants[i];
                                var iv = infoVars[i];
                                AtualizarMapeamento(cs, iv.Mapeamento.Codigo,
                                    criado.Id.ToString()!,
                                    sv.Id.ToString(),
                                    sv.InventoryItemId.ToString());

                                var corId = iv.Info?.CorId ?? 0;
                                if (!shopifyVariantesPorCor.TryGetValue(corId, out var lista))
                                {
                                    lista = new List<long>();
                                    shopifyVariantesPorCor[corId] = lista;
                                }
                                lista.Add(sv.Id);
                            }

                            // Enviar imagens: cada foto da cor vinculada às variantes daquela cor
                            await EnviarImagensProduto(cs, shopify, produtoId, criado.Id, shopifyVariantesPorCor);

                            dbLogger.Info("PRODUTOS", $"Produto criado: {infoProduto.Nome} (Shopify #{criado.Id})");
                        }
                    }

                    // Rate limiting da Shopify (2 req/segundo no plano básico)
                    await Task.Delay(600, ct);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    dbLogger.Warn("PRODUTOS", "Rate limit atingido, aguardando...");
                    await Task.Delay(5000, ct);
                }
                catch (Exception ex)
                {
                    var primeiro = variacoes.First();
                    MarcarErro(cs, primeiro.Codigo, ex.Message);
                    dbLogger.Error("PRODUTOS", $"Erro ao sincronizar produto #{produtoId}", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            dbLogger.Error("PRODUTOS", "Erro geral na sincronização de produtos", ex.Message);
        }
    }

    #endregion

    #region Sincronizar Preços

    private async Task SincronizarPrecosAsync(string cs, ShopifyApiClient shopify,
        IntegrationLogger dbLogger, long empresaId, long tabelaPrecoId, CancellationToken ct)
    {
        try
        {
            var mapeados = ObterMapeamentosSincronizados(cs, empresaId);
            if (mapeados.Count == 0) return;

            dbLogger.Info("PRECOS", $"Atualizando preços de {mapeados.Count} variante(s)...");
            var count = 0;

            foreach (var m in mapeados)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(m.PlataformaVarianteId) || m.PlataformaVarianteId == "0") continue;

                try
                {
                    var preco = ObterPrecoTabela(cs, tabelaPrecoId, m.VariacaoId);
                    if (preco <= 0) continue;

                    var variantId = long.Parse(m.PlataformaVarianteId);
                    await shopify.AtualizarVarianteAsync(variantId, new ShopifyVariant
                    {
                        Id = variantId,
                        Price = preco.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    });
                    count++;
                    await Task.Delay(600, ct);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    dbLogger.Warn("PRECOS", "Rate limit atingido, aguardando...");
                    await Task.Delay(5000, ct);
                }
                catch (Exception ex)
                {
                    dbLogger.Error("PRECOS", $"Erro ao atualizar preço variante #{m.VariacaoId}", ex.Message);
                }
            }

            dbLogger.Info("PRECOS", $"{count} preço(s) atualizado(s).");
        }
        catch (Exception ex)
        {
            dbLogger.Error("PRECOS", "Erro geral na sincronização de preços", ex.Message);
        }
    }

    #endregion

    #region Sincronizar Estoque

    private async Task SincronizarEstoqueAsync(string cs, ShopifyApiClient shopify,
        IntegrationLogger dbLogger, long empresaId, long depositoId, long locationId, CancellationToken ct)
    {
        try
        {
            var mapeados = ObterMapeamentosSincronizados(cs, empresaId);
            if (mapeados.Count == 0) return;

            dbLogger.Info("ESTOQUE", $"Atualizando estoque de {mapeados.Count} variante(s)...");
            var count = 0;

            foreach (var m in mapeados)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(m.PlataformaInventoryItemId) || m.PlataformaInventoryItemId == "0") continue;

                try
                {
                    var qtd = ObterQuantidadeEstoque(cs, empresaId, m.VariacaoId, depositoId);
                    var inventoryItemId = long.Parse(m.PlataformaInventoryItemId);

                    await shopify.AjustarEstoqueAsync(inventoryItemId, locationId, (int)qtd);
                    count++;
                    await Task.Delay(600, ct);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    dbLogger.Warn("ESTOQUE", "Rate limit atingido, aguardando...");
                    await Task.Delay(5000, ct);
                }
                catch (Exception ex)
                {
                    dbLogger.Error("ESTOQUE", $"Erro ao atualizar estoque variação #{m.VariacaoId}", ex.Message);
                }
            }

            dbLogger.Info("ESTOQUE", $"{count} estoque(s) atualizado(s).");
        }
        catch (Exception ex)
        {
            dbLogger.Error("ESTOQUE", "Erro geral na sincronização de estoque", ex.Message);
        }
    }

    #endregion

    #region Consultas ao Banco

    /// <summary>
    /// Insere em integracoes_produtos um registro pendente para cada
    /// produto/variação ativos que ainda não foram mapeados ao Shopify.
    /// Retorna a quantidade de novas linhas criadas.
    /// </summary>
    private static int EnfileirarProdutosPendentes(string cs, long empresaId)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();

        // Produtos COM variações
        var sqlComVar = @"
            INSERT INTO integracoes_produtos
                (empresa_id, plataforma, produto_id, variacao_id, sincronizado, erro)
            SELECT COALESCE(p.empresa_id, 0), 'SHOPIFY', p.codigo, v.codigo, FALSE, ''
            FROM produtos p
            INNER JOIN produtos_variacoes v ON v.produto_id = p.codigo AND v.ativo = TRUE
            WHERE p.ativo = TRUE
              AND (@e = 0 OR p.empresa_id = @e)
            ON CONFLICT (plataforma, produto_id, variacao_id) DO NOTHING";

        // Produtos SEM variações (variacao_id = 0)
        var sqlSemVar = @"
            INSERT INTO integracoes_produtos
                (empresa_id, plataforma, produto_id, variacao_id, sincronizado, erro)
            SELECT COALESCE(p.empresa_id, 0), 'SHOPIFY', p.codigo, 0, FALSE, ''
            FROM produtos p
            WHERE p.ativo = TRUE
              AND (@e = 0 OR p.empresa_id = @e)
              AND NOT EXISTS (SELECT 1 FROM produtos_variacoes v WHERE v.produto_id = p.codigo)
            ON CONFLICT (plataforma, produto_id, variacao_id) DO NOTHING";

        var total = 0;
        using (var cmd = new NpgsqlCommand(sqlComVar, conn))
        {
            cmd.Parameters.AddWithValue("@e", empresaId);
            total += cmd.ExecuteNonQuery();
        }
        using (var cmd = new NpgsqlCommand(sqlSemVar, conn))
        {
            cmd.Parameters.AddWithValue("@e", empresaId);
            total += cmd.ExecuteNonQuery();
        }
        return total;
    }

    private static List<MapeamentoInfo> ObterMapeamentosPendentes(string cs, long empresaId)
    {
        var lista = new List<MapeamentoInfo>();
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        var sql = "SELECT codigo, produto_id, variacao_id, plataforma_produto_id, plataforma_variante_id, plataforma_inventory_item_id " +
                  "FROM integracoes_produtos WHERE plataforma = 'SHOPIFY' AND (plataforma_produto_id = '' OR plataforma_produto_id IS NULL)";
        if (empresaId > 0) sql += " AND empresa_id = " + empresaId;
        sql += " ORDER BY produto_id";
        using var cmd = new NpgsqlCommand(sql, conn);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            lista.Add(new MapeamentoInfo
            {
                Codigo = r.GetInt64(0),
                ProdutoId = r.GetInt64(1),
                VariacaoId = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                PlataformaProdutoId = r.IsDBNull(3) ? "" : r.GetString(3),
                PlataformaVarianteId = r.IsDBNull(4) ? "" : r.GetString(4),
                PlataformaInventoryItemId = r.IsDBNull(5) ? "" : r.GetString(5)
            });
        }
        return lista;
    }

    private static List<MapeamentoInfo> ObterMapeamentosSincronizados(string cs, long empresaId)
    {
        var lista = new List<MapeamentoInfo>();
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        var sql = "SELECT codigo, produto_id, variacao_id, plataforma_produto_id, plataforma_variante_id, plataforma_inventory_item_id " +
                  "FROM integracoes_produtos WHERE plataforma = 'SHOPIFY' AND sincronizado = TRUE AND plataforma_produto_id <> ''";
        if (empresaId > 0) sql += " AND empresa_id = " + empresaId;
        using var cmd = new NpgsqlCommand(sql, conn);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            lista.Add(new MapeamentoInfo
            {
                Codigo = r.GetInt64(0),
                ProdutoId = r.GetInt64(1),
                VariacaoId = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                PlataformaProdutoId = r.IsDBNull(3) ? "" : r.GetString(3),
                PlataformaVarianteId = r.IsDBNull(4) ? "" : r.GetString(4),
                PlataformaInventoryItemId = r.IsDBNull(5) ? "" : r.GetString(5)
            });
        }
        return lista;
    }

    private static ProdutoInfo? ObterInfoProduto(string cs, long produtoId)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT nome, descricao, descricao_detalhada, peso_bruto FROM produtos WHERE codigo = @id", conn);
        cmd.Parameters.AddWithValue("@id", produtoId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ProdutoInfo
        {
            Nome = r["nome"]?.ToString() ?? "",
            Descricao = r["descricao_detalhada"]?.ToString() ?? r["descricao"]?.ToString() ?? "",
            Tipo = "",
            Peso = r.IsDBNull(r.GetOrdinal("peso_bruto")) ? 0 : r.GetDecimal(r.GetOrdinal("peso_bruto"))
        };
    }

    private static VariacaoInfo? ObterInfoVariacao(string cs, long variacaoId)
    {
        if (variacaoId == 0) return null;
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT pv.sku, pv.ean, pv.preco_venda, pv.tamanho, pv.cor_id, c.nome AS cor " +
            "FROM produtos_variacoes pv LEFT JOIN cores c ON c.codigo = pv.cor_id WHERE pv.codigo = @id", conn);
        cmd.Parameters.AddWithValue("@id", variacaoId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new VariacaoInfo
        {
            Sku = r["sku"]?.ToString() ?? "",
            Ean = r["ean"]?.ToString() ?? "",
            PrecoVenda = r.IsDBNull(r.GetOrdinal("preco_venda")) ? 0 : r.GetDecimal(r.GetOrdinal("preco_venda")),
            Tamanho = r["tamanho"]?.ToString() ?? "",
            Cor = r["cor"]?.ToString() ?? "",
            CorId = r.IsDBNull(r.GetOrdinal("cor_id")) ? 0 : r.GetInt64(r.GetOrdinal("cor_id"))
        };
    }

    private static decimal ObterPrecoTabela(string cs, long tabelaId, long variacaoId)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT preco_venda FROM tabelas_precos_itens WHERE tabela_id = @tid AND variacao_id = @vid", conn);
        cmd.Parameters.AddWithValue("@tid", tabelaId);
        cmd.Parameters.AddWithValue("@vid", variacaoId);
        var result = cmd.ExecuteScalar();
        return result is decimal d ? d : 0;
    }

    private static decimal ObterQuantidadeEstoque(string cs, long empresaId, long variacaoId, long depositoId)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        // A tabela estoque não tem empresa_id; quando informado, filtramos
        // pela empresa do produto através da variação.
        var sql = "SELECT COALESCE(SUM(e.quantidade), 0) - COALESCE(SUM(e.reservado), 0) " +
                  "FROM estoque e WHERE e.variacao_id = @vid";
        if (depositoId > 0) sql += " AND e.deposito_id = @did";
        if (empresaId > 0)
        {
            sql += " AND EXISTS (SELECT 1 FROM produtos_variacoes v " +
                   "INNER JOIN produtos p ON p.codigo = v.produto_id " +
                   "WHERE v.codigo = e.variacao_id AND p.empresa_id = @eid)";
        }
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@vid", variacaoId);
        if (depositoId > 0) cmd.Parameters.AddWithValue("@did", depositoId);
        if (empresaId > 0) cmd.Parameters.AddWithValue("@eid", empresaId);
        var result = cmd.ExecuteScalar();
        return result != null && result != DBNull.Value ? Convert.ToDecimal(result) : 0;
    }

    private static void AtualizarMapeamento(string cs, long codigo, string prodId, string varId, string invItemId)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "UPDATE integracoes_produtos SET plataforma_produto_id=@p, plataforma_variante_id=@v, plataforma_inventory_item_id=@i, sincronizado=TRUE, data_sync=CURRENT_TIMESTAMP, erro='' WHERE codigo=@id", conn);
        cmd.Parameters.AddWithValue("@id", codigo);
        cmd.Parameters.AddWithValue("@p", prodId ?? "");
        cmd.Parameters.AddWithValue("@v", varId ?? "");
        cmd.Parameters.AddWithValue("@i", invItemId ?? "");
        cmd.ExecuteNonQuery();
    }

    private static void MarcarErro(string cs, long codigo, string erro)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "UPDATE integracoes_produtos SET sincronizado=FALSE, erro=@err, data_sync=CURRENT_TIMESTAMP WHERE codigo=@id", conn);
        cmd.Parameters.AddWithValue("@id", codigo);
        cmd.Parameters.AddWithValue("@err", erro ?? "");
        cmd.ExecuteNonQuery();
    }

    private static async Task EnviarImagensProduto(string cs, ShopifyApiClient shopify, long produtoId,
        long shopifyProductId, Dictionary<long, List<long>> variantesPorCor)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = new NpgsqlCommand(
                "SELECT cor_id, foto, principal FROM produtos_fotos " +
                "WHERE produto_id = @id AND foto IS NOT NULL " +
                "ORDER BY principal DESC, ordem ASC, codigo ASC", conn);
            cmd.Parameters.AddWithValue("@id", produtoId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var corId = r.IsDBNull(r.GetOrdinal("cor_id")) ? 0L : r.GetInt64(r.GetOrdinal("cor_id"));
                var bytes = r["foto"] as byte[];
                if (bytes == null || bytes.Length == 0) continue;

                List<long>? variantIds = null;
                if (corId > 0 && variantesPorCor.TryGetValue(corId, out var ids))
                    variantIds = ids;

                try
                {
                    await shopify.AdicionarImagemAsync(shopifyProductId, bytes, variantIds);
                    await Task.Delay(400);
                }
                catch { /* segue tentando as outras */ }
            }
        }
        catch { }
    }

    /// <summary>
    /// Monta a string de tags da Shopify a partir das classificações do produto.
    /// </summary>
    private static string ObterTagsProduto(string cs, long produtoId)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = new NpgsqlCommand(
                "SELECT cv.nome FROM produtos_classificacoes pc " +
                "INNER JOIN classificacoes_valores cv ON cv.codigo = pc.classificacao_valor_id " +
                "WHERE pc.produto_id = @id AND COALESCE(cv.ativo, TRUE) = TRUE " +
                "ORDER BY cv.nome", conn);
            cmd.Parameters.AddWithValue("@id", produtoId);
            using var r = cmd.ExecuteReader();
            var nomes = new List<string>();
            while (r.Read())
            {
                var nome = r[0]?.ToString();
                if (!string.IsNullOrWhiteSpace(nome)) nomes.Add(nome.Trim());
            }
            return string.Join(", ", nomes);
        }
        catch { return ""; }
    }

    private static async Task EnviarImagemProduto(string cs, ShopifyApiClient shopify, long produtoId, long shopifyProductId)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = new NpgsqlCommand(
                "SELECT foto FROM produtos_fotos WHERE produto_id = @id AND principal = TRUE LIMIT 1", conn);
            cmd.Parameters.AddWithValue("@id", produtoId);
            var result = cmd.ExecuteScalar();
            if (result is byte[] bytes && bytes.Length > 0)
            {
                await shopify.AdicionarImagemAsync(shopifyProductId, bytes);
            }
        }
        catch { }
    }

    #endregion

    #region DTOs internos

    private class MapeamentoInfo
    {
        public long Codigo { get; set; }
        public long ProdutoId { get; set; }
        public long VariacaoId { get; set; }
        public string? PlataformaProdutoId { get; set; }
        public string? PlataformaVarianteId { get; set; }
        public string? PlataformaInventoryItemId { get; set; }
    }

    private class ProdutoInfo
    {
        public string Nome { get; set; } = "";
        public string Descricao { get; set; } = "";
        public string Tipo { get; set; } = "";
        public decimal Peso { get; set; }
    }

    private class VariacaoInfo
    {
        public string Sku { get; set; } = "";
        public string Ean { get; set; } = "";
        public decimal PrecoVenda { get; set; }
        public string Tamanho { get; set; } = "";
        public string Cor { get; set; } = "";
        public long CorId { get; set; }
    }

    #endregion
}


