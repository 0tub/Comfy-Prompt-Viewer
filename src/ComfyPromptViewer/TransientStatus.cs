using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

// A one-line message that fades in, holds, and fades out. Two of these can show at once because each has its
// own gate, so the maintenance panel and the folder-cache control never cancel each other's message.
public sealed class TransientStatus : INotifyPropertyChanged
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(120);

    private readonly SessionGate _gate = new();
    private string _text = "";
    private bool _isVisible;
    private double _opacity;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Text => _text;
    public bool IsVisible => _isVisible;
    public double Opacity => _opacity;

    public async void Show(string message)
    {
        var session = _gate.Restart();

        Update(message, true, 0);
        await Task.Yield();
        if (session.IsStale)
        {
            return;
        }

        Update(message, true, 1);

        try
        {
            await Task.Delay(HoldDuration, session.Token);
            Update(message, true, 0);
            await Task.Delay(FadeDuration, session.Token);
            if (session.IsCurrent)
            {
                Update("", false, 0);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Clear()
    {
        _gate.Cancel();
        Update("", false, 0);
    }

    private void Update(string text, bool isVisible, double opacity)
    {
        if (_text != text)
        {
            _text = text;
            Raise(nameof(Text));
        }

        if (_isVisible != isVisible)
        {
            _isVisible = isVisible;
            Raise(nameof(IsVisible));
        }

        if (_opacity != opacity)
        {
            _opacity = opacity;
            Raise(nameof(Opacity));
        }
    }

    private void Raise(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
