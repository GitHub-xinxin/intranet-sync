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
            if (string.IsNullOrWhiteSpace(view.PrimaryKeyColumn))
                throw new InvalidOperationException("Primary key column is required");

            bool useIncremental = !string.IsNullOrWhiteSpace(view.IncrementalColumn);
            int pageSize = view.PageSize > 0 ? view.PageSize : 300;

            // Bracket identifiers loosely — view/column names come from admin config, not end users.
            string viewName = Bracket(view.ViewName);
            string pkCol = Bracket(view.PrimaryKeyColumn);

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
                // Full page by PK for hash-mode; caller filters by row hash.
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
                    object wmValue = string.IsNullOrEmpty(watermark) ? (object)DBNull.Value : watermark;
                    // For typed comparisons, try parse datetime / long
                    if (!string.IsNullOrEmpty(watermark))
                    {
                        DateTime dt;
                        long lng;
                        if (DateTime.TryParse(watermark, out dt))
                            wmValue = dt;
                        else if (long.TryParse(watermark, out lng))
                            wmValue = lng;
                    }
                    else if (useIncremental)
                    {
                        // empty watermark → use minimum
                        wmValue = DateTime.MinValue;
                    }
                    else
                    {
                        wmValue = "";
                    }

                    cmd.Parameters.Add(new SqlParameter("@wm", wmValue ?? DBNull.Value));
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

                                if (string.Equals(name, view.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase))
                                    pk = val == null ? null : Convert.ToString(val);
                                if (useIncremental &&
                                    string.Equals(name, view.IncrementalColumn, StringComparison.OrdinalIgnoreCase))
                                    wm = val == null ? null : Convert.ToString(val);
                            }

                            if (!useIncremental)
                                wm = pk;

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
