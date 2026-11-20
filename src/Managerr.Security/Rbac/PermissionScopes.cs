namespace Managerr.Security.Rbac;

public sealed record ScopeDescriptor(string Scope, string Category, string Label, string Description);

public static class PermissionScopes
{
    public const string CatalogRead = "catalog:read";
    public const string CatalogRequest = "catalog:request";
    public const string CatalogAutoApprove = "catalog:auto_approve";
    public const string CatalogWrite = "catalog:write";
    public const string CatalogDelete = "catalog:delete";

    public const string ProfilesRead = "profiles:read";
    public const string ProfilesWrite = "profiles:write";

    public const string IndexersRead = "indexers:read";
    public const string IndexersWrite = "indexers:write";
    public const string IndexersSearch = "indexers:search";

    public const string DownloadersRead = "downloaders:read";
    public const string DownloadersWrite = "downloaders:write";

    public const string QueueRead = "queue:read";
    public const string QueueManage = "queue:manage";
    public const string QuarantineReview = "quarantine:review";

    public const string LangVerifyRead = "langverify:read";
    public const string LangVerifyExecute = "langverify:execute";

    public const string CleanerrRead = "cleanerr:read";
    public const string CleanerrRescue = "cleanerr:rescue";
    public const string CleanerrExclude = "cleanerr:exclude";
    public const string CleanerrWrite = "cleanerr:write";
    public const string CleanerrExecute = "cleanerr:execute";

    public const string UsersRead = "users:read";
    public const string UsersManage = "users:manage";
    public const string RolesManage = "roles:manage";

    public const string MigrationRead = "migration:read";
    public const string MigrationExecute = "migration:execute";

    public const string WebhooksManage = "webhooks:manage";
    public const string SystemAdmin = "system:admin";
    public const string AuditRead = "audit:read";
    public const string MetricsRead = "metrics:read";

    public const string CompatSonarr = "compat:sonarr";
    public const string CompatRadarr = "compat:radarr";
    public const string CompatLidarr = "compat:lidarr";
    public const string CompatReadarr = "compat:readarr";
    public const string CompatProwlarr = "compat:prowlarr";

    public static readonly IReadOnlyList<ScopeDescriptor> Catalog =
    [
        new(CatalogRead, "Catalog", "Browse Catalog", "View movies, series, music, and books within permitted libraries."),
        new(CatalogRequest, "Catalog", "Request Media", "Submit media requests within permitted libraries."),
        new(CatalogAutoApprove, "Catalog", "Auto-Approve Requests", "Automatically approve media requests without manager review."),
        new(CatalogWrite, "Catalog", "Add & Edit Media", "Add items directly, toggle monitoring, and edit metadata."),
        new(CatalogDelete, "Catalog", "Delete Media", "Remove catalog entries and media files from disk."),

        new(ProfilesRead, "Profiles", "View Profiles", "View quality and language policies."),
        new(ProfilesWrite, "Profiles", "Edit Profiles", "Create and modify composable quality & language policies."),

        new(IndexersRead, "Acquisition", "View Indexers", "View configured indexers and health status."),
        new(IndexersWrite, "Acquisition", "Manage Indexers", "Configure Cardigann/Torznab/Newznab indexers and proxies."),
        new(IndexersSearch, "Acquisition", "Interactive Search", "Trigger manual release searches and grabs."),
        new(DownloadersRead, "Acquisition", "View Downloaders", "View download client configurations."),
        new(DownloadersWrite, "Acquisition", "Manage Downloaders", "Configure Torrent and Usenet download clients."),

        new(QueueRead, "Queue & Verification", "View Queue", "Inspect active downloads and import status."),
        new(QueueManage, "Queue & Verification", "Manage Queue", "Remove, retry, or manually import queued downloads."),
        new(QuarantineReview, "Queue & Verification", "Review Quarantine", "Listen to speech snippets and approve/blocklist quarantined grabs."),
        new(LangVerifyRead, "Queue & Verification", "View Language Verification", "Inspect stream language verification reports."),
        new(LangVerifyExecute, "Queue & Verification", "Run Language Scans", "Trigger on-demand file language verification jobs."),

        new(CleanerrRead, "Cleanerr", "View Cleanerr", "View Cleanerr workflows, deletion queue, and exclusions."),
        new(CleanerrRescue, "Cleanerr", "Rescue Queued Media", "Rescue or postpone items staged in Cleanerr's Leaving Soon queue."),
        new(CleanerrExclude, "Cleanerr", "Create Cleanerr Exclusions", "Protect media items, tags, labels, or users from Cleanerr deletion."),
        new(CleanerrWrite, "Cleanerr", "Manage Cleanerr Studio & Discord", "Create, edit, import/export Cleanerr workflows, Discord settings, and delete exclusion rules."),
        new(CleanerrExecute, "Cleanerr", "Execute Cleanerr Cleanup", "Run live Cleanerr workflows, process the deletion queue, and delete staged media immediately."),

        new(UsersRead, "Administration", "View Users", "View synced Plex users and role assignments."),
        new(UsersManage, "Administration", "Manage Users", "Sync Plex users, assign roles, and edit user quotas."),
        new(RolesManage, "Administration", "Manage Custom Roles", "Create, clone, edit, and delete custom RBAC roles."),
        new(MigrationRead, "Administration", "View Migration", "Inspect *arr and Overseerr dry-run migration plans."),
        new(MigrationExecute, "Administration", "Execute Migration", "Run migrations, Shadow Mode, and cutover."),
        new(WebhooksManage, "Administration", "Manage Webhooks", "Configure outbound signed webhooks and Discord bot integration."),
        new(SystemAdmin, "Administration", "System Administration", "System settings, backups, secrets, and break-glass credentials."),
        new(AuditRead, "Administration", "Read Audit Logs", "Inspect immutable security and system audit events."),
        new(MetricsRead, "Telemetry", "Read Metrics", "Read health, queue counts, and Prometheus /metrics."),

        new(CompatSonarr, "Compatibility Shims", "Sonarr v3 Shim", "Access /compat/sonarr/api/v3/*."),
        new(CompatRadarr, "Compatibility Shims", "Radarr v3 Shim", "Access /compat/radarr/api/v3/*."),
        new(CompatLidarr, "Compatibility Shims", "Lidarr v1 Shim", "Access /compat/lidarr/api/v1/*."),
        new(CompatReadarr, "Compatibility Shims", "Readarr v1 Shim", "Access /compat/readarr/api/v1/*."),
        new(CompatProwlarr, "Compatibility Shims", "Prowlarr v1 / Torznab Shim", "Access /compat/prowlarr/api/v1/* and /torznab/*."),
    ];

    public static readonly IReadOnlySet<string> AllValidScopes =
        Catalog.Select(s => s.Scope).ToHashSet(StringComparer.Ordinal);
}
