using System.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace TabTabGo.Storage.TestKit;

/// <summary>A host environment whose content root is a directory the test controls.</summary>
public sealed class TestHostEnvironment(string contentRoot, string? webRoot = null) : IHostEnvironment
{
    /// <summary>Web root; read through reflection by the path service (as for <c>IWebHostEnvironment</c>).</summary>
    public string? WebRootPath { get; } = webRoot;

    /// <inheritdoc />
    public string EnvironmentName { get; set; } = "Test";

    /// <inheritdoc />
    public string ApplicationName { get; set; } = "Tests";

    /// <inheritdoc />
    public string ContentRootPath { get; set; } = contentRoot;

    /// <inheritdoc />
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>A unique temporary directory deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    /// <summary>Creates the directory.</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ttg-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Absolute path.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of a temp folder.
        }
    }
}

/// <summary>Detects whether a Docker daemon is reachable, so emulator tests can skip cleanly.</summary>
public static class DockerAvailable
{
    private static readonly Lazy<bool> Probe = new(Check);

    /// <summary>Whether <c>docker info</c> succeeds.</summary>
    public static bool IsAvailable => Probe.Value;

    private static bool Check()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(15000))
            {
                process.Kill();
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
