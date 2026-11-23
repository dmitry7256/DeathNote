using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DeathNote.Wpf.Infrastructure;

/// <summary>
/// Keeps the existing view and item instances, but publishes only one UI reset
/// when a collection's membership or order has changed. Thousands of individual
/// ObservableCollection.Add events can otherwise make initial WPF layout O(n²).
/// Call only from the dispatcher thread.
/// </summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach(var item in items)Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
