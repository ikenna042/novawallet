using Microsoft.AspNetCore.Mvc;
using NovaWallet.Application;

namespace NovaWallet.Api.Infrastructure;

public static class ControllerExtensions
{
    public const string ReplayedHeader = "Idempotent-Replayed";

    /// <summary>
    /// 201 for both the first call and a replay (so a retrying client sees the same status),
    /// with a header telling them which one it was.
    /// </summary>
    public static IActionResult IdempotentCreated(this ControllerBase controller, IdempotentResult<TransactionReceipt> result)
    {
        controller.Response.Headers[ReplayedHeader] = result.Replayed ? "true" : "false";
        return controller.StatusCode(StatusCodes.Status201Created, result.Value);
    }
}
