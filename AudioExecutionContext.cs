using System.Collections.Concurrent;
using Serilog;

namespace VRSoundboard;

// WASAPI and MMDevice COM objects are created and used on this one STA thread.
// The queue also gives playback commands a deterministic order across web and WPF callers.
public sealed class AudioExecutionContext : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    public bool IsCurrent => Thread.CurrentThread == _thread;
    public AudioExecutionContext()
    {
        _thread = new Thread(() =>
        {
            foreach (var action in _queue.GetConsumingEnumerable()) action();
        }) { IsBackground = true, Name = "VRSoundboard audio" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
    public T Invoke<T>(Func<T> action)
    {
        if (IsCurrent) return action();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { done.SetResult(action()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task.GetAwaiter().GetResult();
    }
    public void Invoke(Action action) => Invoke(() => { action(); return true; });
    public void Post(Action action)
    {
        if (_queue.IsAddingCompleted) return;
        try { _queue.Add(() => { try { action(); } catch (Exception ex) { Log.Error(ex, "Audio queue action failed"); } }); }
        catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!IsCurrent) _thread.Join(TimeSpan.FromSeconds(3));
        _queue.Dispose();
    }
}
