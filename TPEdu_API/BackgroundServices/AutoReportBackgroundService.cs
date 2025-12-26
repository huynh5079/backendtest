using BusinessLayer.Service.Interface;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TPEdu_API.BackgroundServices
{
    public class AutoReportBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AutoReportBackgroundService> _logger;
        private readonly TimeSpan _interval = TimeSpan.FromHours(24); // Run daily
        private readonly TimeSpan _targetTime = TimeSpan.FromHours(2); // Run at 2 AM Vietnam time

        public AutoReportBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<AutoReportBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[AUTO-REPORT] Background service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Calculate time until next 2 AM
                    var now = DateTime.Now;
                    var nextRun = now.Date.AddHours(2); // Today at 2 AM

                    if (now.TimeOfDay >= _targetTime)
                    {
                        // If past 2 AM today, schedule for tomorrow
                        nextRun = nextRun.AddDays(1);
                    }

                    var delay = nextRun - now;
                    _logger.LogInformation($"[AUTO-REPORT] Next run scheduled at {nextRun:yyyy-MM-dd HH:mm:ss} (in {delay.TotalHours:F1} hours)");

                    try
                    {
                        await Task.Delay(delay, stoppingToken);
                    }
                    catch (TaskCanceledException)
                    {
                        _logger.LogInformation("[AUTO-REPORT] Background service stopping");
                        break;
                    }

                    // Run the auto-report check
                    await RunAutoReportCheckAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AUTO-REPORT] Error in background service loop");
                    
                    // Wait an hour before retrying on error
                    try
                    {
                        await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                    }
                    catch (TaskCanceledException)
                    {
                        _logger.LogInformation("[AUTO-REPORT] Background service stopping after error");
                        break;
                    }
                }
            }
        }

        private async Task RunAutoReportCheckAsync(CancellationToken ct)
        {
            using var scope = _serviceProvider.CreateScope();
            var autoReportService = scope.ServiceProvider.GetRequiredService<IAutoReportService>();

            try
            {
                var startTime = DateTime.Now;
                _logger.LogInformation("[AUTO-REPORT] Starting daily absence check...");

                var count = await autoReportService.CheckAndCreateAutoReportsAsync(ct);

                var duration = DateTime.Now - startTime;
                _logger.LogInformation($"[AUTO-REPORT] Completed: {count} auto-reports created in {duration.TotalSeconds:F1}s");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AUTO-REPORT] Error during auto-report check");
            }
        }
    }
}
