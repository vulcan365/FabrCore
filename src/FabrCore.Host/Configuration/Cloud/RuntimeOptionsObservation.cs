using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Configuration.Cloud;

/// <summary>Observe access by actual consumers rather than resolving every options object from an inspector.</summary>
internal static class RuntimeOptionsObservation
{
    internal static void AddSnapshot<T>(IServiceCollection services, string section) where T : class, new() =>
        services.TryAddSingleton<IOptions<T>>(sp => new Snapshot<T>(sp.GetRequiredService<IOptionsFactory<T>>(),
            sp.GetRequiredService<RuntimeConfigurationState>(), section));

    internal static void AddMonitor<T>(IServiceCollection services, string section) where T : class, new() =>
        services.TryAddSingleton<IOptionsMonitor<T>>(sp => new Monitor<T>(
            new OptionsMonitor<T>(sp.GetRequiredService<IOptionsFactory<T>>(), sp.GetServices<IOptionsChangeTokenSource<T>>(),
                sp.GetRequiredService<IOptionsMonitorCache<T>>()), sp.GetRequiredService<RuntimeConfigurationState>(), section));

    private static void Capture<T>(RuntimeConfigurationState state, string section, T value, string name, bool known = true, bool live = false) where T : class, new()
    {
        var defaults = new T();
        foreach (var property in typeof(T).GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!(type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(TimeSpan) || type == typeof(decimal) || type == typeof(Guid))) continue;
            var key = name == Options.DefaultName ? section + ":" + property.Name : $"Runtime:Options:{typeof(T).Name}:{name}:{property.Name}";
            if (name == Options.DefaultName) state.Derived(key, Format(property.GetValue(defaults)), "Built-in options default.");
            state.Capture(new(key, Format(property.GetValue(value)), typeof(T).Name + (name.Length == 0 ? "" : " / " + name), AppliedKnown: known), refreshBaseline: live);
        }
    }
    private static string? Format(object? value) => value is TimeSpan duration ? duration.ToString("c", CultureInfo.InvariantCulture) :
        value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : value?.ToString();

    private sealed class Snapshot<T>(IOptionsFactory<T> factory, RuntimeConfigurationState state, string section) : IOptions<T> where T : class, new()
    {
        private readonly Lazy<T> value = new(() => factory.Create(Options.DefaultName));
        public T Value { get { var result = value.Value; Capture(state, section, result, Options.DefaultName); return result; } }
    }

    private sealed class Monitor<T>(OptionsMonitor<T> inner, RuntimeConfigurationState state, string section) : IOptionsMonitor<T>, IDisposable where T : class, new()
    {
        public T CurrentValue => Get(Options.DefaultName);
        public T Get(string? name)
        {
            var result = inner.Get(name); Capture(state, section, result, name ?? Options.DefaultName, live: true); return result;
        }
        public IDisposable? OnChange(Action<T, string?> listener) => inner.OnChange((value, name) =>
        {
            try { listener(value, name); Capture(state, section, value, name ?? Options.DefaultName, live: true); }
            catch { Capture(state, section, value, name ?? Options.DefaultName, known: false, live: true); throw; }
        });
        public void Dispose() => inner.Dispose();
    }
}
