using Soundsys.Engine.Abstractions;

namespace Soundsys.Windows;

/// <summary>Wraps a capture source with a <see cref="PlaybackSuppressor"/>: applied on Start, restored on Stop, fault and Dispose.</summary>
public sealed class SuppressedSource : IAudioSource
{
    private readonly IAudioSource _inner;
    private readonly PlaybackSuppressor _suppressor;

    public SuppressedSource(IAudioSource inner, PlaybackSuppressor suppressor)
    {
        _inner = inner;
        _suppressor = suppressor;
        _inner.Faulted += OnInnerFaulted;
    }

    public string Id => _inner.Id;

    public string DisplayName => _inner.DisplayName;

    public IAudioSource Inner => _inner;

    public PlaybackSuppression Suppression => _suppressor.Mode;

    public event EventHandler<DeviceFaultEventArgs>? Faulted;

    public void Start(ISourceSink sink)
    {
        _suppressor.Apply();
        try
        {
            _inner.Start(sink);
        }
        catch
        {
            _suppressor.Restore();
            throw;
        }
    }

    public void Stop()
    {
        try
        {
            _inner.Stop();
        }
        finally
        {
            _suppressor.Restore();
        }
    }

    public void Dispose()
    {
        _inner.Faulted -= OnInnerFaulted;
        try
        {
            _inner.Dispose();
        }
        finally
        {
            _suppressor.Dispose();
        }
    }

    private void OnInnerFaulted(object? sender, DeviceFaultEventArgs e)
    {
        _suppressor.Restore();
        Faulted?.Invoke(this, e);
    }
}
