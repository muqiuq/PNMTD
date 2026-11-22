using PNMTD.Ai;

namespace PNMTD.Knowledge;

public static class KnowledgeSetup
{
    /// <summary>
    /// The knowledge base (docs/locations-knowledge-plan.md §6, §7, §8.3): its services per request, and in memory who is
    /// editing, the views' 30 minutes, the running AI scans and the AI question's draw. The <c>KnowledgeBase</c> settings
    /// come with the files (FilesSetup).
    /// </summary>
    public static IServiceCollection AddKnowledgeBase(this IServiceCollection services)
    {
        services.AddScoped<KbRevisions>();
        services.AddScoped<KbPageService>();
        services.AddScoped<KbPageList>();
        services.AddScoped<KbChanges>();
        // Likes and uses, and their e-mails to the page's authors (docs/knowledge-base-extensions-plan.md §9).
        services.AddScoped<KbReactionService>();
        // The knowledge map (docs/knowledge-base-extensions-plan.md §8).
        services.AddScoped<KbMap>();
        services.AddSingleton<Notifications.IUserNotificationRenderer, KbLikedNotice>();
        services.AddSingleton<Notifications.IUserNotificationRenderer, KbUsedNotice>();
        services.AddScoped<KbTags>();
        services.AddScoped<KbAiTags>();
        services.AddSingleton<KbPresence>();
        services.AddSingleton<KbViews>();
        services.AddSingleton<KbAiScans>();
        services.AddSingleton(new KbAiDraw(new Random()));
        services.AddSingleton<IAiWorkHandler, KbTagsHandler>();
        return services;
    }
}
