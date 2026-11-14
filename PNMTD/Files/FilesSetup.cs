using PNMTD.Knowledge;

namespace PNMTD.Files;

public static class FilesSetup
{
    /// <summary>
    /// Uploaded files (docs/locations-knowledge-plan.md §5.5, §8.3), and the <c>KnowledgeBase</c> settings they share with the
    /// knowledge base and the locations (§8.4).
    /// </summary>
    public static IServiceCollection AddFiles(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KnowledgeBaseOptions>(configuration.GetSection(KnowledgeBaseOptions.Section));
        services.AddSingleton<FileStore>();
        services.AddSingleton<FileAdmin>();
        services.AddSingleton<UploadGuard>();
        return services;
    }
}
