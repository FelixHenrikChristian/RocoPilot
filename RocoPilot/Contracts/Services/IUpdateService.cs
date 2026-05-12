using RocoPilot.Models;

namespace RocoPilot.Contracts.Services;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckUpdateAsync(UpdateOption option);
}
