using System.Diagnostics;
using System.Text;

namespace WSL2Compactor.Services;

internal sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var argumentList = arguments.ToList();
        log?.Report($"> {FormatCommand(fileName, argumentList)}");

        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            var line = NormalizeProcessText(args.Data);
            stdout.AppendLine(line);
            log?.Report(line);
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            var line = NormalizeProcessText(args.Data);
            stderr.AppendLine(line);
            log?.Report(line);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        await Task.Run(process.WaitForExit, CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();

        log?.Report($"Exit code: {process.ExitCode}");
        log?.Report($"Elapsed: {stopwatch.Elapsed:c}");

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public async Task<bool> CommandExistsAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync(fileName, arguments, log: null, cancellationToken).ConfigureAwait(false);
            return result.Succeeded && !string.IsNullOrWhiteSpace(NormalizeProcessText(result.StandardOutput));
        }
        catch
        {
            return false;
        }
    }

    public static string NormalizeProcessText(string text)
        => text.Replace("\0", string.Empty).TrimEnd();

    private static string FormatCommand(string fileName, IReadOnlyCollection<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return Quote(fileName);
        }

        return $"{Quote(fileName)} {string.Join(" ", arguments.Select(Quote))}";
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may already be gone.
        }
    }
}
