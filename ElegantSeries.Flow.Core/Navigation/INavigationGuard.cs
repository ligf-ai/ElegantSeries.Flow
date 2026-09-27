namespace ElegantSeries.Flow.Core.Navigation;

/// <summary>
/// Implement this interface on a ViewModel to intercept navigation away from the current page.
/// </summary>
/// <remarks>
/// <para>
/// The guard is invoked <b>outside</b> the navigation lock to avoid deadlocks,
/// making it safe to display confirmation dialogs or perform async I/O.
/// </para>
/// <para>
/// If the guard returns <see langword="false"/>, the navigation is cancelled and
/// the current page remains active.
/// </para>
/// </remarks>
public interface INavigationGuard
{
    /// <summary>
    /// Determines whether navigation away from the current ViewModel is allowed.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> to allow navigation; <see langword="false"/> to cancel it.
    /// </returns>
    Task<bool> CanNavigateFromAsync();
}
