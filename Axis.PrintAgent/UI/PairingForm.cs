#if AXIS_WINFORMS
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using Axis.PrintAgent.Configuration;

namespace Axis.PrintAgent.UI;

/// <summary>
/// Janela de pareamento estilo "instalador":
/// usuário cola a URL do Axis e o código gerado no navegador (AX-{TENANT}-XXXX) e clica em Parear.
/// O tenant é extraído do próprio código, não há campo separado.
/// O agente confirma com o servidor, grava o agent.json e fica residente.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PairingForm : Form
{
    private readonly AgentConfig _config;
    private readonly TextBox _txtUrl;
    private readonly TextBox _txtCodigo;
    private readonly Button _btnParear;
    private readonly Label _lblStatus;
    private readonly Label _lblTenantDetectado;
    private readonly LinkLabel _lnkAbrir;
    private bool _pareadoComSucesso;

    public bool PareadoComSucesso => _pareadoComSucesso;

    public PairingForm(AgentConfig config)
    {
        _config = config;

        Text = "Axis – Agente de Impressão";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 300);
        Font = new Font("Segoe UI", 9F);

        var pad = 16;
        var lblTitulo = new Label
        {
            Text = "Parear este computador",
            Font = new Font("Segoe UI Semibold", 14F),
            Location = new Point(pad, pad),
            AutoSize = true
        };
        var lblSub = new Label
        {
            Text = "Cole o endereço do seu Axis e o código gerado em\nConfigurações ▸ Agentes de Impressão.",
            ForeColor = SystemColors.GrayText,
            Location = new Point(pad, pad + 28),
            AutoSize = true
        };

        var lblUrl = new Label { Text = "URL do Axis", Location = new Point(pad, 80), AutoSize = true };
        _txtUrl = new TextBox
        {
            Location = new Point(pad, 98),
            Width = ClientSize.Width - pad * 2,
            Text = string.IsNullOrWhiteSpace(_config.ServerUrl) ? "https://" : _config.ServerUrl,
            PlaceholderText = "https://seu-tenant.axisapp.com.br"
        };

        var lblCod = new Label { Text = "Código de pareamento", Location = new Point(pad, 130), AutoSize = true };
        _txtCodigo = new TextBox
        {
            Location = new Point(pad, 148),
            Width = ClientSize.Width - pad * 2,
            CharacterCasing = CharacterCasing.Upper,
            Font = new Font("Consolas", 11F),
            PlaceholderText = "AX-TENANT-XXXX"
        };
        _txtCodigo.TextChanged += (_, _) => AtualizarTenantDetectado();

        _lblTenantDetectado = new Label
        {
            Location = new Point(pad, 178),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = ""
        };

        _btnParear = new Button
        {
            Text = "Parear",
            Location = new Point(ClientSize.Width - pad - 110, 216),
            Size = new Size(110, 30),
            BackColor = Color.FromArgb(13, 110, 253),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnParear.FlatAppearance.BorderSize = 0;
        _btnParear.Click += async (_, _) => await PairAsync();
        AcceptButton = _btnParear;

        _lblStatus = new Label
        {
            Location = new Point(pad, 224),
            Size = new Size(ClientSize.Width - pad * 2 - 130, 40),
            ForeColor = SystemColors.GrayText
        };

        _lnkAbrir = new LinkLabel
        {
            Text = "Abrir Axis no navegador",
            Location = new Point(pad, 270),
            AutoSize = true
        };
        _lnkAbrir.LinkClicked += (_, _) => AbrirUrl();

        Controls.AddRange(new Control[] {
            lblTitulo, lblSub,
            lblUrl, _txtUrl,
            lblCod, _txtCodigo, _lblTenantDetectado,
            _btnParear, _lblStatus,
            _lnkAbrir
        });

        Shown += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_txtUrl.Text) || _txtUrl.Text == "https://") _txtUrl.Focus();
            else _txtCodigo.Focus();
        };
    }

    private void AtualizarTenantDetectado()
    {
        var tenant = ExtrairTenantDoCodigo(_txtCodigo.Text);
        _lblTenantDetectado.Text = string.IsNullOrEmpty(tenant)
            ? ""
            : $"Tenant detectado: {tenant}";
    }

    private static string ExtrairTenantDoCodigo(string? codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo)) return string.Empty;
        var partes = codigo.Trim().ToUpperInvariant().Split('-');
        // AX-TENANT-XXXX
        if (partes.Length == 3 && partes[0] == "AX") return partes[1];
        return string.Empty;
    }

    private void AbrirUrl()
    {
        var url = _txtUrl.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async Task PairAsync()
    {
        var url = _txtUrl.Text?.Trim().TrimEnd('/') ?? string.Empty;
        var codigo = (_txtCodigo.Text ?? string.Empty).Trim().ToUpperInvariant();
        var tenant = ExtrairTenantDoCodigo(codigo);

        if (string.IsNullOrWhiteSpace(url) || !(url.StartsWith("http://") || url.StartsWith("https://")))
        {
            SetStatus("Informe a URL completa (https://...).", erro: true);
            _txtUrl.Focus(); return;
        }
        if (codigo.Length < 5)
        {
            SetStatus("Informe o código (ex.: AX-DEMO-AB12).", erro: true);
            _txtCodigo.Focus(); return;
        }

        _btnParear.Enabled = false;
        SetStatus("Pareando…", erro: false);

        try
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/print-agent/pair/confirm")
            {
                Content = JsonContent.Create(new
                {
                    codigo,
                    nome = Environment.MachineName,
                    sistema = "Windows"
                })
            };
            // Manda também o tenant em header, caso o servidor já tenha o middleware
            // pronto para receber por header em vez de subdomínio.
            if (!string.IsNullOrEmpty(tenant)) req.Headers.TryAddWithoutValidation("X-Tenant", tenant);

            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                SetStatus($"Falha {(int)resp.StatusCode}. {Resumir(body)}", erro: true);
                return;
            }
            var data = await resp.Content.ReadFromJsonAsync<ConfirmPairResponse>();
            if (data is null || string.IsNullOrEmpty(data.Token))
            {
                SetStatus("Resposta inválida do servidor.", erro: true);
                return;
            }

            _config.ServerUrl = url;
            _config.Tenant = string.IsNullOrEmpty(data.Tenant) ? tenant : data.Tenant;
            _config.Token = data.Token;
            _config.AgentId = data.AgenteId;
            _config.AgentName = Environment.MachineName;
            _config.Save();

            _pareadoComSucesso = true;
            SetStatus("Pareado com sucesso! Iniciando o agente…", erro: false);
            await Task.Delay(900);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            SetStatus("Erro: " + ex.Message, erro: true);
        }
        finally
        {
            _btnParear.Enabled = true;
        }
    }

    private void SetStatus(string texto, bool erro)
    {
        _lblStatus.ForeColor = erro ? Color.Firebrick : SystemColors.GrayText;
        _lblStatus.Text = texto;
    }

    private static string Resumir(string s)
        => string.IsNullOrEmpty(s) ? "" : (s.Length > 160 ? s.Substring(0, 160) + "…" : s);

    private sealed class ConfirmPairResponse
    {
        [JsonPropertyName("token")] public string Token { get; set; } = "";
        [JsonPropertyName("agenteId")] public string AgenteId { get; set; } = "";
        [JsonPropertyName("tenant")] public string Tenant { get; set; } = "";
    }
}
#endif
