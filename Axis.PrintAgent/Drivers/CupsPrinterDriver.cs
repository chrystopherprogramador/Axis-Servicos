using System.Diagnostics;

namespace Axis.PrintAgent.Drivers;

/// <summary>
/// Driver Linux/macOS: usa o CUPS via `lpstat -a` para listar impressoras e
/// `lp -d {nome} -o raw` (stdin = bytes) para enviar dados crus.
/// </summary>
internal sealed class CupsPrinterDriver : IPrinterDriver
{
    public string Tipo => "CUPS";

    public IReadOnlyList<string> ListarImpressoras()
    {
        try
        {
            var psi = new ProcessStartInfo("lpstat", "-a")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return Array.Empty<string>();
            var saida = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5_000);

            var nomes = new List<string>();
            foreach (var linha in saida.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var sp = linha.IndexOf(' ');
                var nome = sp > 0 ? linha[..sp].Trim() : linha.Trim();
                if (!string.IsNullOrWhiteSpace(nome)) nomes.Add(nome);
            }
            return nomes;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task ImprimirRawAsync(string impressora, byte[] dados, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(impressora))
            throw new ArgumentException("Impressora obrigatoria.", nameof(impressora));
        if (dados is null || dados.Length == 0)
            throw new ArgumentException("Dados vazios.", nameof(dados));

        var psi = new ProcessStartInfo("lp", $"-d \"{impressora}\" -o raw")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Nao foi possivel executar 'lp' (CUPS instalado?).");
        await p.StandardInput.BaseStream.WriteAsync(dados, ct);
        p.StandardInput.Close();
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"Falha ao imprimir via CUPS: {err}");
        }
    }
}
