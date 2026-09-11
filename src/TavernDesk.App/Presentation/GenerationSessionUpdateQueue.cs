using System.Windows.Threading;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.App.Presentation;

/// <summary>Coalesces display snapshots before they enter the WPF dispatcher.</summary>
public sealed class GenerationSessionUpdateQueue : IDisposable
{
    private readonly object _sync = new();
    private readonly Dispatcher? _dispatcher;
    private readonly DispatcherTimer? _timer;
    private readonly Action<ConversationGenerationSession> _apply;
    private IConversationGenerationSessionStore? _store;
    private readonly Dictionary<(string Conversation, string? Operation, string? Message),
        (long Sequence, ConversationGenerationSessionUpdate Session)> _pending = new();
    private readonly Dictionary<string, ConversationGenerationSessionUpdate> _latest = new();
    private long _sequence;
    private bool _timerActive;
    private bool _timerStartQueued;
    private bool _urgentQueued;
    private bool _disposed;

    public GenerationSessionUpdateQueue(Dispatcher? dispatcher,
        Action<ConversationGenerationSession> apply, TimeSpan? interval = null)
    {
        _dispatcher = dispatcher;
        _apply = apply;
        if (dispatcher is not null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = interval ?? TimeSpan.FromMilliseconds(120)
            };
            _timer.Tick += OnTick;
        }
    }

    public GenerationSessionUpdateQueue(IConversationGenerationSessionStore store,
        Dispatcher? dispatcher, Action<ConversationGenerationSession> apply, TimeSpan? interval = null)
        : this(dispatcher, apply, interval)
    {
        _store = store;
        if (store is IConversationGenerationSessionUpdates updates)
            updates.SessionUpdated += OnSessionUpdated;
        else
            store.SessionChanged += OnSessionChanged;
    }

    private void OnSessionUpdated(object? sender, ConversationGenerationSessionUpdate update) => Post(update);
    private void OnSessionChanged(object? sender, ConversationGenerationSession session) => Post(session);

    public void Post(ConversationGenerationSession session) => Post(new ConversationGenerationSessionUpdate(session));

    public void Post(ConversationGenerationSessionUpdate session)
    {
        if (_dispatcher is null)
        {
            if (!_disposed) _apply(session.GetSnapshot());
            return;
        }
        if (_dispatcher.HasShutdownStarted) return;
        bool urgent;
        lock (_sync)
        {
            if (_disposed) return;
            _latest.TryGetValue(session.ConversationId, out var previous);
            urgent = !session.IsBusy || !session.SawContent || session.IsThinking
                || session.HasCompletion
                || previous is null || !previous.SawContent
                || previous.OperationId != session.OperationId || previous.MessageId != session.MessageId;
            if (session.IsBusy) _latest[session.ConversationId] = session;
            else _latest.Remove(session.ConversationId);
            _pending[(session.ConversationId, session.OperationId, session.MessageId)] = (++_sequence, session);
            if (urgent)
            {
                if (_urgentQueued) return;
                _urgentQueued = true;
            }
            else
            {
                if (_urgentQueued || _timerActive || _timerStartQueued) return;
                _timerStartQueued = true;
            }
        }
        _dispatcher.BeginInvoke(DispatcherPriority.Background,
            urgent ? new Action(Drain) : new Action(StartTimer));
    }

    private void StartTimer()
    {
        lock (_sync)
        {
            _timerStartQueued = false;
            if (_disposed || _pending.Count == 0 || _timerActive) return;
            _timerActive = true;
            _timer!.Start();
        }
    }

    private void OnTick(object? sender, EventArgs args) => Drain();

    private void Drain()
    {
        ConversationGenerationSessionUpdate[] snapshots;
        lock (_sync)
        {
            _timer!.Stop();
            _timerActive = false;
            _urgentQueued = false;
            if (_disposed) return;
            snapshots = _pending.Values.OrderBy(value => value.Sequence).Select(value => value.Session).ToArray();
            _pending.Clear();
        }
        foreach (var snapshot in snapshots)
        {
            if (_disposed) return;
            _apply(snapshot.GetSnapshot());
        }
    }

    public void Dispose()
    {
        if (_store is IConversationGenerationSessionUpdates updates)
            updates.SessionUpdated -= OnSessionUpdated;
        else if (_store is not null)
            _store.SessionChanged -= OnSessionChanged;
        _store = null;
        lock (_sync)
        {
            _disposed = true;
            _pending.Clear();
            _latest.Clear();
        }
        void Stop()
        {
            if (_timer is null) return;
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
        if (_dispatcher is null || _dispatcher.CheckAccess()) Stop();
        else if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(new Action(Stop));
    }
}
