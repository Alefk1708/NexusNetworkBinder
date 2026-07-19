using System;
using System.Net;
using System.Net.Sockets;

namespace NexusNetworkBinder
{
    public static class CidrUtility
    {
        public static bool TryNormalizeIPv4(string? input, out string normalized, out string error)
        {
            normalized = "";
            error = "";
            if (string.IsNullOrWhiteSpace(input))
            {
                error = "O IP/CIDR está vazio.";
                return false;
            }

            var text = input.Trim();
            var parts = text.Split('/', StringSplitOptions.TrimEntries);
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip) ||
                ip.AddressFamily != AddressFamily.InterNetwork)
            {
                error = "Use um endereço IPv4 válido, por exemplo 45.7.36.0/22 ou 8.8.8.8.";
                return false;
            }

            var prefix = 32;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > 32))
            {
                error = "O prefixo CIDR precisa estar entre 0 e 32.";
                return false;
            }

            var value = ToUInt32(ip);
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var network = value & mask;
            normalized = $"{FromUInt32(network)}/{prefix}";
            return true;
        }

        public static bool TryGetRange(string cidr, out uint start, out uint end)
        {
            start = end = 0;
            if (!TryNormalizeIPv4(cidr, out var normalized, out _)) return false;
            var parts = normalized.Split('/');
            var prefix = int.Parse(parts[1]);
            var network = ToUInt32(IPAddress.Parse(parts[0]));
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            start = network;
            end = network | ~mask;
            return true;
        }

        public static bool Contains(string cidr, string ip)
        {
            if (!TryGetRange(cidr, out var start, out var end) ||
                !IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                return false;
            var value = ToUInt32(address);
            return value >= start && value <= end;
        }

        public static string GetProbeAddress(string cidr)
        {
            if (!TryGetRange(cidr, out var start, out var end)) throw new ArgumentException("CIDR inválido.", nameof(cidr));
            var probe = end > start + 1 ? start + 1 : start;
            return FromUInt32(probe).ToString();
        }

        public static int GetPrefixLength(string cidr)
        {
            if (!TryNormalizeIPv4(cidr, out var normalized, out _)) return -1;
            return int.Parse(normalized.Split('/')[1]);
        }

        public static bool IsUnsafeDestination(string cidr, out string reason)
        {
            reason = "";
            if (!TryNormalizeIPv4(cidr, out var normalized, out reason)) return true;
            var prefix = GetPrefixLength(normalized);
            if (prefix < 8)
            {
                reason = "Prefixos menores que /8 desviam uma parte excessiva da Internet e foram bloqueados por segurança.";
                return true;
            }

            if (!TryGetRange(normalized, out var start, out var end))
            {
                reason = "Não foi possível calcular o intervalo da rota.";
                return true;
            }

            var protectedRanges = new[]
            {
                ("0.0.0.0/8", "endereços não especificados"),
                ("10.0.0.0/8", "rede privada 10/8"),
                ("100.64.0.0/10", "CGNAT"),
                ("127.0.0.0/8", "loopback"),
                ("169.254.0.0/16", "link-local"),
                ("172.16.0.0/12", "rede privada 172.16/12"),
                ("192.168.0.0/16", "rede privada 192.168/16"),
                ("224.0.0.0/4", "multicast"),
                ("240.0.0.0/4", "endereços reservados")
            };

            foreach (var (blockedCidr, label) in protectedRanges)
            {
                TryGetRange(blockedCidr, out var blockedStart, out var blockedEnd);
                if (start <= blockedEnd && end >= blockedStart)
                {
                    reason = $"A rota se sobrepõe a {label} e foi bloqueada para proteger a rede local.";
                    return true;
                }
            }

            return false;
        }

        private static uint ToUInt32(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }

        private static IPAddress FromUInt32(uint value) => new(new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        });
    }
}
