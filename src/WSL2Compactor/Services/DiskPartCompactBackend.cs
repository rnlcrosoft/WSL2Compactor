using System.Text;

namespace WSL2Compactor.Services;

internal sealed class DiskPartCompactBackend : ICompactBackend
{
    private readonly ProcessRunner _processRunner;

    public DiskPartCompactBackend(ProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public string Name => "DiskPart";

    public async Task CompactAsync(string vhdPath, IProgress<CompactProgressUpdate> progress, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"wsl2compactor-{Guid.NewGuid():N}.txt");
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
            progress.Report(CompactProgressUpdate.Indeterminate("DiskPart", $"script: {scriptPath}", backend: Name));
            var result = await _processRunner.RunAsync("diskpart.exe", ["/s", scriptPath], new ProcessProgressAdapter(progress, "DiskPart", backend: Name), cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"diskpart compact vdisk failed with exit code {result.ExitCode}.");
            }

            progress.Report(CompactProgressUpdate.Complete("DiskPart", "completed", backend: Name));
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
