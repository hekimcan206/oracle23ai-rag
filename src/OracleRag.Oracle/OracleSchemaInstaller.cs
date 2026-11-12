using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using OracleRag.Retrieval;

namespace OracleRag.Oracle;

/// <summary>
/// Installs the schema as the owner account and grants the application account the minimum it needs. Idempotent:
/// objects that already exist are left alone, so the compose installer can run on every start.
/// </summary>
public sealed class OracleSchemaInstaller(string ownerConnectionString)
{
    private const int NameAlreadyUsed = 955;

    public async Task InstallAsync(string appUser, int dimensions, string embeddingModel, CancellationToken cancellationToken = default)
    {
        SqlIdentifier.Validate(appUser);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 65535);

        await using var connection = new OracleConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);

        var schema = SqlScripts.Load("OracleRag.Oracle.Sql.01-schema.sql")
            .Replace("{{DIMENSIONS}}", dimensions.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        foreach (var statement in SqlScripts.Split(schema))
        {
            await ExecuteIgnoringExistingAsync(connection, statement, cancellationToken);
        }

        var grants = SqlScripts.Load("OracleRag.Oracle.Sql.02-grants.sql").Replace("{{APP_USER}}", appUser, StringComparison.Ordinal);
        foreach (var statement in SqlScripts.Split(grants))
        {
            await ExecuteAsync(connection, statement, cancellationToken);
        }

        await RecordEmbeddingModelAsync(connection, embeddingModel, dimensions, cancellationToken);
    }

    /// <summary>
    /// Stores the embedding model id on first install and refuses to continue when a different model was recorded:
    /// vectors from two models in one index give meaningless distances without any error.
    /// </summary>
    private static async Task RecordEmbeddingModelAsync(OracleConnection connection, string model, int dimensions, CancellationToken cancellationToken)
    {
        await using var merge = connection.CreateCommand();
        merge.BindByName = true;
        merge.CommandText = """
            MERGE INTO rag_meta m
            USING (SELECT 'embedding_model' AS k, :model AS v FROM dual
                   UNION ALL SELECT 'dimensions', :dims FROM dual) s
            ON (m.meta_key = s.k)
            WHEN NOT MATCHED THEN INSERT (meta_key, meta_value) VALUES (s.k, s.v)
            """;
        merge.Parameters.Add("model", model);
        merge.Parameters.Add("dims", dimensions.ToString(CultureInfo.InvariantCulture));
        await merge.ExecuteNonQueryAsync(cancellationToken);

        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT meta_value FROM rag_meta WHERE meta_key = 'embedding_model'";
        var stored = (string?)await check.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(stored, model, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The schema holds vectors from embedding model '{stored}', not '{model}'. Re-embed into a new schema.");
        }
    }

    private static async Task ExecuteIgnoringExistingAsync(OracleConnection connection, string sql, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(connection, sql, cancellationToken);
        }
        catch (OracleException ex) when (ex.Number == NameAlreadyUsed)
        {
            // Re-run of the installer: the object is already there.
        }
    }

    private static async Task ExecuteAsync(OracleConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal static class SqlScripts
{
    public static string Load(string resourceName)
    {
        using var stream = typeof(SqlScripts).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded script {resourceName} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Splits on lines holding a single '/', dropping comment-only fragments.</summary>
    public static IEnumerable<string> Split(string script) =>
        script.ReplaceLineEndings("\n")
            .Split("\n/\n")
            .Select(s => string.Join('\n', s.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal))).Trim().TrimEnd('/').Trim())
            .Where(s => s.Length > 0);
}
