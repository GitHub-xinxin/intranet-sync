using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using IntranetSync.Core.Config;
using IntranetSync.Core.Logging;
using Newtonsoft.Json.Linq;

namespace IntranetSync.Core.Sync
{
    public sealed class ViewRow
    {
        public string PrimaryKey { get; set; }
        public string Watermark { get; set; }
        public JObject Fields { get; set; }
    }

    /// <summary>
    /// Read-only paged SELECT against a SQL Server view.
    /// </summary>
    public sealed class SqlViewReader
    {
        private readonly SyncLogger _log;

        public SqlViewReader(SyncLogger log)
        {
            _log = log;
        }

        public async Task<string> TestConnectionAsync(SqlSettings sql, CancellationToken ct)
        {
            using (var conn = new SqlConnection(sql.BuildConnectionString()))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand("SELECT 1", conn))
                {
                    var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    return "OK; SELECT 1 => " + result + "; " + sql.BuildMaskedConnectionString();
                }
            }
        }

        public async Task<IList<ViewRow>> ReadPageAsync(
            SqlSettings sql,
            ViewSettings view,
            string watermark,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(view.ViewName))
                throw new InvalidOperationException("View name is required");

            bool useIncremental = !string.IsNullOrWhiteSpace(view.IncrementalColumn);
            bool hasPk = !string.IsNullOrWhiteSpace(view.PrimaryKeyColumn);
            if (!useIncremental && !hasPk)
                throw new InvalidOperationException("未配置唯一标识列时，必须配置增量列（如 updated_at），才能按更新时间推送全部记录");

            int pageSize = view.PageSize > 0 ? view.PageSize : 300;

            // Bracket identifiers loosely — view/column names come from admin config, not end users.
            string viewName = Bracket(view.ViewName);

            string sqlText;
            if (useIncremental)
            {
                string inc = Bracket(view.IncrementalColumn);
                sqlText = string.Format(
                    "SELECT TOP ({0}) * FROM {1} WHERE {2} > @wm ORDER BY {2} ASC",
                    pageSize, viewName, inc);
            }
            else
            {
                // Hash-mode cursor pages by unique column.
                string pkCol = Bracket(view.PrimaryKeyColumn);
                sqlText = string.Format(
                    "SELECT TOP ({0}) * FROM {1} WHERE {2} > @wm ORDER BY {2} ASC",
                    pageSize, viewName, pkCol);
            }

            var rows = new List<ViewRow>();
            using (var conn = new SqlConnection(sql.BuildConnectionString()))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand(sqlText, conn))
                {
                    object wmValue;
                    // For typed comparisons, try parse datetime / long
                    if (!string.IsNullOrEmpty(watermark))
                    {
                        DateTime dt;
                        long lng;
                        if (TryParseWatermarkDate(watermark, out dt))
                            wmValue = dt;
                        else if (long.TryParse(watermark, out lng))
                            wmValue = lng;
                        else
                            wmValue = watermark;
                    }
                    else if (useIncremental)
                    {
                        // empty watermark → floor below any SQL DATETIME / DATETIME2 value.
                        // Do NOT use DateTime.MinValue: SqlParameter defaults to SqlDbType.DateTime
                        // which rejects < 1753-01-01 ("SqlDateTime overflow").
                        wmValue = SqlDateTimeFloor;
                    }
                    else
                    {
                        wmValue = "";
                    }

                    AddWatermarkParameter(cmd, wmValue);
                    _log.Debug("SQL page query view=" + view.ViewName + " wm=" + (watermark ?? ""));

                    using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            var fields = new JObject();
                            string pk = null;
                            string wm = null;
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                string name = reader.GetName(i);
                                object val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                                JToken token = val == null ? JValue.CreateNull() : JToken.FromObject(val);
                                fields[name] = token;

                                if (hasPk &&
                                    string.Equals(name, view.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase))
                                    pk = val == null ? null : Convert.ToString(val);
                                if (useIncremental &&
                                    string.Equals(name, view.IncrementalColumn, StringComparison.OrdinalIgnoreCase))
                                    wm = FormatWatermarkValue(val);
                            }

                            if (!useIncremental)
                                wm = pk;

                            // 无唯一列时：仅按增量时间推送，pk 可为空（云端按追加/自行处理）
                            rows.Add(new ViewRow
                            {
                                PrimaryKey = pk,
                                Watermark = wm,
                                Fields = fields
                            });
                        }
                    }
                }
            }

            return rows;
        }

        /// <summary>
        /// SQL Server DATETIME lower bound (also safe for DATETIME2 comparisons).
        /// </summary>
        private static readonly DateTime SqlDateTimeFloor = new DateTime(1753, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        private static void AddWatermarkParameter(SqlCommand cmd, object wmValue)
        {
            if (wmValue is DateTime)
            {
                // Explicit DateTime2 avoids SqlDbType.DateTime range validation on the client.
                var p = new SqlParameter("@wm", SqlDbType.DateTime2)
                {
                    Value = ClampToSqlDateTimeRange((DateTime)wmValue)
                };
                cmd.Parameters.Add(p);
                return;
            }

            cmd.Parameters.Add(new SqlParameter("@wm", wmValue ?? (object)DBNull.Value));
        }

        private static bool TryParseWatermarkDate(string watermark, out DateTime dt)
        {
            if (DateTime.TryParse(
                    watermark,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out dt))
                return true;

            return DateTime.TryParse(watermark, out dt);
        }

        private static DateTime ClampToSqlDateTimeRange(DateTime dt)
        {
            // Keep values usable against both DATETIME and DATETIME2 columns.
            return dt < SqlDateTimeFloor ? SqlDateTimeFloor : dt;
        }

        private static string FormatWatermarkValue(object val)
        {
            if (val == null) return null;
            if (val is DateTime)
                return ((DateTime)val).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            if (val is DateTimeOffset)
                return ((DateTimeOffset)val).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            return Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Bracket(string ident)
        {
            if (string.IsNullOrWhiteSpace(ident)) return "[]";
            // allow schema.view
            if (ident.Contains("."))
            {
                var parts = ident.Split('.');
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = "[" + parts[i].Replace("]", "]]") + "]";
                return string.Join(".", parts);
            }
            return "[" + ident.Replace("]", "]]") + "]";
        }
    }
}
