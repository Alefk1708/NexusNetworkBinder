using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NexusNetworkBinder
{
    internal sealed class ProcessResult
    {
        public int ExitCode { get; init; }
        public string StandardOutput { get; init; } = "";
        public string StandardError { get; init; } = "";
        public bool TimedOut { get; init; }
        public bool Success => !TimedOut && ExitCode == 0;
    }

    internal static class PowerShellRunner
    {
        public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

        public static ProcessResult Run(string script, TimeSpan? timeout = null) =>
            RunAsync(script, timeout ?? TimeSpan.FromSeconds(25), CancellationToken.None).GetAwaiter().GetResult();

        public static async Task<ProcessResult> RunAsync(
            string script,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
                "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" + script));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(encoded);

            using var process = new Process { StartInfo = psi };
            try
            {
                if (!process.Start())
                    return new ProcessResult { ExitCode = -1, StandardError = "Não foi possível iniciar o PowerShell." };

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);

                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return new ProcessResult
                    {
                        ExitCode = -1,
                        StandardOutput = await stdoutTask.ConfigureAwait(false),
                        StandardError = await stderrTask.ConfigureAwait(false),
                        TimedOut = !cancellationToken.IsCancellationRequested
                    };
                }

                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = await stdoutTask.ConfigureAwait(false),
                    StandardError = await stderrTask.ConfigureAwait(false)
                };
            }
            catch (Exception ex)
            {
                return new ProcessResult { ExitCode = -1, StandardError = ex.Message };
            }
        }
    }
}
