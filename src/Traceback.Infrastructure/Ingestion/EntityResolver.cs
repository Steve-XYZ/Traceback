using Microsoft.EntityFrameworkCore;
using Traceback.Connectors.Abstractions;
using Traceback.Domain.Entities;
using Traceback.Infrastructure.Persistence;

namespace Traceback.Infrastructure.Ingestion;

/// <summary>
/// Resolves external references to domain entities, creating placeholder rows
/// (and identity mappings) when an entity is referenced before it has been
/// observed. All resolution goes through the external identity table; natural
/// keys provide a secondary correlation path so the same object reported twice
/// converges on one row where a stable natural key exists.
///
/// Resolution is repository-scoped wherever the source semantics demand it:
/// pull request numbers, workflow run ids, and even commit SHAs are only
/// meaningful inside a repository, so their identity keys carry the repository
/// scope and their domain rows link to a <see cref="SourceRepository"/>.
///
/// The resolver keeps per-batch memo caches: large synchronization batches
/// reference the same identities many times (every PR event re-resolves its
/// author, its repository, and its commits), and the cache turns those repeats
/// into dictionary lookups instead of database round trips. Caches live exactly
/// as long as the enclosing ingestion transaction.
/// </summary>
internal sealed class EntityResolver(TracebackDbContext db)
{
    private readonly Dictionary<(string Provider, string EntityType, string Key), object> _entityCache = [];
    private readonly Dictionary<string, Engineer> _engineerCache = [];
    private readonly Dictionary<string, BuildArtifact> _artifactCache = [];
    private readonly Dictionary<string, Service> _serviceCache = [];
    private readonly Dictionary<string, DeploymentEnvironment> _environmentCache = [];

    public async Task<SourceRepository> ResolveRepositoryAsync(string provider, string key, DateTimeOffset observedAt, CancellationToken ct)
    {
        var normalizedKey = NormalizeName(key);
        var cacheKey = (provider, ExternalEntityTypes.Repository, normalizedKey);
        if (_entityCache.TryGetValue(cacheKey, out var cached))
            return (SourceRepository)cached;

        var identity = await FindIdentityAsync(provider, ExternalEntityTypes.Repository, normalizedKey, ct);
        var repo = await LoadAsync<SourceRepository>(identity?.SourceRepositoryId, ct);
        if (repo is null)
        {
            repo = await db.SourceRepositories.FirstOrDefaultAsync(
                r => r.CreatedByProvider == provider && r.Key == normalizedKey, ct);
            if (repo is not null && identity is null)
            {
                await AttachNewIdentityAsync(ExternalEntityTypes.Repository, provider, normalizedKey, repo.Id, observedAt,
                    (i, id) => i.SourceRepositoryId = id, ct);
            }
        }

        if (repo is null)
        {
            repo = new SourceRepository
            {
                Key = normalizedKey,
                FullName = normalizedKey,
                CreatedByProvider = provider,
                FirstObservedAt = observedAt,
                LastObservedAt = observedAt,
                IsPlaceholder = true,
            };
            await db.SourceRepositories.AddAsync(repo, ct);
            await AttachNewIdentityAsync(ExternalEntityTypes.Repository, provider, normalizedKey, repo.Id, observedAt,
                (i, id) => i.SourceRepositoryId = id, ct);
        }

        _entityCache[cacheKey] = repo;
        return repo;
    }

    public async Task<WorkItem> ResolveWorkItemAsync(string provider, string key, DateTimeOffset observedAt, CancellationToken ct)
    {
        var cacheKey = (provider, ExternalEntityTypes.WorkItem, key);
        if (_entityCache.TryGetValue(cacheKey, out var cachedWorkItem))
            return (WorkItem)cachedWorkItem;

        var identity = await FindIdentityAsync(provider, ExternalEntityTypes.WorkItem, key, ct);
        var mapped = await LoadAsync<WorkItem>(identity?.WorkItemId, ct);
        WorkItem item;
        if (mapped is not null)
        {
            item = mapped;
        }
        else
        {
            // Natural-key convergence across providers.
            var existing = await db.WorkItems.FirstOrDefaultAsync(w => w.Key == key, ct);
            if (existing is not null)
            {
                await AttachIdentityAsync(identity, ExternalEntityTypes.WorkItem, provider, key, existing.Id, observedAt,
                    (i, id) => i.WorkItemId = id, ct);
                item = existing;
            }
            else
            {
                item = new WorkItem
                {
                    Key = key,
                    CreatedByProvider = provider,
                    FirstObservedAt = observedAt,
                    LastObservedAt = observedAt,
                    IsPlaceholder = true,
                };
                await db.WorkItems.AddAsync(item, ct);
                await AttachIdentityAsync(identity, ExternalEntityTypes.WorkItem, provider, key, item.Id, observedAt,
                    (i, id) => i.WorkItemId = id, ct);
            }
        }

        _entityCache[cacheKey] = item;
        return item;
    }

    public async Task<PullRequest> ResolvePullRequestAsync(
        string provider, string externalName, string? repositoryKey, DateTimeOffset observedAt, CancellationToken ct)
    {
        var cacheKey = (provider, ExternalEntityTypes.PullRequest, externalName);
        if (_entityCache.TryGetValue(cacheKey, out var cached))
            return (PullRequest)cached;

        var identity = await FindIdentityAsync(provider, ExternalEntityTypes.PullRequest, externalName, ct);
        var pr = await LoadAsync<PullRequest>(identity?.PullRequestId, ct);
        if (pr is null)
        {
            pr = new PullRequest
            {
                ExternalName = externalName,
                CreatedByProvider = provider,
                FirstObservedAt = observedAt,
                LastObservedAt = observedAt,
                IsPlaceholder = true,
            };
            await db.PullRequests.AddAsync(pr, ct);
            await AttachNewIdentityAsync(ExternalEntityTypes.PullRequest, provider, externalName, pr.Id, observedAt,
                (i, id) => i.PullRequestId = id, ct);
        }

        if (!string.IsNullOrWhiteSpace(repositoryKey))
        {
            // Link the pull request to its repository; numbers and branch names
            // are meaningless outside that scope.
            var repo = await ResolveRepositoryAsync(provider, repositoryKey, observedAt, ct);
            pr.SourceRepositoryId ??= repo.Id;
        }

        _entityCache[cacheKey] = pr;
        return pr;
    }

    /// <summary>
    /// Commit identity is repository-scoped: the identity key embeds the
    /// repository (<c>"owner/name@sha"</c>) because the same SHA may exist in
    /// several repositories and their relationships must stay independent.
    /// Events without repository context fall back to the legacy SHA-only key.
    /// </summary>
    public static string CommitIdentityKey(string? repositoryKey, string sha) =>
        string.IsNullOrEmpty(repositoryKey) ? sha : $"{NormalizeName(repositoryKey)}@{NormalizeSha(sha)}";

    public async Task<Commit> ResolveCommitAsync(
        string provider, string? repositoryKey, string sha, DateTimeOffset observedAt, CancellationToken ct)
    {
        var normalized = NormalizeSha(sha);
        var identityKey = CommitIdentityKey(repositoryKey, normalized);
        var cacheKey = (provider, ExternalEntityTypes.Commit, identityKey);
        if (_entityCache.TryGetValue(cacheKey, out var cached))
            return (Commit)cached;

        SourceRepository? repo = null;
        if (!string.IsNullOrEmpty(repositoryKey))
            repo = await ResolveRepositoryAsync(provider, repositoryKey, observedAt, ct);

        var identity = await FindIdentityAsync(provider, ExternalEntityTypes.Commit, identityKey, ct);
        var commit = await LoadAsync<Commit>(identity?.CommitId, ct);

        if (commit is null && repo is not null)
        {
            // Natural-key path: the same (repository, sha) observed under a
            // different identity spelling converges here.
            commit = await db.Commits.FirstOrDefaultAsync(c => c.SourceRepositoryId == repo.Id && c.Sha == normalized, ct);
            if (commit is not null)
                await AttachNewIdentityAsync(ExternalEntityTypes.Commit, provider, identityKey, commit.Id, observedAt,
                    (i, id) => i.CommitId = id, ct);
        }

        if (commit is null && repo is null)
        {
            // Legacy unscoped path: converge on any prior row for this SHA.
            commit = await db.Commits.FirstOrDefaultAsync(
                c => c.SourceRepositoryId == null && c.Sha == normalized, ct);
            if (commit is not null)
                await AttachNewIdentityAsync(ExternalEntityTypes.Commit, provider, identityKey, commit.Id, observedAt,
                    (i, id) => i.CommitId = id, ct);
        }

        if (commit is null)
        {
            commit = new Commit
            {
                Sha = normalized,
                CreatedByProvider = provider,
                FirstObservedAt = observedAt,
                LastObservedAt = observedAt,
                IsPlaceholder = true,
            };
            await db.Commits.AddAsync(commit, ct);
            await AttachNewIdentityAsync(ExternalEntityTypes.Commit, provider, identityKey, commit.Id, observedAt,
                (i, id) => i.CommitId = id, ct);
        }

        if (repo is not null && commit.SourceRepositoryId != repo.Id)
        {
            // Adopt the repository scope once known; rows never migrate between
            // repositories, they only gain the scope they were missing.
            commit.SourceRepositoryId ??= repo.Id;
        }

        _entityCache[cacheKey] = commit;
        return commit;
    }

    public async Task<WorkflowRun> ResolveWorkflowRunAsync(
        string provider, string externalName, string? repositoryKey, DateTimeOffset observedAt, CancellationToken ct)
    {
        var cacheKey = (provider, ExternalEntityTypes.WorkflowRun, externalName);
        if (_entityCache.TryGetValue(cacheKey, out var cached))
            return (WorkflowRun)cached;

        var identity = await FindIdentityAsync(provider, ExternalEntityTypes.WorkflowRun, externalName, ct);
        var run = await LoadAsync<WorkflowRun>(identity?.WorkflowRunId, ct);
        if (run is null)
        {
            run = new WorkflowRun
            {
                ExternalName = externalName,
                CreatedByProvider = provider,
                FirstObservedAt = observedAt,
                LastObservedAt = observedAt,
                IsPlaceholder = true,
            };
            await db.WorkflowRuns.AddAsync(run, ct);
            await AttachNewIdentityAsync(ExternalEntityTypes.WorkflowRun, provider, externalName, run.Id, observedAt,
                (i, id) => i.WorkflowRunId = id, ct);
        }

        if (!string.IsNullOrWhiteSpace(repositoryKey))
        {
            // Runs belong to exactly one repository.
            var repo = await ResolveRepositoryAsync(provider, repositoryKey, observedAt, ct);
            run.SourceRepositoryId ??= repo.Id;
        }

        _entityCache[cacheKey] = run;
        return run;
    }

    /// <summary>
    /// Artifacts are resolved by alias identities: digest first (content
    /// identity), then a provider-stable external key hint, then name@version.
    /// Identifiers learned later are registered as additional aliases so
    /// references remain stable regardless of which identifier arrived first.
    /// </summary>
    public async Task<BuildArtifact> ResolveArtifactAsync(string provider, ArtifactDescriptor descriptor, DateTimeOffset observedAt, CancellationToken ct)
    {
        var digestKey = ArtifactDescriptorKeys.DigestKey(descriptor);
        var externalKey = ArtifactDescriptorKeys.ExternalKey(descriptor);
        var versionKey = ArtifactDescriptorKeys.VersionKey(descriptor);

        // Memo-first: artifacts created (or resolved) earlier in this batch may
        // not be flushed yet, so database lookups alone would duplicate them.
        foreach (var key in new[] { digestKey, externalKey, versionKey })
        {
            if (key is not null && _artifactCache.TryGetValue(key, out var cachedArtifact))
            {
                foreach (var alias in new[] { digestKey, externalKey, versionKey }.Where(k => k is not null))
                    await EnsureAliasAsync(cachedArtifact, alias!, provider, observedAt, ct);
                Touch(cachedArtifact, observedAt);
                return cachedArtifact;
            }
        }

        BuildArtifact? artifact = null;
        ExternalIdentity? matchedIdentity = null;

        if (digestKey is not null)
        {
            matchedIdentity = await FindAnyProviderIdentityAsync(ExternalEntityTypes.BuildArtifact, digestKey, ct);
            artifact ??= await LoadAsync<BuildArtifact>(matchedIdentity?.BuildArtifactId, ct)
                ?? await db.BuildArtifacts.FirstOrDefaultAsync(a => a.CanonicalKey == digestKey, ct);
        }
        if (artifact is null && externalKey is not null)
        {
            matchedIdentity ??= await FindAnyProviderIdentityAsync(ExternalEntityTypes.BuildArtifact, externalKey, ct);
            artifact ??= await LoadAsync<BuildArtifact>(matchedIdentity?.BuildArtifactId, ct)
                ?? await db.BuildArtifacts.FirstOrDefaultAsync(a => a.CanonicalKey == externalKey, ct);
        }
        if (artifact is null && versionKey is not null)
        {
            matchedIdentity ??= await FindAnyProviderIdentityAsync(ExternalEntityTypes.BuildArtifact, versionKey, ct);
            artifact ??= await LoadAsync<BuildArtifact>(matchedIdentity?.BuildArtifactId, ct)
                ?? await db.BuildArtifacts.FirstOrDefaultAsync(a => a.CanonicalKey == versionKey, ct);
        }
        if (artifact is not null)
        {
            // Register any newly-learned aliases against the same artifact.
            foreach (var alias in new[] { digestKey, externalKey, versionKey }.Where(k => k is not null))
            {
                await EnsureAliasAsync(artifact, alias!, provider, observedAt, ct);
                _artifactCache.TryAdd(alias!, artifact);
            }
            Touch(artifact, observedAt);
            return artifact;
        }

        // Unknown artifact: create with the most specific canonical key available.
        var canonical = digestKey ?? externalKey ?? versionKey ?? throw new ArgumentException("Artifact descriptor must carry a digest, a canonical key hint, or a version.", nameof(descriptor));
        artifact = new BuildArtifact
        {
            Name = descriptor.Name.Trim(),
            Version = descriptor.Version,
            Digest = descriptor.Digest,
            Uri = descriptor.Uri,
            CanonicalKey = canonical,
            CreatedByProvider = provider,
            FirstObservedAt = observedAt,
            LastObservedAt = observedAt,
            IsPlaceholder = true,
        };
        await db.BuildArtifacts.AddAsync(artifact, ct);
        foreach (var alias in new[] { digestKey, externalKey, versionKey }.Where(k => k is not null))
        {
            await EnsureAliasAsync(artifact, alias!, provider, observedAt, ct);
            _artifactCache[alias!] = artifact;
        }
        return artifact;
    }

    public async Task<Service> ResolveServiceAsync(string provider, string rawName, DateTimeOffset observedAt, CancellationToken ct)
    {
        var name = NormalizeName(rawName);
        var cacheKey = name;
        if (_serviceCache.TryGetValue(cacheKey, out var cachedService))
        {
            await EnsureIdentityForNaturalKeyAsync(ExternalEntityTypes.Service, provider, name, cachedService.Id, observedAt,
                (i, id) => i.ServiceId = id, ct);
            return cachedService;
        }

        var existing = await db.Services.FirstOrDefaultAsync(s => s.Name == name, ct);
        Service service;
        if (existing is not null)
        {
            await EnsureIdentityForNaturalKeyAsync(ExternalEntityTypes.Service, provider, name, existing.Id, observedAt, (i, id) => i.ServiceId = id, ct);
            service = existing;
        }
        else
        {
            service = new Service { Name = name, CreatedByProvider = provider, FirstObservedAt = observedAt, LastObservedAt = observedAt, IsPlaceholder = true };
            await db.Services.AddAsync(service, ct);
            await AttachNewIdentityAsync(ExternalEntityTypes.Service, provider, name, service.Id, observedAt, (i, id) => i.ServiceId = id, ct);
        }

        _serviceCache[cacheKey] = service;
        return service;
    }

    public async Task<DeploymentEnvironment> ResolveEnvironmentAsync(string provider, string rawName, DateTimeOffset observedAt, CancellationToken ct)
    {
        var name = NormalizeName(rawName);
        var cacheKey = name;
        if (_environmentCache.TryGetValue(cacheKey, out var cachedEnvironment))
        {
            await EnsureIdentityForNaturalKeyAsync(ExternalEntityTypes.Environment, provider, name, cachedEnvironment.Id, observedAt,
                (i, id) => i.EnvironmentId = id, ct);
            return cachedEnvironment;
        }

        var existing = await db.Environments.FirstOrDefaultAsync(e => e.Name == name, ct);
        DeploymentEnvironment env;
        if (existing is not null)
        {
            await EnsureIdentityForNaturalKeyAsync(ExternalEntityTypes.Environment, provider, name, existing.Id, observedAt, (i, id) => i.EnvironmentId = id, ct);
            env = existing;
        }
        else
        {
            env = new DeploymentEnvironment { Name = name, CreatedByProvider = provider, FirstObservedAt = observedAt, LastObservedAt = observedAt, IsPlaceholder = true };
            await db.Environments.AddAsync(env, ct);
            await AttachNewIdentityAsync(ExternalEntityTypes.Environment, provider, name, env.Id, observedAt, (i, id) => i.EnvironmentId = id, ct);
        }

        _environmentCache[cacheKey] = env;
        return env;
    }

    public async Task<Engineer?> ResolveEngineerAsync(string provider, EngineerRef? reference, DateTimeOffset observedAt, CancellationToken ct)
    {
        if (reference is null)
            return null;

        var email = NormalizeNullable(reference.Email);
        var displayName = NormalizeNullable(reference.DisplayName);
        var cacheKey = email ?? $"name:{displayName}";
        if (_engineerCache.TryGetValue(cacheKey, out var cached))
            return cached;

        Engineer? engineer = null;
        if (email is not null)
            engineer = await db.Engineers.FirstOrDefaultAsync(e => e.Email == email, ct);
        if (engineer is null && displayName is not null)
            engineer = await db.Engineers.FirstOrDefaultAsync(e => e.Email == null && e.DisplayName == displayName, ct);

        if (engineer is null)
        {
            engineer = new Engineer
            {
                DisplayName = displayName ?? email ?? "unknown",
                Email = email,
                CreatedByProvider = provider,
                FirstObservedAt = observedAt,
                LastObservedAt = observedAt,
            };
            await db.Engineers.AddAsync(engineer, ct);
        }
        else
        {
            if (email is not null) engineer.Email = email;
            if (displayName is not null) engineer.DisplayName = displayName;
            Touch(engineer, observedAt);
        }
        _engineerCache[cacheKey] = engineer;
        return engineer;
    }

    /// <summary>
    /// Looks up one identity row. Deliberately without Include: an identity has
    /// ten typed foreign keys and eager-loading all of them turns a single-row
    /// index seek into an eleven-relation LEFT JOIN that PostgreSQL has to plan
    /// on every call. Callers load the one entity the row actually points at
    /// with <see cref="LoadAsync{TEntity}"/> instead.
    /// </summary>
    private Task<ExternalIdentity?> FindIdentityAsync(string provider, string entityType, string key, CancellationToken ct) =>
        db.ExternalIdentities
            .FirstOrDefaultAsync(i => i.Provider == provider && i.EntityTypeName == entityType && i.ExternalKey == key, ct);

    /// <summary>Loads the entity an identity points at; already-tracked rows resolve without a round trip.</summary>
    private async ValueTask<TEntity?> LoadAsync<TEntity>(Guid? id, CancellationToken ct) where TEntity : class =>
        id is { } value ? await db.Set<TEntity>().FindAsync([value], ct) : null;

    private Task<ExternalIdentity?> FindAnyProviderIdentityAsync(string entityType, string key, CancellationToken ct) =>
        db.ExternalIdentities
            .FirstOrDefaultAsync(i => i.EntityTypeName == entityType && i.ExternalKey == key, ct);

    private async Task AttachIdentityAsync(
        ExternalIdentity? existingIdentity,
        string entityType,
        string provider,
        string key,
        Guid entityId,
        DateTimeOffset observedAt,
        Action<ExternalIdentity, Guid> assign,
        CancellationToken ct)
    {
        if (existingIdentity is not null)
        {
            assign(existingIdentity, entityId);
            if (observedAt > existingIdentity.LastObservedAt)
                existingIdentity.LastObservedAt = observedAt;
            return;
        }
        await AttachNewIdentityAsync(entityType, provider, key, entityId, observedAt, assign, ct);
    }

    private async Task AttachNewIdentityAsync(
        string entityType, string provider, string key, Guid entityId, DateTimeOffset observedAt,
        Action<ExternalIdentity, Guid> assign,
        CancellationToken ct)
    {
        // Entity ids are client-generated; persistence is deferred to the single
        // batch save so the whole ingestion stays atomic.
        var identity = new ExternalIdentity
        {
            Provider = provider,
            EntityTypeName = entityType,
            ExternalKey = key,
            FirstObservedAt = observedAt,
            LastObservedAt = observedAt,
        };
        assign(identity, entityId);
        await db.ExternalIdentities.AddAsync(identity, ct);
    }

    private async Task EnsureIdentityForNaturalKeyAsync(
        string entityType, string provider, string key, Guid entityId, DateTimeOffset observedAt,
        Action<ExternalIdentity, Guid> assign,
        CancellationToken ct)
    {
        var exists = db.ChangeTracker.Entries<ExternalIdentity>()
            .Any(entry => entry.State != EntityState.Deleted
                && entry.Entity.Provider == provider
                && entry.Entity.EntityTypeName == entityType
                && entry.Entity.ExternalKey == key);
        if (!exists)
        {
            exists = await db.ExternalIdentities.AnyAsync(
                i => i.Provider == provider && i.EntityTypeName == entityType && i.ExternalKey == key, ct);
        }
        if (!exists)
            await AttachNewIdentityAsync(entityType, provider, key, entityId, observedAt, assign, ct);
    }

    private async Task EnsureAliasAsync(BuildArtifact artifact, string aliasKey, string provider, DateTimeOffset observedAt, CancellationToken ct)
    {
        var exists = db.ChangeTracker.Entries<ExternalIdentity>()
            .Any(entry => entry.State != EntityState.Deleted
                && entry.Entity.Provider == provider
                && entry.Entity.EntityTypeName == ExternalEntityTypes.BuildArtifact
                && entry.Entity.ExternalKey == aliasKey);
        if (!exists)
        {
            exists = await db.ExternalIdentities.AnyAsync(
                i => i.Provider == provider
                    && i.EntityTypeName == ExternalEntityTypes.BuildArtifact
                    && i.ExternalKey == aliasKey, ct);
        }
        if (exists)
            return;
        var identity = new ExternalIdentity
        {
            Provider = provider,
            EntityTypeName = ExternalEntityTypes.BuildArtifact,
            ExternalKey = aliasKey,
            BuildArtifactId = artifact.Id,
            FirstObservedAt = observedAt,
            LastObservedAt = observedAt,
        };
        await db.ExternalIdentities.AddAsync(identity, ct);
    }

    internal static void Touch(IExternallySourced entity, DateTimeOffset observedAt)
    {
        if (observedAt < entity.FirstObservedAt)
            entity.FirstObservedAt = observedAt;
        if (observedAt > entity.LastObservedAt)
            entity.LastObservedAt = observedAt;
    }

    internal static void MarkObserved(IExternallySourced entity, DateTimeOffset observedAt)
    {
        entity.IsPlaceholder = false;
        Touch(entity, observedAt);
    }

    internal static string NormalizeSha(string sha) => sha.Trim().ToLowerInvariant();

    internal static string NormalizeName(string raw) => raw.Trim().ToLowerInvariant();

    internal static string? NormalizeNullable(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();
}

/// <summary>Canonical key helpers shared by resolver and appliers.</summary>
internal static class ArtifactDescriptorKeys
{
    public static string? VersionKey(ArtifactDescriptor d)
    {
        var name = d.Name?.Trim();
        var version = d.Version?.Trim();
        return string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version) ? null : $"{name}@{version}".ToLowerInvariant();
    }

    public static string? DigestKey(ArtifactDescriptor d) =>
        string.IsNullOrWhiteSpace(d.Digest) ? null : d.Digest!.Trim().ToLowerInvariant();

    public static string? ExternalKey(ArtifactDescriptor d) =>
        string.IsNullOrWhiteSpace(d.CanonicalKeyHint) ? null : d.CanonicalKeyHint!.Trim().ToLowerInvariant();
}
