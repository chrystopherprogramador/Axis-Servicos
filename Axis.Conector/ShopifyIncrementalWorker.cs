using Npgsql;

namespace Axis.Conector;

/// <summary>
/// Worker incremental: sincroniza apenas produtos alterados recentemente no Axis.
/// Roda com frequência menor que o ShopifyWorker principal, verificando
/// a coluna data_alteracao para enviar atualizações parciais.
/// </summary>
public class ShopifyIncrementalWorker(ILogger<ShopifyIncrementalWorker> logger) : BackgroundService
{
    private bool _schemaOk = false;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ShopifyIncrementalWorker iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cs = ConfigHelper.ObterConnectionString();
                if (string.IsNullOrEmpty(cs) || !ConfigHelper.ObterParametroBool("SHOPIFY_ATIVO")
                    || !ConfigHelper.ObterParametroBool("SHOPIFY_SYNC_INCREMENTAL"))
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                if (!_schemaOk)
                {
                    try { GarantirSchema(cs); _schemaOk = true; }
                    catch (Exception exSchema)
                    {
                        logger.LogError(exSchema, "Falha ao garantir schema incremental.");
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                        continue;
                    }
                }

                var loja = ConfigHelper.ObterParametro("SHOPIFY_LOJA");
                var token = ConfigHelper.ObterParametro("SHOPIFY_ACCESS_TOKEN");
                var empresaId = ConfigHelper.ObterParametroLong("SHOPIFY_EMPRESA_ID");
                var tabelaPrecoId = ConfigHelper.ObterParametroLong("SHOPIFY_TABELA_PRECO_ID");
                var depositoId = ConfigHelper.ObterParametroLong("SHOPIFY_DEPOSITO_ID");

                if (string.IsNullOrEmpty(loja) || string.IsNullOrEmpty(token))
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                IntegrationLogger? dbLogger = null;
                try
                {
                    dbLogger = new IntegrationLogger(cs, empresaId);
                }
                catch { }

                using var shopify = new ShopifyApiClient(loja, token);

                // Buscar locations para estoque
                long locationId = 0;
                if (depositoId > 0)
                {
                    var locations = await shopify.ListarLocationsAsync();
                    locationId = locations.FirstOrDefault(l => l.Active)?.Id ?? locations.FirstOrDefault()?.Id ?? 0;
                }

                // Buscar variações alteradas nos últimos N minutos
                // Usamos uma janela maior que o intervalo do loop para não perder alterações.
                var intervalo = ConfigHelper.ObterParametroDouble("SHOPIFY_INTERVALO_INCREMENTAL", 0.5);
                var janela = Math.Max(intervalo * 3, 2);

                List<(long variacaoId, long shopifyVariantId, long inventoryItemId, long produtoId, long shopifyProductId)> alteradas;
                try
                {
                    alteradas = ObterVariacoesAlteradas(cs, empresaId, janela);
                }
                catch (Exception exQuery)
                {
                    dbLogger?.Error("INCREMENTAL", "Erro ao buscar variações alteradas", exQuery.Message);
                    logger.LogError(exQuery, "Erro ao buscar variações alteradas");
                    await Task.Delay(TimeSpan.FromMinutes(intervalo), stoppingToken);
                    continue;
                }

                if (alteradas.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMinutes(intervalo), stoppingToken);
                    continue;
                }

                dbLogger?.Info("INCREMENTAL", $"{alteradas.Count} variação(ões) alterada(s) detectada(s).");

                int sucesso = 0, falha = 0;
                var produtosAtualizados = new HashSet<long>();
                foreach (var (variacaoId, shopifyVariantId, inventoryItemId, produtoId, shopifyProductId) in alteradas)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    try
                    {
                        // Sincronizar produto inteiro (title, body_html, type, vendor, status, tags)
                        // — uma vez por produto neste ciclo
                        if (shopifyProductId > 0 && produtosAtualizados.Add(shopifyProductId))
                        {
                            try
                            {
                                var info = ObterInfoProduto(cs, produtoId);
                                var tags = ObterTagsProduto(cs, produtoId);
                                if (info != null)
                                {
                                    await shopify.AtualizarProdutoAsync(shopifyProductId, new ShopifyProduct
                                    {
                                        Title = info.Nome,
                                        BodyHtml = info.Descricao,
                                        ProductType = info.Tipo,
                                        Status = info.Ativo ? "active" : "draft",
                                        Tags = tags
                                    });
                                }
                            }
                            catch (Exception exProd)
                            {
                                dbLogger?.Warn("INCREMENTAL", $"Falha ao atualizar produto #{shopifyProductId}: {exProd.Message}");
                            }

                            // Sincronizar imagens se houve mudança em produtos_fotos
                            try
                            {
                                var hashAtual = ObterHashFotos(cs, produtoId);
                                var hashSalvo = ObterHashFotosSync(cs, produtoId);
                                if (!string.Equals(hashAtual, hashSalvo, StringComparison.Ordinal))
                                {
                                    await SincronizarImagensProduto(cs, shopify, produtoId, shopifyProductId);
                                    SalvarHashFotosSync(cs, produtoId, hashAtual);
                                    dbLogger?.Info("INCREMENTAL", $"Imagens do produto #{shopifyProductId} sincronizadas.");
                                }
                            }
                            catch (Exception exImg)
                            {
                                dbLogger?.Warn("INCREMENTAL", $"Falha ao sincronizar imagens do produto #{shopifyProductId}: {exImg.Message}");
                            }
                        }

                        // Sincronizar variação (sku, barcode, preço, peso)
                        // OBS: Option1/Option2 não são alteradas — Shopify gerencia opções em nível de produto.
                        if (shopifyVariantId > 0)
                        {
                            var infoVar = ObterInfoVariacao(cs, variacaoId);
                            decimal preco = infoVar?.PrecoVenda ?? 0;
                            if (tabelaPrecoId > 0)
                            {
                                var precoTab = ObterPrecoVariacao(cs, variacaoId, tabelaPrecoId);
                                if (precoTab > 0) preco = precoTab;
                            }
                            var pesoProd = ObterInfoProduto(cs, produtoId);
                            await shopify.AtualizarVarianteAsync(shopifyVariantId, new ShopifyVariant
                            {
                                Sku = string.IsNullOrEmpty(infoVar?.Sku) ? null : infoVar!.Sku,
                                Barcode = string.IsNullOrEmpty(infoVar?.Ean) ? null : infoVar!.Ean,
                                Price = preco > 0 ? preco.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : null,
                                Weight = pesoProd?.Peso > 0 ? pesoProd.Peso : null,
                                WeightUnit = pesoProd?.Peso > 0 ? "kg" : null
                            });
                        }

                        // Atualizar estoque
                        if (locationId > 0 && inventoryItemId > 0 && depositoId > 0)
                        {
                            var qtd = ObterEstoqueVariacao(cs, variacaoId, depositoId);
                            await shopify.AjustarEstoqueAsync(inventoryItemId, locationId, (int)qtd);
                        }

                        sucesso++;
                        await Task.Delay(400, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        falha++;
                        dbLogger?.Error("INCREMENTAL", $"Erro ao atualizar variação {variacaoId}: {ex.Message}");
                    }
                }

                dbLogger?.Info("INCREMENTAL", $"Incremental concluído: {sucesso} ok, {falha} erro(s).");

                ConfigHelper.LimparCache();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no ShopifyIncrementalWorker");
            }

            var intervaloLoop = ConfigHelper.ObterParametroDouble("SHOPIFY_INTERVALO_INCREMENTAL", 0.5);
            await Task.Delay(TimeSpan.FromMinutes(intervaloLoop), stoppingToken);
        }
    }

    private static void GarantirSchema(string cs)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        var stmts = new[]
        {
            "ALTER TABLE produtos_variacoes ADD COLUMN IF NOT EXISTS data_alteracao TIMESTAMP DEFAULT CURRENT_TIMESTAMP",
            "CREATE INDEX IF NOT EXISTS idx_pv_data_alt ON produtos_variacoes(data_alteracao)",
            "CREATE OR REPLACE FUNCTION trg_pv_set_data_alteracao() RETURNS TRIGGER AS $$ BEGIN NEW.data_alteracao = CURRENT_TIMESTAMP; RETURN NEW; END; $$ LANGUAGE plpgsql",
            "DROP TRIGGER IF EXISTS trg_pv_data_alteracao ON produtos_variacoes",
            "CREATE TRIGGER trg_pv_data_alteracao BEFORE UPDATE ON produtos_variacoes FOR EACH ROW EXECUTE FUNCTION trg_pv_set_data_alteracao()",
            "CREATE OR REPLACE FUNCTION trg_estoque_touch_variacao() RETURNS TRIGGER AS $$ BEGIN UPDATE produtos_variacoes SET data_alteracao = CURRENT_TIMESTAMP WHERE codigo = COALESCE(NEW.variacao_id, OLD.variacao_id); RETURN COALESCE(NEW, OLD); END; $$ LANGUAGE plpgsql",
            "DROP TRIGGER IF EXISTS trg_estoque_touch ON estoque",
            "CREATE TRIGGER trg_estoque_touch AFTER INSERT OR UPDATE OR DELETE ON estoque FOR EACH ROW EXECUTE FUNCTION trg_estoque_touch_variacao()",
            "CREATE OR REPLACE FUNCTION trg_preco_touch_variacao() RETURNS TRIGGER AS $$ BEGIN UPDATE produtos_variacoes SET data_alteracao = CURRENT_TIMESTAMP WHERE codigo = COALESCE(NEW.variacao_id, OLD.variacao_id); RETURN COALESCE(NEW, OLD); END; $$ LANGUAGE plpgsql",
            "DROP TRIGGER IF EXISTS trg_preco_touch ON tabelas_precos_itens",
            "CREATE TRIGGER trg_preco_touch AFTER INSERT OR UPDATE OR DELETE ON tabelas_precos_itens FOR EACH ROW EXECUTE FUNCTION trg_preco_touch_variacao()",
            // Mudanças em produtos -> tocar todas variações
            "CREATE OR REPLACE FUNCTION trg_produto_touch_variacoes() RETURNS TRIGGER AS $$ BEGIN UPDATE produtos_variacoes SET data_alteracao = CURRENT_TIMESTAMP WHERE produto_id = NEW.codigo; RETURN NEW; END; $$ LANGUAGE plpgsql",
            "DROP TRIGGER IF EXISTS trg_produto_touch ON produtos",
            "CREATE TRIGGER trg_produto_touch AFTER UPDATE ON produtos FOR EACH ROW EXECUTE FUNCTION trg_produto_touch_variacoes()",
            // Mudanças em classificações -> tocar variações do produto
            "CREATE OR REPLACE FUNCTION trg_pc_touch_variacoes() RETURNS TRIGGER AS $$ BEGIN UPDATE produtos_variacoes SET data_alteracao = CURRENT_TIMESTAMP WHERE produto_id = COALESCE(NEW.produto_id, OLD.produto_id); RETURN COALESCE(NEW, OLD); END; $$ LANGUAGE plpgsql",
            "DROP TRIGGER IF EXISTS trg_pc_touch ON produtos_classificacoes",
            "CREATE TRIGGER trg_pc_touch AFTER INSERT OR UPDATE OR DELETE ON produtos_classificacoes FOR EACH ROW EXECUTE FUNCTION trg_pc_touch_variacoes()",
            // Mudanças em fotos -> tocar variações do produto
            "CREATE OR REPLACE FUNCTION trg_pf_touch_variacoes() RETURNS TRIGGER AS $$ BEGIN UPDATE produtos_variacoes SET data_alteracao = CURRENT_TIMESTAMP WHERE produto_id = COALESCE(NEW.produto_id, OLD.produto_id); RETURN COALESCE(NEW, OLD); END; $$ LANGUAGE plpgsql",
            "DROP TRIGGER IF EXISTS trg_pf_touch ON produtos_fotos",
            "CREATE TRIGGER trg_pf_touch AFTER INSERT OR UPDATE OR DELETE ON produtos_fotos FOR EACH ROW EXECUTE FUNCTION trg_pf_touch_variacoes()",
            // Mudanças em códigos auxiliares -> tocar variações do produto
            "CREATE OR REPLACE FUNCTION trg_pca_touch_variacoes() RETURNS TRIGGER AS $$ BEGIN UPDATE produtos_variacoes SET data_alteracao = CURRENT_TIMESTAMP WHERE produto_id = COALESCE(NEW.produto_id, OLD.produto_id); RETURN COALESCE(NEW, OLD); END; $$ LANGUAGE plpgsql",
            "DROP TRIGGER IF EXISTS trg_pca_touch ON produtos_codigos_auxiliares",
            "CREATE TRIGGER trg_pca_touch AFTER INSERT OR UPDATE OR DELETE ON produtos_codigos_auxiliares FOR EACH ROW EXECUTE FUNCTION trg_pca_touch_variacoes()",
            // Hash de fotos sincronizado por produto (para sync incremental de imagens)
            "ALTER TABLE produtos ADD COLUMN IF NOT EXISTS fotos_hash_sync TEXT",
            "UPDATE produtos_variacoes SET data_alteracao = CURRENT_TIMESTAMP WHERE data_alteracao IS NULL"
        };
        foreach (var s in stmts)
        {
            try { using var cmd = conn.CreateCommand(); cmd.CommandText = s; cmd.ExecuteNonQuery(); }
            catch { /* tabela pode não existir ainda */ }
        }
    }

    private static List<(long variacaoId, long shopifyVariantId, long inventoryItemId, long produtoId, long shopifyProductId)> ObterVariacoesAlteradas(
        string cs, long empresaId, double minutosAtras)
    {
        var lista = new List<(long, long, long, long, long)>();
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        var intervalo = minutosAtras.ToString(System.Globalization.CultureInfo.InvariantCulture);
        cmd.CommandText = $@"SELECT v.codigo,
            COALESCE(NULLIF(m.plataforma_variante_id, '')::BIGINT, 0),
            COALESCE(NULLIF(m.plataforma_inventory_item_id, '')::BIGINT, 0),
            v.produto_id,
            COALESCE(NULLIF(m.plataforma_produto_id, '')::BIGINT, 0)
            FROM produtos_variacoes v
            INNER JOIN produtos p ON p.codigo = v.produto_id
            INNER JOIN integracoes_produtos m
                ON m.variacao_id = v.codigo
               AND m.plataforma = 'SHOPIFY'
               AND m.sincronizado = TRUE
               AND COALESCE(m.plataforma_variante_id, '') <> ''
               AND COALESCE(m.plataforma_variante_id, '') <> '0'
            WHERE (@e = 0 OR p.empresa_id = @e)
              AND v.ativo = TRUE
              AND v.data_alteracao >= NOW() - INTERVAL '{intervalo} minutes'";
        cmd.Parameters.AddWithValue("@e", empresaId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            lista.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));
        }
        return lista;
    }

    private static string ObterTagsProduto(string cs, long produtoId)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT cv.nome FROM produtos_classificacoes pc " +
                              "INNER JOIN classificacoes_valores cv ON cv.codigo = pc.classificacao_valor_id " +
                              "WHERE pc.produto_id = @id AND COALESCE(cv.ativo, TRUE) = TRUE " +
                              "ORDER BY cv.nome";
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

    private static ProdInfo? ObterInfoProduto(string cs, long produtoId)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT nome, descricao, descricao_detalhada, peso_bruto, ativo FROM produtos WHERE codigo = @id";
            cmd.Parameters.AddWithValue("@id", produtoId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new ProdInfo
            {
                Nome = r["nome"]?.ToString() ?? "",
                Descricao = r["descricao_detalhada"]?.ToString() ?? r["descricao"]?.ToString() ?? "",
                Tipo = "",
                Peso = r.IsDBNull(r.GetOrdinal("peso_bruto")) ? 0 : r.GetDecimal(r.GetOrdinal("peso_bruto")),
                Ativo = r.IsDBNull(r.GetOrdinal("ativo")) || r.GetBoolean(r.GetOrdinal("ativo"))
            };
        }
        catch { return null; }
    }

    private static VarInfo? ObterInfoVariacao(string cs, long variacaoId)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pv.sku, pv.ean, pv.preco_venda, pv.tamanho, c.nome AS cor " +
                              "FROM produtos_variacoes pv LEFT JOIN cores c ON c.codigo = pv.cor_id " +
                              "WHERE pv.codigo = @id";
            cmd.Parameters.AddWithValue("@id", variacaoId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new VarInfo
            {
                Sku = r["sku"]?.ToString() ?? "",
                Ean = r["ean"]?.ToString() ?? "",
                PrecoVenda = r.IsDBNull(r.GetOrdinal("preco_venda")) ? 0 : r.GetDecimal(r.GetOrdinal("preco_venda")),
                Tamanho = r["tamanho"]?.ToString() ?? "",
                Cor = r["cor"]?.ToString() ?? ""
            };
        }
        catch { return null; }
    }

    private static decimal ObterPrecoVariacao(string cs, long variacaoId, long tabelaPrecoId)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT preco_venda FROM tabelas_precos_itens WHERE tabela_id=@t AND variacao_id=@v LIMIT 1";
            cmd.Parameters.AddWithValue("@t", tabelaPrecoId);
            cmd.Parameters.AddWithValue("@v", variacaoId);
            var r = cmd.ExecuteScalar();
            return r != null && r != DBNull.Value ? Convert.ToDecimal(r) : 0;
        }
        catch { return 0; }
    }

    private static decimal ObterEstoqueVariacao(string cs, long variacaoId, long depositoId)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(quantidade), 0) - COALESCE(SUM(reservado), 0) FROM estoque WHERE variacao_id=@v AND deposito_id=@d";
            cmd.Parameters.AddWithValue("@v", variacaoId);
            cmd.Parameters.AddWithValue("@d", depositoId);
            var r = cmd.ExecuteScalar();
            return r != null && r != DBNull.Value ? Convert.ToDecimal(r) : 0;
        }
        catch { return 0; }
    }

    /// <summary>Calcula um hash representativo das fotos atuais do produto (md5 das fotos + ordem + cor).</summary>
    private static string ObterHashFotos(string cs, long produtoId)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT codigo, cor_id, principal, ordem, COALESCE(OCTET_LENGTH(foto), 0) AS tam, " +
                              "MD5(COALESCE(foto, ''::bytea)) AS h " +
                              "FROM produtos_fotos WHERE produto_id = @id AND foto IS NOT NULL " +
                              "ORDER BY principal DESC, ordem ASC, codigo ASC";
            cmd.Parameters.AddWithValue("@id", produtoId);
            using var r = cmd.ExecuteReader();
            var sb = new System.Text.StringBuilder();
            while (r.Read())
            {
                sb.Append(r["codigo"]).Append('|')
                  .Append(r.IsDBNull(r.GetOrdinal("cor_id")) ? "0" : r["cor_id"]?.ToString()).Append('|')
                  .Append(r.IsDBNull(r.GetOrdinal("principal")) ? "0" : (r.GetBoolean(r.GetOrdinal("principal")) ? "1" : "0")).Append('|')
                  .Append(r["ordem"]).Append('|')
                  .Append(r["tam"]).Append('|')
                  .Append(r["h"]).Append(';');
            }
            using var md5 = System.Security.Cryptography.MD5.Create();
            var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(bytes);
        }
        catch { return ""; }
    }

    private static string ObterHashFotosSync(string cs, long produtoId)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT fotos_hash_sync FROM produtos WHERE codigo = @id";
            cmd.Parameters.AddWithValue("@id", produtoId);
            var r = cmd.ExecuteScalar();
            return r == null || r == DBNull.Value ? "" : r.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static void SalvarHashFotosSync(string cs, long produtoId, string hash)
    {
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE produtos SET fotos_hash_sync = @h WHERE codigo = @id";
            cmd.Parameters.AddWithValue("@h", hash ?? "");
            cmd.Parameters.AddWithValue("@id", produtoId);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    /// <summary>Reenvia todas as fotos do produto para a Shopify, removendo as existentes antes.</summary>
    private static async Task SincronizarImagensProduto(string cs, ShopifyApiClient shopify, long produtoId, long shopifyProductId)
    {
        // 1) Apagar as imagens existentes na Shopify
        try
        {
            var existentes = await shopify.ListarImagensAsync(shopifyProductId);
            foreach (var img in existentes)
            {
                await shopify.DeletarImagemAsync(shopifyProductId, img.Id);
                await Task.Delay(200);
            }
        }
        catch { }

        // 2) Mapear cor_id -> shopify variant_ids
        var variantesPorCor = new Dictionary<long, List<long>>();
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pv.cor_id, COALESCE(NULLIF(m.plataforma_variante_id, '')::BIGINT, 0) " +
                              "FROM produtos_variacoes pv " +
                              "INNER JOIN integracoes_produtos m ON m.variacao_id = pv.codigo AND m.plataforma = 'SHOPIFY' " +
                              "WHERE pv.produto_id = @id AND pv.cor_id IS NOT NULL";
            cmd.Parameters.AddWithValue("@id", produtoId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0)) continue;
                var corId = r.GetInt64(0);
                var varId = r.GetInt64(1);
                if (corId <= 0 || varId <= 0) continue;
                if (!variantesPorCor.TryGetValue(corId, out var lista))
                {
                    lista = new List<long>();
                    variantesPorCor[corId] = lista;
                }
                if (!lista.Contains(varId)) lista.Add(varId);
            }
        }
        catch { }

        // 3) Reenviar as fotos atuais
        try
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT cor_id, foto FROM produtos_fotos " +
                              "WHERE produto_id = @id AND foto IS NOT NULL " +
                              "ORDER BY principal DESC, ordem ASC, codigo ASC";
            cmd.Parameters.AddWithValue("@id", produtoId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var corId = r.IsDBNull(r.GetOrdinal("cor_id")) ? 0L : r.GetInt64(r.GetOrdinal("cor_id"));
                var bytes = r["foto"] as byte[];
                if (bytes == null || bytes.Length == 0) continue;
                List<long>? variantIds = null;
                if (corId > 0 && variantesPorCor.TryGetValue(corId, out var ids)) variantIds = ids;
                try
                {
                    await shopify.AdicionarImagemAsync(shopifyProductId, bytes, variantIds);
                    await Task.Delay(400);
                }
                catch { }
            }
        }
        catch { }
    }
}

internal class ProdInfo
{
    public string Nome { get; set; } = "";
    public string Descricao { get; set; } = "";
    public string Tipo { get; set; } = "";
    public decimal Peso { get; set; }
    public bool Ativo { get; set; }
}

internal class VarInfo
{
    public string Sku { get; set; } = "";
    public string Ean { get; set; } = "";
    public decimal PrecoVenda { get; set; }
    public string Tamanho { get; set; } = "";
    public string Cor { get; set; } = "";
}
