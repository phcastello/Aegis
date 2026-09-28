using System.Globalization;
using Aegis.Application.Memory;
using Neo4j.Driver;

namespace Aegis.Infrastructure.Memory;

// Only IDs and structure come back from Neo4j. PostgreSQL supplies canonical names and status.
public sealed class Neo4jMemoryGraphStore(IDriver driver, MemoryGraphOptions options) : IMemoryGraphStore
{
    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await driver.VerifyConnectivityAsync();
        await WriteAsync("CREATE CONSTRAINT aegis_memory_entity_id IF NOT EXISTS FOR (n:AegisMemoryEntity) REQUIRE n.entityId IS UNIQUE", null, ct);
        await WriteAsync("CREATE CONSTRAINT aegis_memory_relation_id IF NOT EXISTS FOR ()-[r:AEGIS_RELATION]-() REQUIRE r.relationId IS UNIQUE", null, ct);
        await WriteAsync("CREATE INDEX aegis_memory_entity_name IF NOT EXISTS FOR (n:AegisMemoryEntity) ON (n.normalizedName)", null, ct);
    }

    public async Task<MemoryGraphEntityProjection?> GetEntityProjectionAsync(Guid id, CancellationToken ct)
    {
        var records = await ReadAsync("MATCH (n:AegisMemoryEntity {entityId:$id}) RETURN n", new { id = id.ToString() }, ct);
        if (records.Count == 0) return null;
        var n = records[0]["n"].As<INode>();
        return new(id, n["canonicalName"].As<string>(), n["normalizedName"].As<string>(), GetOptional<string>(n.Properties, "entityType"),
            n["aliases"].As<List<string>>(), checked((int)n["revision"].As<long>()),
            ParseDate(GetOptional<string>(n.Properties, "retiredAt")));
    }

    public Task UpsertEntityAsync(MemoryGraphEntityProjection e, CancellationToken ct) => WriteAsync("""
        MERGE (n:AegisMemoryEntity {entityId:$id})
        SET n.canonicalName=$name, n.normalizedName=$normalizedName, n.entityType=$type,
            n.aliases=$aliases, n.revision=$revision, n.retiredAt=$retiredAt
        """, new { id = e.EntityId.ToString(), name = e.CanonicalName, normalizedName = e.NormalizedName,
            type = e.EntityType, aliases = e.Aliases.ToArray(), revision = e.Revision, retiredAt = Date(e.RetiredAt) }, ct);

    public Task DeleteEntityAsync(Guid id, CancellationToken ct) =>
        WriteAsync("MATCH (n:AegisMemoryEntity {entityId:$id}) DETACH DELETE n", new { id = id.ToString() }, ct);

    public async Task<MemoryGraphRelationProjection?> GetRelationProjectionAsync(Guid id, CancellationToken ct)
    {
        var records = await ReadAsync("MATCH (a:AegisMemoryEntity)-[r:AEGIS_RELATION {relationId:$id}]->(b:AegisMemoryEntity) RETURN a.entityId AS subject, r, b.entityId AS object", new { id = id.ToString() }, ct);
        if (records.Count == 0) return null;
        var row = records[0];
        var r = row["r"].As<IRelationship>();
        return new(id, Guid.Parse(row["subject"].As<string>()), r["predicate"].As<string>(), Guid.Parse(row["object"].As<string>()),
            checked((int)r["revision"].As<long>()), ParseDate(GetOptional<string>(r.Properties, "validFrom")),
            ParseDate(GetOptional<string>(r.Properties, "validUntil")));
    }

    public Task UpsertRelationAsync(MemoryGraphRelationProjection r, CancellationToken ct) => WriteAsync("""
        OPTIONAL MATCH ()-[old:AEGIS_RELATION {relationId:$id}]-()
        WITH collect(old) AS oldRelations
        FOREACH (old IN oldRelations | DELETE old)
        WITH 1 AS ignored
        MATCH (a:AegisMemoryEntity {entityId:$subject}), (b:AegisMemoryEntity {entityId:$object})
        CREATE (a)-[:AEGIS_RELATION {relationId:$id, predicate:$predicate, revision:$revision,
            validFrom:$validFrom, validUntil:$validUntil, validFromTicks:$fromTicks, validUntilTicks:$untilTicks}]->(b)
        """, RelationParameters(r), ct);

    public Task DeleteRelationAsync(Guid id, CancellationToken ct) =>
        WriteAsync("MATCH ()-[r:AEGIS_RELATION {relationId:$id}]-() DELETE r", new { id = id.ToString() }, ct);

    public async Task<IReadOnlyList<MemoryGraphCandidatePath>> TraverseAsync(IReadOnlyList<Guid> startIds,
        MemoryGraphDirection direction, IReadOnlyList<string> predicates, int maxDepth, int limit, DateTimeOffset asOf, CancellationToken ct)
    {
        if (maxDepth is < 1 or > 3 || limit is < 1 or > 200 || startIds.Count is < 1 or > 10 || !Enum.IsDefined(direction))
            throw new ArgumentException("Invalid graph traversal bounds.");
        // Only the bounded direction/depth are interpolated; IDs, predicates and time are parameters.
        var pattern = direction switch
        {
            MemoryGraphDirection.Outgoing => $"-[r:AEGIS_RELATION*1..{maxDepth}]->",
            MemoryGraphDirection.Incoming => $"<-[r:AEGIS_RELATION*1..{maxDepth}]-",
            _ => $"-[r:AEGIS_RELATION*1..{maxDepth}]-"
        };
        var cypher = $"""
            MATCH p=(start:AegisMemoryEntity){pattern}(finish:AegisMemoryEntity)
            WHERE start.entityId IN $startIds
              AND ALL(rel IN relationships(p) WHERE
                (rel.validFromTicks IS NULL OR rel.validFromTicks <= $asOf)
                AND (rel.validUntilTicks IS NULL OR rel.validUntilTicks > $asOf)
                AND (size($predicates) = 0 OR rel.predicate IN $predicates))
              AND ALL(node IN nodes(p) WHERE node:AegisMemoryEntity)
            RETURN [node IN nodes(p) | node.entityId] AS entityIds,
                   [rel IN relationships(p) | rel.relationId] AS relationIds
            LIMIT $limit
            """;
        var records = await ReadAsync(cypher, new { startIds = startIds.Select(x => x.ToString()).ToArray(),
            predicates = predicates.ToArray(), asOf = asOf.UtcTicks, limit }, ct);
        return records.Select(row => new MemoryGraphCandidatePath(row["entityIds"].As<List<string>>().Select(Guid.Parse).ToArray(),
            row["relationIds"].As<List<string>>().Select(Guid.Parse).ToArray())).ToArray();
    }

    public Task DeleteManagedProjectionAsync(CancellationToken ct) =>
        WriteAsync("MATCH (n:AegisMemoryEntity) DETACH DELETE n", null, ct);

    private static object RelationParameters(MemoryGraphRelationProjection r) => new
    {
        id = r.RelationId.ToString(), subject = r.SubjectEntityId.ToString(), @object = r.ObjectEntityId.ToString(),
        predicate = r.Predicate, revision = r.Revision, validFrom = Date(r.ValidFrom), validUntil = Date(r.ValidUntil),
        fromTicks = r.ValidFrom?.UtcTicks, untilTicks = r.ValidUntil?.UtcTicks
    };

    private static string? Date(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset? ParseDate(string? value) => value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    private static T? GetOptional<T>(IReadOnlyDictionary<string, object> values, string key) where T : class =>
        values.TryGetValue(key, out var value) && value is not null ? value.As<T>() : null;

    private async Task WriteAsync(string cypher, object? parameters, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await using var session = driver.AsyncSession(config => config.WithDatabase(options.Neo4jDatabase));
            await session.ExecuteWriteAsync(async tx =>
            {
                var cursor = await tx.RunAsync(cypher, parameters);
                await cursor.ConsumeAsync();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AuthenticationException) { throw new MemoryGraphException("neo4j_auth_failed", false); }
        catch (Exception) { throw new MemoryGraphException("neo4j_query_failed"); }
    }

    private async Task<IReadOnlyList<IRecord>> ReadAsync(string cypher, object? parameters, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await using var session = driver.AsyncSession(config => config.WithDatabase(options.Neo4jDatabase));
            return await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(cypher, parameters);
                return (IReadOnlyList<IRecord>)await cursor.ToListAsync();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AuthenticationException) { throw new MemoryGraphException("neo4j_auth_failed", false); }
        catch (Exception) { throw new MemoryGraphException("neo4j_query_failed"); }
    }
}
