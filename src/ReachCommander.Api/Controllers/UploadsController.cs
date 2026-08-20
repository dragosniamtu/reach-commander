using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReachCommander.Api.Contracts.Uploads;
using ReachCommander.Api.Filters;
using ReachCommander.Api.Uploads;
using ReachCommander.Application.Uploads;
using ReachCommander.Infrastructure.Uploads;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/uploads")]
public sealed class UploadsController(
    IUploadService uploads,
    MultipartUploadReader reader,
    IOptions<UploadOptions> options) : ControllerBase
{
    [HttpGet("limits")]
    [ProducesResponseType<UploadLimitsDto>(StatusCodes.Status200OK)]
    public ActionResult<UploadLimitsDto> GetLimits() =>
        Ok(UploadLimitsDto.FromOptions(options.Value));

    [HttpPost]
    [DisableFormValueModelBinding]
    [ProducesResponseType<UploadResultDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<UploadResultDto>> Upload(
        [FromQuery] string sourceId,
        [FromQuery(Name = "path")] string directoryPath,
        CancellationToken cancellationToken)
    {
        ConfigureBodyLimit(HttpContext, options.Value);
        var parts = reader.ReadAsync(Request, options.Value, cancellationToken);
        var result = await uploads.UploadAsync(
            new UploadBatchCommand(sourceId, directoryPath),
            parts,
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, UploadResultDto.FromResult(result));
    }

    private static void ConfigureBodyLimit(HttpContext context, UploadOptions uploadOptions)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = uploadOptions.GetMaximumRequestBodyBytes();
        }
    }
}
