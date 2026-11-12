using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace IntranetSync.Core.Pdf
{
    public sealed class PdfMapEntry
    {
        public string Sha256 { get; set; }
        public string OssKey { get; set; }
        public string Url { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    /// <summary>
    /// Local durable map: SHA256 → OSS key / cloud URL (dedupe across sync runs).
    /// </summary>
    public sealed class PdfHashMap
    {
        private readonly string _path;
        private readonly object _gate = new object();
        private Dictionary<string, PdfMapEntry> _map = new Dictionary<string, PdfMapEntry>(StringComparer.OrdinalIgnoreCase);

        public PdfHashMap(string dataDirectory)
        {
            Directory.CreateDirectory(dataDirectory);
            _path = Path.Combine(dataDirectory, "pdf-hash-map.json");
            Load();
        }

        public bool TryGet(string sha256, out PdfMapEntry entry)
        {
            lock (_gate)
            {
                return _map.TryGetValue(sha256, out entry);
            }
        }

        public void Put(string sha256, string ossKey, string url)
        {
            lock (_gate)
            {
                _map[sha256] = new PdfMapEntry
                {
                    Sha256 = sha256,
                    OssKey = ossKey,
                    Url = url,
                    UpdatedUtc = DateTime.UtcNow
                };
                Persist();
            }
        }

        private void Load()
        {
            if (!File.Exists(_path)) return;
            try
            {
                var list = JsonConvert.DeserializeObject<List<PdfMapEntry>>(File.ReadAllText(_path))
                           ?? new List<PdfMapEntry>();
                _map = new Dictionary<string, PdfMapEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in list)
                {
                    if (!string.IsNullOrEmpty(e.Sha256))
                        _map[e.Sha256] = e;
                }
            }
            catch
            {
                _map = new Dictionary<string, PdfMapEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Persist()
        {
            var list = new List<PdfMapEntry>(_map.Values);
            File.WriteAllText(_path, JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }
}
