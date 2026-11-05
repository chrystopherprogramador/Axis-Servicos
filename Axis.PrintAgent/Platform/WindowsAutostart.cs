using System.Runtime.Versioning;

namespace Axis.PrintAgent.Platform;

/// <summary>
/// Garante que o agente inicie junto com o Windows (HKCU\...\Run).
/// Não exige privilégios de administrador (escopo de usuário).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Value = "AxisPrintAgent";

    public static void EnsureRegistered()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;

            // Para ".exe" comum o ProcessPath aponta para o próprio executável.
            // Para "dotnet axis-print-agent.dll" o ProcessPath aponta para dotnet.exe
            // e nesse cenário não registramos autostart (modo dev).
            if (exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return;

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;

            var atual = key.GetValue(Value) as string;
            var alvo = $"\"{exe}\"";
            if (!string.Equals(atual, alvo, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(Value, alvo, Microsoft.Win32.RegistryValueKind.String);
            }
        }
        catch
        {
            // Falha silenciosa: agente continua funcionando, só não auto-inicia.
        }
    }
}
