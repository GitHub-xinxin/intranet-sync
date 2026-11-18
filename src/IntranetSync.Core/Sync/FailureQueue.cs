using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace IntranetSync.Core.Sync
{
    public sealed class FailureItem
    {
        public string Id { get; set; }
        public string ViewName { get; set; }
        public string PrimaryKey { get; set; }
        public string PdfColumn { get; set; }
        public string PdfUrl { get; set; }
        public string Error { get; set; }
        public DateTime FailedAtUtc { get; set; }
        public int Attempts { get; set; }
    }

    /// <summary>
    /// Per-row PDF failure queue — does not block the whole batch.
    /// </summary>
    public sealed class FailureQueue
    {
        private readonly string _path;
        private readonly object _gate = new object();
        private List<FailureItem> _items = new List<FailureItem>();

        public FailureQueue(string dataDirectory)
        {
            Directory.CreateDirectory(dataDirectory);
            _path = Path.Combine(dataDirectory, "pdf-failures.json");
            Load();
        }

        public void Enqueue(FailureItem item)
        {
            if (item == null) return;
            if (string.IsNullOrEmpty(item.Id))
                item.Id = Guid.NewGuid().ToString("N");
            item.FailedAtUtc = DateTime.UtcNow;
            lock (_gate)
            {
                // replace same pk+column；attempts 累加，便于观察间歇性故障
                var prev = _items.FirstOrDefault(x => SameKey(x, item.ViewName, item.PrimaryKey, item.PdfColumn));
                item.Attempts = (prev != null ? prev.Attempts : 0) + 1;
                if (prev != null)
                    _items.Remove(prev);
                _items.Add(item);
                Persist();
            }
        }

        /// <summary>PDF 成功后从失败队列摘掉对应项。</summary>
        public void Remove(string viewName, string primaryKey, string pdfColumn)
        {
            lock (_gate)
            {
                int n = _items.RemoveAll(x => SameKey(x, viewName, primaryKey, pdfColumn));
                if (n > 0)
                    Persist();
            }
        }

        public IList<FailureItem> ListRecent(int take = 100)
        {
            lock (_gate)
            {
                return _items.OrderByDescending(x => x.FailedAtUtc).Take(take).ToList();
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _items.Clear();
                Persist();
            }
        }

        private static bool SameKey(FailureItem x, string viewName, string primaryKey, string pdfColumn)
        {
            return string.Equals(x.ViewName, viewName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.PrimaryKey, primaryKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.PdfColumn, pdfColumn, StringComparison.OrdinalIgnoreCase);
        }

        private void Load()
        {
            if (!File.Exists(_path)) return;
            try
            {
                _items = JsonConvert.DeserializeObject<List<FailureItem>>(File.ReadAllText(_path))
                         ?? new List<FailureItem>();
            }
            catch
            {
                _items = new List<FailureItem>();
            }
        }

        private void Persist()
        {
            File.WriteAllText(_path, JsonConvert.SerializeObject(_items, Formatting.Indented));
        }
    }
}
