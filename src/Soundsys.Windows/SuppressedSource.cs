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
        try
        {
            _suppressor.Apply();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            _suppressor.Restore();
            throw new AudioDeviceException("Couldn't mute the laptop speakers, so you may hear the music from the laptop too.", ex);
        }

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
