using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Axis.PrintAgent.Drivers;

/// <summary>
/// Driver Windows: enumera impressoras pelo registry e envia bytes RAW via
/// winspool (mesma técnica do legado WindowsRawPrinter do servidor).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsPrinterDriver : IPrinterDriver
{
    public string Tipo => "Windows";

    public IReadOnlyList<string> ListarImpressoras()
    {
        var list = new List<string>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers");
            if (k is not null)
            {
                foreach (var sub in k.GetSubKeyNames())
                {
                    if (!string.IsNullOrWhiteSpace(sub))
                        list.Add(sub);
                }
            }
        }
        catch { /* segue o baile */ }

        // Fallback CUPS-like: enumeração via WMI seria possível, mas o registry já cobre o caso comum.
        return list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
    }

    public Task ImprimirRawAsync(string impressora, byte[] dados, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(impressora))
            throw new ArgumentException("Impressora obrigatoria.", nameof(impressora));
        if (dados is null || dados.Length == 0)
            throw new ArgumentException("Dados vazios.", nameof(dados));

        IntPtr hPrinter = IntPtr.Zero, pBytes = IntPtr.Zero;
        try
        {
            if (!OpenPrinter(impressora, out hPrinter, IntPtr.Zero))
                throw new InvalidOperationException($"Nao foi possivel abrir a impressora '{impressora}'.");

            var di = new DOCINFOA { pDocName = "Etiqueta Axis", pDataType = "RAW" };
            if (!StartDocPrinter(hPrinter, 1, ref di)) throw new InvalidOperationException("Falha ao iniciar documento.");
            if (!StartPagePrinter(hPrinter)) throw new InvalidOperationException("Falha ao iniciar pagina.");

            pBytes = Marshal.AllocCoTaskMem(dados.Length);
            Marshal.Copy(dados, 0, pBytes, dados.Length);
            if (!WritePrinter(hPrinter, pBytes, dados.Length, out _))
                throw new InvalidOperationException("Falha ao gravar dados na impressora.");

            EndPagePrinter(hPrinter);
            EndDocPrinter(hPrinter);
            return Task.CompletedTask;
        }
        finally
        {
            if (pBytes != IntPtr.Zero) Marshal.FreeCoTaskMem(pBytes);
            if (hPrinter != IntPtr.Zero) ClosePrinter(hPrinter);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
    }

    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPStr)] string szPrinter, out IntPtr hPrinter, IntPtr pDefault);
    [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);
    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFOA di);
    [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);
    [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);
}
