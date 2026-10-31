using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ComfyPromptViewer;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // Raises the property itself plus any derived properties that read it. Returns whether it changed.
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null, params string[] alsoChanged)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        foreach (var other in alsoChanged)
        {
            Raise(other);
        }

        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
