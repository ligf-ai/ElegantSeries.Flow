namespace ElegantSeries.Flow.Core.Navigation;

/// <summary>
/// Implement this interface on a ViewModel to receive navigation lifecycle callbacks.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle callbacks are invoked synchronously during the navigation transition.
/// Implementations should return quickly and must not throw exceptions.
/// </para>
/// <para>
/// Do not perform dialogs, I/O, long-running computations, or synchronously wait
/// for async operations inside these callbacks. Schedule such work after the
/// navigation completes and handle any exceptions independently.
/// </para>
/// </remarks>
public interface INavigationAware
{
    /// <summary>
    /// Called when the ViewModel becomes the active page in its region.
    /// </summary>
    /// <param name="parameter">
    /// The navigation parameter passed by the caller, or <see langword="null"/> if none was provided.
    /// </param>
    void OnNavigatedTo(object? parameter);

    /// <summary>
    /// Called when the ViewModel is being navigated away from.
    /// </summary>
    /// <remarks>
    /// To perform async confirmation before leaving (e.g., "save changes?"),
    /// implement <see cref="INavigationGuard"/> instead.
    /// </remarks>
    void OnNavigatedFrom();
}

/// <summary>
/// Strongly-typed variant of <see cref="INavigationAware"/> that provides
/// compile-time type safety for navigation parameters.
/// </summary>
/// <typeparam name="TParam">The expected type of the navigation parameter.</typeparam>
public interface INavigationAware<in TParam> : INavigationAware
{
    /// <summary>
    /// Called when the ViewModel becomes the active page with a strongly-typed parameter.
    /// </summary>
    /// <param name="parameter">The strongly-typed navigation parameter.</param>
    void OnNavigatedTo(TParam parameter);

    /// <inheritdoc />
    void INavigationAware.OnNavigatedTo(object? parameter)
    {
        if (parameter is TParam typed)
        {
            OnNavigatedTo(typed);
        }
        else if (parameter is null && default(TParam) is null)
        {
            OnNavigatedTo(default!);
        }
        else
        {
            throw new ArgumentException(
                $"Navigation parameter type mismatch: expected {typeof(TParam).Name}, " +
                $"but got {parameter?.GetType().Name ?? "null"}.",
                nameof(parameter));
        }
    }
}
