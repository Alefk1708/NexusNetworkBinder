using System;
using System.IO;
using System.Linq;

namespace NexusNetworkBinder
{
    internal static class ApplicationSafety
    {
        // O proxy local é intencionalmente bloqueado para executáveis e caminhos
        // conhecidos de jogos/anti-cheats. Eles devem usar Rotas seguras.
        private static readonly string[] RestrictedProxyTokens =
        {
            "valorant-win64-shipping",
            "valorant",
            "riotclientservices",
            "league of legends",
            "leagueclient",
            "easyanticheat",
            "easyanticheat_eos",
            "eac_launcher",
            "beservice",
            "battleye",
            "fortniteclient-win64-shipping",
            "destiny2",
            "vgc",
            "vgtray",
            "vgk",
            "eaanticheat",
            "anticheatexpert",
            "faceit"
        };

        private static readonly string[] KnownProxyClientTokens =
        {
            "chrome",
            "msedge",
            "firefox",
            "brave",
            "vivaldi",
            "opera",
            "discord",
            "slack",
            "teams",
            "telegram",
            "spotify",
            "thunderbird",
            "curl",
            "wget",
            "git"
        };

        public static bool IsRestrictedProxyTarget(string path, out string reason)
        {
            reason = "";
            string normalized;
            try { normalized = Path.GetFullPath(path ?? "").ToLowerInvariant(); }
            catch { normalized = (path ?? "").ToLowerInvariant(); }

            var name = Path.GetFileNameWithoutExtension(normalized);
            if (string.IsNullOrWhiteSpace(name))
            {
                reason = "O caminho do executável é inválido.";
                return true;
            }

            var token = RestrictedProxyTokens.FirstOrDefault(normalized.Contains);
            if (token == null) return false;

            reason = "Este executável ou caminho parece pertencer a um jogo protegido ou anti-cheat. " +
                     "Por segurança, use 'Rotas seguras' ou 'Somente observar'; o modo Proxy TCP não será iniciado.";
            return true;
        }

        public static bool IsKnownProxyClient(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path ?? "").ToLowerInvariant();
            return KnownProxyClientTokens.Any(name.Contains);
        }
    }
}
