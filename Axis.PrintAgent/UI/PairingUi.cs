#if AXIS_WINFORMS
using System.Runtime.Versioning;
using Axis.PrintAgent.Configuration;

namespace Axis.PrintAgent.UI;

/// <summary>
/// Helpers de UI para WinForms (apenas no TFM net9.0-windows).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PairingUi
{
    /// <summary>
    /// Abre o formulário modal de pareamento. Retorna true se o usuário pareou com sucesso.
    /// </summary>
    public static bool MostrarFormPareamento(AgentConfig config)
    {
        ApplicationConfiguration.Initialize();
        using var form = new PairingForm(config);
        var r = form.ShowDialog();
        return r == DialogResult.OK && form.PareadoComSucesso;
    }
}
#endif
