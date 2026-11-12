using System.Diagnostics;
using Soundsys.Engine;
using Soundsys.Engine.Abstractions;

namespace Soundsys.App.ViewModels;

/// <summary>
/// Runs engine calls off the UI thread and turns failures into plain English (the details go to the trace log, never
/// to the screen).
/// </summary>
public sealed class EngineRunner(SyncEngine engine, Action<string> reportError)
{
    public const string UnexpectedMessage = "Something went wrong. Try again, and restart soundsys if it keeps happening.";

    public SyncEngine Engine => engine;

    /// <summary>Runs <paramref name="action"/> on the thread pool. Returns false (and shows a plain message) if it threw.</summary>
    public async Task<bool> RunAsync(Action<SyncEngine> action)
    {
        try
        {
            await Task.Run(() => action(engine));
            return true;
        }
        catch (Exception ex)
        {
            reportError(Describe(ex));
            return false;
        }
    }

    public async Task<T?> RunAsync<T>(Func<SyncEngine, T> func)
    {
        try
        {
            return await Task.Run(() => func(engine));
        }
        catch (Exception ex)
        {
            reportError(Describe(ex));
            return default;
        }
    }

    public async Task<T?> RunTaskAsync<T>(Func<SyncEngine, Task<T>> func)
    {
        try
        {
            return await Task.Run(() => func(engine));
        }
        catch (Exception ex)
        {
            reportError(Describe(ex));
            return default;
        }
    }

    /// <summary>Fire-and-forget (sliders, toggles); failures still surface as a plain message.</summary>
    public void Post(Action<SyncEngine> action) => _ = RunAsync(action);

    /// <summary>A message fit for the screen. Device errors already are; anything else is logged and summarised.</summary>
    public static string Describe(Exception ex)
    {
        if (ex is AudioDeviceException && !string.IsNullOrWhiteSpace(ex.Message))
        {
            return ex.Message;
        }

        Trace.TraceError($"soundsys ui: {ex}");
        return UnexpectedMessage;
    }
}

/// <summary>
/// Keeps the poll from overwriting a value the user is changing (a slider being dragged) until the engine has caught up.
/// </summary>
internal sealed class EditHold(int milliseconds = 1200)
{
    private long _until;

    public bool Active => Environment.TickCount64 < Volatile.Read(ref _until);

    public void Touch() => Volatile.Write(ref _until, Environment.TickCount64 + milliseconds);
}
