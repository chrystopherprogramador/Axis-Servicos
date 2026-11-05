using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Axis.PrintAgent.Platform;

/// <summary>
/// Esconde a janela do console no Windows para o agente parecer um
/// processo de bandeja. Pode ser desativado com a flag --console.
/// </summary>
internal static class ConsoleVisibility
{
    [SupportedOSPlatform("windows")]
    public static void HideOnWindows()
    {
        try
        {
            var hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);
        }
        catch { /* sem console disponível, ignora */ }
    }

    private const int SW_HIDE = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
