using PNMTD.Lib.Helper;

namespace PNMTD.Availability;

public static class AvailabilitySetup
{
    public static IServiceCollection AddAvailability(this IServiceCollection services)
    {
        services.AddSingleton<AvailabilityPlan>();
        services.AddSingleton<AvailabilityService>();
        services.AddSingleton<AvailabilityFillTask>();
        return services;
    }
}

/// <summary>
/// Keeps the plan filled for the next 365 days (docs/cookie-plan.md §8.2): once a day, the day that newly comes into reach
/// (and whatever else is empty) is filled from each person's default week. It never touches what a person set.
/// </summary>
public sealed class AvailabilityFillTask(AvailabilityService service, ILogger<AvailabilityFillTask> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        DateOnly? done = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var today = AvailabilityPlan.Today;
            if (done != today)
            {
                try
                {
                    var result = await service.FillAsync(null, again: false, "the nightly fill", stoppingToken);
                    if (result.Written > 0)
                    {
                        logger.LogInformation("The availability plan's fill wrote {Count} cell(s)", result.Written);
                    }
                    done = today;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "The availability plan's fill failed");
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }
}
