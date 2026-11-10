namespace Cyberverse.Server;

/// <summary>Runs an action on the first Dispose.</summary>
internal sealed class Disposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

internal static class Arrays
{
    /// <summary>A copy without the first element that is the same object as item.</summary>
    public static T[] WithoutFirst<T>(T[] array, T item) where T : class
    {
        var index = Array.FindIndex(array, x => ReferenceEquals(x, item));
        return index < 0 ? array : [.. array[..index], .. array[(index + 1)..]];
    }
}
