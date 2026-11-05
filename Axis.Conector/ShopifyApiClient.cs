using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Axis.Conector;

/// <summary>
/// Cliente HTTP para comunicação com a API REST da Shopify (Admin API 2024-01).
/// </summary>
public class ShopifyApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly JsonSerializerOptions _jsonOpts;

    public ShopifyApiClient(string loja, string accessToken)
    {
        _baseUrl = $"https://{loja}.myshopify.com/admin/api/2024-01";
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("X-Shopify-Access-Token", accessToken);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _jsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            // Permite emojis e caracteres unicode literais (ex.: 😀, ç, ã)
            // sem escapar para \uXXXX, preservando-os no body_html.
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };
    }

    #region Produtos

    public async Task<ShopifyProductResult?> CriarProdutoAsync(ShopifyProduct product)
    {
        var body = JsonSerializer.Serialize(new { product }, _jsonOpts);
        var resp = await _http.PostAsync($"{_baseUrl}/products.json",
            new StringContent(body, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        var wrapper = JsonSerializer.Deserialize<ShopifyProductWrapper>(json, _jsonOpts);
        return wrapper?.Product;
    }

    public async Task AtualizarVarianteAsync(long variantId, ShopifyVariant variant)
    {
        var body = JsonSerializer.Serialize(new { variant }, _jsonOpts);
        var resp = await _http.PutAsync($"{_baseUrl}/variants/{variantId}.json",
            new StringContent(body, Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"PUT /variants/{variantId} {(int)resp.StatusCode}: {err}");
        }
    }

    public async Task AtualizarProdutoAsync(long productId, ShopifyProduct product)
    {
        product.Id = productId;
        var body = JsonSerializer.Serialize(new { product }, _jsonOpts);
        var resp = await _http.PutAsync($"{_baseUrl}/products/{productId}.json",
            new StringContent(body, Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"PUT /products/{productId} {(int)resp.StatusCode}: {err}");
        }
    }

    public async Task AdicionarImagemAsync(long productId, byte[] imageBytes, List<long>? variantIds = null)
    {
        var base64 = Convert.ToBase64String(imageBytes);
        object image = variantIds != null && variantIds.Count > 0
            ? new { attachment = base64, variant_ids = variantIds }
            : new { attachment = base64 };
        var body = JsonSerializer.Serialize(new { image }, _jsonOpts);
        var resp = await _http.PostAsync($"{_baseUrl}/products/{productId}/images.json",
            new StringContent(body, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Lista as imagens já cadastradas em um produto da Shopify.</summary>
    public async Task<List<ShopifyImage>> ListarImagensAsync(long productId)
    {
        var resp = await _http.GetAsync($"{_baseUrl}/products/{productId}/images.json");
        if (!resp.IsSuccessStatusCode) return new List<ShopifyImage>();
        var json = await resp.Content.ReadAsStringAsync();
        var wrapper = JsonSerializer.Deserialize<ShopifyImagesWrapper>(json, _jsonOpts);
        return wrapper?.Images ?? new List<ShopifyImage>();
    }

    /// <summary>Remove uma imagem específica de um produto da Shopify.</summary>
    public async Task DeletarImagemAsync(long productId, long imageId)
    {
        try
        {
            await _http.DeleteAsync($"{_baseUrl}/products/{productId}/images/{imageId}.json");
        }
        catch { /* ignora — pode já ter sido removida */ }
    }

    #endregion

    #region Locations / Estoque

    public async Task<List<ShopifyLocation>> ListarLocationsAsync()
    {
        var resp = await _http.GetAsync($"{_baseUrl}/locations.json");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        var wrapper = JsonSerializer.Deserialize<ShopifyLocationsWrapper>(json, _jsonOpts);
        return wrapper?.Locations ?? [];
    }

    public async Task AjustarEstoqueAsync(long inventoryItemId, long locationId, int quantity)
    {
        var resp = await EnviarSetEstoqueAsync(inventoryItemId, locationId, quantity);

        // 422 normalmente significa que o inventory_item ainda não está
        // conectado ao location. Conectar e tentar novamente.
        if (resp.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            var connectBody = JsonSerializer.Serialize(new
            {
                location_id = locationId,
                inventory_item_id = inventoryItemId
            }, _jsonOpts);
            var connectResp = await _http.PostAsync($"{_baseUrl}/inventory_levels/connect.json",
                new StringContent(connectBody, Encoding.UTF8, "application/json"));

            // 422 aqui costuma indicar "already connected" — seguimos.
            if (!connectResp.IsSuccessStatusCode &&
                connectResp.StatusCode != System.Net.HttpStatusCode.UnprocessableEntity)
            {
                connectResp.EnsureSuccessStatusCode();
            }

            // Garantir que o item está marcado como "tracked".
            try
            {
                var trackedBody = JsonSerializer.Serialize(new
                {
                    inventory_item = new { id = inventoryItemId, tracked = true }
                }, _jsonOpts);
                await _http.PutAsync($"{_baseUrl}/inventory_items/{inventoryItemId}.json",
                    new StringContent(trackedBody, Encoding.UTF8, "application/json"));
            }
            catch { }

            resp = await EnviarSetEstoqueAsync(inventoryItemId, locationId, quantity);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var detalhe = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Shopify inventory_levels/set falhou: {(int)resp.StatusCode} {resp.ReasonPhrase} — {detalhe}");
        }
    }

    private async Task<HttpResponseMessage> EnviarSetEstoqueAsync(long inventoryItemId, long locationId, int quantity)
    {
        var body = JsonSerializer.Serialize(new
        {
            location_id = locationId,
            inventory_item_id = inventoryItemId,
            available = quantity
        }, _jsonOpts);
        return await _http.PostAsync($"{_baseUrl}/inventory_levels/set.json",
            new StringContent(body, Encoding.UTF8, "application/json"));
    }

    #endregion

    #region Pedidos

    public async Task<List<ShopifyOrder>> ListarPedidosAsync(string status = "any", DateTime? since = null)
    {
        var url = $"{_baseUrl}/orders.json?status={status}&limit=250";
        if (since.HasValue) url += $"&created_at_min={since.Value:yyyy-MM-ddTHH:mm:sszzz}";
        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        var wrapper = JsonSerializer.Deserialize<ShopifyOrdersWrapper>(json, _jsonOpts);
        return wrapper?.Orders ?? [];
    }

    public async Task<ShopifyFulfillment?> CriarFulfillmentAsync(long orderId, ShopifyFulfillment fulfillment)
    {
        var body = JsonSerializer.Serialize(new { fulfillment }, _jsonOpts);
        var resp = await _http.PostAsync($"{_baseUrl}/orders/{orderId}/fulfillments.json",
            new StringContent(body, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        var wrapper = JsonSerializer.Deserialize<ShopifyFulfillmentWrapper>(json, _jsonOpts);
        return wrapper?.Fulfillment;
    }

    #endregion

    public void Dispose() => _http.Dispose();
}

#region Models

public class ShopifyProduct
{
    public long Id { get; set; }
    public string? Title { get; set; }
    public string? BodyHtml { get; set; }
    public string? ProductType { get; set; }
    public string? Vendor { get; set; }
    public string? Status { get; set; }
    /// <summary>Lista de tags separadas por vírgula (formato esperado pela Shopify).</summary>
    public string? Tags { get; set; }
    public List<ShopifyVariant>? Variants { get; set; }
}

public class ShopifyVariant
{
    public long Id { get; set; }
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string? Price { get; set; }
    public string? Option1 { get; set; }
    public string? Option2 { get; set; }
    public string? InventoryManagement { get; set; }
    public bool? RequiresShipping { get; set; }
    public decimal? Weight { get; set; }
    public string? WeightUnit { get; set; }
    public long InventoryItemId { get; set; }
}

public class ShopifyProductResult
{
    public long Id { get; set; }
    public List<ShopifyVariant>? Variants { get; set; }
}

public class ShopifyLocation
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public bool Active { get; set; }
}

public class ShopifyOrder
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? FinancialStatus { get; set; }
    public string? FulfillmentStatus { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<ShopifyLineItem>? LineItems { get; set; }
    public ShopifyCustomer? Customer { get; set; }
    public ShopifyAddress? ShippingAddress { get; set; }
    public decimal TotalPrice { get; set; }
}

public class ShopifyLineItem
{
    public long Id { get; set; }
    public long VariantId { get; set; }
    public string? Sku { get; set; }
    public string? Title { get; set; }
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}

public class ShopifyCustomer
{
    public long Id { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
}

public class ShopifyAddress
{
    public string? Address1 { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? Zip { get; set; }
    public string? Country { get; set; }
}

public class ShopifyFulfillment
{
    public long Id { get; set; }
    public string? TrackingNumber { get; set; }
    public string? TrackingCompany { get; set; }
    public List<ShopifyFulfillmentLineItem>? LineItems { get; set; }
}

public class ShopifyFulfillmentLineItem
{
    public long Id { get; set; }
    public int Quantity { get; set; }
}

// Wrappers para deserialização
public class ShopifyProductWrapper { public ShopifyProductResult? Product { get; set; } }
public class ShopifyLocationsWrapper { public List<ShopifyLocation>? Locations { get; set; } }
public class ShopifyOrdersWrapper { public List<ShopifyOrder>? Orders { get; set; } }
public class ShopifyFulfillmentWrapper { public ShopifyFulfillment? Fulfillment { get; set; } }
public class ShopifyImagesWrapper { public List<ShopifyImage>? Images { get; set; } }

public class ShopifyImage
{
    public long Id { get; set; }
    public long ProductId { get; set; }
    public int? Position { get; set; }
    public string? Src { get; set; }
    public List<long>? VariantIds { get; set; }
}

#endregion
