using System.Text;

namespace WslAutoCompact.Services;

internal sealed class DiskPartCompactBackend : ICompactBackend
{
    private readonly ProcessRunner _processRunner;

    public DiskPartCompactBackend(ProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public string Name => "DiskPart";

    public async Task CompactAsync(string vhdPath, IProgress<string> log, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"wsl-auto-compact-{Guid.NewGuid():N}.txt");
        var script = string.Join(Environment.NewLine, [
            $"select vdisk file=\"{vhdPath}\"",
            "attach vdisk readonly",
            "compact vdisk",
            "detach vdisk",
            "exit",
            string.Empty
        ]);

        await File.WriteAllTextAsync(scriptPath, script, Encoding.Unicode, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            log.Report($"diskpart script: {scriptPath}");
            var result = await _processRunner.RunAsync("diskpart.exe", ["/s", scriptPath], log, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"diskpart compact vdisk failed with exit code {result.ExitCode}.");
            }
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch
            {
                // Temporary cleanup failure should not hide the compact result.
            }
        }
    }
}
