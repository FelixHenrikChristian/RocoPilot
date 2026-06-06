namespace RocoPilot.Contracts.Services;

public interface IInterceptionDriverService
{
    Uri ReleasePageUri { get; }

    bool IsDriverInstalled();

    Task<InterceptionDriverInstallResult> InstallAsync(
        IProgress<InterceptionDriverInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record InterceptionDriverInstallProgress(
    string Message,
    double? Percent = null);

public sealed record InterceptionDriverInstallResult(
    bool WasAlreadyInstalled,
    string InstallerPath);
