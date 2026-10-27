using System.Diagnostics;

namespace WgSwitch.Services;

/// <summary>Запуск внешних утилит без окна консоли, асинхронно.</summary>
internal static class Proc
{
    internal readonly record struct Result(int ExitCode, string Output, string Error);

    internal static async Task<Result> RunAsync(string exe, string args, int timeoutMs = 20_000, string? stdin = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить {Path.GetFileName(exe)}");
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            p.StandardInput.Close();
        }
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
            throw new TimeoutException($"{Path.GetFileName(exe)} не ответил вовремя");
        }

        return new Result(p.ExitCode, (await stdout.ConfigureAwait(false)).Trim(), (await stderr.ConfigureAwait(false)).Trim());
    }
}
