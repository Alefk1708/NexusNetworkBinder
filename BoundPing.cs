using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NexusNetworkBinder
{
    internal sealed class BoundPingResult
    {
        public bool Success { get; init; }
        public long RoundtripMilliseconds { get; init; }
        public string Error { get; init; } = "";
    }

    internal static class BoundPing
    {
        private static readonly Regex TimeRegex = new(
            @"(?:time|tempo)\s*[=<]\s*(\d+)\s*ms",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static async Task<BoundPingResult> SendAsync(
            string sourceIp,
            string destination,
            int timeoutMs = 2000,
            CancellationToken cancellationToken = default)
        {
            if (!IPAddress.TryParse(sourceIp, out var source) || source.AddressFamily != AddressFamily.InterNetwork)
                return new BoundPingResult { Error = "Endereço de origem inválido." };

            IPAddress? target = null;
            if (IPAddress.TryParse(destination, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
                target = parsed;
            else
            {
                try
                {
                    target = (await Dns.GetHostAddressesAsync(destination, cancellationToken).ConfigureAwait(false))
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                }
                catch (Exception ex) { return new BoundPingResult { Error = ex.Message }; }
            }

            if (target == null) return new BoundPingResult { Error = "O destino não possui endereço IPv4." };

            var psi = new ProcessStartInfo
            {
                FileName = "ping.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-4");
            psi.ArgumentList.Add("-S");
            psi.ArgumentList.Add(source.ToString());
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(timeoutMs.ToString());
            psi.ArgumentList.Add(target.ToString());

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeoutMs + 2000);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                    return new BoundPingResult { Error = string.IsNullOrWhiteSpace(error) ? "Timeout" : error.Trim() };

                var match = TimeRegex.Match(output);
                if (match.Success && long.TryParse(match.Groups[1].Value, out var ms))
                    return new BoundPingResult { Success = true, RoundtripMilliseconds = ms };

                if (output.Contains("<1ms", StringComparison.OrdinalIgnoreCase))
                    return new BoundPingResult { Success = true, RoundtripMilliseconds = 1 };

                return new BoundPingResult { Error = "Resposta recebida, mas o tempo não pôde ser interpretado." };
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new BoundPingResult { Error = "Timeout" };
            }
            catch (Exception ex)
            {
                return new BoundPingResult { Error = ex.Message };
            }
        }
    }
}
