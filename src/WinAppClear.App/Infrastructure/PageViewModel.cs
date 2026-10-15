using CommunityToolkit.Mvvm.ComponentModel;
using WinAppClear.Core.Util;

namespace WinAppClear.App.Infrastructure;

/// <summary>Base for every page: busy state, status text, and one-time loading.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private string? _statusText;

    private bool _loaded;

    public abstract string Title { get; }
    public virtual string? Subtitle => null;

    /// <summary>Called whenever the page is shown; loads data the first time.</summary>
    public async Task OnShownAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RunBusy("Loading…", LoadAsync);
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>Runs work with the busy overlay and turns unexpected exceptions into a friendly error.</summary>
    protected async Task RunBusy(string text, Func<Task> work)
    {
        IsBusy = true;
        BusyText = text;
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error($"{Title}: {text}", ex);
            Dialogs.Error($"Something went wrong: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    protected IProgress<string> BusyProgress => new Progress<string>(s => BusyText = s);
}

/// <summary>A row with a checkbox around any item.</summary>
public partial class Selectable<T> : ObservableObject
{
    public Selectable(T item, bool selected = false)
    {
        Item = item;
        _isSelected = selected;
    }

    public T Item { get; }

    [ObservableProperty] private bool _isSelected;
}
