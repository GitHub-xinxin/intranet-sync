using System;
using System.Threading;
using System.Threading.Tasks;
using IntranetSync.Core.Config;
using IntranetSync.Core.Logging;

namespace IntranetSync.Core.Sync
{
    /// <summary>
    /// Background timer host: start/stop sync loop at configured interval.
    /// </summary>
    public sealed class SyncHost : IDisposable
    {
        private readonly ConfigStore _store;
        private readonly SyncLogger _log;
        private readonly SyncEngine _engine;
        private CancellationTokenSource _cts;
        private Task _loop;
        private readonly object _gate = new object();
        private SyncRunResult _lastResult;
        private DateTime? _lastRunUtc;
        private bool _running;

        public SyncHost(ConfigStore store, SyncLogger log)
        {
            _store = store;
            _log = log;
            _engine = new SyncEngine(store, log);
        }

        public SyncEngine Engine { get { return _engine; } }
        public bool IsRunning { get { return _running; } }
        public SyncRunResult LastResult { get { return _lastResult; } }
        public DateTime? LastRunUtc { get { return _lastRunUtc; } }

        public void Start()
        {
            lock (_gate)
            {
                if (_running) return;
                _cts = new CancellationTokenSource();
                _running = true;
                _loop = Task.Run(() => LoopAsync(_cts.Token));
                _log.Info("Sync host started");
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!_running) return;
                try { _cts.Cancel(); } catch { }
                try
                {
                    if (_loop != null)
                        _loop.Wait(TimeSpan.FromSeconds(15));
                }
                catch { }
                _running = false;
                _log.Info("Sync host stopped");
            }
        }

        public async Task<SyncRunResult> RunOnceAsync()
        {
            var cfg = _store.LoadOrDefault();
            using (var roundCts = CreateRoundCts(CancellationToken.None, cfg.Sync.MaxRoundSeconds))
            {
                try
                {
                    var result = await _engine.RunOnceAsync(roundCts.Token).ConfigureAwait(false);
                    _lastResult = result;
                    _lastRunUtc = DateTime.UtcNow;
                    return result;
                }
                catch (OperationCanceledException)
                {
                    var timedOut = new SyncRunResult
                    {
                        Success = false,
                        Message = "Round canceled or timed out (MaxRoundSeconds=" + cfg.Sync.MaxRoundSeconds + ")"
                    };
                    _log.Error(timedOut.Message);
                    _lastResult = timedOut;
                    _lastRunUtc = DateTime.UtcNow;
                    return timedOut;
                }
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                int interval = 180;
                try
                {
                    var cfg = _store.LoadOrDefault();
                    interval = cfg.Sync.IntervalSeconds > 0 ? cfg.Sync.IntervalSeconds : 180;
                    if (cfg.Sync.Enabled)
                    {
                        using (var roundCts = CreateRoundCts(ct, cfg.Sync.MaxRoundSeconds))
                        {
                            try
                            {
                                _lastResult = await _engine.RunOnceAsync(roundCts.Token).ConfigureAwait(false);
                                _lastRunUtc = DateTime.UtcNow;
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                // Round-level timeout / cloud hang cancel — keep host loop alive.
                                _log.Error("Sync round timed out or canceled; will retry next interval (MaxRoundSeconds=" +
                                           cfg.Sync.MaxRoundSeconds + ")");
                                _lastResult = new SyncRunResult
                                {
                                    Success = false,
                                    Message = "Round timed out; retry next interval"
                                };
                                _lastRunUtc = DateTime.UtcNow;
                            }
                        }
                    }
                    else
                    {
                        _log.Debug("Sync disabled; sleeping");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Includes cloud TimeoutException after retries — watermark not advanced, next round retries.
                    _log.Error("Sync loop error", ex);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static CancellationTokenSource CreateRoundCts(CancellationToken parent, int maxRoundSeconds)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            if (maxRoundSeconds > 0)
                cts.CancelAfter(TimeSpan.FromSeconds(maxRoundSeconds));
            return cts;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
