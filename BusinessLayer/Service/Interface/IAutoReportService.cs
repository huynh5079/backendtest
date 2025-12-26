using BusinessLayer.Service.Interface;
using BusinessLayer.DTOs.Reports;
using System.Threading;
using System.Threading.Tasks;

namespace BusinessLayer.Service.Interface
{
    public interface IAutoReportService
    {
        /// <summary>
        /// Check attendance and create auto-reports for students with excessive absences
        /// </summary>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Number of auto-reports created</returns>
        Task<int> CheckAndCreateAutoReportsAsync(CancellationToken ct = default);

        /// <summary>
        /// Get paginated list of auto-reports for admin
        /// </summary>
        Task<AutoReportPagedResponse> GetAutoReportsAsync(AutoReportQuery query, CancellationToken ct = default);
    }
}
