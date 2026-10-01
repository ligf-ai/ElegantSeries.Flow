# ElegantSeries.Flow

[![NuGet](https://img.shields.io/nuget/v/ElegantSeries.Flow.svg)](https://www.nuget.org/packages/ElegantSeries.Flow)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.txt)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)

A lightweight, **AOT-friendly** MVVM navigation library for .NET.

ElegantSeries.Flow gives you region-based navigation stacks, an opt-in KeepAlive ViewModel cache,
strongly-typed navigation parameters, navigation guards, and ViewModel lifecycle callbacks —
with no dependency on any UI framework. It targets `net10.0`, is trim/AOT compatible, and is
thread-safe by design.

## Features

- **Region-based navigation** — independent navigation stacks per region (`"MainRegion"` by default)
- **Navigation modes** — `New`, `Replace`, `KeepAlive`, `ClearStack`
- **KeepAlive cache** — opt-in ViewModel reuse across navigations
- **Typed parameters** — `NavigateToAsync<TViewModel, TParam>(param)` with compile-time type safety
- **Lifecycle callbacks** — `INavigationAware` (`OnNavigatedTo` / `OnNavigatedFrom`)
- **Navigation guards** — `INavigationGuard.CanNavigateFromAsync()` can cancel navigation
- **AOT / trimming safe** — `DynamicallyAccessedMembers` annotations, no runtime reflection
- **Thread-safe** — transitions are serialized; all shared state is lock-guarded
- **Best-effort disposal** — one failing `Dispose` never leaks the remaining ViewModels
  (exceptions are collected and rethrown: single as-is, multiple as `AggregateException`)

## Install

```bash
dotnet add package ElegantSeries.Flow
```

Requires **.NET 10** or later.

## Quick start

### 1. Register the service

```csharp
using ElegantSeries.Flow.Core.Extensions;

services.AddFlowNavigation();          // singleton — single-window apps
// or
services.AddScopedFlowNavigation();    // scoped — each window gets its own navigation stack
```

### 2. Write a ViewModel

```csharp
using ElegantSeries.Flow.Core.ViewModels;
using ElegantSeries.Flow.Core.Navigation;

public partial class HomeViewModel : BaseViewModel, INavigationAware
{
    public void OnNavigatedTo(object? parameter) { /* page appeared */ }
    public void OnNavigatedFrom() { /* page is leaving */ }

    public Task GoToDetailAsync()
        => NavigateToAsync<DetailViewModel, string>("hello", mode: NavigationMode.New);
}
```

`BaseViewModel.Navigation` is attached automatically when the ViewModel becomes the active
page and cleared when it is navigated away from, so the `protected NavigateToAsync` /
`GoBackAsync` helpers can be called directly from the ViewModel.

Register your ViewModels with DI (transient is the typical lifetime):

```csharp
services.AddTransient<HomeViewModel>();
services.AddTransient<DetailViewModel>();
```

### 3. Navigate

```csharp
var navigation = serviceProvider.GetRequiredService<INavigationService>();

await navigation.NavigateToAsync<HomeViewModel>();                              // push a new page
await navigation.NavigateToAsync<DetailViewModel, string>("hello");             // with a typed parameter
await navigation.NavigateToAsync<DetailViewModel>("Sidebar", NavigationMode.Replace);
await navigation.GoBackAsync();                                                // pop

bool canGoBack = navigation.CanGoBack();
var current = navigation.GetCurrentViewModel();
```

### 4. Render the active ViewModel

ElegantSeries.Flow is UI-agnostic: it manages ViewModels, and your platform layer maps the
active ViewModel to a View. The typical pattern is a `ContentControl`-style host bound to
`GetCurrentViewModel()` and refreshed on the `RegionNavigated` event. For AOT-safe
ViewModel→View mapping, decorate ViewModels with `[AotRoute(typeof(DetailView))]` from
the `ElegantSeries.Flow.Core.Routing` namespace.

## Navigation modes

| Mode | Behavior |
|------|----------|
| `New` | Push a new page onto the region stack. The previous page is deactivated. |
| `Replace` | Replace the current page. The old page is disposed unless it was `KeepAlive`. |
| `KeepAlive` | Reuse a cached ViewModel of the same type in the region instead of creating a new one. |
| `ClearStack` | Drop the whole stack and start fresh with the new page. |

## Lifecycle

```csharp
public interface INavigationAware
{
    void OnNavigatedTo(object? parameter);  // strongly-typed via INavigationAware<T>
    void OnNavigatedFrom();
}

public interface INavigationGuard
{
    Task<bool> CanNavigateFromAsync();      // return false to cancel navigation
}
```

Lifecycle callbacks and `Dispose` calls always run **outside** internal locks, so they may
safely call back into `INavigationService` without deadlocking.

## KeepAlive cache

`NavigationMode.KeepAlive` keeps the ViewModel in a per-region cache keyed by
`(region, ViewModel type)`. Navigating to the same type reuses the cached instance.

```csharp
await navigation.NavigateToAsync<SettingsViewModel>(mode: NavigationMode.KeepAlive);

navigation.ClearCache("MainRegion");   // or await navigation.ClearCacheAsync("MainRegion");
navigation.ClearAllCache();            // or await navigation.ClearAllCacheAsync();
```

ViewModels that are still referenced by a navigation stack are **not** disposed immediately;
their disposal is deferred until the last reference disappears. Disposal prefers
`IAsyncDisposable` on the async paths; the synchronous paths only call `IDisposable`
(ViewModels implementing only `IAsyncDisposable` are skipped there — use the async
variants for full cleanup).

### Disposal error semantics

Cache clearing and service disposal are **best-effort**: every ViewModel is attempted even
if one of them throws, so a single faulty `Dispose` can never leak the remaining
ViewModels. After cleanup and events finish, collected exceptions are rethrown —
a single exception is rethrown preserving its original stack trace, several are wrapped
in an `AggregateException`.

## Thread safety

All public members are safe to call from any thread. Navigation transitions and disposal
are serialized with an async lock; shared state is guarded by a dedicated lock.

## Native AOT / trimming

The library sets `IsAotCompatible=true` and annotates generic ViewModel parameters with
`[DynamicallyAccessedMembers(PublicConstructors)]`. ViewModels are resolved through
`IServiceProvider` — register them in DI and avoid `Activator.CreateInstance` at the
app layer. The `[AotRoute]` attribute exists for source-generator-based View resolution
in platform-specific layers.

## API overview

| Member | Description |
|--------|-------------|
| `NavigateToAsync<T>(region?, mode?)` | Navigate to a ViewModel type |
| `NavigateToAsync<T, TParam>(param, region?, mode?)` | Navigate with a typed parameter |
| `GoBackAsync(region?)` | Pop the current page |
| `CanGoBack(region?)` / `GetCurrentViewModel(region?)` / `IsActive<T>(region?)` / `GetCurrentMode(region?)` | Queries |
| `ClearCache(region?)` / `ClearCacheAsync(region?)` | Clear one region's KeepAlive cache |
| `ClearAllCache()` / `ClearAllCacheAsync()` | Clear all KeepAlive caches |
| `RegionNavigated` / `ViewModelDisposed` / `RegionCacheCleared` | Events |
| `AddFlowNavigation()` / `AddScopedFlowNavigation()` | DI registration |

## Contributing

Issues and pull requests are welcome. Please keep new code AOT/trimming compatible
(the test suite and `IsAotCompatible` flag guard this) and add tests for behavior changes.

## License

MIT — see [LICENSE.txt](LICENSE.txt).
