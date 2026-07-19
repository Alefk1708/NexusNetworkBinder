using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NexusNetworkBinder
{
    internal sealed class InterfaceMetricSnapshot
    {
        public int InterfaceIndex { get; set; }
        public string AdapterId { get; set; } = "";
        public string AdapterName { get; set; } = "";
        public string AddressFamily { get; set; } = "IPv4";
        public string AutomaticMetric { get; set; } = "Enabled";
        public int InterfaceMetric { get; set; }
        public bool ChangeAttempted { get; set; }
        public bool ChangeApplied { get; set; }
    }

    internal sealed class OwnedRouteJournal
    {
        public string DestinationPrefix { get; set; } = "";
        public int InterfaceIndex { get; set; }
        public string NextHop { get; set; } = "";
        public int RouteMetric { get; set; } = 1;
        public bool WasPreExisting { get; set; }
        public int OriginalRouteMetric { get; set; } = -1;
        public bool ExistingMetricChanged { get; set; }
        public bool ApplyAttempted { get; set; }
        public bool Applied { get; set; }
    }

    internal sealed class FirewallRuleJournal
    {
        public string DisplayName { get; set; } = "";
        public string ProgramPath { get; set; } = "";
        public bool ApplyAttempted { get; set; }
        public bool Applied { get; set; }
    }

    internal sealed class NetworkJournal
    {
        public int SchemaVersion { get; set; } = 3;
        public string TransactionId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public bool ActivationCompleted { get; set; }
        public bool Restored { get; set; }
        public string WeakAdapterId { get; set; } = "";
        public string StrongAdapterId { get; set; } = "";
        public string WeakIp { get; set; } = "";
        public string StrongIp { get; set; } = "";
        public string StrongGateway { get; set; } = "";
        public List<InterfaceMetricSnapshot> Interfaces { get; set; } = new();
        public List<OwnedRouteJournal> Routes { get; set; } = new();
        public List<FirewallRuleJournal> FirewallRules { get; set; } = new();
    }

    internal static class AppPaths
    {
        public static string UserDataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NexusNetworkBinder");

        public static string MachineDataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NexusNetworkBinder");

        public static string ConfigPath => Path.Combine(UserDataDirectory, "config.json");
        public static string JournalPath => Path.Combine(MachineDataDirectory, "network-state.json");
        public static string LogPath => Path.Combine(UserDataDirectory, "nexus.log");
    }

    internal static class AtomicFile
    {
        public static void WriteAllText(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temp = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, content);
            File.Move(temp, path, true);
        }
    }

    internal static class NetworkStateStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static bool Exists => File.Exists(AppPaths.JournalPath);

        public static void Save(NetworkJournal journal) =>
            AtomicFile.WriteAllText(AppPaths.JournalPath, JsonSerializer.Serialize(journal, JsonOptions));

        public static bool TryLoad(out NetworkJournal? journal, out string error)
        {
            journal = null;
            error = "";
            if (!Exists) return true;

            try
            {
                journal = JsonSerializer.Deserialize<NetworkJournal>(
                    File.ReadAllText(AppPaths.JournalPath),
                    JsonOptions);
                if (journal == null)
                {
                    error = "O arquivo de diário está vazio ou não contém uma transação válida.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "O diário de rede não pôde ser lido: " + ex.Message;
                return false;
            }
        }

        public static NetworkJournal? Load() =>
            TryLoad(out var journal, out _) ? journal : null;

        public static void Delete()
        {
            try { if (Exists) File.Delete(AppPaths.JournalPath); } catch { }
        }
    }
}
