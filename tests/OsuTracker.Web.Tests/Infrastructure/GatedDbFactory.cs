using Microsoft.EntityFrameworkCore;
using OsuTracker.Web.Data;

namespace OsuTracker.Web.Tests.Infrastructure;

/// <summary>
/// Puts the order in which queries finish into the test's hands. After HoldCalls, the
/// context requests at the given positions (counted from that call, in arrival order) wait
/// at a gate until the test releases them, so two overlapping page loads can be made to
/// finish in whichever order a race needs. Every context reports its disposal, which is
/// the moment its query's results are back in the component.
/// </summary>
public sealed class GatedDbFactory(TestDatabase db) : IDbContextFactory<TrackerDbContext>
{
    private readonly List<TaskCompletionSource> _gates = [];
    private HashSet<int> _holdAt = [];
    private int _calls;
    private int _disposed;

    /// <summary>Hold the context requests at these positions from now on: 0 is the next one.</summary>
    public void HoldCalls(params int[] positions)
    {
        lock (_gates)
        {
            _calls = 0;
            _holdAt = [.. positions];
        }
    }

    public int Held { get { lock (_gates) return _gates.Count; } }

    public int Disposed => Volatile.Read(ref _disposed);

    /// <summary>Releases the gate-th held request, counting in the order they were held.</summary>
    public void Release(int gate)
    {
        lock (_gates) _gates[gate].SetResult();
    }

    public TrackerDbContext CreateDbContext() => new Tracked(db.Options, () => Interlocked.Increment(ref _disposed));

    public async Task<TrackerDbContext> CreateDbContextAsync(CancellationToken ct = default)
    {
        TaskCompletionSource? gate = null;
        lock (_gates)
        {
            if (_holdAt.Contains(_calls++))
            {
                gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates.Add(gate);
            }
        }

        if (gate is not null) await gate.Task.WaitAsync(ct);
        return CreateDbContext();
    }

    private sealed class Tracked(DbContextOptions<TrackerDbContext> options, Action onDisposed) : TrackerDbContext(options)
    {
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            onDisposed();
        }
    }
}
