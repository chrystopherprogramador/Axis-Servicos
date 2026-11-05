namespace Axis.PrintAgent.Drivers;

public interface IPrinterDriver
{
    string Tipo { get; }
    IReadOnlyList<string> ListarImpressoras();
    Task ImprimirRawAsync(string impressora, byte[] dados, CancellationToken ct);
}

internal static class PrinterDriverFactory
{
    public static IPrinterDriver Create()
    {
        if (OperatingSystem.IsWindows())
            return CreateWindows();
        return new CupsPrinterDriver();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IPrinterDriver CreateWindows() => new WindowsPrinterDriver();
}
