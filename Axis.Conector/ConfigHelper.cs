using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace Axis.Conector;

/// <summary>
/// Helper para leitura de configurações do SQLite local (mesma base do Axis.Config).
/// Lê o arquivo axis_config.db gerado pelo Axis.Config (tabela "configuracao").
/// </summary>
public static class ConfigHelper
{
    private static readonly string DbPassword = Environment.GetEnvironmentVariable("AXIS_CONFIG_KEY") ?? "";

    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static string? _csCache;
    private static string? _dbPathCache;

    /// <summary>
    /// Localiza o arquivo axis_config.db.
    /// Regra: o axis_config.db SEMPRE fica na pasta do Axis ERP.
    /// Em produção, o Axis.Conector.exe é instalado na mesma pasta do Axis.exe,
    /// então BaseDirectory já é o caminho correto.
    /// Em debug, defina a variavel AXIS_CONFIG_DB apontando para o axis_config.db
    /// dentro do bin do projeto Axis.
    /// </summary>
    private static string ResolveDbPath()
    {
        if (_dbPathCache != null) return _dbPathCache;

        // 1) Override explicito (debug / cenarios especiais)
        var envPath = Environment.GetEnvironmentVariable("AXIS_CONFIG_DB");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            _dbPathCache = envPath;
            return envPath;
        }

        // 2) Mesma pasta do executavel (regra padrao do Axis)
        var local = Path.Combine(AppContext.BaseDirectory, "axis_config.db");
        if (File.Exists(local))
        {
            _dbPathCache = local;
            return local;
        }

        _dbPathCache = "";
        return "";
    }

    private static string Descriptografar(string textoCriptografado)
    {
        if (string.IsNullOrEmpty(textoCriptografado)) return "";
        try
        {
            var key = Encoding.UTF8.GetBytes(DbPassword.PadRight(32).Substring(0, 32));
            var buffer = Convert.FromBase64String(textoCriptografado);

            using var aes = Aes.Create();
            aes.Key = key;
            var iv = new byte[16];
            Array.Copy(buffer, 0, iv, 0, iv.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream(buffer, iv.Length, buffer.Length - iv.Length);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cs);
            return sr.ReadToEnd();
        }
        catch
        {
            return textoCriptografado;
        }
    }

    public static string ObterConnectionString()
    {
        if (_csCache != null) return _csCache;
        try
        {
            var dbPath = ResolveDbPath();
            if (string.IsNullOrEmpty(dbPath)) return "";

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT endereco_db, porta_db, nome_db, usuario_db, senha_db FROM configuracao ORDER BY id DESC LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                var host = r.IsDBNull(0) ? "" : r.GetString(0);
                var portaStr = r.IsDBNull(1) ? "5432" : r.GetString(1);
                var db = r.IsDBNull(2) ? "" : r.GetString(2);
                var user = r.IsDBNull(3) ? "" : r.GetString(3);
                var passEnc = r.IsDBNull(4) ? "" : r.GetString(4);
                var pass = Descriptografar(passEnc);

                if (!int.TryParse(portaStr, out var porta)) porta = 5432;

                _csCache = $"Host={host};Port={porta};Database={db};Username={user};Password={pass}";
            }
        }
        catch { }
        return _csCache ?? "";
    }

    /// <summary>Caminho do axis_config.db efetivamente em uso (para diagnóstico).</summary>
    public static string DbPath => ResolveDbPath();

    public static string ObterParametro(string chave)
    {
        if (_cache.TryGetValue(chave, out var cached)) return cached;
        try
        {
            var cs = ObterConnectionString();
            if (string.IsNullOrEmpty(cs)) return "";
            using var conn = new Npgsql.NpgsqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT valor FROM parametros WHERE chave = @c LIMIT 1";
            cmd.Parameters.AddWithValue("@c", chave);
            var val = cmd.ExecuteScalar()?.ToString() ?? "";
            _cache[chave] = val;
            return val;
        }
        catch { return ""; }
    }

    public static long ObterParametroLong(string chave, long defaultVal = 0)
    {
        var v = ObterParametro(chave);
        return long.TryParse(v, out var r) ? r : defaultVal;
    }

    public static int ObterParametroInt(string chave, int defaultVal = 0)
    {
        var v = ObterParametro(chave);
        return int.TryParse(v, out var r) ? r : defaultVal;
    }

    public static double ObterParametroDouble(string chave, double defaultVal = 0)
    {
        var v = ObterParametro(chave);
        return double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : defaultVal;
    }

    public static bool ObterParametroBool(string chave)
    {
        var v = ObterParametro(chave).ToLower();
        return v is "true" or "1" or "sim" or "s";
    }

    public static void LimparCache()
    {
        _cache.Clear();
        _csCache = null;
    }
}
