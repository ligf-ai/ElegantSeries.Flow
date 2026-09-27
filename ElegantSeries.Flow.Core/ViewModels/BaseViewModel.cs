using CommunityToolkit.Mvvm.ComponentModel;
using ElegantSeries.Flow.Core.Navigation;
using System.Diagnostics.CodeAnalysis;

namespace ElegantSeries.Flow.Core.ViewModels;

/// <summary>
/// Base class for all ViewModels in the ElegantSeries.Flow framework.
/// Provides convenient navigation methods that delegate to the attached
/// <see cref="INavigationService"/>.
/// </summary>
/// <remarks>
/// The <see cref="Navigation"/> property is managed automatically by the
/// <see cref="NavigationService"/>: it is set when the ViewModel becomes the
/// active page and cleared when it is navigated away from.
/// </remarks>
public abstract partial class BaseViewModel : ObservableObject
{
    /// <summary>
    /// Gets the navigation service attached to this ViewModel, or <see langword="null"/>
    /// if the ViewModel is not currently the active page.
    /// </summary>
    public INavigationService? Navigation { get; internal set; }

    /// <summary>
    /// Navigates to the specified ViewModel type.
    /// </summary>
    /// <typeparam name="TViewModel">The target ViewModel type.</typeparam>
    /// <param name="regionName">The target navigation region. Defaults to <c>"MainRegion"</c>.</param>
    /// <param name="mode">The navigation mode. Defaults to <see cref="NavigationMode.New"/>.</param>
    /// <returns>
    /// <see langword="true"/> if navigation succeeded;
    /// <see langword="false"/> if it was cancelled or the ViewModel is not active.
    /// </returns>
    protected Task<bool> NavigateToAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(
        string regionName = "MainRegion",
        NavigationMode mode = NavigationMode.New)
        where TViewModel : BaseViewModel
        => Navigation?.NavigateToAsync<TViewModel>(regionName, mode) ?? Task.FromResult(false);

    /// <summary>
    /// Navigates to the specified ViewModel type with a strongly-typed parameter.
    /// </summary>
    /// <typeparam name="TViewModel">The target ViewModel type.</typeparam>
    /// <typeparam name="TParam">The parameter type.</typeparam>
    /// <param name="parameter">The navigation parameter.</param>
    /// <param name="regionName">The target navigation region. Defaults to <c>"MainRegion"</c>.</param>
    /// <param name="mode">The navigation mode. Defaults to <see cref="NavigationMode.New"/>.</param>
    /// <returns>
    /// <see langword="true"/> if navigation succeeded;
    /// <see langword="false"/> if it was cancelled or the ViewModel is not active.
    /// </returns>
    protected Task<bool> NavigateToAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel, TParam>(
        TParam parameter,
        string regionName = "MainRegion",
        NavigationMode mode = NavigationMode.New)
        where TViewModel : BaseViewModel
        => Navigation?.NavigateToAsync<TViewModel, TParam>(parameter, regionName, mode) ?? Task.FromResult(false);

    /// <summary>
    /// Navigates back to the previous page in the specified region.
    /// </summary>
    /// <param name="regionName">The target navigation region. Defaults to <c>"MainRegion"</c>.</param>
    /// <returns>
    /// <see langword="true"/> if back navigation succeeded;
    /// <see langword="false"/> if the stack is empty, the guard cancelled, or the ViewModel is not active.
    /// </returns>
    protected Task<bool> GoBackAsync(string regionName = "MainRegion")
        => Navigation?.GoBackAsync(regionName) ?? Task.FromResult(false);
}
