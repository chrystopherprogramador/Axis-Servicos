using Npgsql;

namespace Axis.Conector;

/// <summary>
/// Logger de integração que grava logs na tabela integracoes_log no PostgreSQL.
/// </summary>
public class IntegrationLogger
{
    private readonly string _cs;
    private readonly long _empresaId;

    public IntegrationLogger(string connectionString, long empresaId)
    {
        _cs = connectionString;
        _empresaId = empresaId;
    }

    public void Info(string categoria, string mensagem) => Gravar("INFO", categoria, mensagem);
    public void Warn(string categoria, string mensagem) => Gravar("WARN", categoria, mensagem);
    public void Error(string categoria, string mensagem, string? detalhes = null) => Gravar("ERROR", categoria, mensagem, detalhes);

    private void Gravar(string nivel, string categoria, string mensagem, string? detalhes = null)
    {
        try
        {
            using var conn = new NpgsqlConnection(_cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO integracoes_log 
                (empresa_id, nivel, categoria, mensagem, detalhes, data_hora) 
                VALUES (@emp, @nivel, @cat, @msg, @det, NOW())";
            cmd.Parameters.AddWithValue("@emp", _empresaId);
            cmd.Parameters.AddWithValue("@nivel", nivel);
            cmd.Parameters.AddWithValue("@cat", categoria);
            cmd.Parameters.AddWithValue("@msg", mensagem);
            cmd.Parameters.AddWithValue("@det", (object?)detalhes ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Falha silenciosa para não interromper o worker
            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "conector_debug.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{nivel}] [{categoria}] {mensagem}\n");
            }
            catch { }
        }
    }
}
