using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YfgDesk.Client.ViewModels;

/// <summary>
/// Base class for MVVM bindable objects. Implements <see cref="INotifyPropertyChanged"/>
/// and a simple <see cref="SetProperty{T}"/> helper. Cross-platform (no WPF dependency).
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Set a backing field and raise <see cref="PropertyChanged"/> if the value changed.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        RaisePropertyChanged(name);
        return true;
    }

    /// <summary>
    /// Raise <see cref="PropertyChanged"/> for the given property.
    /// </summary>
    protected void RaisePropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}
