namespace ElegantSeries.Flow.Core.Navigation;

/// <summary>
/// Specifies how a page is navigated to within a region.
/// </summary>
public enum NavigationMode
{
    /// <summary>Pushes a new page onto the stack. The page is destroyed on back navigation.</summary>
    New,

    /// <summary>Caches the ViewModel instance for reuse on subsequent navigations.</summary>
    KeepAlive,

    /// <summary>Replaces the current page without pushing onto the back stack.</summary>
    Replace,

    /// <summary>Clears the entire region stack before pushing the new page.</summary>
    ClearStack
}
