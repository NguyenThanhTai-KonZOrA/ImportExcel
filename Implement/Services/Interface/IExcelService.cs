using Implement.ViewModels;
using Microsoft.AspNetCore.Http;

namespace Implement.Services.Interface
{
    public interface IExcelService
    {
        Task<ImportSummaryDto> ImportAndValidateAsync(IFormFile file);
    }
}
