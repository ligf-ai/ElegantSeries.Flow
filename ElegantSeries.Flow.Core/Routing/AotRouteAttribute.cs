using System.Diagnostics.CodeAnalysis;

namespace ElegantSeries.Flow.Core.Routing;

/// <summary>
/// Associates a ViewModel with its corresponding View type for AOT-compatible route resolution.
/// </summary>
/// <remarks>
/// This attribute is intended for use by Source Generators or view resolvers
/// at the platform-specific layer (e.g., WPF, MAUI, Avalonia).
/// </remarks>
/// <param name="viewType">The View type associated with the decorated ViewModel.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AotRouteAttribute(
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type viewType) : Attribute
{
    /// <summary>
    /// Gets the View type associated with the decorated ViewModel.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type ViewType { get; } = viewType ?? throw new ArgumentNullException(nameof(viewType));
}
