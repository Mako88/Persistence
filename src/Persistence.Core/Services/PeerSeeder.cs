using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Runtime;
using System.Text.Json;

namespace Persistence.Services;

/// <summary>
/// Seeds a brand-new store from a per-database seed file. Each database <c>{name}.db</c> is paired
/// with an optional <c>{name}.json</c> in <see cref="IAppConfig.SeedsDirectory"/> (default: a
/// <c>seeds</c> folder beside the database directory) holding an array of <see cref="PeerSeed"/>s.
/// They become authored fragments sourced to the remote peer — the peer's own, freely curatable —
/// in contrast to the embedded onboarding text, which becomes protected System fragments.
/// </summary>
[Singleton]
public class PeerSeeder : IPeerSeeder
{
    private static readonly JsonSerializerOptions SeedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IAppConfig config;
    private readonly ITagRepository tagRepo;
    private readonly ISessionContext sessionContext;

    /// <summary>
    /// Constructor
    /// </summary>
    public PeerSeeder(IAppConfig config, ITagRepository tagRepo, ISessionContext sessionContext)
    {
        this.config = config;
        this.tagRepo = tagRepo;
        this.sessionContext = sessionContext;
    }

    /// <inheritdoc />
    public async Task<int> SeedAsync(WorkingContextEntity context, CancellationToken ct = default)
    {
        var path = ResolveSeedFilePath();

        if (!File.Exists(path))
        {
            return 0;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, ct);
        }
        catch (IOException)
        {
            // An unreadable seed file should never block a peer from waking — better an empty start
            // than a crashed boot. (A new peer can always author its identity by hand.)
            return 0;
        }

        var seeds = Parse(json);
        var now = DateTimeOffset.UtcNow;
        var seeded = 0;

        foreach (var seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed.Content))
            {
                continue;
            }

            var (type, _) = FragmentTypeRules.ParseAuthorable(seed.Type);

            context.AddFragment(new WeightedContextFragment
            {
                FragmentType = type,
                Status = ContextFragmentStatus.Active,
                Content = seed.Content,
                Importance = Math.Clamp(seed.Importance, 0f, 1f),
                Confidence = Math.Clamp(seed.Confidence, 0f, 1f),
                Relevance = Math.Clamp(seed.Relevance, 0f, 1f),
                IsProtected = false, // the peer's own — curatable from the first turn
                Sources =
                [
                    new SourceEntity
                    {
                        Id = sessionContext.RemotePeerSourceId,
                        SourceType = SourceType.DigitalPeer,
                        CreatedUtc = now,
                        LastModifiedUtc = now,
                    },
                ],
                Tags = await ResolveTagsAsync(seed.Tags, ct),
                CreatedUtc = now,
                LastModifiedUtc = now,
            });

            seeded++;
        }

        return seeded;
    }

    /// <summary>
    /// The seed file for the active database: <c>{SeedsDirectory}/{dbName}.json</c>, where the db name
    /// is the active store's filename without extension. Public so the resolution is testable.
    /// </summary>
    public string ResolveSeedFilePath()
    {
        var dbName = Path.GetFileNameWithoutExtension(config.DatabasePath);
        return Path.Combine(ResolveSeedsDirectory(), $"{dbName}.json");
    }

    /// <summary>
    /// Parses a seed-file's JSON array into <see cref="PeerSeed"/>s. Lenient (case-insensitive,
    /// trailing commas, comments) and never throws on malformed input — returns empty instead, so a
    /// bad file degrades to "no seeds" rather than a crashed boot.
    /// </summary>
    public static IReadOnlyList<PeerSeed> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PeerSeed>>(json, SeedJsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The effective seeds directory: the configured <see cref="IAppConfig.SeedsDirectory"/>, or — when
    /// blank — a <c>seeds</c> folder beside the active database's directory (so it sits next to
    /// <c>dbs</c> by default, matching the rest of the store layout).
    /// </summary>
    private string ResolveSeedsDirectory()
    {
        if (!string.IsNullOrWhiteSpace(config.SeedsDirectory))
        {
            return config.SeedsDirectory.Trim();
        }

        var dbDir = Path.GetDirectoryName(config.DatabasePath);
        var parent = string.IsNullOrEmpty(dbDir) ? null : Path.GetDirectoryName(dbDir);

        return string.IsNullOrEmpty(parent) ? "seeds" : Path.Combine(parent, "seeds");
    }

    /// <summary>
    /// Resolves a seed's tag field — a single <c>a/b</c> path or several comma-separated — to leaf tags,
    /// creating any missing segments.
    /// </summary>
    private async Task<List<TagEntity>> ResolveTagsAsync(string? tags, CancellationToken ct)
    {
        var resolved = new List<TagEntity>();

        if (string.IsNullOrWhiteSpace(tags))
        {
            return resolved;
        }

        foreach (var path in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (tag, _) = await tagRepo.GetOrCreateByPathAsync(path, ct: ct);

            if (tag != null)
            {
                resolved.Add(tag);
            }
        }

        return resolved;
    }
}
