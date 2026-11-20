using Dapper;
using Microsoft.Data.Sqlite;

namespace Managerr.Datastore.Migrations;

public sealed record SqlMigration(int Version, string Name, string UpSql, bool VacuumAfter = false);

public interface ISchemaMigrator
{
    Task MigrateAllAsync(CancellationToken ct = default);
}

public sealed class SchemaMigrator(ISqliteDbFactory dbFactory) : ISchemaMigrator
{
    // Append-only history. The Cleanerr (005, 014, 015, part of 007) and AV1 compression (008) migrations stay even though
    // those modules live on the feature/cleanerr-compression branch for now: their tables and columns are dormant on
    // main, existing databases keep their data, and version numbers stay in sync when the modules come back.
    private static readonly IReadOnlyList<SqlMigration> CoreMigrations =
    [
        new SqlMigration(
            Version: 1,
            Name: "001_phase1_core_users_roles_sessions_fts5",
            UpSql: """
                CREATE TABLE IF NOT EXISTS app_settings (
                    key         TEXT PRIMARY KEY NOT NULL,
                    value       TEXT NOT NULL,
                    is_secret   INTEGER NOT NULL DEFAULT 0,
                    updated_at  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS libraries (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    slug                TEXT NOT NULL UNIQUE,
                    name                TEXT NOT NULL,
                    media_type          TEXT NOT NULL,
                    root_folder_path    TEXT NOT NULL,
                    plex_section_id     TEXT NULL,
                    created_at          TEXT NOT NULL,
                    updated_at          TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS users (
                    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                    plex_user_id                INTEGER NOT NULL UNIQUE,
                    plex_uuid                   TEXT NOT NULL,
                    username                    TEXT NOT NULL,
                    email                       TEXT NULL,
                    avatar_url                  TEXT NULL,
                    is_server_owner             INTEGER NOT NULL DEFAULT 0,
                    is_home_user                INTEGER NOT NULL DEFAULT 0,
                    is_active                   INTEGER NOT NULL DEFAULT 1,
                    shared_plex_section_ids_json TEXT NOT NULL DEFAULT '[]',
                    preferred_audio_lang        TEXT NULL,
                    preferred_sub_lang          TEXT NULL,
                    discord_user_id             TEXT NULL,
                    discord_username            TEXT NULL,
                    seerr_user_id               INTEGER NULL,
                    break_glass_password_hash   TEXT NULL,
                    last_login_at               TEXT NULL,
                    created_at                  TEXT NOT NULL,
                    updated_at                  TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_users_discord_user_id ON users(discord_user_id) WHERE discord_user_id IS NOT NULL;

                CREATE TABLE IF NOT EXISTS roles (
                    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                    key                         TEXT NOT NULL UNIQUE,
                    name                        TEXT NOT NULL,
                    description                 TEXT NOT NULL DEFAULT '',
                    color                       TEXT NOT NULL DEFAULT '#3b82f6',
                    is_system_preset            INTEGER NOT NULL DEFAULT 0,
                    is_immutable                INTEGER NOT NULL DEFAULT 0,
                    scopes_json                 TEXT NOT NULL DEFAULT '[]',
                    library_access_mode         TEXT NOT NULL DEFAULT 'inherit_from_plex',
                    allowed_library_ids_json    TEXT NOT NULL DEFAULT '[]',
                    auto_approve_requests       INTEGER NOT NULL DEFAULT 0,
                    movie_request_quota         INTEGER NULL,
                    tv_season_request_quota     INTEGER NULL,
                    music_book_request_quota    INTEGER NULL,
                    cleanerr_exclusion_limit    INTEGER NULL,
                    created_at                  TEXT NOT NULL,
                    updated_at                  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS user_roles (
                    user_id     INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    role_id     INTEGER NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
                    assigned_at TEXT NOT NULL,
                    PRIMARY KEY (user_id, role_id)
                );

                CREATE TABLE IF NOT EXISTS user_sessions (
                    id          TEXT PRIMARY KEY NOT NULL,
                    user_id     INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    token_hash  TEXT NOT NULL UNIQUE,
                    csrf_token  TEXT NOT NULL,
                    user_agent  TEXT NULL,
                    ip_address  TEXT NULL,
                    expires_at  TEXT NOT NULL,
                    revoked_at  TEXT NULL,
                    created_at  TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_user_sessions_user_id ON user_sessions(user_id);

                CREATE TABLE IF NOT EXISTS api_keys (
                    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                    owner_user_id               INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    name                        TEXT NOT NULL,
                    public_prefix               TEXT NOT NULL UNIQUE,
                    secret_hash                 TEXT NOT NULL UNIQUE,
                    masked_hint                 TEXT NOT NULL,
                    scopes_json                 TEXT NOT NULL DEFAULT '[]',
                    allowed_library_ids_json    TEXT NOT NULL DEFAULT '[]',
                    allowed_cidrs_json          TEXT NOT NULL DEFAULT '[]',
                    expires_at                  TEXT NULL,
                    last_used_at                TEXT NULL,
                    revoked_at                  TEXT NULL,
                    created_at                  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS idempotency_records (
                    key_hash                TEXT PRIMARY KEY NOT NULL,
                    actor_key               TEXT NOT NULL,
                    method                  TEXT NOT NULL,
                    path                    TEXT NOT NULL,
                    status_code             INTEGER NOT NULL,
                    response_content_type   TEXT NOT NULL,
                    response_body           TEXT NOT NULL,
                    expires_at              TEXT NOT NULL,
                    created_at              TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS catalog_search_items (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    media_type      TEXT NOT NULL,
                    title           TEXT NOT NULL,
                    alt_titles      TEXT NOT NULL DEFAULT '',
                    year            INTEGER NULL,
                    external_ids    TEXT NOT NULL DEFAULT ''
                );

                CREATE VIRTUAL TABLE IF NOT EXISTS catalog_search_fts USING fts5(
                    title,
                    alt_titles,
                    media_type,
                    external_ids,
                    content='catalog_search_items',
                    content_rowid='id',
                    tokenize='unicode61 remove_diacritics 2'
                );

                CREATE TRIGGER IF NOT EXISTS trg_catalog_search_ai AFTER INSERT ON catalog_search_items BEGIN
                    INSERT INTO catalog_search_fts(rowid, title, alt_titles, media_type, external_ids)
                    VALUES (new.id, new.title, new.alt_titles, new.media_type, new.external_ids);
                END;

                CREATE TRIGGER IF NOT EXISTS trg_catalog_search_ad AFTER DELETE ON catalog_search_items BEGIN
                    INSERT INTO catalog_search_fts(catalog_search_fts, rowid, title, alt_titles, media_type, external_ids)
                    VALUES ('delete', old.id, old.title, old.alt_titles, old.media_type, old.external_ids);
                END;

                CREATE TRIGGER IF NOT EXISTS trg_catalog_search_au AFTER UPDATE ON catalog_search_items BEGIN
                    INSERT INTO catalog_search_fts(catalog_search_fts, rowid, title, alt_titles, media_type, external_ids)
                    VALUES ('delete', old.id, old.title, old.alt_titles, old.media_type, old.external_ids);
                    INSERT INTO catalog_search_fts(rowid, title, alt_titles, media_type, external_ids)
                    VALUES (new.id, new.title, new.alt_titles, new.media_type, new.external_ids);
                END;
                """),
        new SqlMigration(
            Version: 2,
            Name: "002_phase2_indexers_downloaders_blocklist",
            UpSql: """
                CREATE TABLE IF NOT EXISTS indexer_proxies (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    name                TEXT NOT NULL UNIQUE,
                    proxy_type          TEXT NOT NULL,
                    host                TEXT NOT NULL,
                    port                INTEGER NOT NULL,
                    username            TEXT NULL,
                    password_enc        TEXT NULL,
                    request_timeout_sec INTEGER NOT NULL DEFAULT 60,
                    tags_json           TEXT NOT NULL DEFAULT '[]',
                    created_at          TEXT NOT NULL,
                    updated_at          TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS indexers (
                    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                    name                        TEXT NOT NULL UNIQUE,
                    implementation              TEXT NOT NULL,
                    definition_id               TEXT NULL,
                    protocol                    TEXT NOT NULL,
                    base_url                    TEXT NOT NULL,
                    api_key_enc                 TEXT NULL,
                    extra_settings_json         TEXT NOT NULL DEFAULT '{}',
                    categories_json             TEXT NOT NULL DEFAULT '[]',
                    tags_json                   TEXT NOT NULL DEFAULT '[]',
                    priority                    INTEGER NOT NULL DEFAULT 25,
                    rss_enabled                 INTEGER NOT NULL DEFAULT 1,
                    interactive_search_enabled  INTEGER NOT NULL DEFAULT 1,
                    auto_search_enabled         INTEGER NOT NULL DEFAULT 1,
                    request_delay_ms            INTEGER NOT NULL DEFAULT 1000,
                    consecutive_failures        INTEGER NOT NULL DEFAULT 0,
                    disabled_until              TEXT NULL,
                    last_error                  TEXT NULL,
                    created_at                  TEXT NOT NULL,
                    updated_at                  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS download_clients (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    name                    TEXT NOT NULL UNIQUE,
                    implementation          TEXT NOT NULL,
                    protocol                TEXT NOT NULL,
                    host                    TEXT NOT NULL,
                    port                    INTEGER NOT NULL,
                    use_ssl                 INTEGER NOT NULL DEFAULT 0,
                    url_base                TEXT NULL,
                    username                TEXT NULL,
                    password_enc            TEXT NULL,
                    api_key_enc             TEXT NULL,
                    category_mappings_json  TEXT NOT NULL DEFAULT '{}',
                    extra_settings_json     TEXT NOT NULL DEFAULT '{}',
                    priority                INTEGER NOT NULL DEFAULT 1,
                    is_enabled              INTEGER NOT NULL DEFAULT 1,
                    remove_completed        INTEGER NOT NULL DEFAULT 1,
                    remove_failed           INTEGER NOT NULL DEFAULT 1,
                    created_at              TEXT NOT NULL,
                    updated_at              TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS remote_path_mappings (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    download_client_id  INTEGER NULL REFERENCES download_clients(id) ON DELETE CASCADE,
                    host                TEXT NOT NULL,
                    remote_path         TEXT NOT NULL,
                    local_path          TEXT NOT NULL,
                    created_at          TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS download_queue_strikes (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    download_client_id      INTEGER NOT NULL REFERENCES download_clients(id) ON DELETE CASCADE,
                    external_download_id    TEXT NOT NULL,
                    info_hash               TEXT NULL,
                    title                   TEXT NOT NULL,
                    strike_reason           TEXT NOT NULL,
                    strike_count            INTEGER NOT NULL DEFAULT 1,
                    first_seen_at           TEXT NOT NULL,
                    last_strike_at          TEXT NOT NULL,
                    UNIQUE(download_client_id, external_download_id, strike_reason)
                );

                CREATE TABLE IF NOT EXISTS recurring_blocklist_hashes (
                    info_hash   TEXT PRIMARY KEY NOT NULL,
                    title       TEXT NOT NULL,
                    reason      TEXT NOT NULL,
                    blocked_at  TEXT NOT NULL
                );
                """),
        new SqlMigration(
            Version: 3,
            Name: "003_phase3_polymorphic_catalog_and_metadata_crosswalk",
            UpSql: """
                ALTER TABLE libraries ADD COLUMN metadata_waterfall_json TEXT NOT NULL DEFAULT '[]';
                ALTER TABLE libraries ADD COLUMN default_profile_id INTEGER NULL;

                CREATE TABLE IF NOT EXISTS media_works (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    library_id              INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
                    work_kind               TEXT NOT NULL,
                    primary_provider        TEXT NOT NULL DEFAULT 'auto',
                    episode_ordering_scheme TEXT NOT NULL DEFAULT 'aired',
                    title                   TEXT NOT NULL,
                    sort_title              TEXT NOT NULL,
                    alt_titles_json         TEXT NOT NULL DEFAULT '[]',
                    original_language       TEXT NOT NULL DEFAULT 'en',
                    year                    INTEGER NULL,
                    status                  TEXT NOT NULL DEFAULT 'released',
                    overview                TEXT NOT NULL DEFAULT '',
                    poster_url              TEXT NULL,
                    monitored               INTEGER NOT NULL DEFAULT 1,
                    locked_fields_json      TEXT NOT NULL DEFAULT '[]',
                    external_ids_json       TEXT NOT NULL DEFAULT '{}',
                    tags_json               TEXT NOT NULL DEFAULT '[]',
                    plex_rating_key         TEXT NULL,
                    fts_doc_id              INTEGER NULL,
                    added_by_user_id        INTEGER NULL REFERENCES users(id) ON DELETE SET NULL,
                    added_at                TEXT NOT NULL,
                    updated_at              TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_media_works_library ON media_works(library_id);

                CREATE TABLE IF NOT EXISTS media_containers (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    work_id             INTEGER NOT NULL REFERENCES media_works(id) ON DELETE CASCADE,
                    container_kind      TEXT NOT NULL,
                    container_number    INTEGER NOT NULL DEFAULT 1,
                    title               TEXT NOT NULL,
                    release_date        TEXT NULL,
                    monitored           INTEGER NOT NULL DEFAULT 1,
                    external_ids_json   TEXT NOT NULL DEFAULT '{}',
                    plex_rating_key     TEXT NULL,
                    created_at          TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_media_containers_work ON media_containers(work_id);

                CREATE TABLE IF NOT EXISTS media_units (
                    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                    work_id                     INTEGER NOT NULL REFERENCES media_works(id) ON DELETE CASCADE,
                    container_id                INTEGER NOT NULL REFERENCES media_containers(id) ON DELETE CASCADE,
                    unit_kind                   TEXT NOT NULL,
                    unit_number                 INTEGER NOT NULL DEFAULT 1,
                    absolute_number             INTEGER NULL,
                    title                       TEXT NOT NULL,
                    air_or_release_date         TEXT NULL,
                    monitored                   INTEGER NOT NULL DEFAULT 1,
                    profile_override_id         INTEGER NULL,
                    required_audio_langs_json   TEXT NOT NULL DEFAULT '[]',
                    required_sub_langs_json     TEXT NOT NULL DEFAULT '[]',
                    quality_cutoff_met          INTEGER NOT NULL DEFAULT 0,
                    language_cutoff_met         INTEGER NOT NULL DEFAULT 0,
                    plex_rating_key             TEXT NULL,
                    watch_count                 INTEGER NOT NULL DEFAULT 0,
                    last_watched_at             TEXT NULL,
                    created_at                  TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_media_units_work ON media_units(work_id);
                CREATE INDEX IF NOT EXISTS ix_media_units_air_date ON media_units(air_or_release_date);

                CREATE TABLE IF NOT EXISTS media_editions (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    unit_id         INTEGER NOT NULL REFERENCES media_units(id) ON DELETE CASCADE,
                    edition_name    TEXT NOT NULL,
                    is_preferred    INTEGER NOT NULL DEFAULT 1,
                    created_at      TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS media_files (
                    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                    work_id                     INTEGER NOT NULL REFERENCES media_works(id) ON DELETE CASCADE,
                    unit_id                     INTEGER NOT NULL REFERENCES media_units(id) ON DELETE CASCADE,
                    edition_id                  INTEGER NULL REFERENCES media_editions(id) ON DELETE SET NULL,
                    relative_path               TEXT NOT NULL,
                    size_bytes                  INTEGER NOT NULL DEFAULT 0,
                    quality_name                TEXT NOT NULL,
                    resolution                  TEXT NULL,
                    video_codec                 TEXT NULL,
                    hdr_format                  TEXT NULL,
                    audio_codec                 TEXT NULL,
                    detected_audio_langs_json   TEXT NOT NULL DEFAULT '[]',
                    detected_sub_langs_json     TEXT NOT NULL DEFAULT '[]',
                    verified_status             TEXT NOT NULL DEFAULT 'unverified',
                    inode_hardlinked            INTEGER NOT NULL DEFAULT 0,
                    imported_at                 TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_media_files_unit ON media_files(unit_id);

                CREATE TABLE IF NOT EXISTS media_requests (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    requester_user_id   INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    library_id          INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
                    work_id             INTEGER NULL REFERENCES media_works(id) ON DELETE SET NULL,
                    media_vertical      TEXT NOT NULL,
                    title               TEXT NOT NULL,
                    external_ids_json   TEXT NOT NULL DEFAULT '{}',
                    status              TEXT NOT NULL,
                    requested_audio_lang TEXT NULL,
                    requested_sub_lang  TEXT NULL,
                    created_at          TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_media_requests_user_created ON media_requests(requester_user_id, created_at DESC);

                CREATE TABLE IF NOT EXISTS external_id_links (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    source_provider TEXT NOT NULL,
                    source_id       TEXT NOT NULL,
                    target_provider TEXT NOT NULL,
                    target_id       TEXT NOT NULL,
                    media_vertical  TEXT NOT NULL,
                    confidence      REAL NOT NULL DEFAULT 1.0,
                    updated_at      TEXT NOT NULL,
                    UNIQUE(source_provider, source_id, target_provider)
                );

                CREATE TABLE IF NOT EXISTS custom_metadata_webhooks (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    provider_key            TEXT NOT NULL UNIQUE,
                    display_name            TEXT NOT NULL,
                    supported_verticals_json TEXT NOT NULL DEFAULT '[]',
                    search_url_template     TEXT NOT NULL,
                    lookup_url_template     TEXT NULL,
                    api_key_enc             TEXT NULL,
                    created_at              TEXT NOT NULL
                );
                """),
        new SqlMigration(
            Version: 4,
            Name: "004_phase4_profiles_language_policies_and_verification",
            UpSql: """
                CREATE TABLE IF NOT EXISTS language_policies (
                    id                              INTEGER PRIMARY KEY AUTOINCREMENT,
                    slug                            TEXT NOT NULL UNIQUE,
                    name                            TEXT NOT NULL,
                    description                     TEXT NOT NULL DEFAULT '',
                    is_system_preset                INTEGER NOT NULL DEFAULT 0,
                    tiers_json                      TEXT NOT NULL DEFAULT '[]',
                    enable_language_upgrades        INTEGER NOT NULL DEFAULT 1,
                    language_cutoff_tier            INTEGER NOT NULL DEFAULT 1,
                    tradeoff_mode                   TEXT NOT NULL DEFAULT 'bounded_stepdown',
                    max_quality_tier_drop           INTEGER NOT NULL DEFAULT 1,
                    minimum_absolute_quality        TEXT NULL,
                    preserve_previous_as_alternate  INTEGER NOT NULL DEFAULT 0,
                    verification_mode               TEXT NOT NULL DEFAULT 'verify_content_on_upgrades_and_ambiguous',
                    created_at                      TEXT NOT NULL,
                    updated_at                      TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS profile_mixins (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    slug            TEXT NOT NULL UNIQUE,
                    name            TEXT NOT NULL,
                    category        TEXT NOT NULL,
                    rules_json      TEXT NOT NULL DEFAULT '[]',
                    created_at      TEXT NOT NULL,
                    updated_at      TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS custom_formats (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    trash_id                TEXT NULL UNIQUE,
                    name                    TEXT NOT NULL,
                    include_when_renaming   INTEGER NOT NULL DEFAULT 0,
                    specifications_json     TEXT NOT NULL DEFAULT '[]',
                    default_score           INTEGER NOT NULL DEFAULT 0,
                    created_at              TEXT NOT NULL,
                    updated_at              TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS quality_profiles (
                    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                    slug                        TEXT NOT NULL UNIQUE,
                    name                        TEXT NOT NULL,
                    media_vertical              TEXT NOT NULL,
                    is_system_preset            INTEGER NOT NULL DEFAULT 0,
                    allowed_qualities_json      TEXT NOT NULL DEFAULT '[]',
                    quality_cutoff              TEXT NOT NULL,
                    language_policy_id          INTEGER NULL REFERENCES language_policies(id) ON DELETE SET NULL,
                    mixin_ids_json              TEXT NOT NULL DEFAULT '[]',
                    custom_format_scores_json   TEXT NOT NULL DEFAULT '{}',
                    min_trait_score             INTEGER NOT NULL DEFAULT 0,
                    weight_quality              REAL NOT NULL DEFAULT 1.0,
                    weight_language             REAL NOT NULL DEFAULT 1.0,
                    weight_traits               REAL NOT NULL DEFAULT 1.0,
                    weight_efficiency           REAL NOT NULL DEFAULT 1.0,
                    retain_audio_langs_json     TEXT NOT NULL DEFAULT '[]',
                    retain_sub_langs_json       TEXT NOT NULL DEFAULT '[]',
                    created_at                  TEXT NOT NULL,
                    updated_at                  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS verification_jobs (
                    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                    unit_id                     INTEGER NULL REFERENCES media_units(id) ON DELETE SET NULL,
                    media_file_id               INTEGER NULL REFERENCES media_files(id) ON DELETE SET NULL,
                    staged_file_path            TEXT NOT NULL,
                    media_vertical              TEXT NOT NULL,
                    verdict                     TEXT NOT NULL,
                    overall_confidence          REAL NOT NULL,
                    header_auto_patched         INTEGER NOT NULL DEFAULT 0,
                    detected_audio_langs_json   TEXT NOT NULL DEFAULT '[]',
                    detected_sub_langs_json     TEXT NOT NULL DEFAULT '[]',
                    report_json                 TEXT NOT NULL,
                    duration_ms                 INTEGER NOT NULL,
                    created_at                  TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_verification_jobs_unit ON verification_jobs(unit_id);

                CREATE TABLE IF NOT EXISTS quarantined_imports (
                    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                    verification_job_id         INTEGER NOT NULL REFERENCES verification_jobs(id) ON DELETE CASCADE,
                    unit_id                     INTEGER NULL REFERENCES media_units(id) ON DELETE SET NULL,
                    release_title               TEXT NOT NULL,
                    info_hash                   TEXT NULL,
                    staged_file_path            TEXT NOT NULL,
                    expected_languages_json     TEXT NOT NULL DEFAULT '[]',
                    detected_languages_json     TEXT NOT NULL DEFAULT '[]',
                    confidence                  REAL NOT NULL,
                    reason                      TEXT NOT NULL,
                    snippet_paths_json          TEXT NOT NULL DEFAULT '[]',
                    status                      TEXT NOT NULL DEFAULT 'quarantined',
                    reviewed_by_user_id         INTEGER NULL REFERENCES users(id) ON DELETE SET NULL,
                    reviewed_at                 TEXT NULL,
                    created_at                  TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_quarantined_imports_status ON quarantined_imports(status);
                """),
        new SqlMigration(
            Version: 5,
            Name: "005_phase5_cleanerr_lifecycle_engine",
            UpSql: """
                CREATE TABLE IF NOT EXISTS cleanerr_media_items (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    plex_rating_key         TEXT NOT NULL UNIQUE,
                    parent_rating_key       TEXT NULL,
                    grandparent_rating_key  TEXT NULL,
                    parent_title            TEXT NULL,
                    grandparent_title       TEXT NULL,
                    library_id              INTEGER NULL REFERENCES libraries(id) ON DELETE SET NULL,
                    work_id                 INTEGER NULL REFERENCES media_works(id) ON DELETE SET NULL,
                    unit_id                 INTEGER NULL REFERENCES media_units(id) ON DELETE SET NULL,
                    media_type              TEXT NOT NULL,
                    title                   TEXT NOT NULL,
                    year                    INTEGER NULL,
                    season_number           INTEGER NULL,
                    episode_number          INTEGER NULL,
                    size_bytes              INTEGER NOT NULL DEFAULT 0,
                    resolution              TEXT NULL,
                    rating                  REAL NULL,
                    genres_json             TEXT NOT NULL DEFAULT '[]',
                    tags_json               TEXT NOT NULL DEFAULT '[]',
                    monitored               INTEGER NOT NULL DEFAULT 1,
                    added_at                TEXT NOT NULL,
                    last_viewed_at          TEXT NULL,
                    view_count              INTEGER NOT NULL DEFAULT 0,
                    viewed_by_json          TEXT NOT NULL DEFAULT '[]',
                    active_stream_count     INTEGER NOT NULL DEFAULT 0,
                    seerr_id                INTEGER NULL,
                    seerr_requested_by      TEXT NULL,
                    seerr_requested_at      TEXT NULL,
                    requester_watched       INTEGER NOT NULL DEFAULT 0,
                    updated_at              TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_cleanerr_media_type ON cleanerr_media_items(media_type);
                CREATE INDEX IF NOT EXISTS ix_cleanerr_media_parent ON cleanerr_media_items(parent_rating_key, grandparent_rating_key);

                CREATE TABLE IF NOT EXISTS cleanerr_workflows (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    name                TEXT NOT NULL,
                    description         TEXT NOT NULL DEFAULT '',
                    enabled             INTEGER NOT NULL DEFAULT 1,
                    dry_run             INTEGER NOT NULL DEFAULT 1,
                    schedule_cron       TEXT NULL,
                    graph_json          TEXT NOT NULL DEFAULT '{"nodes":[],"edges":[]}',
                    last_run_at         TEXT NULL,
                    last_run_status     TEXT NULL,
                    created_at          TEXT NOT NULL,
                    updated_at          TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS cleanerr_runs (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    workflow_id         INTEGER NULL REFERENCES cleanerr_workflows(id) ON DELETE SET NULL,
                    trigger_type        TEXT NOT NULL,
                    dry_run             INTEGER NOT NULL DEFAULT 1,
                    status              TEXT NOT NULL,
                    started_at          TEXT NOT NULL,
                    finished_at         TEXT NOT NULL,
                    summary_json        TEXT NOT NULL DEFAULT '{}'
                );
                CREATE INDEX IF NOT EXISTS ix_cleanerr_runs_wf ON cleanerr_runs(workflow_id);

                CREATE TABLE IF NOT EXISTS cleanerr_deletion_queue (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    media_item_id           INTEGER NOT NULL REFERENCES cleanerr_media_items(id) ON DELETE CASCADE,
                    workflow_id             INTEGER NULL REFERENCES cleanerr_workflows(id) ON DELETE SET NULL,
                    granularity             TEXT NOT NULL DEFAULT 'movie',
                    staged_at               TEXT NOT NULL,
                    delete_at               TEXT NOT NULL,
                    grace_days              INTEGER NOT NULL DEFAULT 7,
                    status                  TEXT NOT NULL DEFAULT 'staged',
                    plex_collection_name    TEXT NULL,
                    action_config_json      TEXT NOT NULL DEFAULT '{}',
                    rescued_reason          TEXT NULL,
                    executed_at             TEXT NULL,
                    logs_json               TEXT NOT NULL DEFAULT '[]'
                );
                CREATE INDEX IF NOT EXISTS ix_cleanerr_queue_status ON cleanerr_deletion_queue(status, delete_at);

                CREATE TABLE IF NOT EXISTS cleanerr_exclusion_rules (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    media_item_id           INTEGER NULL REFERENCES cleanerr_media_items(id) ON DELETE SET NULL,
                    external_id             TEXT NULL,
                    rule_type               TEXT NOT NULL DEFAULT 'media_item',
                    value                   TEXT NOT NULL,
                    granularity             TEXT NOT NULL DEFAULT 'movie',
                    workflow_id             INTEGER NULL REFERENCES cleanerr_workflows(id) ON DELETE CASCADE,
                    reason                  TEXT NULL,
                    created_by              TEXT NOT NULL DEFAULT 'admin',
                    created_by_user_id      INTEGER NULL REFERENCES users(id) ON DELETE SET NULL,
                    expires_at              TEXT NULL,
                    created_at              TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_cleanerr_excl_type ON cleanerr_exclusion_rules(rule_type, media_item_id);
                """),
        new SqlMigration(
            Version: 6,
            Name: "006_phase6_migration_and_shadow_mode",
            UpSql: """
                CREATE TABLE IF NOT EXISTS migration_sources (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    name                TEXT NOT NULL,
                    source_kind         TEXT NOT NULL,
                    transport_mode      TEXT NOT NULL DEFAULT 'sqlite_file',
                    file_or_url         TEXT NOT NULL,
                    api_key_enc         TEXT NULL,
                    readarr_slot_hint   TEXT NOT NULL DEFAULT 'auto',
                    path_mappings_json  TEXT NOT NULL DEFAULT '{}',
                    is_enabled          INTEGER NOT NULL DEFAULT 1,
                    last_synced_at      TEXT NULL,
                    created_at          TEXT NOT NULL,
                    updated_at          TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS migration_runs (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    mode                    TEXT NOT NULL,
                    uplift_language_cfs     INTEGER NOT NULL DEFAULT 1,
                    status                  TEXT NOT NULL,
                    started_at              TEXT NOT NULL,
                    finished_at             TEXT NOT NULL,
                    summary_json            TEXT NOT NULL DEFAULT '{}'
                );
                CREATE INDEX IF NOT EXISTS ix_migration_runs_started ON migration_runs(started_at DESC);

                CREATE TABLE IF NOT EXISTS shadow_mode_decisions (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_at             TEXT NOT NULL,
                    media_vertical          TEXT NOT NULL,
                    work_id                 INTEGER NULL REFERENCES media_works(id) ON DELETE SET NULL,
                    work_title              TEXT NOT NULL,
                    release_title           TEXT NOT NULL,
                    managerr_decision       TEXT NOT NULL,
                    managerr_score          REAL NOT NULL DEFAULT 0,
                    legacy_app              TEXT NOT NULL,
                    legacy_decision         TEXT NOT NULL,
                    would_upgrade_language  INTEGER NOT NULL DEFAULT 0,
                    notes                   TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS ix_shadow_decisions_occurred ON shadow_mode_decisions(occurred_at DESC);
                """),
        new SqlMigration(
            Version: 7,
            Name: "007_phase7_live_plex_and_completed_imports",
            UpSql: """
                ALTER TABLE cleanerr_media_items ADD COLUMN file_path TEXT NULL;

                CREATE TABLE IF NOT EXISTS processed_download_imports (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    download_client_id      INTEGER NOT NULL REFERENCES download_clients(id) ON DELETE CASCADE,
                    external_download_id    TEXT NOT NULL,
                    title                   TEXT NOT NULL,
                    verdict                 TEXT NOT NULL,
                    destination_path        TEXT NULL,
                    hardlinked              INTEGER NOT NULL DEFAULT 0,
                    message                 TEXT NOT NULL DEFAULT '',
                    processed_at            TEXT NOT NULL,
                    UNIQUE(download_client_id, external_download_id)
                );
                """),
        new SqlMigration(
            Version: 8,
            Name: "008_phase8_av1_compression_engine",
            UpSql: """
                ALTER TABLE libraries ADD COLUMN auto_compress_profile_id INTEGER NULL;

                ALTER TABLE media_files ADD COLUMN source_quality_name TEXT NULL;
                ALTER TABLE media_files ADD COLUMN source_video_codec  TEXT NULL;
                ALTER TABLE media_files ADD COLUMN source_size_bytes   INTEGER NULL;
                ALTER TABLE media_files ADD COLUMN compression_status  TEXT NOT NULL DEFAULT 'uncompressed';
                ALTER TABLE media_files ADD COLUMN vmaf_neg_mean       REAL NULL;
                ALTER TABLE media_files ADD COLUMN xpsnr_min_db        REAL NULL;

                ALTER TABLE cleanerr_media_items ADD COLUMN video_codec        TEXT NULL;
                ALTER TABLE cleanerr_media_items ADD COLUMN compression_status TEXT NOT NULL DEFAULT 'uncompressed';

                CREATE TABLE IF NOT EXISTS compression_profiles (
                    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                    slug                        TEXT NOT NULL UNIQUE,
                    name                        TEXT NOT NULL,
                    description                 TEXT NOT NULL DEFAULT '',
                    is_system_preset            INTEGER NOT NULL DEFAULT 0,
                    fidelity_mode               TEXT NOT NULL DEFAULT 'perceptual_reference',
                    encoder_backend             TEXT NOT NULL DEFAULT 'svt_av1',
                    svt_preset                  INTEGER NOT NULL DEFAULT 3,
                    vmaf_neg_mean_floor         REAL NOT NULL DEFAULT 97.0,
                    vmaf_neg_p5_floor           REAL NOT NULL DEFAULT 94.0,
                    xpsnr_y_floor_db            REAL NOT NULL DEFAULT 42.0,
                    xpsnr_uv_floor_db           REAL NOT NULL DEFAULT 40.0,
                    crf_min                     INTEGER NOT NULL DEFAULT 12,
                    crf_max                     INTEGER NOT NULL DEFAULT 32,
                    min_savings_h264_pct        REAL NOT NULL DEFAULT 15.0,
                    min_savings_hevc_pct        REAL NOT NULL DEFAULT 10.0,
                    film_grain_synth_auto       INTEGER NOT NULL DEFAULT 1,
                    hdr_policy                  TEXT NOT NULL DEFAULT 'preserve_rpu_and_hdr10plus',
                    interlace_policy            TEXT NOT NULL DEFAULT 'skip_interlaced',
                    seeding_policy              TEXT NOT NULL DEFAULT 'wait_until_unlinked',
                    rollback_hours              INTEGER NOT NULL DEFAULT 48,
                    pause_on_active_plex_stream INTEGER NOT NULL DEFAULT 1,
                    max_cpu_threads             INTEGER NOT NULL DEFAULT 0,
                    created_at                  TEXT NOT NULL,
                    updated_at                  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS compression_jobs (
                    id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                    media_file_id        INTEGER NOT NULL REFERENCES media_files(id) ON DELETE CASCADE,
                    work_id              INTEGER NOT NULL REFERENCES media_works(id) ON DELETE CASCADE,
                    unit_id              INTEGER NOT NULL REFERENCES media_units(id) ON DELETE CASCADE,
                    library_id           INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
                    profile_id           INTEGER NOT NULL REFERENCES compression_profiles(id),
                    trigger_source       TEXT NOT NULL DEFAULT 'manual',
                    priority             INTEGER NOT NULL DEFAULT 50,
                    state                TEXT NOT NULL DEFAULT 'queued',
                    progress_pct         REAL NOT NULL DEFAULT 0,
                    source_codec         TEXT NOT NULL,
                    source_quality_name  TEXT NULL,
                    source_size_bytes    INTEGER NOT NULL,
                    output_size_bytes    INTEGER NULL,
                    selected_crf         INTEGER NULL,
                    film_grain_level     INTEGER NULL,
                    vmaf_neg_mean        REAL NULL,
                    vmaf_neg_p5          REAL NULL,
                    xpsnr_min_db         REAL NULL,
                    frame_count_source   INTEGER NULL,
                    frame_count_output   INTEGER NULL,
                    completed_chunks     INTEGER NOT NULL DEFAULT 0,
                    total_chunks         INTEGER NOT NULL DEFAULT 0,
                    sample_metrics_json  TEXT NOT NULL DEFAULT '[]',
                    preview_dir          TEXT NULL,
                    rollback_backup_path TEXT NULL,
                    rollback_expires_at  TEXT NULL,
                    assigned_worker_id   TEXT NULL,
                    skip_or_error_reason TEXT NULL,
                    created_at           TEXT NOT NULL,
                    started_at           TEXT NULL,
                    completed_at         TEXT NULL,
                    updated_at           TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_compression_jobs_state_prio ON compression_jobs(state, priority DESC, id ASC);
                CREATE INDEX IF NOT EXISTS ix_compression_jobs_media_file ON compression_jobs(media_file_id);

                CREATE TABLE IF NOT EXISTS compression_job_chunks (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    job_id        INTEGER NOT NULL REFERENCES compression_jobs(id) ON DELETE CASCADE,
                    chunk_index   INTEGER NOT NULL,
                    start_pts_sec REAL NOT NULL DEFAULT 0,
                    end_pts_sec   REAL NOT NULL DEFAULT 0,
                    start_frame   INTEGER NOT NULL,
                    end_frame     INTEGER NOT NULL,
                    state         TEXT NOT NULL DEFAULT 'pending',
                    crf_used      INTEGER NULL,
                    output_bytes  INTEGER NULL,
                    vmaf_neg_p5   REAL NULL,
                    artifact_path TEXT NULL,
                    updated_at    TEXT NOT NULL,
                    UNIQUE (job_id, chunk_index)
                );
                """),
        new SqlMigration(
            Version: 9,
            Name: "009_arr_cdh_tracked_downloads_and_scheduler",
            UpSql: """
                CREATE TABLE IF NOT EXISTS download_grab_history (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    download_client_id      INTEGER NOT NULL REFERENCES download_clients(id) ON DELETE CASCADE,
                    external_download_id    TEXT NOT NULL,
                    info_hash               TEXT NULL,
                    release_guid            TEXT NULL,
                    release_title           TEXT NOT NULL,
                    indexer_id              INTEGER NULL,
                    media_vertical          TEXT NOT NULL,
                    work_id                 INTEGER NULL,
                    container_id            INTEGER NULL,
                    unit_ids_json           TEXT NOT NULL DEFAULT '[]',
                    source_type             TEXT NOT NULL DEFAULT 'interactive',
                    grabbed_at              TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_grab_history_client_ext ON download_grab_history(download_client_id, external_download_id);
                CREATE INDEX IF NOT EXISTS ix_grab_history_hash ON download_grab_history(info_hash);
                CREATE INDEX IF NOT EXISTS ix_grab_history_work ON download_grab_history(work_id);

                CREATE TABLE IF NOT EXISTS tracked_downloads (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    download_client_id      INTEGER NOT NULL REFERENCES download_clients(id) ON DELETE CASCADE,
                    client_name             TEXT NOT NULL DEFAULT '',
                    external_download_id    TEXT NOT NULL,
                    protocol                TEXT NOT NULL DEFAULT 'usenet',
                    info_hash               TEXT NULL,
                    title                   TEXT NOT NULL,
                    media_vertical          TEXT NOT NULL DEFAULT 'movies',
                    work_id                 INTEGER NULL,
                    work_title              TEXT NULL,
                    unit_ids_json           TEXT NOT NULL DEFAULT '[]',
                    state                   TEXT NOT NULL DEFAULT 'Downloading',
                    tracked_status          TEXT NOT NULL DEFAULT 'ok',
                    status_messages_json    TEXT NOT NULL DEFAULT '[]',
                    output_path             TEXT NULL,
                    quarantine_id           INTEGER NULL,
                    can_move_files          INTEGER NOT NULL DEFAULT 1,
                    first_seen_at           TEXT NOT NULL,
                    updated_at              TEXT NOT NULL,
                    UNIQUE(download_client_id, external_download_id)
                );
                CREATE INDEX IF NOT EXISTS ix_tracked_downloads_state ON tracked_downloads(state, updated_at DESC);

                CREATE TABLE IF NOT EXISTS scheduled_tasks (
                    task_key                TEXT PRIMARY KEY NOT NULL,
                    name                    TEXT NOT NULL,
                    description             TEXT NOT NULL DEFAULT '',
                    interval_seconds        INTEGER NOT NULL,
                    is_enabled              INTEGER NOT NULL DEFAULT 1,
                    last_execution_at       TEXT NULL,
                    last_duration_ms        INTEGER NULL,
                    last_status             TEXT NULL,
                    last_message            TEXT NULL,
                    next_execution_at       TEXT NULL,
                    updated_at              TEXT NOT NULL
                );
                """),
        new SqlMigration(
            Version: 10,
            Name: "010_arr_media_history_and_wanted",
            UpSql: """
                CREATE TABLE IF NOT EXISTS media_history (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_at             TEXT NOT NULL,
                    event_type              TEXT NOT NULL,
                    media_vertical          TEXT NOT NULL DEFAULT 'movies',
                    work_id                 INTEGER NULL,
                    work_title              TEXT NULL,
                    container_id            INTEGER NULL,
                    unit_id                 INTEGER NULL,
                    episode_code            TEXT NULL,
                    source_title            TEXT NOT NULL,
                    quality_name            TEXT NULL,
                    languages_json          TEXT NOT NULL DEFAULT '[]',
                    indexer_name            TEXT NULL,
                    download_client_id      INTEGER NULL,
                    download_client_name    TEXT NULL,
                    external_download_id    TEXT NULL,
                    info_hash               TEXT NULL,
                    destination_path        TEXT NULL,
                    data_json               TEXT NOT NULL DEFAULT '{}'
                );
                CREATE INDEX IF NOT EXISTS ix_media_history_occurred ON media_history(occurred_at DESC, id DESC);
                CREATE INDEX IF NOT EXISTS ix_media_history_work ON media_history(work_id, occurred_at DESC);
                CREATE INDEX IF NOT EXISTS ix_media_history_event ON media_history(event_type, occurred_at DESC);
                """),
        new SqlMigration(
            Version: 11,
            Name: "011_arr_media_management_and_catalog_ux",
            UpSql: """
                ALTER TABLE media_works ADD COLUMN minimum_availability TEXT NOT NULL DEFAULT 'released';
                ALTER TABLE media_works ADD COLUMN quality_profile_id INTEGER NULL;

                CREATE TABLE IF NOT EXISTS unmapped_media_files (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    library_id              INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
                    full_path               TEXT NOT NULL,
                    relative_path           TEXT NOT NULL,
                    file_name               TEXT NOT NULL,
                    size_bytes              INTEGER NOT NULL DEFAULT 0,
                    parsed_title            TEXT NOT NULL DEFAULT '',
                    parsed_season           INTEGER NULL,
                    parsed_episodes_json    TEXT NOT NULL DEFAULT '[]',
                    parsed_quality          TEXT NOT NULL DEFAULT '',
                    parsed_languages_json   TEXT NOT NULL DEFAULT '[]',
                    suggested_work_id       INTEGER NULL,
                    suggested_work_title    TEXT NULL,
                    reason                  TEXT NOT NULL DEFAULT '',
                    discovered_at           TEXT NOT NULL,
                    UNIQUE(library_id, relative_path)
                );
                CREATE INDEX IF NOT EXISTS ix_unmapped_media_files_lib ON unmapped_media_files(library_id, discovered_at DESC);
                """),
        new SqlMigration(
            Version: 12,
            Name: "012_arr_profiles_notifications_and_import_lists",
            UpSql: """
                CREATE TABLE IF NOT EXISTS release_profiles (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    name                    TEXT NOT NULL,
                    is_enabled              INTEGER NOT NULL DEFAULT 1,
                    must_contain_json       TEXT NOT NULL DEFAULT '[]',
                    must_not_contain_json   TEXT NOT NULL DEFAULT '[]',
                    preferred_terms_json    TEXT NOT NULL DEFAULT '[]',
                    indexer_id              INTEGER NULL,
                    tags_json               TEXT NOT NULL DEFAULT '[]',
                    media_vertical          TEXT NULL,
                    created_at              TEXT NOT NULL,
                    updated_at              TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS delay_profiles (
                    id                                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    name                                    TEXT NOT NULL,
                    preferred_protocol                      TEXT NOT NULL DEFAULT 'usenet',
                    usenet_delay_minutes                    INTEGER NOT NULL DEFAULT 0,
                    torrent_delay_minutes                   INTEGER NOT NULL DEFAULT 0,
                    bypass_if_highest_quality               INTEGER NOT NULL DEFAULT 1,
                    bypass_if_above_custom_format_score     INTEGER NOT NULL DEFAULT 0,
                    min_custom_format_score                 INTEGER NOT NULL DEFAULT 0,
                    tags_json                               TEXT NOT NULL DEFAULT '[]',
                    sort_order                              INTEGER NOT NULL DEFAULT 1000,
                    is_default                              INTEGER NOT NULL DEFAULT 0,
                    created_at                              TEXT NOT NULL,
                    updated_at                              TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS quality_size_limits (
                    quality_name            TEXT PRIMARY KEY NOT NULL,
                    media_vertical          TEXT NOT NULL DEFAULT 'video',
                    min_mb_per_min          REAL NOT NULL DEFAULT 0,
                    preferred_mb_per_min    REAL NOT NULL DEFAULT 0,
                    max_mb_per_min          REAL NOT NULL DEFAULT 0,
                    updated_at              TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS notification_connections (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    name                    TEXT NOT NULL,
                    provider_type           TEXT NOT NULL,
                    settings_json           TEXT NOT NULL DEFAULT '{}',
                    on_grab                 INTEGER NOT NULL DEFAULT 1,
                    on_import               INTEGER NOT NULL DEFAULT 1,
                    on_upgrade              INTEGER NOT NULL DEFAULT 1,
                    on_rename               INTEGER NOT NULL DEFAULT 0,
                    on_delete               INTEGER NOT NULL DEFAULT 1,
                    on_health_issue         INTEGER NOT NULL DEFAULT 1,
                    on_application_update   INTEGER NOT NULL DEFAULT 0,
                    tags_json               TEXT NOT NULL DEFAULT '[]',
                    is_enabled              INTEGER NOT NULL DEFAULT 1,
                    created_at              TEXT NOT NULL,
                    updated_at              TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS import_lists (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    name                    TEXT NOT NULL,
                    list_type               TEXT NOT NULL,
                    media_vertical          TEXT NOT NULL,
                    library_id              INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
                    quality_profile_id      INTEGER NULL,
                    monitor_mode            TEXT NOT NULL DEFAULT 'all',
                    minimum_availability    TEXT NOT NULL DEFAULT 'released',
                    search_on_add           INTEGER NOT NULL DEFAULT 0,
                    settings_json           TEXT NOT NULL DEFAULT '{}',
                    tags_json               TEXT NOT NULL DEFAULT '[]',
                    sync_interval_hours     INTEGER NOT NULL DEFAULT 12,
                    is_enabled              INTEGER NOT NULL DEFAULT 1,
                    last_synced_at          TEXT NULL,
                    last_result_message     TEXT NULL,
                    created_at              TEXT NOT NULL,
                    updated_at              TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS import_list_exclusions (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    external_key            TEXT NOT NULL UNIQUE,
                    title                   TEXT NOT NULL,
                    media_vertical          TEXT NOT NULL DEFAULT 'movies',
                    year                    INTEGER NULL,
                    created_at              TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_import_list_exclusions_vertical ON import_list_exclusions(media_vertical, created_at DESC);
                """),
        new SqlMigration(
            Version: 13,
            Name: "013_failed_nzb_and_release_blocklist_fingerprints",
            UpSql: """
                ALTER TABLE download_grab_history ADD COLUMN download_url TEXT NULL;
                ALTER TABLE download_grab_history ADD COLUMN indexer_name TEXT NULL;
                ALTER TABLE download_grab_history ADD COLUMN size_bytes INTEGER NULL;
                ALTER TABLE download_grab_history ADD COLUMN published_at TEXT NULL;

                ALTER TABLE recurring_blocklist_hashes ADD COLUMN protocol TEXT NOT NULL DEFAULT 'torrent';
                ALTER TABLE recurring_blocklist_hashes ADD COLUMN release_guid TEXT NULL;
                ALTER TABLE recurring_blocklist_hashes ADD COLUMN download_url TEXT NULL;
                ALTER TABLE recurring_blocklist_hashes ADD COLUMN normalized_title TEXT NOT NULL DEFAULT '';
                ALTER TABLE recurring_blocklist_hashes ADD COLUMN indexer_id INTEGER NULL;
                ALTER TABLE recurring_blocklist_hashes ADD COLUMN indexer_name TEXT NULL;
                ALTER TABLE recurring_blocklist_hashes ADD COLUMN size_bytes INTEGER NULL;
                ALTER TABLE recurring_blocklist_hashes ADD COLUMN published_at TEXT NULL;
                ALTER TABLE recurring_blocklist_hashes ADD COLUMN work_id INTEGER NULL;

                UPDATE recurring_blocklist_hashes
                SET protocol = 'usenet'
                WHERE info_hash LIKE 'nzo:%'
                   OR info_hash LIKE 'nzbget:%'
                   OR info_hash LIKE 'sab:%'
                   OR info_hash LIKE 'guid:%'
                   OR info_hash LIKE 'nzb:%'
                   OR info_hash LIKE 'title:%';

                CREATE INDEX IF NOT EXISTS ix_blocklist_norm_title ON recurring_blocklist_hashes(normalized_title);
                CREATE INDEX IF NOT EXISTS ix_blocklist_guid ON recurring_blocklist_hashes(release_guid);
                CREATE INDEX IF NOT EXISTS ix_blocklist_url ON recurring_blocklist_hashes(download_url);
                CREATE INDEX IF NOT EXISTS ix_blocklist_indexer_pub ON recurring_blocklist_hashes(indexer_id, published_at);
                """),
        new SqlMigration(
            Version: 14,
            Name: "014_cleanerr_finetuning_metadata",
            UpSql: """
                ALTER TABLE cleanerr_media_items ADD COLUMN view_offset_ms INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE cleanerr_media_items ADD COLUMN audio_languages_json TEXT NOT NULL DEFAULT '[]';
                ALTER TABLE cleanerr_media_items ADD COLUMN is_hardlinked INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE cleanerr_media_items ADD COLUMN custom_format_score INTEGER NOT NULL DEFAULT 0;
                """),
        new SqlMigration(
            Version: 15,
            Name: "015_cleanerr_v2_rules_and_discord_bot",
            UpSql: """
                ALTER TABLE cleanerr_workflows ADD COLUMN editor_mode TEXT NOT NULL DEFAULT 'dag';
                ALTER TABLE cleanerr_workflows ADD COLUMN rule_group_json TEXT NULL;

                ALTER TABLE cleanerr_deletion_queue ADD COLUMN is_manual INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE cleanerr_deletion_queue ADD COLUMN postponed_count INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE cleanerr_deletion_queue ADD COLUMN matched_reason TEXT NULL;

                ALTER TABLE cleanerr_media_items ADD COLUMN classification_kind TEXT NULL;
                ALTER TABLE cleanerr_media_items ADD COLUMN is_watchlisted INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE cleanerr_media_items ADD COLUMN work_status TEXT NULL;
                ALTER TABLE cleanerr_media_items ADD COLUMN poster_url TEXT NULL;

                CREATE TABLE IF NOT EXISTS cleanerr_discord_messages (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id              TEXT NOT NULL UNIQUE,
                    channel_id              TEXT NOT NULL,
                    workflow_id             INTEGER NULL REFERENCES cleanerr_workflows(id) ON DELETE SET NULL,
                    workflow_name           TEXT NOT NULL DEFAULT 'Cleanerr Cleanup',
                    grace_days              INTEGER NOT NULL DEFAULT 7,
                    page_index              INTEGER NOT NULL DEFAULT 0,
                    staged_item_ids_json    TEXT NOT NULL DEFAULT '[]',
                    created_at              TEXT NOT NULL,
                    updated_at              TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_cleanerr_discord_msg_wf ON cleanerr_discord_messages(workflow_id, created_at DESC);
                """),
        new SqlMigration(
            Version: 16,
            Name: "016_collapse_duplicate_open_quarantine",
            UpSql: """
                -- Re-verifying the same download used to insert a new open quarantine row each time;
                -- keep the newest open row per download (plus anything a tracked download still points at).
                DELETE FROM quarantined_imports
                WHERE status = 'quarantined'
                  AND id NOT IN (
                      SELECT MAX(id) FROM quarantined_imports
                      WHERE status = 'quarantined'
                      GROUP BY COALESCE(info_hash, staged_file_path))
                  AND id NOT IN (
                      SELECT quarantine_id FROM tracked_downloads WHERE quarantine_id IS NOT NULL);

                CREATE INDEX IF NOT EXISTS ix_quarantined_imports_open_hash ON quarantined_imports(info_hash) WHERE status = 'quarantined';
                """),
        new SqlMigration(
            Version: 17,
            Name: "017_canonical_quality_names",
            UpSql: """
                -- Same rules as QualityNames.Canonicalize, for rows written before it existed.
                -- Non-canonical names never matched a profile tier, so those files were treated as upgradeable forever.
                UPDATE media_files SET quality_name = 'Unknown'
                WHERE UPPER(TRIM(COALESCE(quality_name, ''))) IN ('', 'STANDARD', 'MKV', 'MP4', 'M4V', 'AVI', 'TS', 'WEBM', 'WMV', 'MOV');
                UPDATE media_files SET source_quality_name = 'Unknown'
                WHERE source_quality_name IS NOT NULL
                  AND UPPER(TRIM(source_quality_name)) IN ('', 'STANDARD', 'MKV', 'MP4', 'M4V', 'AVI', 'TS', 'WEBM', 'WMV', 'MOV');

                UPDATE media_files SET quality_name = 'WEBDL-' || SUBSTR(quality_name, 8) WHERE quality_name LIKE 'WEB-DL-%';
                UPDATE media_files SET source_quality_name = 'WEBDL-' || SUBSTR(source_quality_name, 8) WHERE source_quality_name LIKE 'WEB-DL-%';
                UPDATE media_files SET quality_name = 'BluRay-' || SUBSTR(quality_name, 9) WHERE quality_name LIKE 'Blu-Ray-%';
                UPDATE media_files SET source_quality_name = 'BluRay-' || SUBSTR(source_quality_name, 9) WHERE source_quality_name LIKE 'Blu-Ray-%';

                UPDATE media_files SET quality_name = 'SD-480p'
                WHERE quality_name LIKE '%-480p' OR quality_name LIKE '%-480i' OR quality_name LIKE '%-576p' OR quality_name LIKE '%-576i';
                UPDATE media_files SET source_quality_name = 'SD-480p'
                WHERE source_quality_name LIKE '%-480p' OR source_quality_name LIKE '%-480i' OR source_quality_name LIKE '%-576p' OR source_quality_name LIKE '%-576i';
                """),
        new SqlMigration(
            Version: 18,
            Name: "018_strip_pcm_fingerprints_from_reports",
            UpSql: """
                -- Reports stored the raw audio samples of every speech window (~4 MB per report); nothing reads them back
                UPDATE verification_jobs
                SET report_json = json_set(report_json, '$.streams', (
                    SELECT json_group_array(
                        CASE WHEN json_type(s.value, '$.windows') = 'array'
                             THEN json_set(s.value, '$.windows', (
                                 SELECT json_group_array(json_remove(w.value, '$.pcmFingerprint'))
                                 FROM json_each(s.value, '$.windows') AS w))
                             ELSE json(s.value)
                        END)
                    FROM json_each(verification_jobs.report_json, '$.streams') AS s))
                WHERE report_json LIKE '%"pcmFingerprint"%';
                """,
            VacuumAfter: true),
        new SqlMigration(
            Version: 19,
            Name: "019_reclaim_space_after_018",
            // 018 shipped before the vacuum checkpointed the WAL, so databases that already ran it kept their file size
            UpSql: "SELECT 1;",
            VacuumAfter: true),
        new SqlMigration(
            Version: 20,
            Name: "020_detach_disk_scan_duplicate_files",
            UpSql: """
                -- The disk scan used to adopt extra files onto units that already had one. Detach those rows
                -- (files stay on disk and show up as unmapped duplicates); imported files always win, and among
                -- adopted-only duplicates the oldest stays. Different movie editions are not duplicates.
                DELETE FROM media_files
                WHERE verified_status = 'disk_scan_adopted'
                  AND EXISTS (
                      SELECT 1 FROM media_files o
                      WHERE o.unit_id = media_files.unit_id
                        AND COALESCE(o.edition_id, 0) = COALESCE(media_files.edition_id, 0)
                        AND o.id <> media_files.id
                        AND (o.verified_status <> 'disk_scan_adopted' OR o.id < media_files.id));
                """),
        new SqlMigration(
            Version: 21,
            Name: "021_multi_provider_auth_oidc_passkeys",
            UpSql: """
                CREATE TABLE IF NOT EXISTS users_new (
                    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                    plex_user_id                INTEGER NULL UNIQUE,
                    plex_uuid                   TEXT NOT NULL DEFAULT '',
                    username                    TEXT NOT NULL,
                    display_name                TEXT NULL,
                    passkey_user_id             TEXT NOT NULL DEFAULT '',
                    email                       TEXT NULL,
                    avatar_url                  TEXT NULL,
                    is_server_owner             INTEGER NOT NULL DEFAULT 0,
                    is_home_user                INTEGER NOT NULL DEFAULT 0,
                    is_active                   INTEGER NOT NULL DEFAULT 1,
                    shared_plex_section_ids_json TEXT NOT NULL DEFAULT '[]',
                    preferred_audio_lang        TEXT NULL,
                    preferred_sub_lang          TEXT NULL,
                    discord_user_id             TEXT NULL,
                    discord_username            TEXT NULL,
                    seerr_user_id               INTEGER NULL,
                    break_glass_password_hash   TEXT NULL,
                    last_login_at               TEXT NULL,
                    created_at                  TEXT NOT NULL,
                    updated_at                  TEXT NOT NULL
                );

                INSERT INTO users_new (
                    id,
                    plex_user_id,
                    plex_uuid,
                    username,
                    display_name,
                    passkey_user_id,
                    email,
                    avatar_url,
                    is_server_owner,
                    is_home_user,
                    is_active,
                    shared_plex_section_ids_json,
                    preferred_audio_lang,
                    preferred_sub_lang,
                    discord_user_id,
                    discord_username,
                    seerr_user_id,
                    break_glass_password_hash,
                    last_login_at,
                    created_at,
                    updated_at
                )
                SELECT
                    id,
                    plex_user_id,
                    plex_uuid,
                    username,
                    username,
                    SUBSTR(HEX(RANDOMBLOB(16)), 1, 22),
                    email,
                    avatar_url,
                    is_server_owner,
                    is_home_user,
                    is_active,
                    shared_plex_section_ids_json,
                    preferred_audio_lang,
                    preferred_sub_lang,
                    discord_user_id,
                    discord_username,
                    seerr_user_id,
                    break_glass_password_hash,
                    last_login_at,
                    created_at,
                    updated_at
                FROM users;

                DROP TABLE users;

                ALTER TABLE users_new RENAME TO users;

                CREATE INDEX IF NOT EXISTS ix_users_discord_user_id ON users(discord_user_id) WHERE discord_user_id IS NOT NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS ix_users_passkey_user_id ON users(passkey_user_id);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_users_single_server_owner ON users(is_server_owner) WHERE is_server_owner = 1;

                CREATE TABLE IF NOT EXISTS user_identities (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id             INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    provider            TEXT NOT NULL,
                    issuer              TEXT NOT NULL DEFAULT '',
                    subject_id          TEXT NOT NULL,
                    external_username   TEXT NULL,
                    external_email      TEXT NULL,
                    groups_json         TEXT NOT NULL DEFAULT '[]',
                    last_login_at       TEXT NULL,
                    created_at          TEXT NOT NULL,
                    UNIQUE(provider, issuer, subject_id)
                );

                CREATE INDEX IF NOT EXISTS ix_user_identities_user_id ON user_identities(user_id);

                INSERT OR IGNORE INTO user_identities (user_id, provider, issuer, subject_id, external_username, external_email, groups_json, last_login_at, created_at)
                SELECT id, 'plex', '', CAST(plex_user_id AS TEXT), username, email, '[]', last_login_at, created_at
                FROM users
                WHERE plex_user_id IS NOT NULL AND plex_user_id > 0 AND plex_uuid != 'testlab-bootstrap-admin';

                INSERT OR IGNORE INTO user_identities (user_id, provider, issuer, subject_id, external_username, external_email, groups_json, last_login_at, created_at)
                SELECT id, 'local', '', CAST(id AS TEXT), username, email, '[]', last_login_at, created_at
                FROM users
                WHERE break_glass_password_hash IS NOT NULL;

                CREATE TABLE IF NOT EXISTS webauthn_credentials (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    credential_id       TEXT NOT NULL UNIQUE,
                    user_id             INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    public_key_b64      TEXT NOT NULL,
                    sign_count          INTEGER NOT NULL DEFAULT 0,
                    device_type         TEXT NOT NULL DEFAULT 'multiDevice',
                    is_backed_up        INTEGER NOT NULL DEFAULT 0,
                    aaguid              TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                    name                TEXT NOT NULL,
                    transports_json     TEXT NOT NULL DEFAULT '[]',
                    registered_at       INTEGER NOT NULL,
                    last_used_at        INTEGER NULL
                );

                CREATE INDEX IF NOT EXISTS ix_webauthn_credentials_user_id ON webauthn_credentials(user_id);
                """),
        new SqlMigration(
            Version: 22,
            Name: "work_language_policy_and_anime_default_profile",
            UpSql: """
                ALTER TABLE media_works ADD COLUMN language_policy_id INTEGER NULL REFERENCES language_policies(id) ON DELETE SET NULL;

                UPDATE libraries
                SET default_profile_id = (SELECT id FROM quality_profiles WHERE slug = 'german-first-best-quality' LIMIT 1)
                WHERE LOWER(media_type) = 'anime' AND default_profile_id IS NULL;
                """),
        new SqlMigration(
            Version: 23,
            Name: "023_cleanerr_v3_reminders_and_persistence",
            UpSql: """
                ALTER TABLE cleanerr_deletion_queue ADD COLUMN final_warning_sent_at TEXT NULL;
                ALTER TABLE cleanerr_discord_messages ADD COLUMN message_kind TEXT NOT NULL DEFAULT 'staged';

                CREATE TABLE IF NOT EXISTS cleanerr_consumed_magic_tokens (
                    token_hash      TEXT PRIMARY KEY NOT NULL,
                    expires_at_unix INTEGER NOT NULL,
                    consumed_at     TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_cleanerr_magic_tokens_exp ON cleanerr_consumed_magic_tokens(expires_at_unix);
                """),
    ];

    private static readonly IReadOnlyList<SqlMigration> CacheMigrations =
    [
        new SqlMigration(
            Version: 1,
            Name: "001_phase1_cache_and_rate_limits",
            UpSql: """
                CREATE TABLE IF NOT EXISTS cache_entries (
                    cache_key       TEXT PRIMARY KEY NOT NULL,
                    payload_json    TEXT NOT NULL,
                    expires_at      TEXT NOT NULL,
                    updated_at      TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_cache_entries_expires ON cache_entries(expires_at);

                CREATE TABLE IF NOT EXISTS rate_limit_windows (
                    bucket_key      TEXT PRIMARY KEY NOT NULL,
                    window_start    TEXT NOT NULL,
                    hit_count       INTEGER NOT NULL
                );
                """),
        new SqlMigration(
            Version: 2,
            Name: "002_auth_challenges",
            UpSql: """
                CREATE TABLE IF NOT EXISTS auth_challenges (
                    challenge_key   TEXT PRIMARY KEY NOT NULL,
                    kind            TEXT NOT NULL,
                    payload_json    TEXT NOT NULL,
                    expires_at      TEXT NOT NULL,
                    consumed_at     TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_auth_challenges_expires ON auth_challenges(expires_at);
                """),
    ];

    private static readonly IReadOnlyList<SqlMigration> TelemetryMigrations =
    [
        new SqlMigration(
            Version: 1,
            Name: "001_phase1_audit_and_telemetry",
            UpSql: """
                CREATE TABLE IF NOT EXISTS audit_events (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_at             TEXT NOT NULL,
                    actor_user_id           INTEGER NULL,
                    actor_plex_user_id      INTEGER NULL,
                    actor_username          TEXT NULL,
                    actor_api_key_prefix    TEXT NULL,
                    ip_address              TEXT NULL,
                    action                  TEXT NOT NULL,
                    resource_type           TEXT NOT NULL,
                    resource_id             TEXT NULL,
                    details_json            TEXT NOT NULL DEFAULT '{}',
                    trace_id                TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_audit_events_occurred_at ON audit_events(occurred_at DESC);
                CREATE INDEX IF NOT EXISTS ix_audit_events_action ON audit_events(action);

                CREATE TABLE IF NOT EXISTS job_logs (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_at     TEXT NOT NULL,
                    subsystem       TEXT NOT NULL,
                    job_id          TEXT NULL,
                    level           TEXT NOT NULL,
                    message         TEXT NOT NULL,
                    metadata_json   TEXT NOT NULL DEFAULT '{}'
                );
                CREATE INDEX IF NOT EXISTS ix_job_logs_occurred_at ON job_logs(occurred_at DESC);
                """),
    ];

    public async Task MigrateAllAsync(CancellationToken ct = default)
    {
        await ApplyMigrationsAsync(DatabaseKind.Core, CoreMigrations, ct);
        await ApplyMigrationsAsync(DatabaseKind.Cache, CacheMigrations, ct);
        await ApplyMigrationsAsync(DatabaseKind.Telemetry, TelemetryMigrations, ct);
    }

    private async Task ApplyMigrationsAsync(
        DatabaseKind kind,
        IReadOnlyList<SqlMigration> migrations,
        CancellationToken ct)
    {
        var vacuumNeeded = false;
        await using (var conn = await dbFactory.OpenReadConnectionAsync(kind, ct))
        {
            await using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                await pragmaCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct))
            {
                await conn.ExecuteAsync(
                    """
                    CREATE TABLE IF NOT EXISTS schema_migrations (
                        version     INTEGER PRIMARY KEY NOT NULL,
                        name        TEXT NOT NULL,
                        applied_at  TEXT NOT NULL
                    );
                    """,
                    transaction: tx);

                var applied = (await conn.QueryAsync<int>(
                    "SELECT version FROM schema_migrations;",
                    transaction: tx)).ToHashSet();

                foreach (var mig in migrations.OrderBy(m => m.Version))
                {
                    if (applied.Contains(mig.Version))
                    {
                        continue;
                    }

                    if (kind == DatabaseKind.Core && mig.Version == 22)
                    {
                        var cols = (await conn.QueryAsync<string>(
                            "SELECT name FROM pragma_table_info('media_works');",
                            transaction: tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (cols.Contains("language_policy_id"))
                        {
                            // Column already exists, run only the UPDATE libraries statement
                            await ExecuteBatchAsync(conn, tx, """
                                UPDATE libraries
                                SET default_profile_id = (SELECT id FROM quality_profiles WHERE slug = 'german-first-best-quality' LIMIT 1)
                                WHERE LOWER(media_type) = 'anime' AND default_profile_id IS NULL;
                                """, ct);
                            vacuumNeeded |= mig.VacuumAfter;
                            await conn.ExecuteAsync(
                                "INSERT INTO schema_migrations (version, name, applied_at) VALUES (@Version, @Name, @AppliedAt);",
                                new
                                {
                                    mig.Version,
                                    mig.Name,
                                    AppliedAt = DateTimeOffset.UtcNow.ToString("O"),
                                },
                                transaction: tx);
                            continue;
                        }
                    }

                    await ExecuteBatchAsync(conn, tx, mig.UpSql, ct);
                    vacuumNeeded |= mig.VacuumAfter;
                    await conn.ExecuteAsync(
                        "INSERT INTO schema_migrations (version, name, applied_at) VALUES (@Version, @Name, @AppliedAt);",
                        new
                        {
                            mig.Version,
                            mig.Name,
                            AppliedAt = DateTimeOffset.UtcNow.ToString("O"),
                        },
                        transaction: tx);
                }

                await tx.CommitAsync(ct);
            }

            await using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA foreign_key_check;";
                await pragmaCmd.ExecuteNonQueryAsync(ct);
            }
        }

        // VACUUM can't run inside the migration transaction; give freed pages back to the filesystem afterwards.
        // In WAL mode the compacted database lands in the WAL and the file only shrinks on a truncating checkpoint.
        if (vacuumNeeded)
        {
            await using var conn = await dbFactory.OpenReadConnectionAsync(kind, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ExecuteBatchAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string sql,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        try
        {
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var stmt in statements)
            {
                try
                {
                    cmd.CommandText = stmt;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch (SqliteException innerEx) when (innerEx.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
                {
                    // Ignore duplicate column name
                }
            }
        }
    }
}
