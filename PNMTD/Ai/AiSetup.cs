namespace PNMTD.Ai;

/// <summary>The AI's services (docs/ai-operations-plan.md §3): off until <c>Ai__Enabled=true</c> and a key.</summary>
public static class AiSetup
{
    public static IServiceCollection AddAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection("Ai"));
        services.AddHttpClient(ChatClient.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<AiBudget>();
        services.AddSingleton<AiRequestLog>();
        services.AddSingleton<ChatClient>();
        services.AddSingleton<DecisionClient>();
        services.AddSingleton<AiPipeline>();
        services.AddSingleton<AiWorker>();
        services.AddSingleton<AiWorkQueue>();
        // Decisions and the mail handlers (docs/ai-operations-plan.md §5, §6).
        services.AddSingleton<Decisions.DecisionBook>();
        services.AddSingleton<Decisions.IDecisionKind, Decisions.MailRuleDecision>();
        services.AddSingleton<Decisions.IDecisionKind, Decisions.MailSuspiciousDecision>();
        services.AddSingleton<Decisions.IDecisionKind, Decisions.MailOutcomeDecision>();
        services.AddSingleton<Decisions.DecisionMaintenanceTask>();
        services.AddSingleton<IAiWorkHandler, Mails.MailAnalyseHandler>();
        services.AddSingleton<IAiWorkHandler, Mails.MailImproveHandler>();
        services.AddSingleton<IAiWorkHandler, Mails.MailEvaluateHandler>();
        return services;
    }
}
