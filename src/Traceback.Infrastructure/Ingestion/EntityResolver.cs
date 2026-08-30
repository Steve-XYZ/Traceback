using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
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
    // Digest aliases are global content identities; provider-stable artifact
    // keys stay in their provider namespace.
    private readonly Dictionary<(string? Provider, string Key), BuildArtifact> _artifactCache = [];
    private readonly Dictionary<Guid, Guid> _artifactMerges = [];
    private readonly Dictionary<(string Provider, string Name), Service> _serviceCache = [];
    private readonly Dictionary<(string Provider, string Name), DeploymentEnvironment> _environmentCache = [];

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
    /// Artifacts are resolved by alias identities: digest first (global content
    /// identity), then a provider-stable external key hint, then name@version
    /// in the provider namespace. Identifiers learned later are registered as
    /// additional aliases so references remain stable regardless of which
    /// identifier arrived first.
    /// </summary>
    public async Task<BuildArtifact> ResolveArtifactAsync(string provider, ArtifactDescriptor descriptor, DateTimeOffset observedAt, CancellationToken ct)
    {
        var digestKey = ArtifactDescriptorKeys.DigestKey(descriptor);
        var externalKey = ArtifactDescriptorKeys.ExternalKey(descriptor);
        var versionKey = ArtifactDescriptorKeys.VersionKey(descriptor);
        BuildArtifact? artifact = null;

        // Memo-first: artifacts created (or resolved) earlier in this batch may
        // not be flushed yet, so database lookups alone would duplicate them.
        // Keep the hit in the normal merge path below: a later descriptor can
        // add a digest, URI, or alias to an artifact already seen in this batch.
        foreach (var cacheKey in ArtifactCacheKeys(provider, digestKey, externalKey, versionKey))
        {
            if (_artifactCache.TryGetValue(cacheKey, out var cachedArtifact))
            {
                artifact = cachedArtifact;
                break;
            }
        }

        BuildArtifact? digestOwner = null;
        if (digestKey is not null)
        {
            var identity = await FindAnyProviderIdentityAsync(ExternalEntityTypes.BuildArtifact, digestKey, ct);
            var identityArtifact = await LoadAsync<BuildArtifact>(identity?.BuildArtifactId, ct);
            digestOwner = identityArtifact is not null &&
                          string.Equals(NormalizeSha(identityArtifact.Digest ?? string.Empty), digestKey, StringComparison.Ordinal)
                ? identityArtifact
                : await db.BuildArtifacts.FirstOrDefaultAsync(a => a.Digest == digestKey, ct);
        }

        // Resolve provider aliases even when a digest owner was found. A
        // provider key can have been persisted on a different row in an earlier
        // batch; in that case the two rows must be reconciled before the caller
        // creates any new edge or deployment for this observation.
        var providerCandidates = new List<BuildArtifact>();
        foreach (var key in new[] { externalKey, versionKey }.Where(k => k is not null).Distinct(StringComparer.Ordinal))
        {
            var cacheKey = (Provider: (string?)provider, Key: key!);
            if (_artifactCache.TryGetValue(cacheKey, out var cachedArtifact))
            {
                providerCandidates.Add(cachedArtifact);
                continue;
            }

            var identity = await FindIdentityAsync(provider, ExternalEntityTypes.BuildArtifact, key!, ct);
            var candidate = await LoadAsync<BuildArtifact>(identity?.BuildArtifactId, ct)
                ?? await FindProviderCanonicalArtifactAsync(provider, key!, ct);
            if (candidate is not null)
                providerCandidates.Add(candidate);
        }

        // The same alias can arrive through both the per-batch cache and the
        // database. Preserve the first candidate's order while avoiding a
        // repeated merge of the same row.
        providerCandidates = providerCandidates
            .GroupBy(candidate => candidate.Id)
            .Select(group => group.First())
            .ToList();

        if (digestOwner is not null)
        {
            foreach (var candidate in providerCandidates)
            {
                if (candidate.Id == digestOwner.Id)
                    continue;
                ValidateArtifactDigest(candidate, digestKey!);
                digestOwner = await MergeArtifactAsync(candidate, digestOwner, ct);
            }

            if (artifact is not null && artifact.Id != digestOwner.Id)
            {
                ValidateArtifactDigest(artifact, digestKey!);
                artifact = await MergeArtifactAsync(artifact, digestOwner, ct);
            }
            else
            {
                artifact = digestOwner;
            }
        }
        else if (artifact is null)
        {
            artifact = providerCandidates.FirstOrDefault();
        }

        if (artifact is not null)
        {
            // Register any newly-learned aliases against the same artifact.
            foreach (var cacheKey in ArtifactCacheKeys(provider, digestKey, externalKey, versionKey))
            {
                await EnsureAliasAsync(artifact, cacheKey.Key, provider, observedAt, ct);
                _artifactCache[cacheKey] = artifact;
            }
            if (artifact.Digest is null && digestKey is not null)
                artifact.Digest = digestKey;
            if (artifact.Uri is null && descriptor.Uri is not null)
                artifact.Uri = descriptor.Uri;
            Touch(artifact, observedAt);
            return artifact;
        }

        // Unknown artifact: content digests are globally unique. Provider keys
        // and version labels are not, so namespace their persisted canonical
        // fallback while retaining the raw value as an ExternalIdentity alias.
        var canonical = digestKey
            ?? ProviderScopedCanonicalKey(provider, externalKey ?? versionKey
                ?? throw new ArgumentException("Artifact descriptor must carry a digest, a canonical key hint, or a version.", nameof(descriptor)));
        artifact = new BuildArtifact
        {
            Name = descriptor.Name.Trim(),
            Version = descriptor.Version,
            Digest = digestKey,
            Uri = descriptor.Uri,
            CanonicalKey = canonical,
            CreatedByProvider = provider,
            FirstObservedAt = observedAt,
            LastObservedAt = observedAt,
            IsPlaceholder = true,
        };
        await db.BuildArtifacts.AddAsync(artifact, ct);
        foreach (var cacheKey in ArtifactCacheKeys(provider, digestKey, externalKey, versionKey))
        {
            await EnsureAliasAsync(artifact, cacheKey.Key, provider, observedAt, ct);
            _artifactCache[cacheKey] = artifact;
        }
        return artifact;
    }

    public async Task<Service> ResolveServiceAsync(string provider, string rawName, DateTimeOffset observedAt, CancellationToken ct)
    {
        var name = NormalizeName(rawName);
        var cacheKey = (provider, name);
        if (_serviceCache.TryGetValue(cacheKey, out var cachedService))
            return cachedService;

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
        var cacheKey = (provider, name);
        if (_environmentCache.TryGetValue(cacheKey, out var cachedEnvironment))
            return cachedEnvironment;

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

    private Task<BuildArtifact?> FindProviderCanonicalArtifactAsync(string provider, string key, CancellationToken ct)
    {
        var scopedKey = ProviderScopedCanonicalKey(provider, key);
        return db.BuildArtifacts.FirstOrDefaultAsync(
            a => a.CreatedByProvider == provider &&
                 (a.CanonicalKey == key || a.CanonicalKey == scopedKey), ct);
    }

    private static void ValidateArtifactDigest(BuildArtifact artifact, string digestKey)
    {
        if (artifact.Digest is not null &&
            !string.Equals(NormalizeSha(artifact.Digest), digestKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Artifact provider key resolves to digest '{artifact.Digest}', but the observation reports '{digestKey}'.");
        }
    }

    /// <summary>
    /// Reconciles a provider-key artifact into an already persisted digest
    /// owner. Artifact references are foreign keys, so the loser cannot simply
    /// be deleted: all join rows, deployments, and aliases must be moved first.
    /// Duplicate join/deployment natural keys are collapsed before the foreign
    /// key is changed, which keeps the unique indexes valid throughout the
    /// enclosing ingestion transaction.
    /// </summary>
    private async Task<BuildArtifact> MergeArtifactAsync(
        BuildArtifact source,
        BuildArtifact target,
        CancellationToken ct)
    {
        if (source.Id == target.Id)
            return target;

        if (_artifactMerges.TryGetValue(source.Id, out var mergedId) && mergedId == target.Id)
            return target;

        ValidateArtifactDigest(source, NormalizeSha(target.Digest ?? string.Empty));
        if (target.Digest is null && source.Digest is not null)
            target.Digest = NormalizeSha(source.Digest);

        if (target.Version is null && source.Version is not null)
            target.Version = source.Version;
        if (target.Uri is null && source.Uri is not null)
            target.Uri = source.Uri;
        if (target.Name.Length == 0 && source.Name.Length > 0)
            target.Name = source.Name;
        target.IsPlaceholder &= source.IsPlaceholder;
        Touch(target, source.FirstObservedAt);
        Touch(target, source.LastObservedAt);

        await MergeWorkflowRunArtifactReferencesAsync(source.Id, target.Id, ct);
        await MergeDeploymentReferencesAsync(source.Id, target.Id, ct);
        await MergeArtifactIdentitiesAsync(source.Id, target.Id, ct);

        // A source row created earlier in this batch may still be Added. Its
        // identities and references were handled in-memory by the helpers;
        // removing it now prevents the pending insert from resurrecting it.
        db.BuildArtifacts.Remove(source);
        _artifactMerges[source.Id] = target.Id;
        foreach (var key in _artifactCache
                     .Where(pair => pair.Value.Id == source.Id)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _artifactCache[key] = target;
        }

        return target;
    }

    private async Task MergeWorkflowRunArtifactReferencesAsync(Guid sourceId, Guid targetId, CancellationToken ct)
    {
        var trackedSource = db.ChangeTracker.Entries<WorkflowRunArtifact>()
            .Where(entry => entry.State != EntityState.Deleted && entry.Entity.BuildArtifactId == sourceId)
            .ToList();
        var trackedTargetRunIds = db.ChangeTracker.Entries<WorkflowRunArtifact>()
            .Where(entry => entry.State != EntityState.Deleted && entry.Entity.BuildArtifactId == targetId)
            .Select(entry => entry.Entity.WorkflowRunId)
            .ToHashSet();

        var persistedSource = await db.WorkflowRunArtifacts
            .AsNoTracking()
            .Where(edge => edge.BuildArtifactId == sourceId)
            .ToListAsync(ct);
        var persistedTargetRunIds = await db.WorkflowRunArtifacts
            .AsNoTracking()
            .Where(edge => edge.BuildArtifactId == targetId)
            .Select(edge => edge.WorkflowRunId)
            .ToListAsync(ct);
        var targetRunIds = persistedTargetRunIds.ToHashSet();
        targetRunIds.UnionWith(trackedTargetRunIds);

        foreach (var entry in trackedSource)
        {
            if (targetRunIds.Contains(entry.Entity.WorkflowRunId))
            {
                var trackedTarget = db.ChangeTracker.Entries<WorkflowRunArtifact>()
                    .FirstOrDefault(candidate => candidate.State != EntityState.Deleted &&
                        candidate.Entity.WorkflowRunId == entry.Entity.WorkflowRunId &&
                        candidate.Entity.BuildArtifactId == targetId);
                if (trackedTarget is not null &&
                    entry.Entity.EstablishedSequence < trackedTarget.Entity.EstablishedSequence)
                {
                    trackedTarget.Entity.EstablishedSequence = entry.Entity.EstablishedSequence;
                }

                if (entry.State == EntityState.Added)
                    db.WorkflowRunArtifacts.Remove(entry.Entity);
                else
                    entry.State = EntityState.Detached;
            }
            else if (entry.State == EntityState.Added)
            {
                // Added keys are not database keys yet, so changing this value
                // retains the pending edge and its observation sequence.
                entry.Entity.BuildArtifactId = targetId;
            }
            else
            {
                // Existing key values are immutable in EF's change tracker. The
                // database update below handles this row after detaching it.
                entry.State = EntityState.Detached;
            }
        }

        foreach (var edge in persistedSource.Where(edge => targetRunIds.Contains(edge.WorkflowRunId)))
        {
            var targetEdge = await db.WorkflowRunArtifacts.FirstAsync(
                candidate => candidate.WorkflowRunId == edge.WorkflowRunId && candidate.BuildArtifactId == targetId,
                ct);
            if (edge.EstablishedSequence < targetEdge.EstablishedSequence)
                targetEdge.EstablishedSequence = edge.EstablishedSequence;
        }

        if (targetRunIds.Count > 0)
        {
            await db.WorkflowRunArtifacts
                .Where(edge => edge.BuildArtifactId == sourceId && targetRunIds.Contains(edge.WorkflowRunId))
                .ExecuteDeleteAsync(ct);
        }

        await db.WorkflowRunArtifacts
            .Where(edge => edge.BuildArtifactId == sourceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(edge => edge.BuildArtifactId, targetId), ct);
    }

    private async Task MergeDeploymentReferencesAsync(Guid sourceId, Guid targetId, CancellationToken ct)
    {
        var trackedSource = db.ChangeTracker.Entries<Deployment>()
            .Where(entry => entry.State != EntityState.Deleted && entry.Entity.ArtifactId == sourceId)
            .ToList();
        var trackedTargetKeys = db.ChangeTracker.Entries<Deployment>()
            .Where(entry => entry.State != EntityState.Deleted && entry.Entity.ArtifactId == targetId)
            .Select(entry => DeploymentNaturalKey(entry.Entity))
            .ToHashSet();

        var persistedSource = await db.Deployments
            .AsNoTracking()
            .Where(deployment => deployment.ArtifactId == sourceId)
            .ToListAsync(ct);
        var persistedTarget = await db.Deployments
            .AsNoTracking()
            .Where(deployment => deployment.ArtifactId == targetId)
            .ToListAsync(ct);
        var targetKeys = persistedTarget.Select(DeploymentNaturalKey).ToHashSet();
        targetKeys.UnionWith(trackedTargetKeys);

        foreach (var entry in trackedSource)
        {
            var sourceDeployment = entry.Entity;
            if (targetKeys.Contains(DeploymentNaturalKey(sourceDeployment)))
            {
                var targetDeployment = db.ChangeTracker.Entries<Deployment>()
                    .Where(candidate => candidate.State != EntityState.Deleted && candidate.Entity.ArtifactId == targetId)
                    .Select(candidate => candidate.Entity)
                    .FirstOrDefault(candidate => DeploymentNaturalKey(candidate) == DeploymentNaturalKey(sourceDeployment));
                targetDeployment ??= await db.Deployments.FirstOrDefaultAsync(
                    candidate => candidate.ArtifactId == targetId &&
                        candidate.ServiceId == sourceDeployment.ServiceId &&
                        candidate.EnvironmentId == sourceDeployment.EnvironmentId &&
                        candidate.DeployedAt == sourceDeployment.DeployedAt, ct);
                if (targetDeployment is null)
                    throw new InvalidOperationException("Artifact deployment reconciliation lost its target row.");

                MergeDeploymentMetadata(targetDeployment, sourceDeployment);
                await MergeDeploymentIdentitiesAsync(sourceDeployment.Id, targetDeployment.Id, ct);
                if (entry.State == EntityState.Added)
                    db.Deployments.Remove(sourceDeployment);
                else
                    entry.State = EntityState.Detached;
            }
            else if (entry.State == EntityState.Added)
            {
                sourceDeployment.ArtifactId = targetId;
            }
            else
            {
                entry.State = EntityState.Detached;
            }
        }

        foreach (var sourceDeployment in persistedSource)
        {
            var naturalKey = DeploymentNaturalKey(sourceDeployment);
            var targetDeployment = persistedTarget.FirstOrDefault(
                candidate => DeploymentNaturalKey(candidate) == naturalKey);
            if (targetDeployment is null)
            {
                await db.Deployments
                    .Where(deployment => deployment.Id == sourceDeployment.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(deployment => deployment.ArtifactId, targetId), ct);
                continue;
            }

            var trackedTarget = await db.Deployments.FindAsync([targetDeployment.Id], ct)
                ?? throw new InvalidOperationException("Artifact deployment reconciliation lost its target row.");
            MergeDeploymentMetadata(trackedTarget, sourceDeployment);
            await MergeDeploymentIdentitiesAsync(sourceDeployment.Id, trackedTarget.Id, ct);
            await db.Deployments
                .Where(deployment => deployment.Id == sourceDeployment.Id)
                .ExecuteDeleteAsync(ct);
        }
    }

    private async Task MergeArtifactIdentitiesAsync(Guid sourceId, Guid targetId, CancellationToken ct)
    {
        var trackedSource = db.ChangeTracker.Entries<ExternalIdentity>()
            .Where(entry => entry.State != EntityState.Deleted && entry.Entity.BuildArtifactId == sourceId)
            .ToList();
        var trackedTargetKeys = db.ChangeTracker.Entries<ExternalIdentity>()
            .Where(entry => entry.State != EntityState.Deleted && entry.Entity.BuildArtifactId == targetId)
            .Select(entry => IdentityKey(entry.Entity))
            .ToHashSet();

        var persistedSource = await db.ExternalIdentities
            .AsNoTracking()
            .Where(identity => identity.BuildArtifactId == sourceId)
            .ToListAsync(ct);
        var persistedTarget = await db.ExternalIdentities
            .AsNoTracking()
            .Where(identity => identity.BuildArtifactId == targetId)
            .ToListAsync(ct);
        var targetKeys = persistedTarget.Select(IdentityKey).ToHashSet();
        targetKeys.UnionWith(trackedTargetKeys);

        foreach (var entry in trackedSource)
        {
            var identity = entry.Entity;
            if (targetKeys.Contains(IdentityKey(identity)))
            {
                var targetIdentity = db.ChangeTracker.Entries<ExternalIdentity>()
                    .Where(candidate => candidate.State != EntityState.Deleted && candidate.Entity.BuildArtifactId == targetId)
                    .Select(candidate => candidate.Entity)
                    .FirstOrDefault(candidate => IdentityKey(candidate) == IdentityKey(identity));
                targetIdentity ??= await db.ExternalIdentities.FirstOrDefaultAsync(
                    candidate => candidate.Provider == identity.Provider &&
                        candidate.EntityTypeName == identity.EntityTypeName &&
                        candidate.ExternalKey == identity.ExternalKey &&
                        candidate.BuildArtifactId == targetId, ct);
                if (targetIdentity is null)
                    throw new InvalidOperationException("Artifact identity reconciliation lost its target row.");

                MergeIdentityMetadata(targetIdentity, identity);
                if (entry.State == EntityState.Added)
                    db.ExternalIdentities.Remove(identity);
                else
                    entry.State = EntityState.Detached;
            }
            else if (entry.State == EntityState.Added)
            {
                identity.BuildArtifactId = targetId;
            }
            else
            {
                entry.State = EntityState.Detached;
            }
        }

        foreach (var sourceIdentity in persistedSource)
        {
            var targetIdentity = persistedTarget.FirstOrDefault(
                candidate => IdentityKey(candidate) == IdentityKey(sourceIdentity));
            if (targetIdentity is null)
                continue;

            var trackedTarget = await db.ExternalIdentities.FindAsync([targetIdentity.Id], ct)
                ?? throw new InvalidOperationException("Artifact identity reconciliation lost its target row.");
            MergeIdentityMetadata(trackedTarget, sourceIdentity);
            await db.ExternalIdentities
                .Where(identity => identity.Id == sourceIdentity.Id)
                .ExecuteDeleteAsync(ct);
        }

        await db.ExternalIdentities
            .Where(identity => identity.BuildArtifactId == sourceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(identity => identity.BuildArtifactId, targetId), ct);
    }

    private async Task MergeDeploymentIdentitiesAsync(Guid sourceId, Guid targetId, CancellationToken ct)
    {
        var trackedSource = db.ChangeTracker.Entries<ExternalIdentity>()
            .Where(entry => entry.State != EntityState.Deleted && entry.Entity.DeploymentId == sourceId)
            .ToList();
        var trackedTargetKeys = db.ChangeTracker.Entries<ExternalIdentity>()
            .Where(entry => entry.State != EntityState.Deleted && entry.Entity.DeploymentId == targetId)
            .Select(entry => IdentityKey(entry.Entity))
            .ToHashSet();

        var persistedSource = await db.ExternalIdentities
            .AsNoTracking()
            .Where(identity => identity.DeploymentId == sourceId)
            .ToListAsync(ct);
        var persistedTarget = await db.ExternalIdentities
            .AsNoTracking()
            .Where(identity => identity.DeploymentId == targetId)
            .ToListAsync(ct);
        var targetKeys = persistedTarget.Select(IdentityKey).ToHashSet();
        targetKeys.UnionWith(trackedTargetKeys);

        foreach (var entry in trackedSource)
        {
            var identity = entry.Entity;
            if (targetKeys.Contains(IdentityKey(identity)))
            {
                var targetIdentity = db.ChangeTracker.Entries<ExternalIdentity>()
                    .Where(candidate => candidate.State != EntityState.Deleted && candidate.Entity.DeploymentId == targetId)
                    .Select(candidate => candidate.Entity)
                    .FirstOrDefault(candidate => IdentityKey(candidate) == IdentityKey(identity));
                targetIdentity ??= await db.ExternalIdentities.FirstOrDefaultAsync(
                    candidate => candidate.Provider == identity.Provider &&
                        candidate.EntityTypeName == identity.EntityTypeName &&
                        candidate.ExternalKey == identity.ExternalKey &&
                        candidate.DeploymentId == targetId, ct);
                if (targetIdentity is null)
                    throw new InvalidOperationException("Deployment identity reconciliation lost its target row.");

                MergeIdentityMetadata(targetIdentity, identity);
                if (entry.State == EntityState.Added)
                    db.ExternalIdentities.Remove(identity);
                else
                    entry.State = EntityState.Detached;
            }
            else if (entry.State == EntityState.Added)
            {
                identity.DeploymentId = targetId;
            }
            else
            {
                entry.State = EntityState.Detached;
            }
        }

        foreach (var sourceIdentity in persistedSource)
        {
            var targetIdentity = persistedTarget.FirstOrDefault(
                candidate => IdentityKey(candidate) == IdentityKey(sourceIdentity));
            if (targetIdentity is null)
                continue;

            var trackedTarget = await db.ExternalIdentities.FindAsync([targetIdentity.Id], ct)
                ?? throw new InvalidOperationException("Deployment identity reconciliation lost its target row.");
            MergeIdentityMetadata(trackedTarget, sourceIdentity);
            await db.ExternalIdentities
                .Where(identity => identity.Id == sourceIdentity.Id)
                .ExecuteDeleteAsync(ct);
        }

        await db.ExternalIdentities
            .Where(identity => identity.DeploymentId == sourceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(identity => identity.DeploymentId, targetId), ct);
    }

    private static (Guid ServiceId, Guid EnvironmentId, DateTimeOffset DeployedAt) DeploymentNaturalKey(Deployment deployment) =>
        (deployment.ServiceId, deployment.EnvironmentId, deployment.DeployedAt);

    private static (string Provider, string EntityTypeName, string ExternalKey) IdentityKey(ExternalIdentity identity) =>
        (identity.Provider, identity.EntityTypeName, identity.ExternalKey);

    private static void MergeIdentityMetadata(ExternalIdentity target, ExternalIdentity source)
    {
        if (source.FirstObservedAt < target.FirstObservedAt)
            target.FirstObservedAt = source.FirstObservedAt;
        if (source.LastObservedAt > target.LastObservedAt)
            target.LastObservedAt = source.LastObservedAt;
    }

    private static void MergeDeploymentMetadata(Deployment target, Deployment source)
    {
        if (target.WorkflowRunId is null)
            target.WorkflowRunId = source.WorkflowRunId;
        if (target.Status == DeploymentStatus.Unknown)
            target.Status = source.Status;
        target.IsPlaceholder &= source.IsPlaceholder;
        Touch(target, source.FirstObservedAt);
        Touch(target, source.LastObservedAt);
    }

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
        var exists = await db.ExternalIdentities.AnyAsync(
            i => i.Provider == provider && i.EntityTypeName == entityType && i.ExternalKey == key, ct);
        if (!exists)
            await AttachNewIdentityAsync(entityType, provider, key, entityId, observedAt, assign, ct);
    }

    private async Task EnsureAliasAsync(BuildArtifact artifact, string aliasKey, string provider, DateTimeOffset observedAt, CancellationToken ct)
    {
        // A prior descriptor in this batch may have queued the same identity
        // without flushing it yet. Check tracked rows before querying the
        // database so cache-hit merges remain idempotent within the batch.
        var exists = db.ExternalIdentities.Local.Any(
            i => i.Provider == provider &&
                 i.EntityTypeName == ExternalEntityTypes.BuildArtifact &&
                 i.ExternalKey == aliasKey) ||
            await db.ExternalIdentities.AnyAsync(
                i => i.Provider == provider &&
                     i.EntityTypeName == ExternalEntityTypes.BuildArtifact &&
                     i.ExternalKey == aliasKey, ct);
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

    private static IEnumerable<(string? Provider, string Key)> ArtifactCacheKeys(
        string provider, string? digestKey, string? externalKey, string? versionKey)
    {
        if (digestKey is not null)
            yield return (null, digestKey);
        if (externalKey is not null)
            yield return (provider, externalKey);
        if (versionKey is not null)
            yield return (provider, versionKey);
    }

    private const int CanonicalKeyMaxLength = 768;

    private static string ProviderScopedCanonicalKey(string provider, string key)
    {
        // The length prefix makes the provider/key boundary unambiguous even
        // when either input contains delimiters. Long provider keys use a
        // bounded pair hash so the persisted canonical key stays within the
        // schema's 768-character limit.
        var readable = $"artifact:{provider.Length}:{provider}:{key}";
        if (readable.Length <= CanonicalKeyMaxLength)
            return readable;

        var input = Encoding.UTF8.GetBytes($"{provider.Length}:{provider}{key.Length}:{key}");
        var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        var hashedSuffix = $"sha256:{hash}";
        var providerPrefix = $"artifact:{provider.Length}:{provider}:";
        return providerPrefix.Length + hashedSuffix.Length <= CanonicalKeyMaxLength
            ? providerPrefix + hashedSuffix
            : $"artifact:{hashedSuffix}";
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
