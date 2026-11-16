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

        /// <summary>
        /// Incremental page: updated_at &gt; wm, or (updated_at = wm AND pk &gt; lastPk) when PK is configured.
        /// </summary>
        public Task<IList<ViewRow>> ReadPageAsync(
            SqlSettings sql,
            ViewSettings view,
            string watermark,
            CancellationToken ct)
        {
            return ReadPageAsync(sql, view, watermark, null, ct);
        }

        public async Task<IList<ViewRow>> ReadPageAsync(
            SqlSettings sql,
            ViewSettings view,
            string watermark,
            string lastPrimaryKey,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(view.ViewName))
                throw new InvalidOperationException("View name is required");

            bool useIncremental = !string.IsNullOrWhiteSpace(view.IncrementalColumn);
            bool hasPk = !string.IsNullOrWhiteSpace(view.PrimaryKeyColumn);
            if (!useIncremental && !hasPk)
                throw new InvalidOperationException("未配置唯一标识列时，必须配置增量列（如 updated_at），才能按更新时间推送全部记录");

            int pageSize = view.PageSize > 0 ? view.PageSize : 300;
            string viewName = Bracket(view.ViewName);

            string sqlText;
            bool composite = useIncremental && hasPk;
            if (composite)
            {
                string inc = Bracket(view.IncrementalColumn);
                string pkCol = Bracket(view.PrimaryKeyColumn);
                sqlText = string.Format(
                    "SELECT TOP ({0}) * FROM {1} WHERE ({2} > @wm) OR ({2} = @wm AND {3} > @pk) ORDER BY {2} ASC, {3} ASC",
                    pageSize, viewName, inc, pkCol);
            }
            else if (useIncremental)
            {
                string inc = Bracket(view.IncrementalColumn);
                sqlText = string.Format(
                    "SELECT TOP ({0}) * FROM {1} WHERE {2} > @wm ORDER BY {2} ASC",
                    pageSize, viewName, inc);
            }
            else
            {
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
                    if (!useIncremental)
                    {
                        // Hash-mode: watermark parameter is the last PK cursor.
                        wmValue = watermark ?? "";
                    }
                    else if (!string.IsNullOrEmpty(watermark))
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
                    else
                    {
                        wmValue = SqlDateTimeFloor;
                    }

                    AddWatermarkParameter(cmd, wmValue);
                    if (composite)
                    {
                        cmd.Parameters.Add(new SqlParameter("@pk", lastPrimaryKey ?? ""));
                    }

                    _log.Debug("SQL page query view=" + view.ViewName
                        + " wm=" + (watermark ?? "")
                        + (composite ? (" pk=" + (lastPrimaryKey ?? "")) : ""));

                    using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            rows.Add(MapRow(reader, view, useIncremental, hasPk));
                        }
                    }
                }
            }

            return rows;
        }

        public async Task<int> CountAsync(SqlSettings sql, ViewSettings view, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(view.ViewName))
                throw new InvalidOperationException("View name is required");

            string sqlText = "SELECT COUNT_BIG(*) FROM " + Bracket(view.ViewName);
            using (var conn = new SqlConnection(sql.BuildConnectionString()))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand(sqlText, conn))
                {
                    object scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    return Convert.ToInt32(scalar);
                }
            }
        }

        /// <summary>
        /// Cover every physical row. ROW_NUMBER paging does not drop duplicate keys the way
        /// "pk &gt; @last" does when a page boundary splits two rows with the same person_id.
        /// </summary>
        public async Task<IList<ViewRow>> ReadCoveringPageAsync(
            SqlSettings sql,
            ViewSettings view,
            int skip,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(view.ViewName))
                throw new InvalidOperationException("View name is required");

            bool useIncremental = !string.IsNullOrWhiteSpace(view.IncrementalColumn);
            bool hasPk = !string.IsNullOrWhiteSpace(view.PrimaryKeyColumn);
            if (!useIncremental && !hasPk)
                throw new InvalidOperationException("Covering scan requires a primary key or incremental column");

            int pageSize = view.PageSize > 0 ? view.PageSize : 300;
            if (skip < 0) skip = 0;

            string orderBy;
            if (useIncremental && hasPk)
                orderBy = Bracket(view.IncrementalColumn) + " ASC, " + Bracket(view.PrimaryKeyColumn) + " ASC";
            else if (useIncremental)
                orderBy = Bracket(view.IncrementalColumn) + " ASC";
            else
                orderBy = Bracket(view.PrimaryKeyColumn) + " ASC";

            string sqlText = string.Format(
                "SELECT * FROM (SELECT *, ROW_NUMBER() OVER (ORDER BY {0}) AS __sync_rn FROM {1}) AS __sync_page WHERE __sync_rn > @skip AND __sync_rn <= @end ORDER BY __sync_rn",
                orderBy, Bracket(view.ViewName));

            var rows = new List<ViewRow>();
            using (var conn = new SqlConnection(sql.BuildConnectionString()))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand(sqlText, conn))
                {
                    cmd.Parameters.Add(new SqlParameter("@skip", skip));
                    cmd.Parameters.Add(new SqlParameter("@end", skip + pageSize));
                    _log.Debug("SQL cover page view=" + view.ViewName + " skip=" + skip + " take=" + pageSize);

                    using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            rows.Add(MapRow(reader, view, useIncremental, hasPk));
                        }
                    }
                }
            }

            return rows;
        }

        private static ViewRow MapRow(SqlDataReader reader, ViewSettings view, bool useIncremental, bool hasPk)
        {
            var fields = new JObject();
            string pk = null;
            string wm = null;
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                if (string.Equals(name, "__sync_rn", StringComparison.OrdinalIgnoreCase))
                    continue;
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

            return new ViewRow
            {
                PrimaryKey = pk,
                Watermark = wm,
                Fields = fields
            };
        }

        /// <summary>
        /// SQL Server DATETIME lower bound (also safe for DATETIME2 comparisons).
        /// </summary>
        private static readonly DateTime SqlDateTimeFloor = new DateTime(1753, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        private static void AddWatermarkParameter(SqlCommand cmd, object wmValue)
        {
            if (wmValue is DateTime)
            {
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
