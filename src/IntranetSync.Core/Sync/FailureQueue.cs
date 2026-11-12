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
                // replace same pk+column
                _items.RemoveAll(x =>
                    string.Equals(x.ViewName, item.ViewName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.PrimaryKey, item.PrimaryKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.PdfColumn, item.PdfColumn, StringComparison.OrdinalIgnoreCase));
                _items.Add(item);
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
