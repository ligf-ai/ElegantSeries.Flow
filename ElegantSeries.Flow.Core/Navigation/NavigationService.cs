using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using ElegantSeries.Flow.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ElegantSeries.Flow.Core.Navigation;

/// <summary>
/// Default <see cref="INavigationService"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// Manages one navigation stack per region, an opt-in KeepAlive ViewModel cache,
/// and the full ViewModel lifecycle (activation callbacks and disposal).
/// </para>
/// <para>
/// Thread safety: every public member is safe to call from any thread. Navigation
/// transitions are serialized with an async lock, and all shared state is guarded
/// by a dedicated state lock. Lifecycle callbacks and disposal always run outside
/// both locks.
/// </para>
/// <para>
/// Disposal is best-effort: when several ViewModels are disposed together (cache
/// clear or service disposal), every ViewModel is attempted even if one of them
/// throws. Collected exceptions are rethrown afterwards — a single exception is
/// rethrown as-is, several are wrapped in an <see cref="AggregateException"/>.
/// </para>
/// </remarks>
public sealed class NavigationService(IServiceProvider serviceProvider) : INavigationService, IDisposable, IAsyncDisposable
{
    // ────────────────────────────── Fields ──────────────────────────────

    private readonly IServiceProvider _serviceProvider = serviceProvider
        ?? throw new ArgumentNullException(nameof(serviceProvider));

    /// <summary>Navigation stacks keyed by region name. Guarded by <see cref="_stateLock"/>.</summary>
    private readonly Dictionary<string, Stack<NavigationEntry>> _regionStacks = [];

    /// <summary>KeepAlive ViewModels keyed by (region, ViewModel type). Guarded by <see cref="_stateLock"/>.</summary>
    private readonly Dictionary<(string Region, Type ViewModelType), BaseViewModel> _keepAliveCache = [];

    /// <summary>
    /// ViewModels removed from the cache while still referenced elsewhere.
    /// They are disposed once their last reference disappears. Guarded by <see cref="_stateLock"/>.
    /// </summary>
    private readonly Dictionary<BaseViewModel, HashSet<string>> _pendingKeepAliveDisposals = new(ReferenceEqualityComparer.Instance);

    /// <summary>Serializes navigation transitions and disposal.</summary>
    private readonly SemaphoreSlim _navigationLock = new(1, 1);

    /// <summary>
    /// Guards <see cref="_regionStacks"/>, <see cref="_keepAliveCache"/> and
    /// <see cref="_pendingKeepAliveDisposals"/>.
    /// </summary>
    private readonly Lock _stateLock = new();

    private bool _disposed;

    // ────────────────────────────── Events ──────────────────────────────

    /// <inheritdoc />
    public event Action<string, BaseViewModel>? RegionNavigated;

    /// <inheritdoc />
    public event Action<string, BaseViewModel>? ViewModelDisposed;

    /// <inheritdoc />
    public event Action<string>? RegionCacheCleared;

    // ──────────────────────── Navigation (async) ────────────────────────

    /// <inheritdoc />
    public Task<bool> NavigateToAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(
        string regionName = "MainRegion",
        NavigationMode mode = NavigationMode.New)
        where TViewModel : BaseViewModel
        => NavigateInternalAsync<TViewModel>(regionName, null, mode);

    /// <inheritdoc />
    public Task<bool> NavigateToAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel, TParam>(
        TParam parameter,
        string regionName = "MainRegion",
        NavigationMode mode = NavigationMode.New)
        where TViewModel : BaseViewModel
        => NavigateInternalAsync<TViewModel>(regionName, parameter, mode);

    /// <inheritdoc />
    public async Task<bool> GoBackAsync(string regionName = "MainRegion")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        ThrowIfDisposed();

        BaseViewModel? currentVmForGuard = null;
        using (_stateLock.EnterScope())
        {
            if (_regionStacks.TryGetValue(regionName, out var stack) && stack.Count > 1)
            {
                currentVmForGuard = stack.Peek().ViewModel;
            }
        }

        // Run the navigation guard outside the locks to avoid deadlocks.
        if (currentVmForGuard is INavigationGuard guard)
        {
            if (!await guard.CanNavigateFromAsync().ConfigureAwait(false))
            {
                return false;
            }
        }

        var work = new TransitionWork();
        bool success = false;

        await _navigationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return false;

            NavigationEntry current;
            NavigationEntry previous;

            using (_stateLock.EnterScope())
            {
                if (!_regionStacks.TryGetValue(regionName, out var stack) || stack.Count <= 1)
                {
                    return false;
                }

                current = stack.Peek();
                if (!ReferenceEquals(current.ViewModel, currentVmForGuard))
                {
                    return false;
                }

                stack.Pop();
                previous = stack.Peek();
            }

            // Queue the work items while holding _navigationLock but not _stateLock,
            // mirroring the NavigateInternalAsync path.
            DetachNavigation(current.ViewModel);
            var plan = BuildDeactivatePlan(current, destroy: current.Mode != NavigationMode.KeepAlive);
            QueueDeactivatePlan(plan, work);

            AttachNavigation(previous.ViewModel);
            QueueActivate(previous.ViewModel, previous.Parameter, work);
            work.Events.Add(() => RegionNavigated?.Invoke(regionName, previous.ViewModel));

            success = true;
        }
        finally
        {
            _navigationLock.Release();
        }

        if (success)
        {
            await RunTransitionAsync(work).ConfigureAwait(false);
        }

        return success;
    }

    // ───────────────────────── Queries (sync) ─────────────────────────

    /// <inheritdoc />
    public bool CanGoBack(string regionName = "MainRegion")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        using (_stateLock.EnterScope())
        {
            return _regionStacks.TryGetValue(regionName, out var stack) && stack.Count > 1;
        }
    }

    /// <inheritdoc />
    public BaseViewModel? GetCurrentViewModel(string regionName = "MainRegion")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        using (_stateLock.EnterScope())
        {
            return _regionStacks.TryGetValue(regionName, out var stack) && stack.Count > 0
                ? stack.Peek().ViewModel
                : null;
        }
    }

    /// <inheritdoc />
    public bool IsActive<TViewModel>(string regionName = "MainRegion") where TViewModel : BaseViewModel
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        return GetCurrentViewModel(regionName) is TViewModel;
    }

    /// <inheritdoc />
    public NavigationMode? GetCurrentMode(string regionName = "MainRegion")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        using (_stateLock.EnterScope())
        {
            return _regionStacks.TryGetValue(regionName, out var stack) && stack.Count > 0
                ? stack.Peek().Mode
                : null;
        }
    }

    // ───────────────────── Cache management (sync) ─────────────────────

    /// <inheritdoc />
    public void ClearCache(string regionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        if (_disposed) return;

        List<Action> pendingEvents = [];
        var errors = new List<Exception>();

        List<(BaseViewModel ViewModel, string[] Regions)> targets;
        using (_stateLock.EnterScope())
        {
            targets = TakeCacheEntriesForDisposalLocked(RemoveRegionCacheEntriesLocked(regionName));
        }

        DisposeCacheEntriesSync(targets, pendingEvents, errors);
        pendingEvents.Add(() => RegionCacheCleared?.Invoke(regionName));

        RaiseEvents(pendingEvents);
        ThrowCollectedErrors(errors);
    }

    /// <inheritdoc />
    public void ClearAllCache()
    {
        if (_disposed) return;

        List<Action> pendingEvents = [];
        var errors = new List<Exception>();

        ClearAllCacheCore(pendingEvents, errors);

        RaiseEvents(pendingEvents);
        ThrowCollectedErrors(errors);
    }

    // ───────────────────── Cache management (async) ─────────────────────

    /// <inheritdoc />
    public async ValueTask ClearCacheAsync(string regionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        if (_disposed) return;

        List<Action> pendingEvents = [];
        var errors = new List<Exception>();

        List<(BaseViewModel ViewModel, string[] Regions)> targets;
        using (_stateLock.EnterScope())
        {
            targets = TakeCacheEntriesForDisposalLocked(RemoveRegionCacheEntriesLocked(regionName));
        }

        await DisposeCacheEntriesAsync(targets, pendingEvents, errors).ConfigureAwait(false);
        pendingEvents.Add(() => RegionCacheCleared?.Invoke(regionName));

        RaiseEvents(pendingEvents);
        ThrowCollectedErrors(errors);
    }

    /// <inheritdoc />
    public async ValueTask ClearAllCacheAsync()
    {
        if (_disposed) return;

        List<Action> pendingEvents = [];
        var errors = new List<Exception>();

        await ClearAllCacheCoreAsync(pendingEvents, errors).ConfigureAwait(false);

        RaiseEvents(pendingEvents);
        ThrowCollectedErrors(errors);
    }

    // ───────────────────────── Disposal ─────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Best-effort: every cached and stacked ViewModel is attempted even if one of
    /// them throws. Collected exceptions are rethrown after cleanup and events.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;

        List<Action> pendingEvents = [];
        var errors = new List<Exception>();

        _navigationLock.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;

            ClearAllCacheCore(pendingEvents, errors);
            DisposeStackEntriesSync(pendingEvents, errors);
        }
        finally
        {
            _navigationLock.Release();
            RaiseEvents(pendingEvents);
            _navigationLock.Dispose();
        }

        ThrowCollectedErrors(errors);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Best-effort: every cached and stacked ViewModel is attempted even if one of
    /// them throws. Collected exceptions are rethrown after cleanup and events.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        List<Action> pendingEvents = [];
        var errors = new List<Exception>();

        await _navigationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;

            await ClearAllCacheCoreAsync(pendingEvents, errors).ConfigureAwait(false);
            await DisposeStackEntriesAsync(pendingEvents, errors).ConfigureAwait(false);
        }
        finally
        {
            _navigationLock.Release();
            RaiseEvents(pendingEvents);
            _navigationLock.Dispose();
        }

        ThrowCollectedErrors(errors);
    }

    // ──────────────────── Navigation internals ────────────────────

    /// <summary>
    /// Core navigation logic shared by both <see cref="NavigateToAsync{TViewModel}"/> overloads.
    /// </summary>
    private async Task<bool> NavigateInternalAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(
        string regionName,
        object? parameter,
        NavigationMode mode)
        where TViewModel : BaseViewModel
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        ThrowIfDisposed();

        BaseViewModel? currentVmForGuard = null;
        using (_stateLock.EnterScope())
        {
            if (_regionStacks.TryGetValue(regionName, out var peekStack) && peekStack.Count > 0)
            {
                currentVmForGuard = peekStack.Peek().ViewModel;
            }
        }

        if (currentVmForGuard is INavigationGuard guard)
        {
            if (!await guard.CanNavigateFromAsync().ConfigureAwait(false))
            {
                return false;
            }
        }

        BaseViewModel? preResolvedVm = null;
        bool isNewlyResolved = false;
        var cacheKey = (regionName, typeof(TViewModel));
        bool useKeepAlive = mode == NavigationMode.KeepAlive;

        if (useKeepAlive)
        {
            using (_stateLock.EnterScope())
            {
                if (_keepAliveCache.TryGetValue(cacheKey, out var cached))
                {
                    preResolvedVm = cached;
                }
            }
        }

        if (preResolvedVm is null)
        {
            preResolvedVm = _serviceProvider.GetRequiredService<TViewModel>();
            isNewlyResolved = true;
        }

        var work = new TransitionWork();
        bool success = false;
        BaseViewModel? vmToDisposeAfterLock = null;

        await _navigationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                if (isNewlyResolved)
                {
                    vmToDisposeAfterLock = preResolvedVm;
                }
                // Keep success false to skip the queue building below.
            }
            else
            {
                List<NavigationEntry> entriesToDeactivate = [];
                bool destroyOldOnReplace = false;
                NavigationEntry? oldOnReplace = null;
                NavigationEntry? oldOnPush = null;
                BaseViewModel? redundantVmToDispose = null;
                bool stateMismatch = false;

                using (_stateLock.EnterScope())
                {
                    if (!_regionStacks.TryGetValue(regionName, out var stack))
                    {
                        stack = new Stack<NavigationEntry>();
                        _regionStacks[regionName] = stack;
                    }

                    // Make sure the current page did not change while the guard was running.
                    var actualCurrentVm = stack.Count > 0 ? stack.Peek().ViewModel : null;
                    if (!ReferenceEquals(actualCurrentVm, currentVmForGuard))
                    {
                        stateMismatch = true;
                        if (isNewlyResolved)
                        {
                            redundantVmToDispose = preResolvedVm;
                        }
                    }
                    else
                    {
                        if (mode == NavigationMode.ClearStack)
                        {
                            while (stack.Count > 0)
                            {
                                entriesToDeactivate.Add(stack.Pop());
                            }
                        }
                        else if (stack.Count > 0)
                        {
                            if (mode == NavigationMode.Replace)
                            {
                                oldOnReplace = stack.Pop();
                                destroyOldOnReplace = oldOnReplace.Mode != NavigationMode.KeepAlive;
                            }
                            else
                            {
                                oldOnPush = stack.Peek();
                            }
                        }

                        BaseViewModel nextVm = preResolvedVm;
                        if (useKeepAlive)
                        {
                            if (_keepAliveCache.TryGetValue(cacheKey, out var cached))
                            {
                                nextVm = cached;
                                if (isNewlyResolved && !ReferenceEquals(cached, preResolvedVm))
                                {
                                    redundantVmToDispose = preResolvedVm;
                                }
                            }
                            else
                            {
                                _keepAliveCache[cacheKey] = nextVm;
                            }
                        }

                        var entryMode = mode == NavigationMode.ClearStack ? NavigationMode.New : mode;
                        stack.Push(new NavigationEntry(regionName, nextVm, parameter, entryMode));

                        preResolvedVm = nextVm;
                    }
                }

                // A ViewModel resolved by this service but never placed on a stack is owned
                // by the service and is disposed after the lock (see DisposeUnusedViewModelAsync).
                // ViewModels are registered Transient (see README), so the navigation service —
                // not the DI scope — is responsible for instances it resolved but discarded.
                // This covers both the stateMismatch race above and the KeepAlive cache race
                // below: in either case the unused instance would otherwise leak until the
                // DI scope itself is disposed.
                if (redundantVmToDispose != null)
                {
                    vmToDisposeAfterLock = redundantVmToDispose;
                }

                if (stateMismatch)
                {
                    // The stack changed while the guard was running: abandon this navigation.
                    // success stays false; the queue building below is skipped.
                }
                else
                {
                    // Collect the lifecycle/disposal/event work under the lock,
                    // then execute it outside the lock (see RunTransitionAsync).
                    foreach (var old in entriesToDeactivate)
                    {
                        DetachNavigation(old.ViewModel);
                        var plan = BuildDeactivatePlan(old, destroy: old.Mode != NavigationMode.KeepAlive);
                        QueueDeactivatePlan(plan, work);
                    }

                    if (oldOnReplace != null)
                    {
                        DetachNavigation(oldOnReplace.ViewModel);
                        var plan = BuildDeactivatePlan(oldOnReplace, destroy: destroyOldOnReplace);
                        QueueDeactivatePlan(plan, work);
                    }
                    else if (oldOnPush != null)
                    {
                        DetachNavigation(oldOnPush.ViewModel);
                        var plan = BuildDeactivatePlan(oldOnPush, destroy: false);
                        QueueDeactivatePlan(plan, work);
                    }

                    AttachNavigation(preResolvedVm);
                    QueueActivate(preResolvedVm, parameter, work);
                    work.Events.Add(() => RegionNavigated?.Invoke(regionName, preResolvedVm));

                    success = true;
                }
            }
        }
        finally
        {
            _navigationLock.Release();
        }

        if (vmToDisposeAfterLock != null)
            await DisposeUnusedViewModelAsync(vmToDisposeAfterLock).ConfigureAwait(false);

        if (!success)
            return false;

        await RunTransitionAsync(work).ConfigureAwait(false);

        return true;
    }

    // ────────────────────── Cache internals ──────────────────────

    /// <summary>
    /// Removes every KeepAlive entry of one region from the cache and returns them.
    /// The caller must hold <see cref="_stateLock"/>.
    /// </summary>
    private List<KeyValuePair<(string Region, Type ViewModelType), BaseViewModel>> RemoveRegionCacheEntriesLocked(string regionName)
    {
        var keysToRemove = _keepAliveCache.Keys.Where(k => k.Region == regionName).ToList();
        List<KeyValuePair<(string Region, Type ViewModelType), BaseViewModel>> removed = [];
        foreach (var key in keysToRemove)
        {
            if (_keepAliveCache.Remove(key, out var viewModel))
            {
                removed.Add(new(key, viewModel));
            }
        }

        return removed;
    }

    /// <summary>
    /// Removes every KeepAlive entry from the cache and returns them.
    /// The caller must hold <see cref="_stateLock"/>.
    /// </summary>
    private List<KeyValuePair<(string Region, Type ViewModelType), BaseViewModel>> RemoveAllCacheEntriesLocked()
    {
        var removed = _keepAliveCache.ToList();
        _keepAliveCache.Clear();
        return removed;
    }

    /// <summary>
    /// Clears the whole KeepAlive cache on the synchronous path.
    /// Never throws: disposal exceptions are collected into <paramref name="errors"/>.
    /// </summary>
    private void ClearAllCacheCore(List<Action> pendingEvents, List<Exception> errors)
    {
        List<(BaseViewModel ViewModel, string[] Regions)> targets;
        List<string> affectedRegions;

        using (_stateLock.EnterScope())
        {
            affectedRegions = _keepAliveCache.Keys.Select(k => k.Region)
                .Union(_regionStacks.Keys)
                .Distinct()
                .ToList();
            targets = TakeCacheEntriesForDisposalLocked(RemoveAllCacheEntriesLocked());
        }

        DisposeCacheEntriesSync(targets, pendingEvents, errors);

        foreach (var region in affectedRegions)
        {
            pendingEvents.Add(() => RegionCacheCleared?.Invoke(region));
        }
    }

    /// <summary>
    /// Clears the whole KeepAlive cache on the asynchronous path.
    /// Never throws: disposal exceptions are collected into <paramref name="errors"/>.
    /// </summary>
    private async ValueTask ClearAllCacheCoreAsync(List<Action> pendingEvents, List<Exception> errors)
    {
        List<(BaseViewModel ViewModel, string[] Regions)> targets;
        List<string> affectedRegions;

        using (_stateLock.EnterScope())
        {
            affectedRegions = _keepAliveCache.Keys.Select(k => k.Region)
                .Union(_regionStacks.Keys)
                .Distinct()
                .ToList();
            targets = TakeCacheEntriesForDisposalLocked(RemoveAllCacheEntriesLocked());
        }

        await DisposeCacheEntriesAsync(targets, pendingEvents, errors).ConfigureAwait(false);

        foreach (var region in affectedRegions)
        {
            pendingEvents.Add(() => RegionCacheCleared?.Invoke(region));
        }
    }

    /// <summary>
    /// Splits removed cache entries into ViewModels that can be disposed now and ones
    /// whose disposal must be deferred until their last stack/cache reference is gone.
    /// The caller must hold <see cref="_stateLock"/>.
    /// </summary>
    private List<(BaseViewModel ViewModel, string[] Regions)> TakeCacheEntriesForDisposalLocked(
        IEnumerable<KeyValuePair<(string Region, Type ViewModelType), BaseViewModel>> removedEntries)
    {
        var regionsByViewModel = new Dictionary<BaseViewModel, HashSet<string>>(ReferenceEqualityComparer.Instance);
        foreach (var entry in removedEntries)
        {
            if (!regionsByViewModel.TryGetValue(entry.Value, out var regions))
            {
                regions = [];
                regionsByViewModel.Add(entry.Value, regions);
            }

            regions.Add(entry.Key.Region);
        }

        // Build the referenced set once instead of scanning all stacks per ViewModel.
        var referencedViewModels = BuildReferencedViewModelsLocked();

        List<(BaseViewModel ViewModel, string[] Regions)> readyToDispose = [];
        foreach (var (viewModel, regions) in regionsByViewModel)
        {
            if (referencedViewModels.Contains(viewModel))
            {
                if (!_pendingKeepAliveDisposals.TryGetValue(viewModel, out var pendingRegions))
                {
                    pendingRegions = [];
                    _pendingKeepAliveDisposals.Add(viewModel, pendingRegions);
                }

                pendingRegions.UnionWith(regions);
                continue;
            }

            if (_pendingKeepAliveDisposals.Remove(viewModel, out var previouslyPendingRegions))
            {
                regions.UnionWith(previouslyPendingRegions);
            }

            readyToDispose.Add((viewModel, [.. regions]));
        }

        return readyToDispose;
    }

    /// <summary>
    /// Collects every ViewModel that is still referenced by any region stack or by
    /// the KeepAlive cache. The caller must hold <see cref="_stateLock"/>.
    /// </summary>
    private HashSet<BaseViewModel> BuildReferencedViewModelsLocked()
    {
        var referenced = new HashSet<BaseViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var stack in _regionStacks.Values)
        {
            foreach (var entry in stack)
            {
                referenced.Add(entry.ViewModel);
            }
        }

        foreach (var cached in _keepAliveCache.Values)
        {
            referenced.Add(cached);
        }

        return referenced;
    }

    // ───────────────────── Disposal internals ─────────────────────

    /// <summary>
    /// Detaches and disposes every ViewModel left on the region stacks (synchronous path).
    /// Never throws: disposal exceptions are collected into <paramref name="errors"/>.
    /// </summary>
    private void DisposeStackEntriesSync(List<Action> pendingEvents, List<Exception> errors)
    {
        var stackViewModels = TakeStackViewModelsForDisposal();
        foreach (var (viewModel, regions) in stackViewModels)
        {
            DetachNavigation(viewModel);
            try
            {
                DisposeViewModelSync(viewModel);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }

            foreach (var region in regions)
            {
                pendingEvents.Add(() => ViewModelDisposed?.Invoke(region, viewModel));
            }
        }
    }

    /// <summary>
    /// Detaches and disposes every ViewModel left on the region stacks (asynchronous path).
    /// Never throws: disposal exceptions are collected into <paramref name="errors"/>.
    /// </summary>
    private async ValueTask DisposeStackEntriesAsync(List<Action> pendingEvents, List<Exception> errors)
    {
        var stackViewModels = TakeStackViewModelsForDisposal();
        foreach (var (viewModel, regions) in stackViewModels)
        {
            DetachNavigation(viewModel);
            try
            {
                await DisposeViewModelAsync(viewModel).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }

            foreach (var region in regions)
            {
                pendingEvents.Add(() => ViewModelDisposed?.Invoke(region, viewModel));
            }
        }
    }

    /// <summary>
    /// Takes every ViewModel on the region stacks (plus deferred KeepAlive disposals)
    /// for disposal and clears all stacks. The caller must not hold <see cref="_stateLock"/>.
    /// </summary>
    private List<(BaseViewModel ViewModel, string[] Regions)> TakeStackViewModelsForDisposal()
    {
        var regionsByViewModel = new Dictionary<BaseViewModel, HashSet<string>>(ReferenceEqualityComparer.Instance);
        using (_stateLock.EnterScope())
        {
            foreach (var (region, stack) in _regionStacks)
            {
                foreach (var entry in stack)
                {
                    if (!regionsByViewModel.TryGetValue(entry.ViewModel, out var regions))
                    {
                        regions = [];
                        regionsByViewModel.Add(entry.ViewModel, regions);
                    }

                    regions.Add(region);
                }
            }

            foreach (var (viewModel, regions) in _pendingKeepAliveDisposals)
            {
                if (!regionsByViewModel.TryGetValue(viewModel, out var allRegions))
                {
                    allRegions = [];
                    regionsByViewModel.Add(viewModel, allRegions);
                }

                allRegions.UnionWith(regions);
            }

            _regionStacks.Clear();
            _pendingKeepAliveDisposals.Clear();
        }

        return regionsByViewModel.Select(entry => (entry.Key, entry.Value.ToArray())).ToList();
    }

    /// <summary>
    /// Disposes removed cache entries on the synchronous path. Every ViewModel is
    /// attempted even if one throws; exceptions are collected into <paramref name="errors"/>.
    /// </summary>
    private void DisposeCacheEntriesSync(
        List<(BaseViewModel ViewModel, string[] Regions)> targets,
        List<Action> pendingEvents,
        List<Exception> errors)
    {
        foreach (var (viewModel, regions) in targets)
        {
            try
            {
                DisposeViewModelSync(viewModel);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }

            foreach (var region in regions)
            {
                pendingEvents.Add(() => ViewModelDisposed?.Invoke(region, viewModel));
            }
        }
    }

    /// <summary>
    /// Disposes removed cache entries on the asynchronous path. Every ViewModel is
    /// attempted even if one throws; exceptions are collected into <paramref name="errors"/>.
    /// </summary>
    private async ValueTask DisposeCacheEntriesAsync(
        List<(BaseViewModel ViewModel, string[] Regions)> targets,
        List<Action> pendingEvents,
        List<Exception> errors)
    {
        foreach (var (viewModel, regions) in targets)
        {
            try
            {
                await DisposeViewModelAsync(viewModel).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }

            foreach (var region in regions)
            {
                pendingEvents.Add(() => ViewModelDisposed?.Invoke(region, viewModel));
            }
        }
    }

    /// <summary>
    /// Disposes a single ViewModel, preferring <see cref="IAsyncDisposable"/> when implemented.
    /// </summary>
    private static async ValueTask DisposeViewModelAsync(BaseViewModel viewModel)
    {
        if (viewModel is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (viewModel is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Disposes a single ViewModel on the synchronous path via <see cref="IDisposable"/>.
    /// ViewModels that only implement <see cref="IAsyncDisposable"/> are intentionally
    /// skipped here; callers that need async cleanup must use the asynchronous path.
    /// </summary>
    private static void DisposeViewModelSync(BaseViewModel viewModel)
    {
        if (viewModel is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Disposes a ViewModel instance that was resolved but never used (it lost a race
    /// or the service was disposed concurrently). Exceptions are swallowed: a redundant
    /// instance must never break navigation.
    /// </summary>
    private static async ValueTask DisposeUnusedViewModelAsync(BaseViewModel viewModel)
    {
        try
        {
            await DisposeViewModelAsync(viewModel).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup of a redundant instance; never propagate.
        }
    }

    /// <summary>
    /// Rethrows collected exceptions: a single exception is rethrown preserving its
    /// original stack trace; several are wrapped in an <see cref="AggregateException"/>.
    /// Does nothing when <paramref name="errors"/> is empty.
    /// </summary>
    private static void ThrowCollectedErrors(List<Exception> errors)
    {
        if (errors.Count == 1)
        {
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }
        else if (errors.Count > 1)
        {
            throw new AggregateException(
                "The navigation operation completed with lifecycle or disposal errors.",
                errors);
        }
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if the service has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Invokes queued event handlers. Subscriber exceptions are swallowed so a faulty
    /// subscriber can never corrupt navigation state.
    /// </summary>
    private static void RaiseEvents(List<Action>? events)
    {
        if (events is null or { Count: 0 }) return;
        foreach (var action in events)
        {
            try
            {
                action();
            }
            catch
            {
                // Swallow: event subscribers must never break navigation.
            }
        }
    }

    // ──────────────────── Transition internals ────────────────────

    /// <summary>
    /// Executes the work items collected under the navigation lock: lifecycle callbacks,
    /// disposals and activation, then queued events. Lifecycle/disposal exceptions are
    /// collected and rethrown afterwards so one failing ViewModel never aborts the rest.
    /// </summary>
    private static async Task RunTransitionAsync(TransitionWork work)
    {
        foreach (var action in work.NavigatedFromActions)
            SafeLifecycle(action, work.Errors);

        foreach (var dispose in work.DisposeActions)
        {
            try { await dispose().ConfigureAwait(false); }
            catch (Exception ex) { work.Errors.Add(ex); }
        }

        foreach (var action in work.NavigatedToActions)
            SafeLifecycle(action, work.Errors);

        RaiseEvents(work.Events);
        ThrowCollectedErrors(work.Errors);
    }

    /// <summary>
    /// Runs a lifecycle callback and collects any exception instead of propagating it.
    /// </summary>
    private static void SafeLifecycle(Action action, List<Exception> errors)
    {
        try { action(); }
        catch (Exception ex) { errors.Add(ex); }
    }

    /// <summary>
    /// Queues the deactivation work (OnNavigatedFrom callback plus disposal when needed)
    /// for one ViewModel.
    /// </summary>
    private void QueueDeactivatePlan(DeactivatePlan plan, TransitionWork work)
    {
        var viewModel = plan.ViewModel;

        work.NavigatedFromActions.Add(() =>
        {
            if (viewModel is INavigationAware aware)
                aware.OnNavigatedFrom();
        });

        if (!plan.ShouldDispose)
            return;

        work.DisposeActions.Add(async () =>
        {
            await DisposeViewModelAsync(viewModel).ConfigureAwait(false);

            using (_stateLock.EnterScope())
                _pendingKeepAliveDisposals.Remove(viewModel);

            foreach (var region in plan.RegionsToNotify)
                work.Events.Add(() => ViewModelDisposed?.Invoke(region, viewModel));
        });
    }

    /// <summary>
    /// Queues the activation work (OnNavigatedTo callback) for one ViewModel.
    /// </summary>
    private void QueueActivate(BaseViewModel viewModel, object? parameter, TransitionWork work)
    {
        work.NavigatedToActions.Add(() =>
        {
            if (viewModel is INavigationAware aware)
                aware.OnNavigatedTo(parameter);
        });
    }

    /// <summary>
    /// Builds the deactivation plan for a ViewModel that is leaving the active slot.
    /// </summary>
    private DeactivatePlan BuildDeactivatePlan(NavigationEntry entry, bool destroy)
    {
        var viewModel = entry.ViewModel;
        var regionsToNotify = new HashSet<string> { entry.RegionName };
        bool shouldDispose = destroy && entry.Mode != NavigationMode.KeepAlive;

        using (_stateLock.EnterScope())
        {
            if (shouldDispose
                && _keepAliveCache.TryGetValue((entry.RegionName, viewModel.GetType()), out var cached)
                && ReferenceEquals(cached, viewModel))
            {
                _keepAliveCache.Remove((entry.RegionName, viewModel.GetType()));
            }

            if (_pendingKeepAliveDisposals.TryGetValue(viewModel, out var pendingRegions)
                && !BuildReferencedViewModelsLocked().Contains(viewModel))
            {
                regionsToNotify.UnionWith(pendingRegions);
                shouldDispose = true;
                // The pending entry is removed after disposal succeeds (see the queued closure).
            }
        }

        return new DeactivatePlan(viewModel, shouldDispose, regionsToNotify.ToArray());
    }

    /// <summary>
    /// Clears the navigation service reference of a ViewModel that is no longer active.
    /// </summary>
    private static void DetachNavigation(BaseViewModel viewModel)
        => viewModel.Navigation = null;

    /// <summary>
    /// Attaches this navigation service to the newly activated ViewModel.
    /// </summary>
    private void AttachNavigation(BaseViewModel viewModel)
        => viewModel.Navigation = this;

    // ────────────────────── Nested types ──────────────────────

    /// <summary>Work items collected under the navigation lock and executed outside of it.</summary>
    private sealed class TransitionWork
    {
        public List<Action> NavigatedFromActions { get; } = [];

        // Func<Task> is used for dispose actions to avoid subtle ValueTask conversion issues.
        public List<Func<Task>> DisposeActions { get; } = [];

        public List<Action> NavigatedToActions { get; } = [];

        public List<Action> Events { get; } = [];

        public List<Exception> Errors { get; } = [];
    }

    /// <summary>Deactivation plan for one ViewModel: whether to dispose it and which regions to notify.</summary>
    private record struct DeactivatePlan(BaseViewModel ViewModel, bool ShouldDispose, string[] RegionsToNotify);

    /// <summary>One page on a region's navigation stack.</summary>
    private sealed record NavigationEntry(string RegionName, BaseViewModel ViewModel, object? Parameter, NavigationMode Mode);
}
