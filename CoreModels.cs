using System;
using System.Collections.Generic;

namespace NexusNetworkBinder
{
    public enum BinderState
    {
        Inactive,
        Preparing,
        Active,
        Degraded,
        RollingBack,
        Faulted
    }

    public enum ApplicationBindingMode
    {
        GameRoutesSafe = 0,
        ProxyCompatible = 1,
        ObserveOnly = 2
    }

    public enum AdapterPreference
    {
        Strong = 0,
        Weak = 1
    }

    public sealed record AdapterInfo(
        string Id,
        string Name,
        string Ip,
        string Description,
        int InterfaceIndex,
        string Gateway,
        bool IsVirtual = false)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Description)
            ? $"{Name} ({Ip})"
            : $"{Name} — {Description} ({Ip})";
    }

    public sealed class RouteItem
    {
        public string Cidr { get; set; } = "";
        public string Desc { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Source { get; set; } = "Manual";
    }

    public sealed class GameExeItem
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public DateTime? LastSeen { get; set; }
        public ApplicationBindingMode BindingMode { get; set; } = ApplicationBindingMode.GameRoutesSafe;
        public AdapterPreference PreferredAdapter { get; set; } = AdapterPreference.Strong;
        public string LaunchArguments { get; set; } = "";
    }

    public sealed class BinderOperationResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

        public static BinderOperationResult Ok(string message, IReadOnlyList<string>? details = null) =>
            new() { Success = true, Message = message, Details = details ?? Array.Empty<string>() };

        public static BinderOperationResult Fail(string message, IReadOnlyList<string>? details = null) =>
            new() { Success = false, Message = message, Details = details ?? Array.Empty<string>() };
    }

    public enum CompatibilityStatus
    {
        NotRunning,
        Compatible,
        NoMatch,
        Partial
    }

    public sealed class RouteCompatibilityResult
    {
        public string ExePath { get; set; } = "";
        public CompatibilityStatus Status { get; set; }
        public string Summary { get; set; } = "";
        public List<string> Details { get; set; } = new();
    }

    public sealed class ProxyLaunchResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public int LocalPort { get; init; }
    }
}
