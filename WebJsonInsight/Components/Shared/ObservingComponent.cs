using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.AspNetCore.Components;

namespace WebJsonInsight.Components.Shared;

/// <summary>
/// A component that re-renders when the view model it is showing changes.
///
/// <para>
/// This is the whole of the adapter between the two worlds. WPF binds to
/// <see cref="INotifyPropertyChanged"/> and <see cref="INotifyCollectionChanged"/> and re-renders the
/// affected element; Blazor re-renders a component when it is told to. So a component says what it is
/// watching, and every notification from any of those becomes one <c>StateHasChanged</c>.
/// </para>
///
/// <para>
/// It is coarser than WPF's binding — a changed property re-renders the whole component rather than
/// one <c>TextBlock</c> — and that is the right trade here. Blazor diffs its own output, so the cost
/// is a render-tree comparison rather than a DOM rewrite, and the alternative is per-property
/// plumbing in every component to save work the framework already avoids.
/// </para>
/// </summary>
public abstract class ObservingComponent : ComponentBase, IDisposable
{
    private readonly List<INotifyPropertyChanged> _properties = [];
    private readonly List<INotifyCollectionChanged> _collections = [];
    private bool _disposed;

    /// <summary>
    /// Re-render this component whenever <paramref name="source"/> raises a change. Safe to call with
    /// null, and safe to call with the same object twice — a view model reached through two
    /// properties would otherwise subscribe twice and render twice for one change.
    /// </summary>
    protected void Observe(object? source)
    {
        if (source is INotifyPropertyChanged properties && !_properties.Contains(properties))
        {
            properties.PropertyChanged += OnChanged;
            _properties.Add(properties);
        }

        if (source is INotifyCollectionChanged collection && !_collections.Contains(collection))
        {
            collection.CollectionChanged += OnCollectionChanged;
            _collections.Add(collection);
        }
    }

    protected void Observe(params object?[] sources)
    {
        foreach (var source in sources)
        {
            Observe(source);
        }
    }

    /// <summary>
    /// Stop watching everything. For a component whose subject is replaced rather than mutated — the
    /// tabs, when a different project is opened — since the old view model would otherwise keep this
    /// component alive and re-rendering against a document nobody is looking at.
    /// </summary>
    protected void StopObserving()
    {
        foreach (var source in _properties)
        {
            source.PropertyChanged -= OnChanged;
        }

        foreach (var source in _collections)
        {
            source.CollectionChanged -= OnCollectionChanged;
        }

        _properties.Clear();
        _collections.Clear();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Rerender();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rerender();

    /// <summary>
    /// A view model can raise a change from a background continuation — every Vault read does. Blazor
    /// requires renders on its own dispatcher, so this hops there rather than assuming the caller is
    /// already on it.
    /// </summary>
    private void Rerender()
    {
        if (_disposed)
        {
            return;
        }

        _ = InvokeAsync(() =>
        {
            if (!_disposed)
            {
                StateHasChanged();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopObserving();
        Disposing();
        GC.SuppressFinalize(this);
    }

    /// <summary>For a component with something of its own to release. Does nothing by default.</summary>
    protected virtual void Disposing()
    {
    }
}
