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
    [TestCase("limit.sqltest", "limit-non-integer-datatype-mismatch")]
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
    [TestCase("cte-real-affinity-join.sqltest", "cte-real-affinity-join-original-issue")]
    [TestCase("cte-real-affinity-join.sqltest", "cte-real-affinity-left-join")]
    [TestCase("cte-real-affinity-join.sqltest", "cte-real-affinity-zero")]
    [TestCase("cte-real-affinity-join.sqltest", "cte-real-affinity-negative")]
    [TestCase("cte-real-affinity-join.sqltest", "cte-real-affinity-multiple-columns")]
    [TestCase("cte-real-affinity-join.sqltest", "subquery-real-affinity-join")]
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
    [TestCase("scalar-functions.sqltest", "abs")]
    [TestCase("scalar-functions.sqltest", "abs-negative")]
    [TestCase("scalar-functions.sqltest", "abs-null")]
    [TestCase("scalar-functions.sqltest", "ifnull-1")]
    [TestCase("scalar-functions.sqltest", "ifnull-2")]
    [TestCase("scalar-functions.sqltest", "upper")]
    [TestCase("scalar-functions.sqltest", "upper-number")]
    [TestCase("scalar-functions.sqltest", "upper-char")]
    [TestCase("scalar-functions.sqltest", "upper-null")]
    [TestCase("scalar-functions.sqltest", "lower")]
    [TestCase("scalar-functions.sqltest", "lower-number")]
    [TestCase("scalar-functions.sqltest", "lower-char")]
    [TestCase("scalar-functions.sqltest", "lower-null")]
    [TestCase("scalar-functions.sqltest", "hex")]
    [TestCase("scalar-functions.sqltest", "hex-number")]
    [TestCase("scalar-functions.sqltest", "hex-null")]
    [TestCase("scalar-functions.sqltest", "length-text")]
    [TestCase("scalar-functions.sqltest", "length-text-utf8-chars")]
    [TestCase("scalar-functions.sqltest", "length-integer")]
    [TestCase("scalar-functions.sqltest", "length-float")]
    [TestCase("scalar-functions.sqltest", "length-null")]
    [TestCase("scalar-functions.sqltest", "length-empty-text")]
    [TestCase("scalar-functions.sqltest", "nullif")]
    [TestCase("scalar-functions.sqltest", "nullif-2")]
    [TestCase("scalar-functions.sqltest", "nullif-3")]
    [TestCase("scalar-functions.sqltest", "typeof-null")]
    [TestCase("scalar-functions.sqltest", "typeof-text")]
    [TestCase("scalar-functions.sqltest", "typeof-integer")]
    [TestCase("scalar-functions.sqltest", "typeof-real")]
    [TestCase("scalar-functions.sqltest", "typeof-blob")]
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
    [TestCase("agg-functions/memory.sqltest", "select-distinct-aggregate-ungrouped")]
    [TestCase("agg-functions/memory.sqltest", "ungrouped-agg-empty-table-literal")]
    [TestCase("agg-functions/memory.sqltest", "ungrouped-agg-filtered-rows-literal")]
    [TestCase("agg-functions/memory.sqltest", "ungrouped-agg-multiple-literals")]
    [TestCase("agg-functions/memory.sqltest", "filter-total")]
    [TestCase("agg-functions/memory.sqltest", "filter-multiple-different-conditions")]
    [TestCase("agg-functions/memory.sqltest", "filter-all-rows-filtered-out")]
    [TestCase("agg-functions/memory.sqltest", "filter-on-empty-table")]
    [TestCase("agg-functions/memory.sqltest", "filter-null-in-condition")]
    [TestCase("agg-functions/memory.sqltest", "filter-same-agg-different-filters")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-expression-condition")]
    [TestCase("agg-functions/memory.sqltest", "filter-mixed-agg-and-nonagg")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-case-condition")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-subquery-in-condition")]
    [TestCase("agg-functions/memory.sqltest", "filter-group-by-multiple-groups")]
    [TestCase("agg-functions/memory.sqltest", "filter-boolean-condition")]
    [TestCase("agg-functions/memory.sqltest", "filter-negative-values")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-limit")]
    [TestCase("agg-functions/memory.sqltest", "filter-group-concat-with-filter")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-coalesce")]
    [TestCase("agg-functions/memory.sqltest", "filter-single-row")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-between")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-in-list")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-like")]
    [TestCase("agg-functions/memory.sqltest", "filter-with-null-agg-arg")]
    [TestCase("cross_join.sqltest", "cross-join-order-by-desc-limit")]
    [TestCase("cross_join.sqltest", "cross-join-group-by-right")]
    [TestCase("cross_join.sqltest", "cross-join-aggregate-sum")]
    [TestCase("cross_join.sqltest", "cross-join-null-safe-aggregates")]
    [TestCase("correlated-subquery-hash-join.sqltest", "correlated-subquery-with-join-basic")]
    [TestCase("pragma_query_only.sqltest", "pragma-query-only-float-enable")]
    [TestCase("pragma_query_only.sqltest", "pragma-query-only-float-disable")]
    [TestCase("json/default.sqltest", "json_extract_number")]
    [TestCase("json/default.sqltest", "json_extract_number_type")]
    [TestCase("json/default.sqltest", "json_extract_malformed_json_1")]
    [TestCase("json/default.sqltest", "json_extract_null")]
    [TestCase("json/default.sqltest", "json_type_array")]
    [TestCase("json/default.sqltest", "json_type_true")]
    [TestCase("json/default.sqltest", "json_type_null_arg")]
    [TestCase("json/default.sqltest", "json_valid_1")]
    [TestCase("json/default.sqltest", "json_valid_6")]
    [TestCase("json/default.sqltest", "json_valid_blob_utf8_non_json_word")]
    [TestCase("json/default.sqltest", "json_array_nested")]
    [TestCase("json/default.sqltest", "json_array_length_via_prop")]
    [TestCase("json/default.sqltest", "json_array_length_via_bad_prop")]
    [TestCase("json/default.sqltest", "json_extract_object_1")]
    [TestCase("json/default.sqltest", "json_type_text")]
    [TestCase("json/default.sqltest", "json-patch-basic-1")]
    [TestCase("json/default.sqltest", "json-remove-3")]
    [TestCase("json/default.sqltest", "json_remove_basic_1")]
    [TestCase("json/default.sqltest", "json_quote_string_literal")]
    [TestCase("json/default.sqltest", "json_set_multiple_keys")]
    [TestCase("json/default.sqltest", "json_set_add_array_in_nested_object")]
    [TestCase("json/default.sqltest", "json-subtype-query-materialization-boundaries")]
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
    [TestCase("on_conflict.sqltest", "insert-or-abort-unique")]
    [TestCase("on_conflict.sqltest", "insert-or-ignore-unique")]
    [TestCase("on_conflict.sqltest", "insert-or-ignore-pk")]
    [TestCase("on_conflict.sqltest", "insert-or-ignore-continue-after-skip")]
    [TestCase("on_conflict.sqltest", "insert-or-replace-unique")]
    [TestCase("on_conflict.sqltest", "insert-or-replace-pk")]
    [TestCase("on_conflict.sqltest", "insert-or-replace-multiple-conflicts")]
    [TestCase("insert-cte-compound.sqltest", "insert-cte-union-all-basic")]
    [TestCase("insert-cte-compound.sqltest", "insert-cte-union-distinct")]
    [TestCase("insert-cte-compound.sqltest", "insert-cte-except")]
    [TestCase("insert-cte-compound.sqltest", "insert-cte-intersect")]
    [TestCase("insert-cte-compound.sqltest", "insert-multiple-ctes-union-all")]
    [TestCase("insert-cte-compound.sqltest", "insert-cte-from-table-union-all")]
    [TestCase("foreign_keys.sqltest", "fk-basic-ok")]
    [TestCase("foreign_keys.sqltest", "fk-delete-parent-blocked")]
    [TestCase("window/memory.sqltest", "window-same-window-fn-used-multiple-times")]
    [TestCase("window/memory.sqltest", "window-order-by-position-references-window-fn")]
    [TestCase("rollback.sqltest", "simple-rollback")]
    [TestCase("rollback.sqltest", "simple-rollback-2")]
    [TestCase("rollback.sqltest", "rollback-after-update")]
    [TestCase("rollback.sqltest", "rollback-after-delete")]
    [TestCase("rollback.sqltest", "rollback-mixed-operations")]
    [TestCase("rollback.sqltest", "insert-after-rollback")]
    [TestCase("rollback.sqltest", "schema-change-rollback-version")]
    [TestCase("rollback.sqltest", "schema-version-after-update")]
    [TestCase("rollback.sqltest", "schema-change-rollback-2")]
    [TestCase("default_value.sqltest", "default-value-text")]
    [TestCase("default_value.sqltest", "default-value-integer")]
    [TestCase("default_value.sqltest", "default-value-real")]
    [TestCase("default_value.sqltest", "default-value-null")]
    [TestCase("default_value.sqltest", "default-value-boolean")]
    [TestCase("distinct.sqltest", "distinct-multi-column")]
    [TestCase("distinct.sqltest", "distinct-order-by-nonselect")]
    [TestCase("distinct.sqltest", "distinct-limit-offset")]
    [TestCase("distinct.sqltest", "distinct-agg-with-having")]
    [TestCase("distinct.sqltest", "distinct-subquery")]
    [TestCase("insert.sqltest", "insert_from_select_union_all")]
    [TestCase("insert.sqltest", "insert_from_select_same_table")]
    [TestCase("insert.sqltest", "insert-explicit-rowid")]
    [TestCase("correlated-subquery-in-clause.sqltest", "correlated-in-outer-alias")]
    [TestCase("correlated-subquery-in-clause.sqltest", "correlated-in-outer-alias-with-where")]
    [TestCase("correlated-subquery-in-clause.sqltest", "correlated-in-outer-alias-no-match")]
    [TestCase("delete-correlated-subquery.sqltest", "delete-correlated-in-subquery")]
    [TestCase("not_between.sqltest", "not-between-self-ref-group-by")]
    [TestCase("not_between.sqltest", "not-between-self-ref-no-group-by")]
    [TestCase("in-null-or.sqltest", "in-null-or-truthy")]
    [TestCase("in-null-or.sqltest", "in-null-or-subquery")]
    [TestCase("in-null-or.sqltest", "in-null-or-with-offset")]
    [TestCase("in-null-or.sqltest", "in-subquery-or-in-subquery-indexed")]
    [TestCase("bracket-quoting.sqltest", "bracket-update-set-column")]
    [TestCase("bracket-quoting.sqltest", "bracket-qualified-column-reference")]
    [TestCase("bracket-quoting.sqltest", "bracket-cast-type-name")]
    public void ExistingSqltestCaseRunsAgainstManagedCoreWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("scalar-functions-format.sqltest", "format-mixed-all-types")]
    [TestCase("scalar-functions-format.sqltest", "format-char-from-float")]
    [TestCase("scalar-functions-printf.sqltest", "printf-float-precision-2")]
    [TestCase("scalar-functions-printf.sqltest", "printf-int-width-and-precision")]
    [TestCase("scalar-functions-printf.sqltest", "printf-string-width-and-precision")]
    [TestCase("scalar-functions-printf.sqltest", "printf-format-float")]
    [TestCase("scalar-functions-printf.sqltest", "format-alias")]
    public void ManagedPrintfAndFormatSqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("views.sqltest", "view-basic-filtering")]
    [TestCase("views.sqltest", "view-aggregation-groupby")]
    [TestCase("views.sqltest", "view-with-join")]
    [TestCase("views.sqltest", "view-composition-with-functions")]
    [TestCase("views.sqltest", "view-referencing-view")]
    [TestCase("views.sqltest", "view-case-expression")]
    [TestCase("views.sqltest", "view-drop-and-recreate")]
    [TestCase("views.sqltest", "view-recreate-after-drop")]
    [TestCase("views.sqltest", "view-arithmetic-expression")]
    [TestCase("views.sqltest", "view-with-having")]
    [TestCase("views.sqltest", "view-filter-clause")]
    [TestCase("views.sqltest", "view-self-circle-detection")]
    [TestCase("views.sqltest", "view-if-not-exists-idempotent")]
    [TestCase("views.sqltest", "view-duplicate-without-if-not-exists-errors")]
    [TestCase("views.sqltest", "view-bracket-column-list")]
    [TestCase("views.sqltest", "view-quoted-column-list")]
    public void ManagedViewSqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("null/default.sqltest", "is-null")]
    [TestCase("where/memory.sqltest", "where_alias_precedence")]
    [TestCase("join/natural_join_no_common.sqltest", "natural-join-no-common-columns")]
    [TestCase("join/natural_join_no_common.sqltest", "natural-join-no-common-columns-empty")]
    [TestCase("join/natural_join_no_common.sqltest", "natural-join-no-common-columns-single")]
    [TestCase("negative_zero.sqltest", "negative-zero-literal")]
    [TestCase("negative_zero.sqltest", "negative-zero-comparison-equals")]
    [TestCase("negative_zero.sqltest", "negative-zero-comparison-not-equals")]
    [TestCase("negative_zero.sqltest", "negative-zero-comparison-less-than")]
    [TestCase("negative_zero.sqltest", "negative-zero-comparison-greater-than")]
    [TestCase("negative_zero.sqltest", "negative-zero-in-table")]
    [TestCase("negative_zero.sqltest", "negative-zero-in-table-comparison")]
    [TestCase("negative_zero.sqltest", "negative-zero-order-by")]
    [TestCase("negative_zero.sqltest", "negative-zero-distinct")]
    [TestCase("negative_zero.sqltest", "negative-zero-group-by")]
    [TestCase("literal.sqltest", "numberic-literal-1")]
    [TestCase("literal.sqltest", "numberic-literal-10")]
    [TestCase("literal.sqltest", "invalid-numberic-literal-1")]
    [TestCase("literal.sqltest", "invalid-numberic-literal-2")]
    [TestCase("literal.sqltest", "invalid-numberic-literal-3")]
    [TestCase("literal.sqltest", "invalid-numberic-literal-4")]
    [TestCase("strict.sqltest", "strict-insert-select-any-to-blob-rejects-xfer")]
    [TestCase("strict.sqltest", "strict-not-null-still-enforced")]
    [TestCase("last_insert_rowid.sqltest", "last-insert-rowid-unchanged-after-update")]
    [TestCase("last_insert_rowid.sqltest", "last-insert-rowid-unchanged-after-upsert-update")]
    [TestCase("transactions.sqltest", "basic-tx-1")]
    [TestCase("transactions.sqltest", "basic-tx-2")]
    [TestCase("transactions.sqltest", "basic-tx-3")]
    [TestCase("int64-overflow-seek.sqltest", "int64-max-overflow-ge")]
    [TestCase("int64-overflow-seek.sqltest", "int64-max-overflow-ge-expr")]
    [TestCase("int64-overflow-seek.sqltest", "int64-max-gt")]
    [TestCase("int64-overflow-seek.sqltest", "int64-max-ge")]
    [TestCase("int64-overflow-seek.sqltest", "int64-min-overflow-le")]
    [TestCase("int64-overflow-seek.sqltest", "int64-min-le")]
    [TestCase("int64-overflow-seek.sqltest", "int64-min-lt")]
    [TestCase("issue_5212.sqltest", "basic_alias")]
    [TestCase("issue_5212.sqltest", "multiple_aliases")]
    [TestCase("issue_5212.sqltest", "regression_no_explicit_columns")]
    [TestCase("coalesce/memory.sqltest", "coalesce")]
    [TestCase("coalesce/memory.sqltest", "coalesce-2")]
    [TestCase("coalesce/memory.sqltest", "coalesce-nested")]
    [TestCase("coalesce/memory.sqltest", "coalesce-nested-2")]
    [TestCase("coalesce/memory.sqltest", "coalesce-null")]
    [TestCase("coalesce/memory.sqltest", "coalesce-first")]
    public void ManagedAdditionalSqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("delete.sqltest", "delete-insert-alternate-1")]
    [TestCase("delete.sqltest", "delete-ends-1")]
    [TestCase("delete.sqltest", "delete-reuse-1")]
    [TestCase("delete.sqltest", "delete-in-subquery-1")]
    [TestCase("delete.sqltest", "delete-not-in-subquery-1")]
    [TestCase("delete.sqltest", "delete-in-subquery-empty-1")]
    [TestCase("delete.sqltest", "delete-not-in-subquery-empty-1")]
    [TestCase("delete.sqltest", "delete-in-subquery-multicol-1")]
    [TestCase("delete.sqltest", "delete-scalar-eq-subquery-1")]
    [TestCase("delete.sqltest", "delete-scalar-gt-subquery-1")]
    [TestCase("delete.sqltest", "delete-scalar-lt-subquery-1")]
    [TestCase("delete.sqltest", "delete-exists-empty-1")]
    [TestCase("update.sqltest", "update-mul")]
    [TestCase("update.sqltest", "update-where")]
    [TestCase("update.sqltest", "update-where-2")]
    [TestCase("update.sqltest", "update-all-many")]
    [TestCase("update.sqltest", "update-null")]
    [TestCase("update.sqltest", "update-not-null-1")]
    [TestCase("update.sqltest", "update-not-null-2")]
    [TestCase("update.sqltest", "update-not-null-3")]
    [TestCase("update.sqltest", "update-mixed-types")]
    [TestCase("update.sqltest", "update-self-reference")]
    [TestCase("update.sqltest", "update-self-ref-all")]
    [TestCase("update.sqltest", "update-large-text")]
    [TestCase("update.sqltest", "update-with-null-condition")]
    [TestCase("update.sqltest", "update-to-null")]
    [TestCase("update.sqltest", "update-multiple-columns")]
    [TestCase("update.sqltest", "update-true-expr")]
    public void ManagedDmlSqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("boolean.sqltest", "boolean-not-int-1")]
    [TestCase("boolean.sqltest", "boolean-not-int-2")]
    [TestCase("boolean.sqltest", "boolean-not-int-3")]
    [TestCase("boolean.sqltest", "boolean-not-float-1")]
    [TestCase("boolean.sqltest", "boolean-not-float-2")]
    [TestCase("boolean.sqltest", "boolean-not-float-3")]
    [TestCase("boolean.sqltest", "boolean-not-text")]
    [TestCase("boolean.sqltest", "boolean-not-text-int-1")]
    [TestCase("boolean.sqltest", "boolean-not-text-int-2")]
    [TestCase("boolean.sqltest", "boolean-not-text-float-1")]
    [TestCase("boolean.sqltest", "boolean-not-text-float-2")]
    [TestCase("boolean.sqltest", "boolean-not-null")]
    [TestCase("boolean.sqltest", "boolean-not-empty-blob")]
    [TestCase("boolean.sqltest", "boolean-not-cast-blob")]
    [TestCase("boolean.sqltest", "boolean-not-blob")]
    [TestCase("boolean.sqltest", "boolean-not-blob-2")]
    [TestCase("boolean.sqltest", "boolean-and-blob-blob")]
    [TestCase("boolean.sqltest", "boolean-and-1-blob")]
    [TestCase("boolean.sqltest", "boolean-and-0-blob")]
    [TestCase("boolean.sqltest", "boolean-and-0-1")]
    [TestCase("boolean.sqltest", "boolean-and-1-1")]
    [TestCase("boolean.sqltest", "boolean-and-int-int")]
    [TestCase("boolean.sqltest", "boolean-and-int-float")]
    [TestCase("boolean.sqltest", "boolean-and-int-0_0")]
    [TestCase("boolean.sqltest", "boolean-and-0_0-0_0")]
    [TestCase("boolean.sqltest", "boolean-and-text")]
    [TestCase("boolean.sqltest", "boolean-and-text-int-1")]
    [TestCase("boolean.sqltest", "boolean-and-text-int-2")]
    [TestCase("boolean.sqltest", "boolean-and-text-float-1")]
    [TestCase("boolean.sqltest", "boolean-and-text-float-2")]
    [TestCase("boolean.sqltest", "boolean-and-text-float-3")]
    [TestCase("boolean.sqltest", "boolean-and-text-float-edge")]
    [TestCase("boolean.sqltest", "boolean-and-null-null")]
    [TestCase("boolean.sqltest", "boolean-and-1-null")]
    [TestCase("boolean.sqltest", "boolean-and-1_0-null")]
    [TestCase("boolean.sqltest", "boolean-and-blob-null")]
    [TestCase("boolean.sqltest", "boolean-and-blob2-null")]
    [TestCase("boolean.sqltest", "boolean-and-0-null")]
    [TestCase("boolean.sqltest", "boolean-and-0_0-null")]
    [TestCase("boolean.sqltest", "boolean-and-str0_0-null")]
    [TestCase("boolean.sqltest", "where-string-numeric-prefix-3")]
    [TestCase("boolean.sqltest", "where-string-non-numeric")]
    [TestCase("boolean.sqltest", "where-string-empty")]
    [TestCase("boolean.sqltest", "where-string-numeric")]
    [TestCase("boolean.sqltest", "where-string-zero")]
    public void ManagedBooleanSqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("correlated-subquery-hash-join.sqltest", "correlated-subquery-with-join-missing-rows")]
    [TestCase("correlated-subquery-hash-join.sqltest", "correlated-subquery-reversed-join")]
    [TestCase("correlated-subquery-hash-join.sqltest", "correlated-subquery-with-join-multiple-cols")]
    [TestCase("correlated-subquery-hash-join.sqltest", "correlated-subquery-single-outer-row")]
    [TestCase("drop_index.sqltest", "drop-index-if-exists-2")]
    [TestCase("drop_index.sqltest", "drop-index-user-unique-if-exists")]
    [TestCase("drop_index.sqltest", "drop-index-no-index")]
    [TestCase("delete-correlated-subquery.sqltest", "delete-correlated-empty-table")]
    [TestCase("in-null-or.sqltest", "in-null-or-falsy")]
    [TestCase("in-null-or.sqltest", "not-in-null-or-truthy")]
    [TestCase("in-null-or.sqltest", "in-null-or-column-multiple-rows")]
    [TestCase("in-null-or.sqltest", "in-match-or-null")]
    [TestCase("in-null-or.sqltest", "in-null-column-or")]
    [TestCase("distinct.sqltest", "distinct-select-null")]
    [TestCase("distinct.sqltest", "distinct-count-null")]
    [TestCase("distinct.sqltest", "distinct-order-by")]
    [TestCase("distinct.sqltest", "distinct-collate-count")]
    [TestCase("distinct.sqltest", "distinct-agg-group-by")]
    [TestCase("distinct.sqltest", "distinct-expression")]
    [TestCase("distinct.sqltest", "distinct-text-nocase")]
    [TestCase("distinct.sqltest", "distinct-text-binary")]
    [TestCase("distinct.sqltest", "distinct-offset-applies-after-dedup")]
    [TestCase("distinct.sqltest", "distinct-where")]
    [TestCase("distinct.sqltest", "distinct-multi-null-keys")]
    [TestCase("distinct.sqltest", "distinct-empty")]
    [TestCase("distinct.sqltest", "distinct-order-by-expression")]
    [TestCase("distinct.sqltest", "distinct-agg-simple-count")]
    [TestCase("distinct.sqltest", "distinct-exists-with-offset")]
    public void ManagedAdditionalSqltestConformanceCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("hex-real-compare.sqltest", "compare-hex-real-le-delete-regression")]
    [TestCase("subquery/cte_chain_regression.sqltest", "cte-chain-large-linear-regression")]
    [TestCase("correlated-subquery-nested-exists.sqltest", "correlated-subquery-nested-exists-count-sum")]
    [TestCase("groupby/duplicate-order-by.sqltest", "duplicate_order_by_with_group_by")]
    [TestCase("groupby/duplicate-order-by.sqltest", "duplicate_order_by_desc_with_group_by")]
    [TestCase("groupby/duplicate-order-by.sqltest", "triple_duplicate_order_by_with_group_by")]
    [TestCase("joins/using_clause_case_insensitive.sqltest", "issue-7371-natural-join-quoted-hyphen")]
    [TestCase("joins/using_clause_case_insensitive.sqltest", "issue-7371-natural-join-mixed-case")]
    [TestCase("joins/using_clause_case_insensitive.sqltest", "issue-7371-using-quoted-mixed-case")]
    [TestCase("joins/using_clause_case_insensitive.sqltest", "issue-7371-using-bare-different-case")]
    [TestCase("using-dedup-outer-ref.sqltest", "correlated-subquery-using-dedup-outer-ref")]
    [TestCase("using-dedup-outer-ref.sqltest", "qualified-ref-to-using-hidden-col-in-subquery")]
    [TestCase("joins/left_join_null_index_bug.sqltest", "left-join-null-index-bug")]
    [TestCase("joins/left_join_null_index_bug.sqltest", "left-join-null-index-bug-composite-key")]
    [TestCase("pragma-unknown-no-op.sqltest", "unknown-pragma-recursive-triggers")]
    [TestCase("pragma-unknown-no-op.sqltest", "known-pragma-still-works")]
    public void ManagedNextUpstreamSqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("column_name_case.sqltest", "select_star_headers")]
    [TestCase("column_name_case.sqltest", "case_insensitive_column_reference")]
    [TestCase("column_name_case.sqltest", "insert_with_mixed_case_column_names")]
    [TestCase("column_name_case.sqltest", "update_with_mixed_case_column_names")]
    [TestCase("column_name_case.sqltest", "create_index_on_mixed_case_column")]
    public void ManagedColumnNameCaseSqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("compare.sqltest", "compare-eq-int-int-1")]
    [TestCase("compare.sqltest", "compare-eq-int-int-2")]
    [TestCase("compare.sqltest", "compare-eq-int-null")]
    [TestCase("compare.sqltest", "compare-eq-float-float-1")]
    [TestCase("compare.sqltest", "compare-eq-float-float-2")]
    [TestCase("compare.sqltest", "compare-eq-float-null")]
    [TestCase("compare.sqltest", "compare-eq-text-text-1")]
    [TestCase("compare.sqltest", "compare-eq-text-text-2")]
    [TestCase("compare.sqltest", "compare-eq-text-null")]
    [TestCase("compare.sqltest", "compare-eq-null-int")]
    [TestCase("compare.sqltest", "compare-eq-null-float")]
    [TestCase("compare.sqltest", "compare-eq-null-text")]
    [TestCase("compare.sqltest", "compare-eq-null-null")]
    [TestCase("compare.sqltest", "compare-neq-int-int-1")]
    [TestCase("compare.sqltest", "compare-neq-int-int-2")]
    [TestCase("compare.sqltest", "compare-neq-int-null")]
    [TestCase("compare.sqltest", "compare-neq-float-float-1")]
    [TestCase("compare.sqltest", "compare-neq-float-float-2")]
    [TestCase("compare.sqltest", "compare-neq-float-null")]
    [TestCase("compare.sqltest", "compare-neq-text-text-1")]
    [TestCase("compare.sqltest", "compare-neq-text-text-2")]
    [TestCase("compare.sqltest", "compare-neq-text-null")]
    [TestCase("compare.sqltest", "compare-neq-null-int")]
    [TestCase("compare.sqltest", "compare-neq-null-float")]
    [TestCase("compare.sqltest", "compare-neq-null-text")]
    [TestCase("compare.sqltest", "compare-neq-null-null")]
    public void ManagedComparisonSqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("compare.sqltest", "compare-gt-int-int-1")]
    [TestCase("compare.sqltest", "compare-gt-int-int-2")]
    [TestCase("compare.sqltest", "compare-gt-int-int-3")]
    [TestCase("compare.sqltest", "compare-gt-int-null")]
    [TestCase("compare.sqltest", "compare-gt-float-float-1")]
    [TestCase("compare.sqltest", "compare-gt-float-float-2")]
    [TestCase("compare.sqltest", "compare-gt-float-float-3")]
    [TestCase("compare.sqltest", "compare-gt-float-null")]
    [TestCase("compare.sqltest", "compare-gt-text-text-1")]
    [TestCase("compare.sqltest", "compare-gt-text-text-2")]
    [TestCase("compare.sqltest", "compare-gt-text-text-3")]
    [TestCase("compare.sqltest", "compare-gt-text-null")]
    [TestCase("compare.sqltest", "compare-gt-null-int")]
    [TestCase("compare.sqltest", "compare-gt-null-float")]
    [TestCase("compare.sqltest", "compare-gt-null-text")]
    [TestCase("compare.sqltest", "compare-gt-null-null")]
    [TestCase("compare.sqltest", "compare-gte-int-int-1")]
    [TestCase("compare.sqltest", "compare-gte-int-int-2")]
    [TestCase("compare.sqltest", "compare-gte-int-int-3")]
    [TestCase("compare.sqltest", "compare-gte-int-null")]
    [TestCase("compare.sqltest", "compare-gte-float-float-1")]
    [TestCase("compare.sqltest", "compare-gte-float-float-2")]
    [TestCase("compare.sqltest", "compare-gte-float-float-3")]
    [TestCase("compare.sqltest", "compare-gte-float-null")]
    [TestCase("compare.sqltest", "compare-gte-text-text-1")]
    [TestCase("compare.sqltest", "compare-gte-text-text-2")]
    [TestCase("compare.sqltest", "compare-gte-text-text-3")]
    [TestCase("compare.sqltest", "compare-gte-text-null")]
    [TestCase("compare.sqltest", "compare-gte-null-int")]
    [TestCase("compare.sqltest", "compare-gte-null-float")]
    [TestCase("compare.sqltest", "compare-gte-null-text")]
    [TestCase("compare.sqltest", "compare-gte-null-null")]
    [TestCase("compare.sqltest", "compare-lt-int-int-1")]
    [TestCase("compare.sqltest", "compare-lt-int-int-2")]
    [TestCase("compare.sqltest", "compare-lt-int-int-3")]
    [TestCase("compare.sqltest", "compare-lt-int-null")]
    [TestCase("compare.sqltest", "compare-lt-float-float-1")]
    [TestCase("compare.sqltest", "compare-lt-float-float-2")]
    [TestCase("compare.sqltest", "compare-lt-float-float-3")]
    [TestCase("compare.sqltest", "compare-lt-float-null")]
    [TestCase("compare.sqltest", "compare-lt-text-text-1")]
    [TestCase("compare.sqltest", "compare-lt-text-text-2")]
    [TestCase("compare.sqltest", "compare-lt-text-text-3")]
    [TestCase("compare.sqltest", "compare-lt-text-null")]
    [TestCase("compare.sqltest", "compare-lt-null-int")]
    [TestCase("compare.sqltest", "compare-lt-null-float")]
    [TestCase("compare.sqltest", "compare-lt-null-text")]
    [TestCase("compare.sqltest", "compare-lt-null-null")]
    [TestCase("compare.sqltest", "compare-lte-int-int-1")]
    [TestCase("compare.sqltest", "compare-lte-int-int-2")]
    [TestCase("compare.sqltest", "compare-lte-int-int-3")]
    [TestCase("compare.sqltest", "compare-lte-int-null")]
    [TestCase("compare.sqltest", "compare-lte-float-float-1")]
    [TestCase("compare.sqltest", "compare-lte-float-float-2")]
    [TestCase("compare.sqltest", "compare-lte-float-float-3")]
    [TestCase("compare.sqltest", "compare-lte-float-null")]
    [TestCase("compare.sqltest", "compare-lte-text-text-1")]
    [TestCase("compare.sqltest", "compare-lte-text-text-2")]
    [TestCase("compare.sqltest", "compare-lte-text-text-3")]
    [TestCase("compare.sqltest", "compare-lte-text-null")]
    [TestCase("compare.sqltest", "compare-lte-null-int")]
    [TestCase("compare.sqltest", "compare-lte-null-float")]
    [TestCase("compare.sqltest", "compare-lte-null-text")]
    [TestCase("compare.sqltest", "compare-lte-null-null")]
    [TestCase("compare.sqltest", "compare-is-int-int-1")]
    [TestCase("compare.sqltest", "compare-is-int-int-2")]
    [TestCase("compare.sqltest", "compare-is-float-float-1")]
    [TestCase("compare.sqltest", "compare-is-float-float-2")]
    [TestCase("compare.sqltest", "compare-is-text-text-1")]
    [TestCase("compare.sqltest", "compare-is-text-text-2")]
    [TestCase("compare.sqltest", "compare-is-not-int-int-1")]
    [TestCase("compare.sqltest", "compare-is-not-int-int-2")]
    [TestCase("compare.sqltest", "compare-is-not-float-float-1")]
    [TestCase("compare.sqltest", "compare-is-not-float-float-2")]
    [TestCase("compare.sqltest", "compare-is-not-text-text-1")]
    [TestCase("compare.sqltest", "compare-is-not-text-text-2")]
    [TestCase("compare.sqltest", "compare-int-float-lte-negative-zero")]
    [TestCase("compare.sqltest", "compare-int-float-lt-negative-zero")]
    public void ManagedRemainingComparisonSqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("affinity.sqltest", "affinity")]
    [TestCase("affinity.sqltest", "affinity-join-blob-vs-text-column")]
    [TestCase("affinity.sqltest", "affinity-insert-text-from-integer")]
    [TestCase("affinity.sqltest", "affinity-insert-text-with-index")]
    [TestCase("affinity.sqltest", "affinity-update-text-from-real")]
    [TestCase("affinity.sqltest", "affinity-update-text-with-index")]
    [TestCase("affinity.sqltest", "affinity-upsert-text")]
    [TestCase("affinity.sqltest", "affinity-real-non-numeric-text")]
    [TestCase("affinity.sqltest", "affinity-mixed-columns-insert")]
    [TestCase("affinity.sqltest", "affinity-mixed-columns-update")]
    [TestCase("affinity.sqltest", "affinity-insert-text-from-integer-2")]
    [TestCase("affinity.sqltest", "affinity-insert-text-with-index-2")]
    [TestCase("affinity.sqltest", "affinity-update-text-from-real-2")]
    [TestCase("affinity.sqltest", "affinity-update-text-with-index-2")]
    [TestCase("affinity.sqltest", "affinity-upsert-text-2")]
    [TestCase("affinity.sqltest", "affinity-mixed-columns-insert-2")]
    [TestCase("affinity.sqltest", "affinity-mixed-columns-update-2")]
    [TestCase("affinity.sqltest", "affinity-real-leading-plus-sign")]
    [TestCase("affinity.sqltest", "affinity-real-mixed-signs")]
    [TestCase("affinity.sqltest", "affinity-any-non-strict")]
    [TestCase("affinity.sqltest", "affinity-compound-subquery-text-numeric-no-affinity")]
    [TestCase("affinity.sqltest", "affinity-compound-subquery-order-independent")]
    [TestCase("affinity.sqltest", "affinity-compound-subquery-all-numeric")]
    [TestCase("affinity.sqltest", "affinity-compound-subquery-text-col-plus-numeric-literal")]
    [TestCase("affinity.sqltest", "affinity-compound-subquery-three-arms-mixed")]
    public void ManagedAffinityAndComparisonSqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("collate.sqltest", "collate_unique_constraint")]
    [TestCase("collate.sqltest", "collate_unique_constraint-2")]
    [TestCase("collate.sqltest", "collate_aggregation_default_binary")]
    [TestCase("collate.sqltest", "collate_aggregation_explicit_binary")]
    [TestCase("collate.sqltest", "collate_grouped_aggregation_default_binary")]
    [TestCase("collate.sqltest", "collate_grouped_aggregation_explicit_binary")]
    public void ManagedCollationSqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("drop_table.sqltest", "drop-table-basic-1")]
    [TestCase("drop_table.sqltest", "drop-table-case-insensitive")]
    [TestCase("drop_table.sqltest", "drop-table-if-exists-1")]
    [TestCase("drop_table.sqltest", "drop-table-if-exists-2")]
    [TestCase("drop_table.sqltest", "drop-table-with-index-1")]
    [TestCase("drop_table.sqltest", "drop-table-schema-cleanup-1")]
    [TestCase("drop_table.sqltest", "drop-table-after-ops-1")]
    [TestCase("drop_table.sqltest", "drop-table-fk-disabled-ok")]
    public void ManagedDropTableSqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    [TestCase("foreign_keys.sqltest", "fk-update-child-to-null-ok")]
    [TestCase("foreign_keys.sqltest", "fk-delete-parent-ok-when-no-child")]
    [TestCase("foreign_keys.sqltest", "fk-composite-pk-ok")]
    [TestCase("foreign_keys.sqltest", "fk-composite-pk-missing")]
    [TestCase("foreign_keys.sqltest", "fk-composite-update-child-missing")]
    [TestCase("foreign_keys.sqltest", "fk-composite-unique-ok")]
    [TestCase("foreign_keys.sqltest", "fk-composite-unique-missing")]
    [TestCase("foreign_keys.sqltest", "fk-update-child-noop-ok")]
    [TestCase("foreign_keys.sqltest", "fk-delete-parent-composite-scan")]
    [TestCase("foreign_keys.sqltest", "fk-update-child-to-existing-ok")]
    [TestCase("foreign_keys.sqltest", "fk-composite-pk-delete-violate")]
    [TestCase("foreign_keys.sqltest", "fk-default-parent-pk-composite-ok")]
    [TestCase("foreign_keys.sqltest", "fk-default-parent-pk-composite-missing")]
    [TestCase("foreign_keys.sqltest", "fk-parent-omit-cols-parent-has-pk")]
    [TestCase("foreign_keys.sqltest", "fk-self-ipk-single-ok")]
    [TestCase("foreign_keys.sqltest", "fk-self-ipk-single-mismatch")]
    [TestCase("foreign_keys.sqltest", "fk-rowid-mustbeint-coercion-ok")]
    [TestCase("foreign_keys.sqltest", "fk-rowid-mustbeint-coercion-fail")]
    [TestCase("foreign_keys.sqltest", "fk-parent-unique-index-ok")]
    [TestCase("foreign_keys.sqltest", "fk-parent-unique-index-missing")]
    [TestCase("foreign_keys.sqltest", "fk-child-null-shortcircuit")]
    [TestCase("foreign_keys.sqltest", "fk-self-unique-ok")]
    [TestCase("foreign_keys.sqltest", "fk-self-unique-parent-affinity-does-not-coerce-same-row-child")]
    [TestCase("foreign_keys.sqltest", "fk-self-unique-parent-text-does-not-coerce-same-row-child")]
    [TestCase("foreign_keys.sqltest", "fk-self-unique-same-row-stored-values-match")]
    [TestCase("foreign_keys.sqltest", "fk-self-unique-parent-collation-does-not-satisfy-same-row-child")]
    [TestCase("foreign_keys.sqltest", "fk-self-unique-reference-existing-ok")]
    [TestCase("foreign_keys.sqltest", "fk-self-unique-multirow-no-fastpath")]
    [TestCase("foreign_keys.sqltest", "fk-self-multirow-one-bad")]
    [TestCase("foreign_keys.sqltest", "fk-cross-table-parent-affinity-still-coerces-child")]
    [TestCase("foreign_keys.sqltest", "fk-cross-table-parent-collation-still-matches-child")]
    [TestCase("foreign_keys.sqltest", "fk-deferred-commit-fails")]
    [TestCase("foreign_keys.sqltest", "fk-deferred-fix-before-commit-succeeds")]
    [TestCase("foreign_keys.sqltest", "fk-deferred-rollback-clears")]
    [TestCase("foreign_keys.sqltest", "fk-deferred-self-ref-succeeds")]
    [TestCase("foreign_keys.sqltest", "fk-deferred-cycle-two-tables-ok")]
    [TestCase("foreign_keys.sqltest", "fk-deferred-composite-parent-update-fix")]
    [TestCase("foreign_keys.sqltest", "fk-deferred-update-self-ref-id-and-pid-one-stmt")]
    [TestCase("foreign_keys.sqltest", "fk-deferred-update-self-ref-composite-key-one-stmt")]
    [TestCase("foreign_keys.sqltest", "fk-deferred-delete-parent-then-reinsert-parent-fix")]
    [TestCase("foreign_keys.sqltest", "fk-deferred-tx-multi-children-one-left-fails")]
    [TestCase("foreign_keys.sqltest", "fk-cascade-delete-basic")]
    [TestCase("foreign_keys.sqltest", "fk-cascade-delete-composite")]
    [TestCase("foreign_keys.sqltest", "fk-cascade-delete-recursive")]
    [TestCase("foreign_keys.sqltest", "fk-cascade-delete-self-referential-chain")]
    [TestCase("foreign_keys.sqltest", "fk-cascade-delete-two-table-cycle")]
    [TestCase("foreign_keys.sqltest", "fk-cascade-update-basic")]
    [TestCase("foreign_keys.sqltest", "fk-cascade-update-composite")]
    [TestCase("foreign_keys.sqltest", "fk-cascade-update-two-table-cycle")]
    [TestCase("foreign_keys.sqltest", "fk-setnull-delete-basic")]
    [TestCase("foreign_keys.sqltest", "fk-setnull-delete-composite")]
    [TestCase("foreign_keys.sqltest", "fk-setnull-update-basic")]
    [TestCase("foreign_keys.sqltest", "fk-setdefault-delete-two-table-cycle-null-default")]
    [TestCase("foreign_keys.sqltest", "fk-setdefault-update-two-table-cycle-missing-default-fails")]
    [TestCase("foreign_keys.sqltest", "fk-replace-delete-cascade")]
    [TestCase("foreign_keys.sqltest", "fk-replace-delete-setnull")]
    [TestCase("foreign_keys.sqltest", "fk-replace-delete-setdefault")]
    [TestCase("foreign_keys.sqltest", "fk-replace-delete-cascade-recursive")]
    [TestCase("foreign_keys.sqltest", "fk-replace-delete-cascade-composite")]
    [TestCase("foreign_keys.sqltest", "fk-upsert-update-cascade")]
    [TestCase("foreign_keys.sqltest", "fk-upsert-update-setnull")]
    [TestCase("foreign_keys.sqltest", "fk-upsert-update-setdefault")]
    [TestCase("foreign_keys.sqltest", "fk-upsert-update-cascade-composite")]
    [TestCase("foreign_keys.sqltest", "fk-upsert-update-cascade-recursive")]
    [TestCase("foreign_keys.sqltest", "fk-delete-restrict-fails")]
    [TestCase("foreign_keys.sqltest", "fk-update-restrict-fails")]
    [TestCase("foreign_keys.sqltest", "fk-restrict-null-child-ok")]
    [TestCase("foreign_keys.sqltest", "fk-drop-parent-with-child-references")]
    [TestCase("foreign_keys.sqltest", "fk-drop-parent-cascade-deletes-children")]
    [TestCase("foreign_keys.sqltest", "fk-drop-parent-setnull-sets-children")]
    [TestCase("foreign_keys.sqltest", "fk-drop-parent-setdefault-null-default")]
    [TestCase("foreign_keys.sqltest", "fk-drop-parent-cascade-composite-fk")]
    [TestCase("foreign_keys.sqltest", "fk-drop-parent-ok-with-orphaned-fk")]
    [TestCase("foreign_keys.sqltest", "fk-drop-parent-fail-with-orphaned-and-valid")]
    [TestCase("foreign_keys.sqltest", "fk-drop-parent-mixed-cascade-setnull")]
    [TestCase("foreign_keys.sqltest", "fk-drop-parent-cascade-recursive")]
    public void ManagedForeignKeySqltestCasesRunWithoutNativeFallback(string fileName, string testName)
        => RunSqltestCase(fileName, testName);

    private static void RunSqltestCase(string fileName, string testName)
    {
        var testCase = SqltestCase.Load(fileName, testName);
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        AssertManagedCoreRouteWithoutNativeFallback(database, connection);

        if (testCase.ExpectedError is { } expectedError)
        {
            var exception = Assert.Throws<EmbeddedSqlException>(() =>
            {
                ExecuteScript(connection, testCase.SetupSql, null);
                ExecuteScript(connection, testCase.Sql, null);
            });
            if (!string.IsNullOrEmpty(expectedError))
                exception!.Message.Should().MatchRegex(expectedError);
            return;
        }

        var rows = new List<string>();

        ExecuteScript(connection, testCase.SetupSql, null);
        ExecuteScript(connection, testCase.Sql, rows);

        string.Join('\n', rows).Should().Be(testCase.Expected);
    }

    private static void AssertManagedCoreRouteWithoutNativeFallback(EmbeddedDatabase database, EmbeddedConnection connection)
    {
        Assert.That(database.GetType().Assembly, Is.SameAs(typeof(EmbeddedDatabase).Assembly));
        Assert.That(connection.GetType().Assembly, Is.SameAs(typeof(EmbeddedDatabase).Assembly));
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
            var path = ResolvePath(fileName);
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

        private static string ResolvePath(string fileName)
        {
            var copiedPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Conformance", fileName);
            if (File.Exists(copiedPath))
                return copiedPath;

            foreach (var root in Ancestors(TestContext.CurrentContext.TestDirectory)
                         .Concat(Ancestors(Directory.GetCurrentDirectory())))
            {
                var sourcePath = Path.Combine(
                    root.FullName,
                    "sqlite",
                    "conformance",
                    "sqlite-sqltests",
                    fileName);
                if (File.Exists(sourcePath))
                    return sourcePath;
            }

            throw new FileNotFoundException(
                $"Could not find conformance fixture {fileName} in the test output or repository checkout.",
                fileName);
        }

        private static IEnumerable<DirectoryInfo> Ancestors(string path)
        {
            for (DirectoryInfo? directory = new DirectoryInfo(path);
                 directory is not null;
                 directory = directory.Parent)
            {
                yield return directory;
            }
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
