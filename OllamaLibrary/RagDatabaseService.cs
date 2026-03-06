using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using OllamaClient;

namespace OllamaLibrary
{
    public class RagDatabaseService : IDisposable
    {
        private readonly string _connectionString;
        private readonly OllamaEmbedding _ollamaEmbedding;

        public RagDatabaseService(string connectionString, OllamaEmbedding ollamaEmbedding)
        {
            _connectionString = connectionString;
            _ollamaEmbedding = ollamaEmbedding;
        }

        public async Task<List<RagChunk>> SearchSimilarChunksAsync(string query, int topK = 5)
        {
            Console.WriteLine("Requesting embedding of the prompt...");
            var embeddingArray = await _ollamaEmbedding.EmbedAsync(query);

            var embeddingLiteral = "[" + string.Join(", ",
                embeddingArray.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

            var results = new List<RagChunk>();

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            Console.WriteLine("Sending sql request...");

            const string sql = """
                               SELECT rc.id,
                                      rc.content,
                                      rc.metadata,
                                      rc.source_type_id,
                                      rst.code AS source_type_code,
                                      rc.source_id,
                                      rc.embedding <-> CAST(@embedding AS vector) AS distance
                               FROM rag_chunks rc
                               JOIN rag_source_types rst ON rst.id = rc.source_type_id
                               ORDER BY rc.embedding <-> CAST(@embedding AS vector)
                               LIMIT @top_k;
                               """;

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("embedding", embeddingLiteral);
            cmd.Parameters.AddWithValue("top_k", topK);

            Console.WriteLine("Reading response...");

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var chunk = new RagChunk
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    Content = reader.GetString(reader.GetOrdinal("content")),
                    Metadata = new Dictionary<string, object?>()
                };

                // minimal routing metadata
                chunk.Metadata["source_type"] = reader.GetString(reader.GetOrdinal("source_type_code"));
                chunk.Metadata["source_id"] = reader.GetString(reader.GetOrdinal("source_id"));

                // optional JSONB metadata
                var metaOrdinal = reader.GetOrdinal("metadata");
                if (!reader.IsDBNull(metaOrdinal))
                {
                    var json = reader.GetString(metaOrdinal);
                    var node = JsonNode.Parse(json) as JsonObject;
                    if (node != null)
                    {
                        foreach (var kv in node)
                        {
                            chunk.Metadata[kv.Key] = kv.Value;
                        }
                    }
                }

                results.Add(chunk);
            }

            Console.WriteLine("Context received and processed");
            return results;
        }

        /// <summary>
        /// Takes N matched chunks, and for the top 3 retrieves full family context
        /// (spouses, children, etc.) and appends that as extra chunks.
        /// </summary>
        public async Task<List<RagChunk>> ExpandContextAsync(List<RagChunk> chunks, int maxPersons = 3)
        {
            var expanded = new List<RagChunk>();

            // Always keep all original matches (e.g. the 5 closest)
            expanded.AddRange(chunks);

            // Enrich only top N (default 3) with full family data
            foreach (var chunk in chunks.Take(maxPersons))
            {
                if (!chunk.Metadata.TryGetValue("source_type", out var typeObj) ||
                    !chunk.Metadata.TryGetValue("source_id", out var idObj))
                {
                    continue;
                }

                var sourceType = typeObj?.ToString();
                var sourceId = idObj?.ToString();

                if (string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(sourceId))
                    continue;

                var cleanId = sourceId.Trim();

                string extraText;
                if (sourceType == "person")
                {
                    // person + their families + children
                    extraText = await GetPersonWithAllRelationsAsync(cleanId);
                }
                else if (sourceType == "family")
                {
                    // family (husband, wife, children, notes)
                    extraText = await GetFamilyInfoAsync(cleanId);
                }
                else
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(extraText))
                    continue;

                // Attach the “family context” as a separate chunk so it participates in RAG
                expanded.Add(new RagChunk
                {
                    Id = chunk.Id, // can reuse or set 0 / -1 if you prefer
                    Content = extraText,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["source_type"] = sourceType + "_details",
                        ["source_id"] = sourceId
                    }
                });
            }

            return expanded;
        }


        public async Task<string> GetPersonInfoAsync(string personId)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // 1. Basic person data
            const string personSql = """

                                                 SELECT id,
                                                        display_name,
                                                        birth_date_raw,
                                                        birth_place,
                                                        death_date_raw,
                                                        death_place,
                                                        notes
                                                 FROM persons
                                                 WHERE id = @id;
                                             
                                     """;

            string? displayName = null,
                birthDate = null,
                birthPlace = null,
                deathDate = null,
                deathPlace = null,
                notes = null;

            await using (var cmd = new NpgsqlCommand(personSql, conn))
            {
                cmd.Parameters.AddWithValue("id", personId);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    displayName = reader["display_name"] as string;
                    birthDate = reader["birth_date_raw"] as string;
                    birthPlace = reader["birth_place"] as string;
                    deathDate = reader["death_date_raw"] as string;
                    deathPlace = reader["death_place"] as string;
                    notes = reader["notes"] as string;
                }
                else
                {
                    return "Brak danych o tej osobie w bazie.";
                }
            }

            // 2. Families where this person is spouse
            const string familiesSql = """

                                                   SELECT f.id,
                                                          p_h.display_name AS husband,
                                                          p_w.display_name AS wife
                                                   FROM families f
                                                   LEFT JOIN persons p_h ON p_h.id = f.husband_id
                                                   LEFT JOIN persons p_w ON p_w.id = f.wife_id
                                                   WHERE f.husband_id = @id OR f.wife_id = @id;
                                               
                                       """;

            var families = new List<(string familyId, string? husband, string? wife)>();

            await using (var cmd = new NpgsqlCommand(familiesSql, conn))
            {
                cmd.Parameters.AddWithValue("id", personId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var famId = reader["id"] as string ?? "";
                    var husband = reader["husband"] as string;
                    var wife = reader["wife"] as string;
                    families.Add((famId, husband, wife));
                }
            }

            // 3. Children across those families
            var children = new List<(string familyId, string childId, string? childName)>();

            if (families.Count > 0)
            {
                const string childrenSql = @"
                SELECT fc.family_id,
                       fc.child_id,
                       p.display_name AS child_name
                FROM family_children fc
                JOIN persons p ON p.id = fc.child_id
                WHERE fc.family_id = ANY(@family_ids);
            ";

                var familyIds = families.Select(f => f.familyId).ToArray();

                await using var cmd = new NpgsqlCommand(childrenSql, conn);
                cmd.Parameters.AddWithValue("family_ids", familyIds);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var famId = reader["family_id"] as string ?? "";
                    var childId = reader["child_id"] as string ?? "";
                    var childName = reader["child_name"] as string;
                    children.Add((famId, childId, childName));
                }
            }

            // 4. Build readable summary
            var sb = new StringBuilder();
            sb.AppendLine($"Osoba: {displayName ?? personId}");

            if (!string.IsNullOrWhiteSpace(birthDate) || !string.IsNullOrWhiteSpace(birthPlace))
                sb.AppendLine($"Urodzenie: {birthDate} {birthPlace}".Trim());

            if (!string.IsNullOrWhiteSpace(deathDate) || !string.IsNullOrWhiteSpace(deathPlace))
                sb.AppendLine($"Śmierć: {deathDate} {deathPlace}".Trim());

            if (!string.IsNullOrWhiteSpace(notes))
                sb.AppendLine($"Notatki: {notes}");

            if (families.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Rodziny:");

                foreach (var fam in families)
                {
                    sb.AppendLine($"- Rodzina {fam.familyId}: {fam.husband} & {fam.wife}");

                    var famChildren = children.Where(c => c.familyId == fam.familyId).ToList();
                    if (famChildren.Count > 0)
                    {
                        sb.Append("  Dzieci: ");
                        sb.AppendLine(string.Join(", ",
                            famChildren.Select(c => c.childName ?? c.childId)));
                    }
                }
            }

            return sb.ToString();
        }

        public async Task<string> GetFamilyInfoAsync(string familyId)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // 1. Family data + spouses
            const string familySql = """

                                                 SELECT f.id,
                                                        p_h.display_name AS husband,
                                                        p_w.display_name AS wife,
                                                        f.marriage_date_raw,
                                                        f.marriage_place,
                                                        f.notes
                                                 FROM families f
                                                 LEFT JOIN persons p_h ON p_h.id = f.husband_id
                                                 LEFT JOIN persons p_w ON p_w.id = f.wife_id
                                                 WHERE f.id = @id;
                                             
                                     """;

            string? husband = null, wife = null, mDate = null, mPlace = null, notes = null;

            await using (var cmd = new NpgsqlCommand(familySql, conn))
            {
                cmd.Parameters.AddWithValue("id", familyId);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    husband = reader["husband"] as string;
                    wife = reader["wife"] as string;
                    mDate = reader["marriage_date_raw"] as string;
                    mPlace = reader["marriage_place"] as string;
                    notes = reader["notes"] as string;
                }
                else
                {
                    return "Brak danych o tej rodzinie w bazie.";
                }
            }

            // 2. Children
            const string childrenSql = """

                                                   SELECT fc.child_id,
                                                          p.display_name AS child_name
                                                   FROM family_children fc
                                                   JOIN persons p ON p.id = fc.child_id
                                                   WHERE fc.family_id = @id;
                                               
                                       """;

            var children = new List<string>();

            await using (var cmd = new NpgsqlCommand(childrenSql, conn))
            {
                cmd.Parameters.AddWithValue("id", familyId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var childName = reader["child_name"] as string;
                    var childId = reader["child_id"] as string ?? "";
                    children.Add(childName ?? childId);
                }
            }

            // 3. Build human‑readable summary
            var sb = new StringBuilder();
            sb.AppendLine($"Rodzina: {husband} & {wife}");

            if (!string.IsNullOrWhiteSpace(mDate) || !string.IsNullOrWhiteSpace(mPlace))
                sb.AppendLine($"Ślub: {mDate} {mPlace}".Trim());

            if (children.Count > 0)
                sb.AppendLine($"Dzieci: {string.Join(", ", children)}");

            if (!string.IsNullOrWhiteSpace(notes))
                sb.AppendLine($"Notatki: {notes}");

            return sb.ToString();
        }

        public async Task<string> GetPersonWithAllRelationsAsync(string personId)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var sb = new StringBuilder();

            // 1. Core person data
            const string personSql = """

                                             SELECT id,
                                                    display_name,
                                                    birth_date_raw,
                                                    birth_place,
                                                    death_date_raw,
                                                    death_place,
                                                    notes
                                             FROM persons
                                             WHERE id = @id;
                                         
                                     """;

            string? displayName = null,
                birthDate = null,
                birthPlace = null,
                deathDate = null,
                deathPlace = null,
                notes = null;

            await using (var cmd = new NpgsqlCommand(personSql, conn))
            {
                cmd.Parameters.AddWithValue("id", personId);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    displayName = reader["display_name"] as string;
                    birthDate = reader["birth_date_raw"] as string;
                    birthPlace = reader["birth_place"] as string;
                    deathDate = reader["death_date_raw"] as string;
                    deathPlace = reader["death_place"] as string;
                    notes = reader["notes"] as string;
                }
                else
                {
                    return $"Brak danych o osobie {personId}.";
                }
            }

            sb.AppendLine($"Osoba: {displayName ?? personId}");
            if (!string.IsNullOrWhiteSpace(birthDate) || !string.IsNullOrWhiteSpace(birthPlace))
                sb.AppendLine($"Urodzenie: {birthDate} {birthPlace}".Trim());
            if (!string.IsNullOrWhiteSpace(deathDate) || !string.IsNullOrWhiteSpace(deathPlace))
                sb.AppendLine($"Śmierć: {deathDate} {deathPlace}".Trim());
            if (!string.IsNullOrWhiteSpace(notes))
                sb.AppendLine($"Notatki: {notes}");

            // 2. Families where the person is spouse
            const string familiesSql = """

                                               SELECT f.id,
                                                      p_h.id           AS husband_id,
                                                      p_h.display_name AS husband_name,
                                                      p_w.id           AS wife_id,
                                                      p_w.display_name AS wife_name,
                                                      f.marriage_date_raw,
                                                      f.marriage_place,
                                                      f.notes
                                               FROM families f
                                               LEFT JOIN persons p_h ON p_h.id = f.husband_id
                                               LEFT JOIN persons p_w ON p_w.id = f.wife_id
                                               WHERE f.husband_id = @id OR f.wife_id = @id;
                                           
                                       """;

            var families = new List<(string familyId,
                string? husbandId, string? husbandName,
                string? wifeId, string? wifeName,
                string? mDate, string? mPlace, string? fNotes)>();

            await using (var cmd = new NpgsqlCommand(familiesSql, conn))
            {
                cmd.Parameters.AddWithValue("id", personId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    families.Add((
                        familyId: reader["id"] as string ?? "",
                        husbandId: reader["husband_id"] as string,
                        husbandName: reader["husband_name"] as string,
                        wifeId: reader["wife_id"] as string,
                        wifeName: reader["wife_name"] as string,
                        mDate: reader["marriage_date_raw"] as string,
                        mPlace: reader["marriage_place"] as string,
                        fNotes: reader["notes"] as string
                    ));
                }
            }

            if (families.Count == 0)
                return sb.ToString();

            // 3. Children for all those families
            const string childrenSql = """

                                               SELECT fc.family_id,
                                                      fc.child_id,
                                                      p.display_name AS child_name,
                                                      p.birth_date_raw AS child_birth_date
                                               FROM family_children fc
                                               JOIN persons p ON p.id = fc.child_id
                                               WHERE fc.family_id = ANY(@family_ids);
                                           
                                       """;

            var familyIds = families.Select(f => f.familyId).ToArray();
            var childrenByFamily = new Dictionary<string, List<(string childId, string? name, string? bDate)>>();

            await using (var cmd = new NpgsqlCommand(childrenSql, conn))
            {
                cmd.Parameters.AddWithValue("family_ids", familyIds);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var famId = reader["family_id"] as string ?? "";
                    var childId = reader["child_id"] as string ?? "";
                    var name = reader["child_name"] as string;
                    var bDateCh = reader["child_birth_date"] as string;

                    if (!childrenByFamily.TryGetValue(famId, out var list))
                    {
                        list = new List<(string, string?, string?)>();
                        childrenByFamily[famId] = list;
                    }

                    list.Add((childId, name, bDateCh));
                }
            }

            sb.AppendLine();
            sb.AppendLine("Rodziny i powiązane osoby:");

            foreach (var fam in families)
            {
                sb.AppendLine($"Rodzina {fam.familyId}: {fam.husbandName} & {fam.wifeName}");

                if (!string.IsNullOrWhiteSpace(fam.mDate) || !string.IsNullOrWhiteSpace(fam.mPlace))
                    sb.AppendLine($"  Ślub: {fam.mDate} {fam.mPlace}".Trim());

                if (!string.IsNullOrWhiteSpace(fam.fNotes))
                    sb.AppendLine($"  Notatki rodziny: {fam.fNotes}");

                if (childrenByFamily.TryGetValue(fam.familyId, out var kids) && kids.Count > 0)
                {
                    sb.AppendLine("  Dzieci:");
                    foreach (var (childId, childName, childBirth) in kids)
                    {
                        var label = childName ?? childId;
                        if (!string.IsNullOrWhiteSpace(childBirth))
                            sb.AppendLine($"    - {label} (ur. {childBirth})");
                        else
                            sb.AppendLine($"    - {label}");
                    }
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }


        public void Dispose()
        {
            // No shared connection yet; nothing to dispose.
        }
    }
}