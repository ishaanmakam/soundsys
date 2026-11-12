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

    // Engine calls run one at a time, in the order the UI issued them (SemaphoreSlim releases async waiters FIFO), so a
    // fast slider drag or a Start quickly followed by Stop can't reach the engine out of order.
    private readonly SemaphoreSlim _serial = new(1, 1);

    public SyncEngine Engine => engine;

    /// <summary>Runs <paramref name="action"/> on the thread pool. Returns false (and shows a plain message) if it threw.</summary>
    public async Task<bool> RunAsync(Action<SyncEngine> action)
    {
        await _serial.WaitAsync();
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
        finally
        {
            _serial.Release();
        }
    }

    public async Task<T?> RunAsync<T>(Func<SyncEngine, T> func)
    {
        await _serial.WaitAsync();
        try
        {
            return await Task.Run(() => func(engine));
        }
        catch (Exception ex)
        {
            reportError(Describe(ex));
            return default;
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>Long-running engine work (calibration): only its start is serialised, so sliders keep working meanwhile.</summary>
    public async Task<T?> RunTaskAsync<T>(Func<SyncEngine, Task<T>> func)
    {
        Task<T> pending;
        await _serial.WaitAsync();
        try
        {
            pending = await Task.Run(() => Task.FromResult(func(engine)));
        }
        catch (Exception ex)
        {
            reportError(Describe(ex));
            return default;
        }
        finally
        {
            _serial.Release();
        }

        try
        {
            return await pending;
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
