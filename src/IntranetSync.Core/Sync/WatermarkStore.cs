using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace IntranetSync.Core.Sync
{
    public sealed class WatermarkRecord
    {
        public string ViewName { get; set; }
        public string LastWatermark { get; set; }
        public DateTime? LastSuccessAt { get; set; }
        public string LastRowHashModeCursor { get; set; }
    }

    /// <summary>
    /// Local durable watermark: view_name / last_watermark / last_success_at
    /// </summary>
    public sealed class WatermarkStore
    {
        private readonly string _path;
        private readonly object _gate = new object();
        private Dictionary<string, WatermarkRecord> _map =
            new Dictionary<string, WatermarkRecord>(StringComparer.OrdinalIgnoreCase);

        public WatermarkStore(string dataDirectory)
        {
            Directory.CreateDirectory(dataDirectory);
            _path = Path.Combine(dataDirectory, "watermarks.json");
            Load();
        }

        public WatermarkRecord Get(string viewName)
        {
            lock (_gate)
            {
                WatermarkRecord r;
                if (_map.TryGetValue(viewName ?? "", out r))
                    return r;
                return new WatermarkRecord { ViewName = viewName, LastWatermark = "" };
            }
        }

        public void Advance(string viewName, string watermark)
        {
            lock (_gate)
            {
                _map[viewName] = new WatermarkRecord
                {
                    ViewName = viewName,
                    LastWatermark = watermark ?? "",
                    LastSuccessAt = DateTime.UtcNow
                };
                Persist();
            }
        }

        private void Load()
        {
            if (!File.Exists(_path)) return;
            try
            {
                var list = JsonConvert.DeserializeObject<List<WatermarkRecord>>(File.ReadAllText(_path))
                           ?? new List<WatermarkRecord>();
                _map = new Dictionary<string, WatermarkRecord>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in list)
                {
                    if (!string.IsNullOrEmpty(r.ViewName))
                        _map[r.ViewName] = r;
                }
            }
            catch
            {
                _map = new Dictionary<string, WatermarkRecord>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Persist()
        {
            File.WriteAllText(_path, JsonConvert.SerializeObject(new List<WatermarkRecord>(_map.Values), Formatting.Indented));
        }
    }
}
