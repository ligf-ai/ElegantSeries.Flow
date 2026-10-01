using Microsoft.Extensions.DependencyInjection;
using ElegantSeries.Flow.Core.Extensions;
using ElegantSeries.Flow.Core.Navigation;
using ElegantSeries.Flow.Core.Routing;
using ElegantSeries.Flow.Core.ViewModels;

namespace ElegantSeries.Flow.Core.Tests;

public class TestHomeViewModel : BaseViewModel
{
    public bool NavigatedToCalled { get; set; }
    public bool NavigatedFromCalled { get; set; }
}

public class ThrowingNavigatedFromViewModel : BaseViewModel, INavigationAware
{
    public bool ThrowOnNavigatedFrom { get; set; }

    public void OnNavigatedTo(object? parameter) { }

    public void OnNavigatedFrom()
    {
        if (ThrowOnNavigatedFrom)
        {
            throw new InvalidOperationException("OnNavigatedFrom failed.");
        }
    }
}

public class ThrowingNavigatedToViewModel : BaseViewModel, INavigationAware
{
    public void OnNavigatedTo(object? parameter)
        => throw new InvalidOperationException("OnNavigatedTo failed.");

    public void OnNavigatedFrom() { }
}

public class TestDetailViewModel : BaseViewModel, INavigationAware<string>, IAsyncDisposable
{
    public string? ReceivedParam { get; private set; }
    public bool Disposed { get; private set; }

    public void OnNavigatedTo(string parameter)
    {
        ReceivedParam = parameter;
    }

    public void OnNavigatedFrom() { }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public class SimpleAwareViewModel : BaseViewModel, INavigationAware
{
    public object? NavigatedToParam { get; private set; }
    public bool NavigatedToCalled { get; private set; }
    public bool NavigatedFromCalled { get; private set; }

    public void OnNavigatedTo(object? parameter)
    {
        NavigatedToCalled = true;
        NavigatedToParam = parameter;
    }

    public void OnNavigatedFrom()
    {
        NavigatedFromCalled = true;
    }
}

public class CallerViewModel : BaseViewModel
{
    public Task<bool> TriggerNavigateToHomeAsync() => NavigateToAsync<TestHomeViewModel>();
    public Task<bool> TriggerNavigateToDetailAsync(string param) => NavigateToAsync<TestDetailViewModel, string>(param);
    public Task<bool> TriggerGoBackAsync() => GoBackAsync();
}

public class GuardedViewModel : BaseViewModel, INavigationGuard
{
    public bool AllowNavigation { get; set; } = true;

    public Task<bool> CanNavigateFromAsync()
    {
        return Task.FromResult(AllowNavigation);
    }
}

public class KeepAliveViewModel : BaseViewModel, IDisposable
{
    public bool Disposed { get; private set; }
    public int DisposeCallCount { get; private set; }

    public void Dispose()
    {
        DisposeCallCount++;
        Disposed = true;
    }
}

/// <summary>
/// A KeepAlive ViewModel whose synchronous <see cref="IDisposable.Dispose"/> always throws.
/// Used to verify that one failing disposal never blocks the disposal of the remaining ViewModels.
/// </summary>
public class ThrowingKeepAliveViewModel : BaseViewModel, IDisposable
{
    public bool DisposeAttempted { get; private set; }

    public void Dispose()
    {
        DisposeAttempted = true;
        throw new InvalidOperationException("Sync dispose failed.");
    }
}

/// <summary>
/// A KeepAlive ViewModel whose <see cref="IAsyncDisposable.DisposeAsync"/> always throws.
/// </summary>
public class ThrowingAsyncDisposeViewModel : BaseViewModel, IAsyncDisposable
{
    public bool DisposeAttempted { get; private set; }

    public ValueTask DisposeAsync()
    {
        DisposeAttempted = true;
        throw new InvalidOperationException("Async dispose failed.");
    }
}

public class TestView { }

public class NavigationServiceTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly INavigationService _navigationService;

    public NavigationServiceTests()
    {
        var services = new ServiceCollection();
        services.AddFlowNavigation();
        services.AddTransient<TestHomeViewModel>();
        services.AddTransient<TestDetailViewModel>();
        services.AddTransient<SimpleAwareViewModel>();
        services.AddTransient<CallerViewModel>();
        services.AddTransient<GuardedViewModel>();
        services.AddTransient<ThrowingNavigatedFromViewModel>();
        services.AddTransient<ThrowingNavigatedToViewModel>();
        services.AddTransient<KeepAliveViewModel>();
        services.AddTransient<ThrowingKeepAliveViewModel>();
        services.AddTransient<ThrowingAsyncDisposeViewModel>();

        _serviceProvider = services.BuildServiceProvider();
        _navigationService = _serviceProvider.GetRequiredService<INavigationService>();
    }

    [Fact]
    public async Task NavigateToAsync_ShouldActivateViewModel_AndTriggerEvent()
    {
        string? notifiedRegion = null;
        BaseViewModel? notifiedVm = null;

        _navigationService.RegionNavigated += (r, vm) =>
        {
            notifiedRegion = r;
            notifiedVm = vm;
        };

        var result = await _navigationService.NavigateToAsync<TestHomeViewModel>("MainRegion");

        Assert.True(result);
        Assert.Equal("MainRegion", notifiedRegion);
        Assert.IsType<TestHomeViewModel>(notifiedVm);
        Assert.NotNull(notifiedVm.Navigation);
        Assert.True(_navigationService.IsActive<TestHomeViewModel>("MainRegion"));
    }

    [Fact]
    public async Task NavigateToAsync_WithParameter_ShouldPassTypedParameter()
    {
        var result = await _navigationService.NavigateToAsync<TestDetailViewModel, string>("Hello Navigation");

        Assert.True(result);
        var current = _navigationService.GetCurrentViewModel() as TestDetailViewModel;
        Assert.NotNull(current);
        Assert.Equal("Hello Navigation", current.ReceivedParam);
    }

    [Fact]
    public async Task NavigateToAsync_WithNullParameter_ShouldPassNullToAware()
    {
        var result = await _navigationService.NavigateToAsync<TestDetailViewModel, string?>(null);

        Assert.True(result);
        var current = _navigationService.GetCurrentViewModel() as TestDetailViewModel;
        Assert.NotNull(current);
        Assert.Null(current.ReceivedParam);
    }

    [Fact]
    public async Task SimpleAwareViewModel_ShouldTriggerNavigatedToAndFrom()
    {
        var navResult = await _navigationService.NavigateToAsync<SimpleAwareViewModel, int>(42);
        Assert.True(navResult);

        var simple = _navigationService.GetCurrentViewModel() as SimpleAwareViewModel;
        Assert.NotNull(simple);
        Assert.True(simple.NavigatedToCalled);
        Assert.Equal(42, simple.NavigatedToParam);

        // 切出到新页面
        await _navigationService.NavigateToAsync<TestHomeViewModel>();
        Assert.True(simple.NavigatedFromCalled);
    }

    [Fact]
    public async Task GoBackAsync_ShouldReturnToPreviousViewModel_AndDisposeCurrent()
    {
        BaseViewModel? disposedVm = null;
        _navigationService.ViewModelDisposed += (_, vm) => disposedVm = vm;

        await _navigationService.NavigateToAsync<TestHomeViewModel>();
        await _navigationService.NavigateToAsync<TestDetailViewModel, string>("Arg");

        Assert.True(_navigationService.CanGoBack());

        var goBackResult = await _navigationService.GoBackAsync();
        Assert.True(goBackResult);

        Assert.True(_navigationService.IsActive<TestHomeViewModel>());
        Assert.IsType<TestDetailViewModel>(disposedVm);
        Assert.True(((TestDetailViewModel)disposedVm).Disposed);
    }

    [Fact]
    public async Task NavigationGuard_WhenDisallowed_ShouldCancelNavigation()
    {
        await _navigationService.NavigateToAsync<GuardedViewModel>();
        var guarded = (GuardedViewModel)_navigationService.GetCurrentViewModel()!;
        guarded.AllowNavigation = false;

        var navResult = await _navigationService.NavigateToAsync<TestHomeViewModel>();
        Assert.False(navResult);
        Assert.True(_navigationService.IsActive<GuardedViewModel>());

        guarded.AllowNavigation = true;
        var retryResult = await _navigationService.NavigateToAsync<TestHomeViewModel>();
        Assert.True(retryResult);
        Assert.True(_navigationService.IsActive<TestHomeViewModel>());
    }

    [Fact]
    public async Task OnNavigatedFromThrows_ShouldCompleteTransitionAndPropagateException()
    {
        await _navigationService.NavigateToAsync<ThrowingNavigatedFromViewModel>();
        var oldViewModel = (ThrowingNavigatedFromViewModel)_navigationService.GetCurrentViewModel()!;
        oldViewModel.ThrowOnNavigatedFrom = true;

        BaseViewModel? navigatedEventViewModel = null;
        _navigationService.RegionNavigated += (_, vm) => navigatedEventViewModel = vm;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _navigationService.NavigateToAsync<TestHomeViewModel>());

        Assert.Equal("OnNavigatedFrom failed.", exception.Message);
        Assert.True(_navigationService.IsActive<TestHomeViewModel>());
        Assert.Null(oldViewModel.Navigation);
        Assert.IsType<TestHomeViewModel>(navigatedEventViewModel);
    }

    [Fact]
    public async Task OnNavigatedToThrows_ShouldKeepCommittedPageActiveAndPropagateException()
    {
        BaseViewModel? navigatedEventViewModel = null;
        _navigationService.RegionNavigated += (_, vm) => navigatedEventViewModel = vm;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _navigationService.NavigateToAsync<ThrowingNavigatedToViewModel>());

        var current = _navigationService.GetCurrentViewModel();
        Assert.Equal("OnNavigatedTo failed.", exception.Message);
        Assert.IsType<ThrowingNavigatedToViewModel>(current);
        Assert.Same(current, navigatedEventViewModel);
        Assert.Same(_navigationService, current!.Navigation);
    }

    [Fact]
    public async Task KeepAliveMode_ShouldReuseSameInstance_AndNotDisposeOnGoBack()
    {
        await _navigationService.NavigateToAsync<TestHomeViewModel>();
        await _navigationService.NavigateToAsync<KeepAliveViewModel>(mode: NavigationMode.KeepAlive);

        var firstInstance = _navigationService.GetCurrentViewModel();
        Assert.IsType<KeepAliveViewModel>(firstInstance);

        // 返回 Home
        await _navigationService.GoBackAsync();
        Assert.False(((KeepAliveViewModel)firstInstance).Disposed);

        // 再次导航到 KeepAlive
        await _navigationService.NavigateToAsync<KeepAliveViewModel>(mode: NavigationMode.KeepAlive);
        var secondInstance = _navigationService.GetCurrentViewModel();

        Assert.Same(firstInstance, secondInstance);

        // 清理缓存后应销毁
        await _navigationService.ClearCacheAsync("MainRegion");
        Assert.False(((KeepAliveViewModel)firstInstance).Disposed);

        await _navigationService.NavigateToAsync<TestHomeViewModel>("MainRegion", NavigationMode.ClearStack);
        Assert.True(((KeepAliveViewModel)firstInstance).Disposed);
        Assert.Equal(1, ((KeepAliveViewModel)firstInstance).DisposeCallCount);
    }

    [Fact]
    public async Task ClearCache_WhenKeepAliveIsInHistory_ShouldDisposeAfterLastStackReferenceIsRemoved()
    {
        await _navigationService.NavigateToAsync<TestHomeViewModel>();
        await _navigationService.NavigateToAsync<KeepAliveViewModel>(mode: NavigationMode.KeepAlive);
        var keepAlive = (KeepAliveViewModel)_navigationService.GetCurrentViewModel()!;
        await _navigationService.NavigateToAsync<TestHomeViewModel>();

        await _navigationService.ClearCacheAsync("MainRegion");
        Assert.False(keepAlive.Disposed);

        Assert.True(await _navigationService.GoBackAsync());
        Assert.Same(keepAlive, _navigationService.GetCurrentViewModel());
        Assert.False(keepAlive.Disposed);

        await _navigationService.NavigateToAsync<TestHomeViewModel>("MainRegion", NavigationMode.ClearStack);
        Assert.True(keepAlive.Disposed);
        Assert.Equal(1, keepAlive.DisposeCallCount);
    }

    [Fact]
    public async Task ClearCache_WhenInstanceIsCachedInAnotherRegion_ShouldDeferDisposal()
    {
        var services = new ServiceCollection();
        services.AddFlowNavigation();
        services.AddSingleton<KeepAliveViewModel>();
        services.AddTransient<TestHomeViewModel>();
        var serviceProvider = services.BuildServiceProvider();
        var navigationService = (NavigationService)serviceProvider.GetRequiredService<INavigationService>();

        await navigationService.NavigateToAsync<TestHomeViewModel>("Region1");
        await navigationService.NavigateToAsync<KeepAliveViewModel>("Region1", NavigationMode.KeepAlive);
        var keepAlive = (KeepAliveViewModel)navigationService.GetCurrentViewModel("Region1")!;
        await navigationService.GoBackAsync("Region1");

        await navigationService.NavigateToAsync<TestHomeViewModel>("Region2");
        await navigationService.NavigateToAsync<KeepAliveViewModel>("Region2", NavigationMode.KeepAlive);
        Assert.Same(keepAlive, navigationService.GetCurrentViewModel("Region2"));
        await navigationService.GoBackAsync("Region2");

        await navigationService.ClearCacheAsync("Region1");
        Assert.False(keepAlive.Disposed);

        await navigationService.ClearCacheAsync("Region2");
        Assert.True(keepAlive.Disposed);
        Assert.Equal(1, keepAlive.DisposeCallCount);

        await navigationService.DisposeAsync();
        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task ReplaceMode_ShouldReplaceCurrentPage()
    {
        await _navigationService.NavigateToAsync<TestHomeViewModel>();
        await _navigationService.NavigateToAsync<TestDetailViewModel, string>("Param1");
        await _navigationService.NavigateToAsync<KeepAliveViewModel>(mode: NavigationMode.Replace);

        Assert.True(_navigationService.IsActive<KeepAliveViewModel>());
        Assert.True(_navigationService.CanGoBack());

        // 返回应直接回到 HomeViewModel，跳过被 Replace 的 DetailViewModel
        await _navigationService.GoBackAsync();
        Assert.True(_navigationService.IsActive<TestHomeViewModel>());
        Assert.False(_navigationService.CanGoBack());
    }

    [Fact]
    public async Task ClearStackMode_ShouldEmptyStackAndPushNew()
    {
        await _navigationService.NavigateToAsync<TestHomeViewModel>();
        await _navigationService.NavigateToAsync<TestDetailViewModel, string>("Param");

        Assert.True(_navigationService.CanGoBack());

        await _navigationService.NavigateToAsync<TestHomeViewModel>(mode: NavigationMode.ClearStack);

        Assert.False(_navigationService.CanGoBack());
        Assert.True(_navigationService.IsActive<TestHomeViewModel>());
    }

    [Fact]
    public async Task GetCurrentMode_And_GetCurrentViewModel_ShouldReflectState()
    {
        Assert.Null(_navigationService.GetCurrentMode());
        Assert.Null(_navigationService.GetCurrentViewModel());
        Assert.False(_navigationService.CanGoBack());
        Assert.False(_navigationService.IsActive<TestHomeViewModel>());

        await _navigationService.NavigateToAsync<TestHomeViewModel>(mode: NavigationMode.New);
        Assert.Equal(NavigationMode.New, _navigationService.GetCurrentMode());
        Assert.IsType<TestHomeViewModel>(_navigationService.GetCurrentViewModel());
        Assert.False(_navigationService.CanGoBack());

        await _navigationService.NavigateToAsync<KeepAliveViewModel>(mode: NavigationMode.KeepAlive);
        Assert.Equal(NavigationMode.KeepAlive, _navigationService.GetCurrentMode());
        Assert.IsType<KeepAliveViewModel>(_navigationService.GetCurrentViewModel());
        Assert.True(_navigationService.CanGoBack());
    }

    [Fact]
    public async Task GoBackAsync_WhenStackHasSingleOrNoItem_ShouldReturnFalse()
    {
        // 空栈
        var emptyBack = await _navigationService.GoBackAsync("EmptyRegion");
        Assert.False(emptyBack);

        // 仅有一个页面
        await _navigationService.NavigateToAsync<TestHomeViewModel>("SingleRegion");
        var singleBack = await _navigationService.GoBackAsync("SingleRegion");
        Assert.False(singleBack);
    }

    [Fact]
    public async Task ClearCache_Sync_ShouldDisposeAndTriggerEvents()
    {
        string? clearedRegion = null;
        BaseViewModel? disposedVm = null;

        _navigationService.RegionCacheCleared += r => clearedRegion = r;
        _navigationService.ViewModelDisposed += (_, vm) => disposedVm = vm;

        await _navigationService.NavigateToAsync<KeepAliveViewModel>(mode: NavigationMode.KeepAlive);
        _navigationService.ClearCache("MainRegion");

        Assert.Equal("MainRegion", clearedRegion);
        var keepAlive = (KeepAliveViewModel)_navigationService.GetCurrentViewModel()!;
        Assert.Null(disposedVm);
        Assert.False(keepAlive.Disposed);

        await _navigationService.NavigateToAsync<TestHomeViewModel>("MainRegion", NavigationMode.ClearStack);
        Assert.Same(keepAlive, disposedVm);
        Assert.True(keepAlive.Disposed);
    }

    [Fact]
    public async Task ClearAllCacheAsync_ShouldDisposeAllCachedInstances()
    {
        var clearedRegions = new HashSet<string>();
        int disposedCount = 0;

        _navigationService.RegionCacheCleared += r => clearedRegions.Add(r);
        _navigationService.ViewModelDisposed += (_, _) => disposedCount++;

        await _navigationService.NavigateToAsync<KeepAliveViewModel>("Region1", NavigationMode.KeepAlive);
        await _navigationService.NavigateToAsync<KeepAliveViewModel>("Region2", NavigationMode.KeepAlive);

        await _navigationService.ClearAllCacheAsync();

        Assert.Contains("Region1", clearedRegions);
        Assert.Contains("Region2", clearedRegions);
        Assert.Equal(0, disposedCount);

        await _navigationService.NavigateToAsync<TestHomeViewModel>("Region1", NavigationMode.ClearStack);
        await _navigationService.NavigateToAsync<TestHomeViewModel>("Region2", NavigationMode.ClearStack);
        Assert.Equal(2, disposedCount);
    }

    [Fact]
    public async Task Dispose_Sync_ShouldDisposeStacksAndCaches()
    {
        var services = new ServiceCollection();
        services.AddFlowNavigation();
        services.AddTransient<TestHomeViewModel>();
        services.AddTransient<KeepAliveViewModel>();
        var sp = services.BuildServiceProvider();
        var navService = (NavigationService)sp.GetRequiredService<INavigationService>();

        await navService.NavigateToAsync<TestHomeViewModel>();
        await navService.NavigateToAsync<KeepAliveViewModel>(mode: NavigationMode.KeepAlive);
        var keepAlive = (KeepAliveViewModel)navService.GetCurrentViewModel()!;
        await navService.NavigateToAsync<KeepAliveViewModel>(mode: NavigationMode.KeepAlive);

        bool disposedEventTriggered = false;
        navService.ViewModelDisposed += (_, _) => disposedEventTriggered = true;

        navService.Dispose();
        Assert.True(disposedEventTriggered);
        Assert.Equal(1, keepAlive.DisposeCallCount);

        // 验证已释放后再次调用是幂等的
        navService.Dispose();
        await navService.DisposeAsync();

        // 验证已释放后调用导航抛出 ObjectDisposedException
        await Assert.ThrowsAsync<ObjectDisposedException>(() => navService.NavigateToAsync<TestHomeViewModel>());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => navService.GoBackAsync());
    }

    [Fact]
    public async Task BaseViewModel_ProtectedMethods_ShouldWorkWhenNavAttached_AndGracefullyFallback()
    {
        // 1. 当未附加 Navigation 时，调用方法优雅回退 false
        var detachedCaller = new CallerViewModel();
        Assert.False(await detachedCaller.TriggerNavigateToHomeAsync());
        Assert.False(await detachedCaller.TriggerNavigateToDetailAsync("test"));
        Assert.False(await detachedCaller.TriggerGoBackAsync());

        // 2. 当附加 Navigation 时，通过 CallerViewModel 内部发起导航
        await _navigationService.NavigateToAsync<CallerViewModel>();
        var attachedCaller = (CallerViewModel)_navigationService.GetCurrentViewModel()!;
        Assert.NotNull(attachedCaller.Navigation);

        var navResult = await attachedCaller.TriggerNavigateToDetailAsync("from-caller");
        Assert.True(navResult);
        Assert.True(_navigationService.IsActive<TestDetailViewModel>());

        // 3. 切出到新页面后，老页面的 Navigation 引用被置空（无法在切出状态下越权导航）
        Assert.Null(attachedCaller.Navigation);
        Assert.False(await attachedCaller.TriggerGoBackAsync());

        // 4. 通过导航服务退栈回到 CallerViewModel，Navigation 重新注入
        await _navigationService.GoBackAsync();
        Assert.True(_navigationService.IsActive<CallerViewModel>());
        Assert.NotNull(attachedCaller.Navigation);

        // 5. 重新激活后可继续正常导航
        var homeNav = await attachedCaller.TriggerNavigateToHomeAsync();
        Assert.True(homeNav);
        Assert.True(_navigationService.IsActive<TestHomeViewModel>());
    }

    [Fact]
    public async Task AddScopedFlowNavigation_ShouldIsolateNavigationStacksBetweenScopes()
    {
        var services = new ServiceCollection();
        services.AddScopedFlowNavigation();
        services.AddTransient<TestHomeViewModel>();
        services.AddTransient<TestDetailViewModel>();
        var sp = services.BuildServiceProvider();

        await using var scope1 = sp.CreateAsyncScope();
        await using var scope2 = sp.CreateAsyncScope();

        var nav1 = scope1.ServiceProvider.GetRequiredService<INavigationService>();
        var nav2 = scope2.ServiceProvider.GetRequiredService<INavigationService>();

        Assert.NotSame(nav1, nav2);

        await nav1.NavigateToAsync<TestHomeViewModel>();
        await nav2.NavigateToAsync<TestDetailViewModel, string>("scope2");

        Assert.True(nav1.IsActive<TestHomeViewModel>());
        Assert.False(nav1.IsActive<TestDetailViewModel>());

        Assert.True(nav2.IsActive<TestDetailViewModel>());
        Assert.False(nav2.IsActive<TestHomeViewModel>());
    }

    [Fact]
    public void AotRouteAttribute_ShouldStoreViewType_AndThrowOnNull()
    {
        var attr = new AotRouteAttribute(typeof(TestView));
        Assert.Equal(typeof(TestView), attr.ViewType);

        Assert.Throws<ArgumentNullException>(() => new AotRouteAttribute(null!));
    }

    [Fact]
    public void ServiceCollectionExtensions_ShouldThrowOnNullServices()
    {
        Assert.Throws<ArgumentNullException>(() => FlowServiceCollectionExtensions.AddFlowNavigation(null!));
        Assert.Throws<ArgumentNullException>(() => FlowServiceCollectionExtensions.AddScopedFlowNavigation(null!));
    }

    [Fact]
    public async Task Validation_AllMethods_ShouldThrowOnInvalidRegionName()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _navigationService.NavigateToAsync<TestHomeViewModel>(string.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => _navigationService.NavigateToAsync<TestDetailViewModel, string>("param", "   "));
        await Assert.ThrowsAsync<ArgumentException>(() => _navigationService.GoBackAsync("  "));
        Assert.Throws<ArgumentException>(() => _navigationService.CanGoBack(""));
        Assert.Throws<ArgumentException>(() => _navigationService.GetCurrentViewModel("   "));
        Assert.Throws<ArgumentException>(() => _navigationService.IsActive<TestHomeViewModel>(""));
        Assert.Throws<ArgumentException>(() => _navigationService.GetCurrentMode("  "));
        Assert.Throws<ArgumentException>(() => _navigationService.ClearCache(""));
        await Assert.ThrowsAsync<ArgumentException>(() => _navigationService.ClearCacheAsync("  ").AsTask());
    }

    [Fact]
    public async Task NavigateToAsync_ReentrancyInRegionNavigated_ShouldNotDeadlock()
    {
        bool secondNavigated = false;

        void Handler(string region, BaseViewModel vm)
        {
            if (vm is TestHomeViewModel)
            {
                // In RegionNavigated callback, trigger another navigation (re-entrancy test)
                _ = Task.Run(async () =>
                {
                    await _navigationService.NavigateToAsync<TestDetailViewModel, string>("nested");
                    secondNavigated = true;
                });
            }
        }

        _navigationService.RegionNavigated += Handler;
        try
        {
            var firstNav = await _navigationService.NavigateToAsync<TestHomeViewModel>();
            Assert.True(firstNav);

            // Wait briefly for nested task completion
            for (int i = 0; i < 50 && !secondNavigated; i++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.True(secondNavigated);
            Assert.True(_navigationService.IsActive<TestDetailViewModel>());
        }
        finally
        {
            _navigationService.RegionNavigated -= Handler;
        }
    }

    [Fact]
    public async Task ClearCacheAsync_ShouldRaiseRegionCacheCleared_AndViewModelDisposedEvents()
    {
        string? clearedRegion = null;
        BaseViewModel? disposedVm = null;

        _navigationService.RegionCacheCleared += r => clearedRegion = r;
        _navigationService.ViewModelDisposed += (_, vm) => disposedVm = vm;

        await _navigationService.NavigateToAsync<KeepAliveViewModel>(mode: NavigationMode.KeepAlive);
        await _navigationService.ClearCacheAsync("MainRegion");

        Assert.Equal("MainRegion", clearedRegion);
        var keepAlive = (KeepAliveViewModel)_navigationService.GetCurrentViewModel()!;
        Assert.Null(disposedVm);
        Assert.False(keepAlive.Disposed);

        await _navigationService.NavigateToAsync<TestHomeViewModel>("MainRegion", NavigationMode.ClearStack);
        Assert.Same(keepAlive, disposedVm);
        Assert.True(keepAlive.Disposed);
    }

    [Fact]
    public void NavigationAware_TypeMismatch_ShouldThrowArgumentExceptionWithEnglishMessage()
    {
        INavigationAware aware = new TestDetailViewModel();
        var ex = Assert.Throws<ArgumentException>(() => aware.OnNavigatedTo(12345));

        Assert.Contains("Navigation parameter type mismatch: expected String, but got Int32.", ex.Message);
    }

    [Fact]
    public async Task DisposeAsync_ShouldRaiseEventsOutOfLock_AndNotDeadlock()
    {
        var services = new ServiceCollection();
        services.AddFlowNavigation();
        services.AddTransient<KeepAliveViewModel>();
        var sp = services.BuildServiceProvider();
        var navService = (NavigationService)sp.GetRequiredService<INavigationService>();

        await navService.NavigateToAsync<KeepAliveViewModel>(mode: NavigationMode.KeepAlive);
        var keepAlive = (KeepAliveViewModel)navService.GetCurrentViewModel()!;
        await navService.NavigateToAsync<KeepAliveViewModel>(mode: NavigationMode.KeepAlive);

        bool callbackExecuted = false;
        navService.RegionCacheCleared += region =>
        {
            // If events were raised while holding _navigationLock, calling CanGoBack or other methods could lock
            callbackExecuted = true;
            bool canGoBack = navService.CanGoBack(region);
            Assert.False(canGoBack);
        };

        await navService.DisposeAsync();
        Assert.True(callbackExecuted);
        Assert.Equal(1, keepAlive.DisposeCallCount);
    }

    [Fact]
    public async Task ClearAllCache_WhenStackIsEmpty_ShouldStillNotifyCachedRegion()
    {
        await _navigationService.NavigateToAsync<TestHomeViewModel>("EmptyStackRegion");
        await _navigationService.NavigateToAsync<KeepAliveViewModel>("EmptyStackRegion", NavigationMode.KeepAlive);

        // Pop back to home, then clear stack
        await _navigationService.GoBackAsync("EmptyStackRegion");
        await _navigationService.NavigateToAsync<TestHomeViewModel>("EmptyStackRegion", NavigationMode.ClearStack);

        var notifiedRegions = new List<string>();
        _navigationService.RegionCacheCleared += r => notifiedRegions.Add(r);

        _navigationService.ClearAllCache();

        Assert.Contains("EmptyStackRegion", notifiedRegions);
    }

    // ────────────────── Disposal-failure isolation (P1 regression) ──────────────────

    /// <summary>
    /// Puts a KeepAlive ViewModel into the cache and then off the navigation stack,
    /// so a later cache clear disposes it directly.
    /// </summary>
    private async Task<ThrowingKeepAliveViewModel> CacheThrowingViewModelAsync(string regionName)
    {
        await _navigationService.NavigateToAsync<ThrowingKeepAliveViewModel>(regionName, NavigationMode.KeepAlive);
        var vm = (ThrowingKeepAliveViewModel)_navigationService.GetCurrentViewModel(regionName)!;
        await _navigationService.NavigateToAsync<TestHomeViewModel>(regionName, NavigationMode.Replace);
        return vm;
    }

    private async Task<KeepAliveViewModel> CacheHealthyViewModelAsync(string regionName)
    {
        await _navigationService.NavigateToAsync<KeepAliveViewModel>(regionName, NavigationMode.KeepAlive);
        var vm = (KeepAliveViewModel)_navigationService.GetCurrentViewModel(regionName)!;
        await _navigationService.NavigateToAsync<TestHomeViewModel>(regionName, NavigationMode.Replace);
        return vm;
    }

    [Fact]
    public async Task ClearCacheAsync_WhenOneViewModelThrows_ShouldStillDisposeRemaining()
    {
        var throwing = await CacheThrowingViewModelAsync("CacheRegion");
        var healthy = await CacheHealthyViewModelAsync("CacheRegion");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _navigationService.ClearCacheAsync("CacheRegion").AsTask());

        Assert.Equal("Sync dispose failed.", ex.Message);
        Assert.True(throwing.DisposeAttempted);
        Assert.True(healthy.Disposed);
    }

    [Fact]
    public async Task ClearCache_WhenOneViewModelThrows_ShouldStillDisposeRemaining()
    {
        var throwing = await CacheThrowingViewModelAsync("SyncCacheRegion");
        var healthy = await CacheHealthyViewModelAsync("SyncCacheRegion");

        // Exercise the synchronous clear path; only the setup above is asynchronous.
        var ex = Assert.Throws<InvalidOperationException>(() => _navigationService.ClearCache("SyncCacheRegion"));

        Assert.Equal("Sync dispose failed.", ex.Message);
        Assert.True(throwing.DisposeAttempted);
        Assert.True(healthy.Disposed);
    }

    [Fact]
    public async Task ClearAllCacheAsync_WhenMultipleViewModelsThrow_ShouldAggregateExceptions()
    {
        var throwingA = await CacheThrowingViewModelAsync("RegionA");
        var throwingB = await CacheThrowingViewModelAsync("RegionB");

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => _navigationService.ClearAllCacheAsync().AsTask());

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.True(throwingA.DisposeAttempted);
        Assert.True(throwingB.DisposeAttempted);
    }

    [Fact]
    public async Task DisposeAsync_WhenCacheViewModelThrows_ShouldStillDisposeStackViewModels()
    {
        var throwing = await CacheThrowingViewModelAsync("DisposeRegion");

        // A healthy KeepAlive ViewModel that stays on the navigation stack.
        await _navigationService.NavigateToAsync<KeepAliveViewModel>("DisposeRegion", NavigationMode.KeepAlive);
        var stacked = (KeepAliveViewModel)_navigationService.GetCurrentViewModel("DisposeRegion")!;

        var navService = (NavigationService)_navigationService;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => navService.DisposeAsync().AsTask());

        Assert.Equal("Sync dispose failed.", ex.Message);
        Assert.True(throwing.DisposeAttempted);
        Assert.True(stacked.Disposed);
    }

    [Fact]
    public async Task Dispose_WhenStackViewModelThrows_ShouldStillDisposeRemainingStackViewModels()
    {
        var services = new ServiceCollection();
        services.AddFlowNavigation();
        services.AddTransient<ThrowingKeepAliveViewModel>();
        services.AddTransient<KeepAliveViewModel>();
        services.AddTransient<TestHomeViewModel>();
        var sp = services.BuildServiceProvider();
        var navService = (NavigationService)sp.GetRequiredService<INavigationService>();

        await navService.NavigateToAsync<ThrowingKeepAliveViewModel>("StackRegion");
        var throwing = (ThrowingKeepAliveViewModel)navService.GetCurrentViewModel("StackRegion")!;
        await navService.NavigateToAsync<KeepAliveViewModel>("StackRegion");
        var stacked = (KeepAliveViewModel)navService.GetCurrentViewModel("StackRegion")!;

        var ex = Assert.Throws<InvalidOperationException>(() => navService.Dispose());

        Assert.Equal("Sync dispose failed.", ex.Message);
        Assert.True(throwing.DisposeAttempted);
        Assert.True(stacked.Disposed);
    }
}
