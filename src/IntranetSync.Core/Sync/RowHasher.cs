using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace IntranetSync.Core.Sync
{
    /// <summary>
    /// Fallback when no incremental column: hash PK + row field values (incl. PDF URLs).
    /// </summary>
    public static class RowHasher
    {
        public static string Compute(string primaryKey, JObject fields)
        {
            var sb = new StringBuilder();
            sb.Append(primaryKey ?? "").Append('\n');
            if (fields != null)
            {
                foreach (var prop in fields.Properties().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append(prop.Name).Append('=');
                    sb.Append(prop.Value == null || prop.Value.Type == JTokenType.Null
                        ? ""
                        : prop.Value.ToString());
                    sb.Append('\n');
                }
            }

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// Tracks last-seen row hashes for hash-mode sync (local SQLite-less JSON set).
    /// </summary>
    public sealed class RowHashCursor
    {
        private readonly Dictionary<string, string> _hashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();
        private readonly string _path;

        public RowHashCursor(string dataDirectory)
        {
            System.IO.Directory.CreateDirectory(dataDirectory);
            _path = System.IO.Path.Combine(dataDirectory, "row-hashes.json");
            Load();
        }

        public bool HasChanged(string pk, string hash)
        {
            lock (_gate)
            {
                string old;
                if (!_hashes.TryGetValue(pk, out old)) return true;
                return !string.Equals(old, hash, StringComparison.OrdinalIgnoreCase);
            }
        }

        public void Commit(string pk, string hash)
        {
            lock (_gate)
            {
                _hashes[pk] = hash;
            }
        }

        public void Persist()
        {
            lock (_gate)
            {
                System.IO.File.WriteAllText(_path,
                    Newtonsoft.Json.JsonConvert.SerializeObject(_hashes, Newtonsoft.Json.Formatting.Indented));
            }
        }

        private void Load()
        {
            if (!System.IO.File.Exists(_path)) return;
            try
            {
                var map = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    System.IO.File.ReadAllText(_path));
                if (map == null) return;
                foreach (var kv in map)
                    _hashes[kv.Key] = kv.Value;
            }
            catch { /* reset on corrupt */ }
        }
    }
}
