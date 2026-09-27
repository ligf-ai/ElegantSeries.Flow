using ElegantSeries.Flow.Core.ViewModels;
using System.Diagnostics.CodeAnalysis;

namespace ElegantSeries.Flow.Core.Navigation;

/// <summary>
/// Platform-agnostic navigation service that manages region-based navigation stacks,
/// KeepAlive caching, and ViewModel lifecycle.
/// </summary>
public interface INavigationService
{
    // ────────────────────────────── Events ──────────────────────────────

    /// <summary>
    /// Raised when a region successfully navigates to a new active ViewModel.
    /// </summary>
    /// <remarks>Parameters: region name, activated ViewModel.</remarks>
    event Action<string, BaseViewModel>? RegionNavigated;

    /// <summary>
    /// Raised when the navigation service disposes a ViewModel.
    /// </summary>
    /// <remarks>
    /// <para>Parameters: region name, disposed ViewModel.</para>
    /// <para>
    /// If a ViewModel only implements <see cref="IAsyncDisposable"/> and is removed
    /// through a synchronous cleanup path, this event indicates that the navigation
    /// reference has been removed — not that async resources have been released.
    /// Use <see cref="ClearCacheAsync"/> or <see cref="ClearAllCacheAsync"/> to ensure
    /// proper async disposal.
    /// </para>
    /// </remarks>
    event Action<string, BaseViewModel>? ViewModelDisposed;

    /// <summary>
    /// Raised when a region's KeepAlive cache is cleared.
    /// </summary>
    /// <remarks>Parameter: region name.</remarks>
    event Action<string>? RegionCacheCleared;

    // ──────────────────────── Query Methods (Sync) ────────────────────────

    /// <summary>
    /// Determines whether back navigation is possible in the specified region.
    /// </summary>
    /// <param name="regionName">The navigation region. Defaults to <c>"MainRegion"</c>.</param>
    /// <returns><see langword="true"/> if the region stack contains more than one page.</returns>
    bool CanGoBack(string regionName = "MainRegion");

    /// <summary>
    /// Gets the currently active ViewModel in the specified region.
    /// </summary>
    /// <param name="regionName">The navigation region. Defaults to <c>"MainRegion"</c>.</param>
    /// <returns>The active ViewModel, or <see langword="null"/> if the region is empty.</returns>
    BaseViewModel? GetCurrentViewModel(string regionName = "MainRegion");

    /// <summary>
    /// Checks whether the active ViewModel in the specified region is of the given type.
    /// </summary>
    /// <typeparam name="TViewModel">The ViewModel type to check.</typeparam>
    /// <param name="regionName">The navigation region. Defaults to <c>"MainRegion"</c>.</param>
    bool IsActive<TViewModel>(string regionName = "MainRegion") where TViewModel : BaseViewModel;

    /// <summary>
    /// Gets the <see cref="NavigationMode"/> of the current page in the specified region.
    /// </summary>
    /// <param name="regionName">The navigation region. Defaults to <c>"MainRegion"</c>.</param>
    /// <returns>The navigation mode, or <see langword="null"/> if the region is empty.</returns>
    NavigationMode? GetCurrentMode(string regionName = "MainRegion");

    // ─────────────────── Navigation Methods (Async) ───────────────────

    /// <summary>
    /// Navigates to the specified ViewModel type.
    /// </summary>
    /// <typeparam name="TViewModel">The target ViewModel type.</typeparam>
    /// <param name="regionName">The target region. Defaults to <c>"MainRegion"</c>.</param>
    /// <param name="mode">The navigation mode. Defaults to <see cref="NavigationMode.New"/>.</param>
    /// <returns>
    /// <see langword="true"/> if navigation succeeded;
    /// <see langword="false"/> if cancelled by a guard.
    /// </returns>
    Task<bool> NavigateToAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(
        string regionName = "MainRegion",
        NavigationMode mode = NavigationMode.New)
        where TViewModel : BaseViewModel;

    /// <summary>
    /// Navigates to the specified ViewModel type with a strongly-typed parameter.
    /// </summary>
    /// <typeparam name="TViewModel">The target ViewModel type.</typeparam>
    /// <typeparam name="TParam">The parameter type.</typeparam>
    /// <param name="parameter">The navigation parameter.</param>
    /// <param name="regionName">The target region. Defaults to <c>"MainRegion"</c>.</param>
    /// <param name="mode">The navigation mode. Defaults to <see cref="NavigationMode.New"/>.</param>
    /// <returns>
    /// <see langword="true"/> if navigation succeeded;
    /// <see langword="false"/> if cancelled by a guard.
    /// </returns>
    Task<bool> NavigateToAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel, TParam>(
        TParam parameter,
        string regionName = "MainRegion",
        NavigationMode mode = NavigationMode.New)
        where TViewModel : BaseViewModel;

    /// <summary>
    /// Navigates back to the previous page in the specified region.
    /// </summary>
    /// <param name="regionName">The target region. Defaults to <c>"MainRegion"</c>.</param>
    /// <returns>
    /// <see langword="true"/> if back navigation succeeded;
    /// <see langword="false"/> if cancelled or the stack has one or fewer pages.
    /// </returns>
    Task<bool> GoBackAsync(string regionName = "MainRegion");

    // ──────────────── Cache Management (Sync then Async) ────────────────

    /// <summary>
    /// Synchronously clears the KeepAlive cache for the specified region.
    /// </summary>
    /// <param name="regionName">The navigation region to clear.</param>
    /// <remarks>
    /// <para>
    /// ViewModels still referenced by the navigation stack are deferred for disposal
    /// until their last reference is removed.
    /// </para>
    /// <para>
    /// The synchronous path calls <see cref="IDisposable.Dispose"/> but does not call
    /// <see cref="IAsyncDisposable.DisposeAsync"/>. Use <see cref="ClearCacheAsync"/>
    /// to ensure proper async disposal.
    /// </para>
    /// </remarks>
    void ClearCache(string regionName);

    /// <summary>
    /// Synchronously clears the KeepAlive cache for all regions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ViewModels still referenced by navigation stacks are deferred for disposal
    /// until their last reference is removed.
    /// </para>
    /// <para>
    /// The synchronous path calls <see cref="IDisposable.Dispose"/> but does not call
    /// <see cref="IAsyncDisposable.DisposeAsync"/>. Use <see cref="ClearAllCacheAsync"/>
    /// to ensure proper async disposal.
    /// </para>
    /// </remarks>
    void ClearAllCache();

    /// <summary>
    /// Asynchronously clears the KeepAlive cache for the specified region,
    /// preferring <see cref="IAsyncDisposable.DisposeAsync"/> when available.
    /// </summary>
    /// <param name="regionName">The navigation region to clear.</param>
    ValueTask ClearCacheAsync(string regionName);

    /// <summary>
    /// Asynchronously clears the KeepAlive cache for all regions,
    /// preferring <see cref="IAsyncDisposable.DisposeAsync"/> when available.
    /// </summary>
    ValueTask ClearAllCacheAsync();
}
