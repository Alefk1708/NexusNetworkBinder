using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NexusNetworkBinder
{
    /// <summary>
    /// Resolvedor DNS A mínimo que liga o socket UDP ao IPv4 do adaptador escolhido.
    /// É usado somente pelo proxy local para impedir que a resolução de nomes siga
    /// silenciosamente a interface padrão do Windows.
    /// </summary>
    internal static class BoundDnsResolver
    {
        private const int DnsPort = 53;
        private const int MaxPacketSize = 4096;
        private static readonly TimeSpan PerServerTimeout = TimeSpan.FromSeconds(4);

        public static async Task<IPAddress?> ResolveIPv4Async(
            string host,
            IPAddress sourceAddress,
            CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(host, out var literal))
                return literal.AddressFamily == AddressFamily.InterNetwork ? literal : null;

            var asciiHost = NormalizeHost(host);
            if (asciiHost == null) return null;

            var dnsServers = FindDnsServers(sourceAddress);
            foreach (var dnsServer in dnsServers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await QueryServerAsync(
                        asciiHost,
                        sourceAddress,
                        dnsServer,
                        cancellationToken).ConfigureAwait(false);
                    if (result != null) return result;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Timeout apenas deste servidor; tenta o próximo DNS da interface.
                }
                catch (SocketException)
                {
                    // Servidor indisponível; tenta o próximo.
                }
            }

            return null;
        }

        private static IReadOnlyList<IPAddress> FindDnsServers(IPAddress sourceAddress)
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var properties = networkInterface.GetIPProperties();
                    if (!properties.UnicastAddresses.Any(item => item.Address.Equals(sourceAddress)))
                        continue;

                    return properties.DnsAddresses
                        .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                        .Distinct()
                        .ToList();
                }
                catch
                {
                    // Continua procurando a interface correspondente.
                }
            }
            return Array.Empty<IPAddress>();
        }

        private static async Task<IPAddress?> QueryServerAsync(
            string host,
            IPAddress sourceAddress,
            IPAddress dnsServer,
            CancellationToken cancellationToken)
        {
            var transactionBytes = RandomNumberGenerator.GetBytes(2);
            var transactionId = BinaryPrimitives.ReadUInt16BigEndian(transactionBytes);
            var query = BuildQuery(host, transactionId);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(PerServerTimeout);
            var token = timeoutCts.Token;

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(sourceAddress, 0));
            socket.Connect(new IPEndPoint(dnsServer, DnsPort));
            await socket.SendAsync(query, SocketFlags.None, token).ConfigureAwait(false);

            var response = new byte[MaxPacketSize];
            var received = await socket.ReceiveAsync(response, SocketFlags.None, token).ConfigureAwait(false);
            return ParseResponse(response.AsSpan(0, received), transactionId);
        }

        private static byte[] BuildQuery(string host, ushort transactionId)
        {
            var packet = new List<byte>(512);
            AddUInt16(packet, transactionId);
            AddUInt16(packet, 0x0100); // Recursion desired.
            AddUInt16(packet, 1);      // QDCOUNT.
            AddUInt16(packet, 0);      // ANCOUNT.
            AddUInt16(packet, 0);      // NSCOUNT.
            AddUInt16(packet, 0);      // ARCOUNT.

            foreach (var label in host.Split('.'))
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                packet.Add((byte)bytes.Length);
                packet.AddRange(bytes);
            }
            packet.Add(0);
            AddUInt16(packet, 1); // A.
            AddUInt16(packet, 1); // IN.
            return packet.ToArray();
        }

        private static IPAddress? ParseResponse(ReadOnlySpan<byte> packet, ushort expectedTransactionId)
        {
            if (packet.Length < 12) return null;
            if (BinaryPrimitives.ReadUInt16BigEndian(packet) != expectedTransactionId) return null;

            var flags = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
            var isResponse = (flags & 0x8000) != 0;
            var truncated = (flags & 0x0200) != 0;
            var responseCode = flags & 0x000F;
            if (!isResponse || truncated || responseCode != 0) return null;

            var questionCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
            var answerCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
            var authorityCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(8, 2));
            var additionalCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(10, 2));
            var offset = 12;

            for (var i = 0; i < questionCount; i++)
            {
                if (!SkipName(packet, ref offset) || offset + 4 > packet.Length) return null;
                offset += 4;
            }

            var records = answerCount + authorityCount + additionalCount;
            for (var i = 0; i < records; i++)
            {
                if (!SkipName(packet, ref offset) || offset + 10 > packet.Length) return null;
                var type = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
                var recordClass = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2));
                var dataLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 8, 2));
                offset += 10;
                if (offset + dataLength > packet.Length) return null;

                if (type == 1 && recordClass == 1 && dataLength == 4)
                    return new IPAddress(packet.Slice(offset, 4));

                offset += dataLength;
            }

            return null;
        }

        private static bool SkipName(ReadOnlySpan<byte> packet, ref int offset)
        {
            var labels = 0;
            while (offset < packet.Length && labels++ < 128)
            {
                var length = packet[offset++];
                if (length == 0) return true;
                if ((length & 0xC0) == 0xC0)
                {
                    if (offset >= packet.Length) return false;
                    offset++;
                    return true;
                }
                if ((length & 0xC0) != 0 || offset + length > packet.Length) return false;
                offset += length;
            }
            return false;
        }

        private static string? NormalizeHost(string host)
        {
            try
            {
                var trimmed = host.Trim().TrimEnd('.');
                if (trimmed.Length is < 1 or > 253) return null;
                var ascii = new IdnMapping().GetAscii(trimmed).ToLowerInvariant();
                var labels = ascii.Split('.');
                if (labels.Any(label => label.Length is < 1 or > 63)) return null;
                return ascii;
            }
            catch
            {
                return null;
            }
        }

        private static void AddUInt16(List<byte> target, ushort value)
        {
            target.Add((byte)(value >> 8));
            target.Add((byte)value);
        }
    }
}
