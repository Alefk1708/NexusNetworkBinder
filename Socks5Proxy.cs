using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NexusNetworkBinder
{
    internal sealed class Socks5ProxyServer : IAsyncDisposable
    {
        private readonly IPAddress _outboundSource;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<int, Task> _clients = new();
        private Task? _acceptLoop;
        private int _clientId;

        public int Port { get; private set; }

        public Socks5ProxyServer(string outboundSourceIp)
        {
            if (!IPAddress.TryParse(outboundSourceIp, out _outboundSource!) ||
                _outboundSource.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("O proxy seguro atualmente requer um endereço IPv4 válido.", nameof(outboundSourceIp));

            _listener = new TcpListener(IPAddress.Loopback, 0);
        }

        public Task StartAsync()
        {
            _listener.Start(128);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync(_cts.Token);
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    if (_clients.Count >= 256)
                    {
                        client.Dispose();
                        continue;
                    }
                    var id = Interlocked.Increment(ref _clientId);
                    var task = HandleClientAsync(client, cancellationToken);
                    _clients[id] = task;
                    _ = task.ContinueWith(
                        completedTask =>
                        {
                            _clients.TryRemove(id, out var removedTask);
                            _ = completedTask.Exception;
                            _ = removedTask;
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    client?.Dispose();
                    if (!cancellationToken.IsCancellationRequested)
                        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                var hello = new byte[2];
                if (!await ReadExactAsync(stream, hello, cancellationToken).ConfigureAwait(false) || hello[0] != 0x05)
                    return;

                var methods = new byte[hello[1]];
                if (!await ReadExactAsync(stream, methods, cancellationToken).ConfigureAwait(false)) return;
                if (!methods.Contains((byte)0x00))
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0xFF }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken).ConfigureAwait(false);

                var request = new byte[4];
                if (!await ReadExactAsync(stream, request, cancellationToken).ConfigureAwait(false) || request[0] != 0x05)
                    return;
                if (request[1] != 0x01)
                {
                    await SendReplyAsync(stream, 0x07, null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                string host;
                switch (request[3])
                {
                    case 0x01:
                        var ipv4 = new byte[4];
                        if (!await ReadExactAsync(stream, ipv4, cancellationToken).ConfigureAwait(false)) return;
                        host = new IPAddress(ipv4).ToString();
                        break;
                    case 0x03:
                        var length = new byte[1];
                        if (!await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false)) return;
                        if (length[0] == 0)
                        {
                            await SendReplyAsync(stream, 0x08, null, cancellationToken).ConfigureAwait(false);
                            return;
                        }
                        var domain = new byte[length[0]];
                        if (!await ReadExactAsync(stream, domain, cancellationToken).ConfigureAwait(false)) return;
                        host = System.Text.Encoding.ASCII.GetString(domain);
                        break;
                    case 0x04:
                        var ipv6 = new byte[16];
                        if (!await ReadExactAsync(stream, ipv6, cancellationToken).ConfigureAwait(false)) return;
                        host = new IPAddress(ipv6).ToString();
                        break;
                    default:
                        await SendReplyAsync(stream, 0x08, null, cancellationToken).ConfigureAwait(false);
                        return;
                }

                var portBuffer = new byte[2];
                if (!await ReadExactAsync(stream, portBuffer, cancellationToken).ConfigureAwait(false)) return;
                var port = BinaryPrimitives.ReadUInt16BigEndian(portBuffer);
                if (port == 0)
                {
                    await SendReplyAsync(stream, 0x01, null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(20));
                var connectToken = connectCts.Token;

                IPAddress? destinationAddress;
                try
                {
                    destinationAddress = await BoundDnsResolver.ResolveIPv4Async(
                        host,
                        _outboundSource,
                        connectToken).ConfigureAwait(false);
                }
                catch
                {
                    destinationAddress = null;
                }

                if (destinationAddress == null)
                {
                    await SendReplyAsync(stream, 0x04, null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                using var outbound = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    outbound.Bind(new IPEndPoint(_outboundSource, 0));
                    await outbound.ConnectAsync(new IPEndPoint(destinationAddress, port), connectToken).ConfigureAwait(false);
                }
                catch
                {
                    await SendReplyAsync(stream, 0x05, null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await SendReplyAsync(stream, 0x00, outbound.LocalEndPoint as IPEndPoint, cancellationToken).ConfigureAwait(false);
                using var outboundStream = new NetworkStream(outbound, ownsSocket: false);
                using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var upstream = PumpAsync(stream, outboundStream, relayCts.Token);
                var downstream = PumpAsync(outboundStream, stream, relayCts.Token);
                await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
                relayCts.Cancel();
                try { await Task.WhenAll(upstream, downstream).ConfigureAwait(false); } catch { }
            }
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        private static async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
        {
            var buffer = new byte[32 * 1024];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task SendReplyAsync(
            NetworkStream stream,
            byte code,
            IPEndPoint? endpoint,
            CancellationToken cancellationToken)
        {
            var address = endpoint?.Address.AddressFamily == AddressFamily.InterNetwork
                ? endpoint.Address.GetAddressBytes()
                : new byte[] { 0, 0, 0, 0 };
            var port = endpoint?.Port ?? 0;
            var response = new byte[10];
            response[0] = 0x05;
            response[1] = code;
            response[2] = 0x00;
            response[3] = 0x01;
            Array.Copy(address, 0, response, 4, 4);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(8, 2), (ushort)port);
            await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            if (_acceptLoop != null)
            {
                try { await _acceptLoop.ConfigureAwait(false); } catch { }
            }
            try { await Task.WhenAll(_clients.Values).ConfigureAwait(false); } catch { }
            _cts.Dispose();
        }
    }

    internal sealed class ProxyBindingManager : IAsyncDisposable
    {
        private readonly Dictionary<string, Socks5ProxyServer> _servers = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<ProxyLaunchResult> LaunchAsync(
            GameExeItem application,
            AdapterInfo adapter,
            CancellationToken cancellationToken = default)
        {
            if (!application.Enabled)
                return new ProxyLaunchResult { Message = "O aplicativo está desabilitado." };
            if (!File.Exists(application.Path))
                return new ProxyLaunchResult { Message = "O executável não foi encontrado." };
            if (ApplicationSafety.IsRestrictedProxyTarget(application.Path, out var safetyReason))
                return new ProxyLaunchResult { Message = safetyReason };

            var executableName = Path.GetFileNameWithoutExtension(application.Path).ToLowerInvariant();
            try
            {
                if (Process.GetProcessesByName(executableName).Any())
                    return new ProxyLaunchResult
                    {
                        Message = "Feche todas as instâncias desse aplicativo antes de iniciá-lo pelo proxy. Processos já abertos podem reutilizar sockets e ignorar o adaptador escolhido."
                    };
            }
            catch { }

            Socks5ProxyServer server;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_servers.TryGetValue(adapter.Ip, out var existingServer))
                {
                    existingServer = new Socks5ProxyServer(adapter.Ip);
                    await existingServer.StartAsync().ConfigureAwait(false);
                    _servers[adapter.Ip] = existingServer;
                }
                server = existingServer;
            }
            finally { _gate.Release(); }

            var psi = new ProcessStartInfo
            {
                FileName = application.Path,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(application.Path) ?? Environment.CurrentDirectory
            };

            var chromiumFamily = new[] { "chrome", "msedge", "brave", "vivaldi", "opera", "opera_gx", "discord" };
            var chromiumBrowserFamily = new[] { "chrome", "msedge", "brave", "vivaldi", "opera", "opera_gx" };
            var isChromium = chromiumFamily.Any(executableName.Contains);
            var isFirefox = executableName.Contains("firefox", StringComparison.OrdinalIgnoreCase);
            if (isChromium)
            {
                psi.ArgumentList.Add($"--proxy-server=socks5://127.0.0.1:{server.Port}");
                // Navegadores Chromium podem preferir QUIC/HTTP3 (UDP), que não passa por
                // um proxy SOCKS5 CONNECT. Desativar QUIC evita que o tráfego web contorne
                // silenciosamente o adaptador escolhido. O Discord não recebe essa flag
                // porque o áudio UDP continuará explicitamente fora deste modo TCP.
                if (chromiumBrowserFamily.Any(executableName.Contains))
                    psi.ArgumentList.Add("--disable-quic");
            }
            else if (isFirefox)
            {
                var profileDirectory = CreateFirefoxProxyProfile(adapter, server.Port);
                psi.ArgumentList.Add("-no-remote");
                psi.ArgumentList.Add("-profile");
                psi.ArgumentList.Add(profileDirectory);
            }
            else
            {
                var proxyUri = $"socks5://127.0.0.1:{server.Port}";
                psi.Environment["ALL_PROXY"] = proxyUri;
                psi.Environment["all_proxy"] = proxyUri;
            }

            foreach (var argument in SplitArguments(application.LaunchArguments))
                psi.ArgumentList.Add(argument);

            try
            {
                Process.Start(psi);
                var note = isChromium
                    ? "Aplicativo Chromium iniciado com proxy SOCKS5 e saída TCP/DNS vinculada ao adaptador selecionado."
                    : isFirefox
                        ? "Firefox iniciado em perfil isolado com SOCKS5, DNS remoto e HTTP/3 desativado."
                        : "Aplicativo iniciado com ALL_PROXY. O programa precisa oferecer suporte a essa variável para obedecer ao vínculo.";
                return new ProxyLaunchResult { Success = true, LocalPort = server.Port, Message = note };
            }
            catch (Exception ex)
            {
                return new ProxyLaunchResult { Message = ex.Message, LocalPort = server.Port };
            }
        }

        private static string CreateFirefoxProxyProfile(AdapterInfo adapter, int port)
        {
            var safeId = new string(adapter.Id.Where(char.IsLetterOrDigit).Take(48).ToArray());
            if (string.IsNullOrWhiteSpace(safeId)) safeId = adapter.InterfaceIndex.ToString();
            var directory = Path.Combine(
                AppPaths.UserDataDirectory,
                "proxy-profiles",
                "firefox-" + safeId);
            Directory.CreateDirectory(directory);

            var preferences = string.Join(Environment.NewLine, new[]
            {
                "// Generated by Nexus Network Binder. Dedicated proxy profile.",
                "user_pref(\"network.proxy.type\", 1);",
                "user_pref(\"network.proxy.socks\", \"127.0.0.1\");",
                $"user_pref(\"network.proxy.socks_port\", {port});",
                "user_pref(\"network.proxy.socks_version\", 5);",
                "user_pref(\"network.proxy.socks_remote_dns\", true);",
                "user_pref(\"network.http.http3.enable\", false);",
                "user_pref(\"network.trr.mode\", 5);",
                "user_pref(\"browser.shell.checkDefaultBrowser\", false);"
            }) + Environment.NewLine;
            AtomicFile.WriteAllText(Path.Combine(directory, "user.js"), preferences);
            return directory;
        }

        private static IEnumerable<string> SplitArguments(string? arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) yield break;
            var current = new System.Text.StringBuilder();
            var quoted = false;
            foreach (var c in arguments)
            {
                if (c == '"') { quoted = !quoted; continue; }
                if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                }
                else current.Append(c);
            }
            if (current.Length > 0) yield return current.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var server in _servers.Values)
                    await server.DisposeAsync().ConfigureAwait(false);
                _servers.Clear();
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }
    }
}
