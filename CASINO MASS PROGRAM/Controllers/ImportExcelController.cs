using Implement.Services.Interface;
using Microsoft.AspNetCore.Mvc;

namespace CASINO_MASS_PROGRAM.Controllers;

[Route("api/[controller]")]
[ApiController]
public class ImportExcelController : ControllerBase
{
    private readonly IExcelService _excelService;

    public ImportExcelController(IExcelService excelService)
    {
        _excelService = excelService;
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Upload([FromForm] IFormFile file)
    {
        try
        {
            if (file is null || file.Length == 0) return BadRequest("File is required.");
            if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                return BadRequest("Only .xlsx files are supported.");

            var result = await _excelService.ImportAndValidateAsync(file);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("GetSummary/{batchId}")]
    public async Task<IActionResult> GetSummary([FromRoute] Guid batchId)
    {
        try
        {
            var batch = await _excelService.GetBatchSummaryAsync(batchId);
            return Ok(batch);
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("{batchId:guid}/annotated")]
    public async Task<IActionResult> DownloadAnnotated([FromRoute] Guid batchId)
    {
        try
        {
            var result = await _excelService.DownloadAnnotatedAsync(batchId);
            return File(result.Content, result.ContentType, result.FileName);
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("approve/{batchId}")]
    public async Task<IActionResult> Approve([FromRoute] Guid batchId)
    {
        var result = await _excelService.ApprovedImport(batchId);
        return Ok(result);
    }
}