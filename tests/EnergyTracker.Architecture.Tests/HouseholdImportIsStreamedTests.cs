using System.Text;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.10 (audit S4): an uploaded household export is never held whole in memory, and an uploaded
// spreadsheet is never opened without limits. A source-text scan (comments stripped), the same
// convention as HouseholdExportReaderDoesNotBypassTenantIsolationTests, so a later change cannot quietly
// bring back `ReadToEnd` on an upload or a second `SpreadsheetDocument.Open`. Each scanned file must
// still exist and still contain the identifier it is expected to, so a rename empties nothing.
public class HouseholdImportIsStreamedTests
{
    private const string Endpoints = "src/EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs";
    private const string Validator = "src/EnergyTracker.Application/ValidateHouseholdImport.cs";
    private const string JsonReader = "src/EnergyTracker.Application/HouseholdImportJsonReader.cs";
    private const string Restore = "src/EnergyTracker.Application/RestoreHouseholdData.cs";
    private const string XlsxParser = "src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs";

    // Reading a whole file or stream into one string or byte array, or parsing/deserializing a whole
    // export, is exactly the S4 hole. The validator's per-element `JsonDocument.ParseValue(` and the
    // restore's stream `DeserializeAsync<HouseholdExportResult>(` are the allowed forms.
    private static readonly string[] ForbiddenInImportPath =
    [
        "ReadToEnd",
        "ReadAllText",
        "ReadAllBytes",
        "JsonDocument.Parse(",
        "JsonDocument.ParseAsync(",
        "JsonSerializer.Deserialize<HouseholdExportResult>(",
        "new StreamReader(",
    ];

    // file -> an identifier it must still contain (so an emptied or renamed file fails the test).
    private static readonly (string File, string MustContain)[] ScannedImportFiles =
    [
        (Endpoints, "FileStream"),
        (Validator, "HouseholdImportJsonReader"),
        (JsonReader, "Utf8JsonReader"),
        (Restore, "DeserializeAsync"),
    ];

    [Fact]
    public void The_household_import_path_never_reads_an_upload_into_one_string_or_array()
    {
        foreach (var (file, mustContain) in ScannedImportFiles)
        {
            var code = CodeOf(file);

            code.ShouldContain(mustContain, Case.Sensitive, $"{file} no longer contains '{mustContain}': was it renamed? Update this guard with it.");
            foreach (var forbidden in ForbiddenInImportPath)
            {
                code.ShouldNotContain(forbidden, Case.Sensitive, $"{file} contains '{forbidden}': an upload must be streamed, never read whole (Story 11.10, audit S4).");
            }
        }
    }

    // Utf8JsonReader.Skip() needs the whole value in the buffer, which is exactly what must never
    // happen: the reader crosses big values token by token and only TrySkip()s one bounded element.
    [Fact]
    public void The_reader_never_calls_Skip_on_a_value_of_unknown_size()
    {
        CodeOf(JsonReader).ShouldNotContain(".Skip()", Case.Sensitive, "Utf8JsonReader.Skip() would buffer a whole value; use SkipValue()/TrySkip() (Story 11.10).");
    }

    [Fact]
    public void The_Eve_Home_parser_opens_a_package_through_the_one_helper_that_sets_MaxCharactersInPart()
    {
        var code = CodeOf(XlsxParser);

        code.ShouldContain("OpenSettings", Case.Sensitive, $"{XlsxParser} no longer mentions OpenSettings.");
        code.ShouldContain("MaxCharactersInPart", Case.Sensitive, $"{XlsxParser} no longer mentions MaxCharactersInPart.");
        CountOf(code, "SpreadsheetDocument.Open(").ShouldBe(1, $"{XlsxParser} must open a package in exactly one place (the OpenDocument helper), so no caller can skip the limits.");

        var helper = MethodBody(code, "private SpreadsheetDocument OpenDocument(Stream fileContent, CancellationToken cancellationToken)");
        helper.ShouldContain("SpreadsheetDocument.Open(", Case.Sensitive, "The one SpreadsheetDocument.Open( call must be inside OpenDocument.");
        helper.ShouldContain("MaxCharactersInPart", Case.Sensitive, "OpenDocument must set OpenSettings.MaxCharactersInPart.");
        helper.ShouldContain("RejectOversizedEntries", Case.Sensitive, "OpenDocument must run the decompressed-size pre-scan before OpenXml opens the package.");
    }

    // Non-vacuous: the stripper and the matcher are proven against known-bad text, so a refactor of
    // this test cannot silently turn it into one that scans nothing.
    [Fact]
    public void The_scan_itself_detects_what_it_guards_against_and_ignores_comments()
    {
        const string bad = "var json = await reader.ReadToEndAsync(); // fine\nvar x = \"//not a comment\"; var d = JsonDocument.Parse(json);";
        var code = StripComments(bad);

        ForbiddenInImportPath.Where(f => code.Contains(f, StringComparison.Ordinal)).ShouldBe(["ReadToEnd", "JsonDocument.Parse("]);
        StripComments("a /* ReadAllText */ b // ReadAllBytes\nc").ShouldNotContain("ReadAll");
        StripComments("x = \"//kept\";").ShouldContain("//kept");
    }

    private static string CodeOf(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");
        var code = StripComments(File.ReadAllText(fullPath));
        code.Trim().ShouldNotBeEmpty($"{relativePath} has no code after stripping comments.");
        return code;
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // The text between the braces of the method that starts with `signature` (brace matching on code
    // that has no braces in strings or comments at this point).
    private static string MethodBody(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Could not find '{signature}': was the helper renamed? Update this guard with it.");
        var open = code.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                depth++;
            }
            else if (code[i] == '}' && --depth == 0)
            {
                return code[(open + 1)..i];
            }
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }

    // Removes // and /* */ comments but keeps the contents of "regular" string literals (a `//` inside
    // a string is not a comment). None of the scanned files use verbatim or raw string literals.
    private static string StripComments(string source)
    {
        var result = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '"')
            {
                result.Append(c);
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        result.Append(source[i++]);
                    }

                    result.Append(source[i++]);
                }

                if (i < source.Length)
                {
                    result.Append(source[i++]);
                }
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }

                i += 2;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EnergyTracker.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (EnergyTracker.sln) from test base directory.");
        }

        return dir.FullName;
    }
}
