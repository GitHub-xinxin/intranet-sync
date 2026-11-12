using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace IntranetSync.Core.Logging
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    public sealed class LogEntry
    {
        public DateTime Utc { get; set; }
        public string Level { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Ring-buffer + file logger. Never writes plaintext secrets (masks common patterns).
    /// </summary>
    public sealed class SyncLogger
    {
        private readonly object _gate = new object();
        private readonly List<LogEntry> _recent = new List<LogEntry>();
        private readonly int _capacity;
        private readonly string _logFile;

        private static readonly Regex[] SecretPatterns = new[]
        {
            new Regex(@"(Password|Pwd)=([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(Bearer\s+)(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(ServiceToken|AccessKeySecret|token)([""':=\s]+)([^""'\s,}]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(AKIA|LTAI)[A-Za-z0-9]{10,}\b", RegexOptions.Compiled)
        };

        public SyncLogger(string dataDirectory, int capacity = 500)
        {
            _capacity = capacity;
            Directory.CreateDirectory(dataDirectory);
            _logFile = Path.Combine(dataDirectory, "sync.log");
        }

        public void Info(string message) { Write(LogLevel.Info, message); }
        public void Warn(string message) { Write(LogLevel.Warn, message); }
        public void Error(string message) { Write(LogLevel.Error, message); }
        public void Debug(string message) { Write(LogLevel.Debug, message); }

        public void Error(string message, Exception ex)
        {
            Write(LogLevel.Error, message + " :: " + Mask(ex.ToString()));
        }

        public IList<LogEntry> GetRecent(int take = 100)
        {
            lock (_gate)
            {
                return _recent.Skip(Math.Max(0, _recent.Count - take)).ToList();
            }
        }

        private void Write(LogLevel level, string message)
        {
            string safe = Mask(message ?? "");
            var entry = new LogEntry
            {
                Utc = DateTime.UtcNow,
                Level = level.ToString(),
                Message = safe
            };

            lock (_gate)
            {
                _recent.Add(entry);
                while (_recent.Count > _capacity)
                    _recent.RemoveAt(0);

                try
                {
                    File.AppendAllText(_logFile,
                        string.Format("{0:o} [{1}] {2}{3}", entry.Utc, entry.Level, entry.Message, Environment.NewLine));
                }
                catch
                {
                    // ignore disk errors in logger
                }
            }
        }

        public static string Mask(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            string s = input;
            foreach (var re in SecretPatterns)
            {
                s = re.Replace(s, m =>
                {
                    if (m.Groups.Count >= 3)
                        return m.Groups[1].Value + (m.Groups.Count > 3 ? m.Groups[2].Value : "=") + "****";
                    return "****";
                });
            }
            return s;
        }
    }
}
