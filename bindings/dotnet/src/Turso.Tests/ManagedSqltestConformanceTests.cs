using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Turso.Core;

namespace Turso.Tests;

public class ManagedSqltestConformanceTests
{
    [TestCase("generate_series.sqltest", "generate-series-three-args")]
    [TestCase("limit.sqltest", "limit-i64-min-no-panic")]
    [TestCase("limit.sqltest", "limit-zero")]
    [TestCase("limit.sqltest", "limit-negative-no-limit")]
    [TestCase("limit.sqltest", "limit-one")]
    [TestCase("limit.sqltest", "limit-in-subquery")]
    [TestCase("simple-count-optimization.sqltest", "simple-count")]
    [TestCase("concat.sqltest", "concat")]
    [TestCase("concat.sqltest", "concat-2")]
    [TestCase("concat.sqltest", "concat-3")]
    [TestCase("concat.sqltest", "concat-blob")]
    [TestCase("delete.sqltest", "delete-single-1")]
    [TestCase("update.sqltest", "basic-update")]
    [TestCase("update.sqltest", "update-with-expression")]
    [TestCase("like.sqltest", "like-with-dot")]
    [TestCase("like.sqltest", "like-with-backslash")]
    [TestCase("agg-functions/memory.sqltest", "min-null-regression-test")]
    [TestCase("agg-functions/memory.sqltest", "max-null-regression-test")]
    [TestCase("agg-functions/memory.sqltest", "group-concat-null-values-test")]
    [TestCase("values.sqltest", "values-1")]
    [TestCase("values.sqltest", "values-2")]
    [TestCase("values.sqltest", "values-3")]
    [TestCase("values.sqltest", "values-in-from")]
    [TestCase("values.sqltest", "values-in-join")]
    [TestCase("values.sqltest", "values-between")]
    [TestCase("values.sqltest", "values-correlated-values-in-select")]
    [TestCase("cross_join.sqltest", "cross-join-basic-2x2")]
    [TestCase("cross_join.sqltest", "cross-join-subquery-right")]
    [TestCase("cross_join.sqltest", "cross-join-group-by-left")]
    [TestCase("cte.sqltest", "cte-basic")]
    [TestCase("cte.sqltest", "cte-multiple")]
    [TestCase("cte.sqltest", "cte-chain")]
    [TestCase("cte.sqltest", "cte-intersect")]
    [TestCase("cte.sqltest", "cte-except")]
    [TestCase("cte.sqltest", "cte-union-limit-one")]
    [TestCase("cte.sqltest", "cte-intersect-limit")]
    [TestCase("cte.sqltest", "cte-except-limit")]
    [TestCase("cte-real-affinity-join.sqltest", "cte-real-affinity-inner-join")]
    [TestCase("compound-select-orderby.sqltest", "union-all-order-by-col-number")]
    [TestCase("compound-select-orderby.sqltest", "union-order-by-col-number")]
    [TestCase("compound-select-orderby.sqltest", "intersect-order-by")]
    [TestCase("compound-select-orderby.sqltest", "intersect-order-by-desc")]
    [TestCase("compound-select-orderby.sqltest", "intersect-order-by-col-name")]
    [TestCase("compound-select-orderby.sqltest", "except-order-by")]
    [TestCase("compound-select-orderby.sqltest", "except-order-by-desc")]
    [TestCase("compound-select-orderby.sqltest", "except-order-by-col-name")]
    [TestCase("compound-select-orderby.sqltest", "union-all-order-by-limit")]
    [TestCase("compound-select-orderby.sqltest", "union-order-by-limit")]
    [TestCase("compound-select-orderby.sqltest", "intersect-order-by-limit")]
    [TestCase("compound-select-orderby.sqltest", "except-order-by-limit")]
    [TestCase("compound-select-orderby.sqltest", "union-all-order-by-limit-offset")]
    [TestCase("compound-select-orderby.sqltest", "union-order-by-limit-offset")]
    [TestCase("compound-select-orderby.sqltest", "union-all-then-except-order-by")]
    [TestCase("compound-select-orderby.sqltest", "union-all-order-by-multiple-cols")]
    [TestCase("compound-select-orderby.sqltest", "union-order-by-desc")]
    [TestCase("compound-select-orderby.sqltest", "three-way-union-all-order-by-limit-offset")]
    [TestCase("compound-select-orderby.sqltest", "union-then-intersect-order-by")]
    [TestCase("compound-select-orderby.sqltest", "union-all-many-order-by-desc")]
    [TestCase("create_index.sqltest", "create-unique-index-with-duplicates-3")]
    [TestCase("create_index.sqltest", "create-unique-index-with-duplicates-4")]
    [TestCase("hash-join-three-table-nested.sqltest", "three-table-hash-join-top-level")]
    [TestCase("hash-join-three-table-nested.sqltest", "three-table-hash-join-in-union-all")]
    [TestCase("hash-join-three-table-nested.sqltest", "three-table-hash-join-in-union")]
    [TestCase("hash-join-three-table-nested.sqltest", "three-table-hash-join-in-intersect")]
    [TestCase("hash-join-three-table-nested.sqltest", "three-table-hash-join-in-except")]
    [TestCase("hash-join-three-table-nested.sqltest", "three-table-hash-join-in-cte")]
    [TestCase("hash-join-three-table-nested.sqltest", "three-table-hash-join-wide-cte")]
    [TestCase("hash-join-three-table-nested.sqltest", "three-table-hash-join-wide-union-all")]
    [TestCase("hash-join-three-table-nested.sqltest", "three-table-hash-join-in-view")]
    [TestCase("cte_expressions.sqltest", "cte-union-intersect-combined")]
    [TestCase("cte-union-all-aggregate-literals.sqltest", "cte-union-all-string-literals")]
    [TestCase("cte-union-all-aggregate-literals.sqltest", "cte-union-all-numeric-literals-three-branches")]
    [TestCase("cte-union-all-aggregate-literals.sqltest", "cte-union-all-mixed-agg-functions")]
    [TestCase("cte-union-all-aggregate-literals.sqltest", "cte-union-all-empty-table")]
    [TestCase("cte-union-all-aggregate-literals.sqltest", "union-all-aggregates-no-cte")]
    [TestCase("union_all.sqltest", "union-all-no-in-subquery")]
    [TestCase("union_all.sqltest", "union-all-where-literal")]
    [TestCase("union_all.sqltest", "union-all-with-in-subquery-two-cols")]
    [TestCase("union_all.sqltest", "union-all-with-in-subquery-three-cols")]
    [TestCase("scalar-functions-datetime.sqltest", "date-specific-date")]
    [TestCase("scalar-functions-datetime.sqltest", "date-with-time")]
    [TestCase("scalar-functions-datetime.sqltest", "date-iso8601")]
    [TestCase("scalar-functions-datetime.sqltest", "date-with-milliseconds")]
    [TestCase("scalar-functions-datetime.sqltest", "date-invalid-input")]
    [TestCase("scalar-functions-datetime.sqltest", "date-null-input")]
    [TestCase("scalar-functions-datetime.sqltest", "date-with-timezone-day-change-positive")]
    [TestCase("scalar-functions-datetime.sqltest", "date-with-timezone-day-change-negative")]
    [TestCase("scalar-functions-datetime.sqltest", "date-with-modifier-add-days")]
    [TestCase("scalar-functions-datetime.sqltest", "date-with-modifier-subtract-days")]
    [TestCase("scalar-functions-datetime.sqltest", "date-with-multiple-modifiers")]
    [TestCase("scalar-functions-datetime.sqltest", "julianday-string-with-start-of-month")]
    [TestCase("scalar-functions.sqltest", "uuid-str-empty")]
    [TestCase("scalar-functions.sqltest", "uuid-blob-empty")]
    [TestCase("scalar-functions.sqltest", "uuid7-timestamp-ms-empty")]
    [TestCase("create_index.sqltest", "create-index-quoted-identifiers")]
    [TestCase("create_index.sqltest", "create-unique-index-with-duplicates-5")]
    [TestCase("create_index.sqltest", "create-index-on-shadowed-rowid")]
    [TestCase("create_index.sqltest", "create-index-on-shadowed-rowid-alias-1")]
    [TestCase("create_index.sqltest", "create-index-on-shadowed-rowid-alias-2")]
    [TestCase("drop_index.sqltest", "drop-index-basic-1")]
    [TestCase("drop_index.sqltest", "drop-index-if-exists-1")]
    [TestCase("drop_index.sqltest", "drop-index-after-ops-1")]
    [TestCase("drop_index.sqltest", "drop-explicit-unique-index")]
    [TestCase("drop_index.sqltest", "drop-index-user-unique-data-intact")]
    [TestCase("drop_index.sqltest", "drop-index-user-unique-composite")]
    [TestCase("agg-functions/memory.sqltest", "count-filter-clause")]
    [TestCase("agg-functions/memory.sqltest", "filter-sum")]
    [TestCase("agg-functions/memory.sqltest", "filter-avg")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-distinct-agg")]
    [TestCase("agg-functions/memory.sqltest", "filter-group-concat")]
    [TestCase("agg-functions/memory.sqltest", "select-distinct-aggregate-ungrouped")]
    [TestCase("agg-functions/memory.sqltest", "filter-min-max")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-group-by")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-order-by-on-agg")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-join")]
    [TestCase("agg-functions/memory.sqltest", "filter-all-agg-types-same-table")]
    [TestCase("cross_join.sqltest", "cross-join-order-by-desc-limit")]
    [TestCase("cross_join.sqltest", "cross-join-group-by-right")]
    [TestCase("cross_join.sqltest", "cross-join-aggregate-sum")]
    [TestCase("cross_join.sqltest", "cross-join-null-safe-aggregates")]
    [TestCase("correlated-subquery-hash-join.sqltest", "correlated-subquery-with-join-basic")]
    [TestCase("pragma_query_only.sqltest", "pragma-query-only-float-enable")]
    [TestCase("pragma_query_only.sqltest", "pragma-query-only-float-disable")]
    [TestCase("json/default.sqltest", "json_extract_number")]
    [TestCase("json/default.sqltest", "json_extract_number_type")]
    [TestCase("json/default.sqltest", "json_type_array")]
    [TestCase("json/default.sqltest", "json_type_true")]
    [TestCase("json/default.sqltest", "json_valid_1")]
    [TestCase("json/default.sqltest", "json_valid_6")]
    [TestCase("json/default.sqltest", "json_array_nested")]
    [TestCase("json/default.sqltest", "json_extract_object_1")]
    [TestCase("json/default.sqltest", "json_type_text")]
    [TestCase("json/default.sqltest", "json-patch-basic-1")]
    [TestCase("json/default.sqltest", "json-remove-3")]
    [TestCase("json/default.sqltest", "json_set_multiple_keys")]
    [TestCase("returning.sqltest", "insert-returning-multiple-rows-expressions")]
    [TestCase("returning.sqltest", "update-returning-column-arithmetic")]
    [TestCase("returning.sqltest", "update-returning-multiple-rows")]
    [TestCase("returning.sqltest", "delete-returning-multiple-rows")]
    [TestCase("returning.sqltest", "delete-returning-with-where")]
    [TestCase("composite-index-sort-elim.sqltest", "eq-prefix-order-suffix")]
    [TestCase("composite-index-sort-elim.sqltest", "two-eq-order-last")]
    [TestCase("composite-index-sort-elim.sqltest", "eq-prefix-wrong-direction")]
    [TestCase("composite-index-sort-elim.sqltest", "no-eq-full-scan-order")]
    [TestCase("composite-index-sort-elim.sqltest", "eq-col-in-order-by-desc")]
    [TestCase("composite-index-sort-elim.sqltest", "eq-prefix-order-suffix-limit-offset")]
    [TestCase("composite-index-sort-elim.sqltest", "join-eq-skip-order-outer-only")]
    [TestCase("composite-index-sort-elim.sqltest", "join-duplicate-outer-key")]
    [TestCase("multi_index_dml.sqltest", "update-multi-index-or-stable-write-set")]
    [TestCase("multi_index_dml.sqltest", "update-multi-index-or-updating-indexed-column")]
    [TestCase("multi_index_dml.sqltest", "delete-multi-index-or-safe-materialization")]
    [TestCase("window/memory.sqltest", "window-partition-by-duplicate-columns")]
    [TestCase("window/filter-over.sqltest", "filter-over-full-window-aggregate-matrix")]
    [TestCase("managed-recursive-cte-upstream.sqltest", "upstream-with2-8-2-union-distinct")]
    [TestCase("managed-recursive-cte-upstream.sqltest", "upstream-with1-6-4-linear-counter")]
    public void ExistingSqltestCaseRunsAgainstManagedCore(string fileName, string testName)
    {
        var testCase = SqltestCase.Load(fileName, testName);
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        if (testCase.ExpectedError is { } expectedError)
        {
            var exception = Assert.Throws<EmbeddedSqlException>(() =>
            {
                ExecuteScript(connection, testCase.SetupSql, null);
                ExecuteScript(connection, testCase.Sql, null);
            });
            exception!.Message.Should().MatchRegex(expectedError);
            return;
        }

        var rows = new List<string>();

        ExecuteScript(connection, testCase.SetupSql, null);
        ExecuteScript(connection, testCase.Sql, rows);

        string.Join('\n', rows).Should().Be(testCase.Expected);
    }

    private static void ExecuteScript(EmbeddedConnection connection, string sql, List<string>? rows)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                    if (rows is not null)
                    {
                        var values = Enumerable.Range(0, statement.ColumnCount)
                            .Select(index => FormatValue(statement.GetValue(index)));
                        rows.Add(string.Join('|', values));
                    }
                }
            }
        }
    }

    private static string FormatValue(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Null => string.Empty,
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => FormatReal(value.AsReal()),
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Blob => Convert.ToHexString(value.AsBlob().Span).ToLowerInvariant(),
            _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
        };
    }

    private static string FormatReal(double value)
    {
        var formatted = value.ToString("R", CultureInfo.InvariantCulture);
        return double.IsFinite(value) &&
               formatted.IndexOf('.') < 0 &&
               formatted.IndexOf('E') < 0 &&
               formatted.IndexOf('e') < 0
            ? $"{formatted}.0"
            : formatted;
    }

    private sealed record SqltestCase(string SetupSql, string Sql, string Expected, string? ExpectedError)
    {
        public static SqltestCase Load(string fileName, string testName)
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Conformance", fileName);
            var source = File.ReadAllText(path).ReplaceLineEndings("\n");
            var pattern = $@"(?ms)^test\s+{Regex.Escape(testName)}\s*\{{\s*(?<sql>.*?)^\s*\}}\s*\nexpect(?<error>\s+error)?\s*\{{\s*(?<expected>.*?)^\s*\}}";
            var match = Regex.Match(source, pattern);
            if (!match.Success)
                throw new InvalidOperationException($"Could not find SQL test {testName} in {fileName}.");

            return new SqltestCase(
                LoadSetup(source, fileName, match.Index),
                match.Groups["sql"].Value.Trim(),
                NormalizeExpectedRows(match.Groups["expected"].Value),
                match.Groups["error"].Success
                    ? match.Groups["expected"].Value.Trim()
                    : null);
        }

        private static string LoadSetup(string source, string fileName, int testIndex)
        {
            var precedingTests = Regex.Matches(source[..testIndex], @"(?m)^test\s+");
            var testDirectives = source[(precedingTests.LastOrDefault()?.Index ?? 0)..testIndex];
            var setupMatch = Regex.Matches(testDirectives, @"(?m)^@setup\s+(?<name>\S+)\s*$")
                .LastOrDefault();
            if (setupMatch is null)
                return string.Empty;

            var setupName = setupMatch.Groups["name"].Value;
            var setupPattern = $@"(?ms)^setup\s+{Regex.Escape(setupName)}\s*\{{\s*(?<sql>.*?)^\s*\}}";
            var definition = Regex.Match(source, setupPattern);
            if (!definition.Success)
                throw new InvalidOperationException($"Could not find setup {setupName} in {fileName}.");

            return definition.Groups["sql"].Value.Trim();
        }

        private static string NormalizeExpectedRows(string expected)
            => string.Join('\n', expected.Trim().Split('\n').Select(static row => row.Trim()));
    }
}
