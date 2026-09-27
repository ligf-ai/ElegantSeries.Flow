using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using ElegantSeries.Flow.Core.ViewModels;

namespace ElegantSeries.Flow.Core.Navigation;

/// <summary>
/// 管理各导航区域的页面栈、KeepAlive 缓存及 ViewModel 生命周期。
/// </summary>
public sealed class NavigationService(IServiceProvider serviceProvider) : INavigationService, IDisposable, IAsyncDisposable
{
    private readonly IServiceProvider _serviceProvider = serviceProvider
        ?? throw new ArgumentNullException(nameof(serviceProvider));

    private readonly Dictionary<string, Stack<NavigationEntry>> _regionStacks = [];
    private readonly Dictionary<(string Region, Type VmType), BaseViewModel> _keepAliveCache = [];
    private readonly Dictionary<BaseViewModel, HashSet<string>> _pendingKeepAliveDisposals = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _navigationLock = new(1, 1);
    private readonly Lock _stateLock = new();

    private bool _disposed;

    public event Action<string, BaseViewModel>? RegionNavigated;
    public event Action<string, BaseViewModel>? ViewModelDisposed;
    public event Action<string>? RegionCacheCleared;

    public Task<bool> NavigateToAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(
        string regionName = "MainRegion",
        NavigationMode mode = NavigationMode.New)
        where TViewModel : BaseViewModel
        => NavigateInternalAsync<TViewModel>(regionName, null, mode);

    public Task<bool> NavigateToAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel, TParam>(
        TParam parameter,
        string regionName = "MainRegion",
        NavigationMode mode = NavigationMode.New)
        where TViewModel : BaseViewModel
        => NavigateInternalAsync<TViewModel>(regionName, parameter, mode);

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

        // 锁外执行导航守卫
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

            // 在 _navigationLock 内但不持有 _stateLock 的位置排队工作，和 NavigateInternalAsync 路径对齐
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
                // 保持 success 为 false，跳过后续队列构建
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

                    // 确认守卫检查期间当前页面未发生变化。
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

                if (stateMismatch)
                {
                    if (redundantVmToDispose != null)
                    {
                        vmToDisposeAfterLock = redundantVmToDispose;
                    }
                    // success 仍为 false，略过队列构建
                }
                else
                {
                    if (redundantVmToDispose != null)
                    {
                        vmToDisposeAfterLock = redundantVmToDispose;
                    }

                    // 使用 TransitionWork 在锁内收集工作项，锁外执行生命周期/销毁/事件/抛错
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
            await SafeDisposeUnusedVmAsync(vmToDisposeAfterLock).ConfigureAwait(false);

        if (!success)
            return false;

        await RunTransitionAsync(work).ConfigureAwait(false);

        return true;
    }

    // 旧的 Activate / DeactivateAndNotifyAsync 已移除。
    // 生命周期回调与销毁现在由 TransitionWork 在锁外统一执行（见 QueueDeactivatePlan / QueueActivate / RunTransitionAsync）。

                private static void ThrowTransitionErrors(List<Exception> transitionErrors)
    {
        if (transitionErrors.Count == 1)
        {
            ExceptionDispatchInfo.Capture(transitionErrors[0]).Throw();
        }

        if (transitionErrors.Count > 1)
        {
            throw new AggregateException("The navigation transition completed with lifecycle or disposal errors.", transitionErrors);
        }
    }

    private bool IsReferencedInStacksLocked(BaseViewModel vm)
        => _regionStacks.Values.Any(stack => stack.Any(entry => ReferenceEquals(entry.ViewModel, vm)));

    private bool IsCachedLocked(BaseViewModel vm)
        => _keepAliveCache.Values.Any(cached => ReferenceEquals(cached, vm));

    private List<(BaseViewModel ViewModel, string[] Regions)> TakeCacheEntriesForDisposalLocked(
        IEnumerable<KeyValuePair<(string Region, Type VmType), BaseViewModel>> entries)
    {
        var regionsByViewModel = new Dictionary<BaseViewModel, HashSet<string>>(ReferenceEqualityComparer.Instance);
        foreach (var entry in entries)
        {
            if (!regionsByViewModel.TryGetValue(entry.Value, out var regions))
            {
                regions = [];
                regionsByViewModel.Add(entry.Value, regions);
            }

            regions.Add(entry.Key.Region);
        }

        List<(BaseViewModel ViewModel, string[] Regions)> readyToDispose = [];
        foreach (var (vm, regions) in regionsByViewModel)
        {
            if (IsReferencedInStacksLocked(vm) || IsCachedLocked(vm))
            {
                if (!_pendingKeepAliveDisposals.TryGetValue(vm, out var pendingRegions))
                {
                    pendingRegions = [];
                    _pendingKeepAliveDisposals.Add(vm, pendingRegions);
                }

                pendingRegions.UnionWith(regions);
                continue;
            }

            if (_pendingKeepAliveDisposals.Remove(vm, out var previouslyPendingRegions))
            {
                regions.UnionWith(previouslyPendingRegions);
            }

            readyToDispose.Add((vm, [.. regions]));
        }

        return readyToDispose;
    }

    private static async ValueTask SafeDisposeUnusedVmAsync(BaseViewModel vm)
    {
        try
        {
            await DisposeViewModelAsync(vm).ConfigureAwait(false);
        }
        catch
        {
            // Ignore non-critical exceptions when disposing a redundant instance
        }
    }

    private static async ValueTask DisposeViewModelAsync(BaseViewModel vm)
    {
        if (vm is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (vm is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static void DisposeViewModelSync(BaseViewModel vm)
    {
        if (vm is IDisposable disposable)
        {
            disposable.Dispose();
        }
        // 纯 IAsyncDisposable 不在同步路径做阻塞或启动后台任务。
        // 调用方若需要彻底释放异步资源，应优先调用 ClearCacheAsync / ClearAllCacheAsync / DisposeAsync。
    }

    public void ClearCache(string regionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        if (_disposed) return;

        List<(BaseViewModel ViewModel, string[] Regions)> vmsToDispose;

        using (_stateLock.EnterScope())
        {
            var keysToRemove = _keepAliveCache.Keys.Where(k => k.Region == regionName).ToList();
            List<KeyValuePair<(string Region, Type VmType), BaseViewModel>> entries = [];
            foreach (var key in keysToRemove)
            {
                if (_keepAliveCache.Remove(key, out var vm))
                {
                    entries.Add(new(key, vm));
                }
            }

            vmsToDispose = TakeCacheEntriesForDisposalLocked(entries);
        }

        List<Action> pendingEvents = [];
        foreach (var (vm, regions) in vmsToDispose)
        {
            DisposeViewModelSync(vm);
            foreach (var region in regions)
            {
                pendingEvents.Add(() => ViewModelDisposed?.Invoke(region, vm));
            }
        }

        pendingEvents.Add(() => RegionCacheCleared?.Invoke(regionName));
        RaiseEvents(pendingEvents);
    }

    public async ValueTask ClearCacheAsync(string regionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        if (_disposed) return;

        List<(BaseViewModel ViewModel, string[] Regions)> vmsToDispose;

        using (_stateLock.EnterScope())
        {
            var keysToRemove = _keepAliveCache.Keys.Where(k => k.Region == regionName).ToList();
            List<KeyValuePair<(string Region, Type VmType), BaseViewModel>> entries = [];
            foreach (var key in keysToRemove)
            {
                if (_keepAliveCache.Remove(key, out var vm))
                {
                    entries.Add(new(key, vm));
                }
            }

            vmsToDispose = TakeCacheEntriesForDisposalLocked(entries);
        }

        List<Action> pendingEvents = [];
        foreach (var (vm, regions) in vmsToDispose)
        {
            await DisposeViewModelAsync(vm).ConfigureAwait(false);
            foreach (var region in regions)
            {
                pendingEvents.Add(() => ViewModelDisposed?.Invoke(region, vm));
            }
        }

        pendingEvents.Add(() => RegionCacheCleared?.Invoke(regionName));
        RaiseEvents(pendingEvents);
    }

    public void ClearAllCache()
    {
        if (_disposed) return;

        List<Action> pendingEvents = [];
        ClearAllCacheCore(pendingEvents);
        RaiseEvents(pendingEvents);
    }

    private void ClearAllCacheCore(List<Action> pendingEvents)
    {
        List<(BaseViewModel ViewModel, string[] Regions)> vmsToDispose;
        List<string> affectedRegions;

        using (_stateLock.EnterScope())
        {
            var entries = _keepAliveCache.ToList();
            _keepAliveCache.Clear();
            affectedRegions = entries.Select(e => e.Key.Region).Union(_regionStacks.Keys).Distinct().ToList();
            vmsToDispose = TakeCacheEntriesForDisposalLocked(entries);
        }

        foreach (var (vm, regions) in vmsToDispose)
        {
            DisposeViewModelSync(vm);
            foreach (var region in regions)
            {
                pendingEvents.Add(() => ViewModelDisposed?.Invoke(region, vm));
            }
        }

        foreach (var region in affectedRegions)
        {
            pendingEvents.Add(() => RegionCacheCleared?.Invoke(region));
        }
    }

    public async ValueTask ClearAllCacheAsync()
    {
        if (_disposed) return;

        List<Action> pendingEvents = [];
        await ClearAllCacheCoreAsync(pendingEvents).ConfigureAwait(false);
        RaiseEvents(pendingEvents);
    }

    private async ValueTask ClearAllCacheCoreAsync(List<Action> pendingEvents)
    {
        List<(BaseViewModel ViewModel, string[] Regions)> vmsToDispose;
        List<string> affectedRegions;

        using (_stateLock.EnterScope())
        {
            var entries = _keepAliveCache.ToList();
            _keepAliveCache.Clear();
            affectedRegions = entries.Select(e => e.Key.Region).Union(_regionStacks.Keys).Distinct().ToList();
            vmsToDispose = TakeCacheEntriesForDisposalLocked(entries);
        }

        foreach (var (vm, regions) in vmsToDispose)
        {
            await DisposeViewModelAsync(vm).ConfigureAwait(false);
            foreach (var region in regions)
            {
                pendingEvents.Add(() => ViewModelDisposed?.Invoke(region, vm));
            }
        }

        foreach (var region in affectedRegions)
        {
            pendingEvents.Add(() => RegionCacheCleared?.Invoke(region));
        }
    }

    public bool CanGoBack(string regionName = "MainRegion")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        using (_stateLock.EnterScope())
        {
            return _regionStacks.TryGetValue(regionName, out var stack) && stack.Count > 1;
        }
    }

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

    public bool IsActive<TViewModel>(string regionName = "MainRegion") where TViewModel : BaseViewModel
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        return GetCurrentViewModel(regionName) is TViewModel;
    }

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

    public void Dispose()
    {
        if (_disposed) return;

        List<Action> pendingEvents = [];
        _navigationLock.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;

            ClearAllCacheCore(pendingEvents);
            DisposeStacksSync(pendingEvents);
        }
        finally
        {
            _navigationLock.Release();
            RaiseEvents(pendingEvents);
            _navigationLock.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        List<Action> pendingEvents = [];
        await _navigationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;

            await ClearAllCacheCoreAsync(pendingEvents).ConfigureAwait(false);

            var stackViewModels = TakeStackViewModelsForDisposal();
            foreach (var (vm, regions) in stackViewModels)
            {
                vm.Navigation = null;
                await DisposeViewModelAsync(vm).ConfigureAwait(false);
                foreach (var region in regions)
                {
                    pendingEvents.Add(() => ViewModelDisposed?.Invoke(region, vm));
                }
            }
        }
        finally
        {
            _navigationLock.Release();
            RaiseEvents(pendingEvents);
            _navigationLock.Dispose();
        }
    }

    private void DisposeStacksSync(List<Action> pendingEvents)
    {
        var stackViewModels = TakeStackViewModelsForDisposal();
        foreach (var (vm, regions) in stackViewModels)
        {
            vm.Navigation = null;
            DisposeViewModelSync(vm);
            foreach (var region in regions)
            {
                pendingEvents.Add(() => ViewModelDisposed?.Invoke(region, vm));
            }
        }
    }

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

            foreach (var (vm, regions) in _pendingKeepAliveDisposals)
            {
                if (!regionsByViewModel.TryGetValue(vm, out var allRegions))
                {
                    allRegions = [];
                    regionsByViewModel.Add(vm, allRegions);
                }

                allRegions.UnionWith(regions);
            }

            _regionStacks.Clear();
            _pendingKeepAliveDisposals.Clear();
        }

        return regionsByViewModel.Select(entry => (entry.Key, entry.Value.ToArray())).ToList();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

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
                // Swallow event subscriber exceptions to protect internal navigation state
            }
        }
    }

    private sealed class TransitionWork
    {
        public List<Action> NavigatedFromActions { get; } = [];
        // Use Func<Task> for dispose actions to avoid subtle ValueTask conversion issues and improve clarity.
        public List<Func<Task>> DisposeActions { get; } = [];
        public List<Action> NavigatedToActions { get; } = [];
        public List<Action> Events { get; } = [];
        public List<Exception> Errors { get; } = [];
    }

    private record struct DeactivatePlan(BaseViewModel ViewModel, bool ShouldDispose, string[] RegionsToNotify);

    private static void SafeLifecycle(Action action, List<Exception> errors)
    {
        try { action(); }
        catch (Exception ex) { errors.Add(ex); }
    }

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
        ThrowTransitionErrors(work.Errors);
    }

    private static void DetachNavigation(BaseViewModel vm)
        => vm.Navigation = null;

    private void AttachNavigation(BaseViewModel vm)
        => vm.Navigation = this;

    private void QueueDeactivatePlan(DeactivatePlan plan, TransitionWork work)
    {
        var vm = plan.ViewModel;

        work.NavigatedFromActions.Add(() =>
        {
            if (vm is INavigationAware aware)
                aware.OnNavigatedFrom();
        });

        if (!plan.ShouldDispose)
            return;

        work.DisposeActions.Add(async () =>
        {
            await DisposeViewModelAsync(vm).ConfigureAwait(false);

            using (_stateLock.EnterScope())
                _pendingKeepAliveDisposals.Remove(vm);

            foreach (var region in plan.RegionsToNotify)
                work.Events.Add(() => ViewModelDisposed?.Invoke(region, vm));
        });
    }

    private void QueueActivate(BaseViewModel vm, object? parameter, TransitionWork work)
    {
        work.NavigatedToActions.Add(() =>
        {
            if (vm is INavigationAware aware)
                aware.OnNavigatedTo(parameter);
        });
    }

    private DeactivatePlan BuildDeactivatePlan(NavigationEntry entry, bool destroy)
    {
        var vm = entry.ViewModel;
        var regionsToNotify = new HashSet<string> { entry.RegionName };
        bool shouldDispose = destroy && entry.Mode != NavigationMode.KeepAlive;

        using (_stateLock.EnterScope())
        {
            if (shouldDispose
                && _keepAliveCache.TryGetValue((entry.RegionName, vm.GetType()), out var cached)
                && ReferenceEquals(cached, vm))
            {
                _keepAliveCache.Remove((entry.RegionName, vm.GetType()));
            }

            if (_pendingKeepAliveDisposals.TryGetValue(vm, out var pendingRegions)
                && !IsReferencedInStacksLocked(vm)
                && !IsCachedLocked(vm))
            {
                regionsToNotify.UnionWith(pendingRegions);
                shouldDispose = true;
                // 不在这里移除 pending；在实际 Dispose 成功后移除（见 Queue 的闭包）
            }
        }

        return new DeactivatePlan(vm, shouldDispose, regionsToNotify.ToArray());
    }

    private sealed class NavigationEntry(string regionName, BaseViewModel viewModel, object? parameter, NavigationMode mode)
    {
        public string RegionName { get; } = regionName;
        public BaseViewModel ViewModel { get; } = viewModel;
        public object? Parameter { get; } = parameter;
        public NavigationMode Mode { get; } = mode;
    }
}
