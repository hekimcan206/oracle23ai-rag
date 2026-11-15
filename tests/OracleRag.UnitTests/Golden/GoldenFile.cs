using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OracleRag.UnitTests.Golden;

/// <summary>
/// Approval files under <c>tests/OracleRag.UnitTests/Golden</c>. Set <c>UPDATE_GOLDEN=1</c> to rewrite them, then review the
/// diff like code.
/// </summary>
internal static class GoldenFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Verify(string name, string actual, [CallerFilePath] string callerPath = "")
    {
        actual = actual.ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            // Resolved only when rewriting: deterministic CI builds map source paths to /_/, where no project file exists.
            var source = Path.Combine(SourceDirectory(callerPath), name);
            File.WriteAllText(source, actual + "\n");
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Golden", name);
        File.Exists(path).Should().BeTrue($"golden file {name} is missing; run with UPDATE_GOLDEN=1 to create it");
        File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd('\n').Should().Be(actual.TrimEnd('\n'));
    }

    private static string SourceDirectory(string callerPath)
    {
        // Callers live anywhere in the test project; golden files live in its Golden folder.
        var dir = Path.GetDirectoryName(callerPath)!;
        while (!File.Exists(Path.Combine(dir, "OracleRag.UnitTests.csproj")))
        {
            dir = Path.GetDirectoryName(dir)!;
        }

        return Path.Combine(dir, "Golden");
    }
}
