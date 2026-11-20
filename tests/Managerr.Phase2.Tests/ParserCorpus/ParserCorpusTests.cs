using Xunit;

namespace Managerr.Phase2.Tests.ParserCorpus;

/// <summary>
/// Runs every release name in ParserCorpus/*.jsonl through the parser. Upstream cases Managerr does not parse yet
/// are pinned in known-gaps.json: they must keep failing the same way until fixed, then leave the baseline.
/// After a parser change, refresh the baseline with
/// <c>MANAGERR_UPDATE_PARSER_GAPS=1 dotnet test tests/Managerr.Phase2.Tests --filter FullyQualifiedName~ParserCorpus</c>
/// and review the diff: entries may disappear, new ones mean a regression.
/// </summary>
public sealed class ParserCorpusTests
{
    public static TheoryData<string> CaseIds() => new(ParserCorpus.CasesWithExpectations.Select(c => c.Id));

    public static TheoryData<string, string> VariantKeys()
    {
        var data = new TheoryData<string, string>();
        foreach (var c in ParserCorpus.CasesWithExpectations)
        {
            foreach (var v in ParserCorpus.Variants.Where(v => v.Rewrite(c.Release) is { } r && r != c.Release))
            {
                data.Add(c.Id, v.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Release_ParsesAsExpected(string id)
    {
        var c = ParserCorpus.ById[id];
        if (ParserCorpus.Verdict(ParserCorpus.Evaluate(c)) is { } failure)
        {
            Assert.Fail($"{failure}\n  [{c.File}, from {c.Origin}]");
        }
    }

    [Theory]
    [MemberData(nameof(VariantKeys))]
    public void RewrittenRelease_ParsesLikeTheOriginal(string id, string variant)
    {
        var c = ParserCorpus.ById[id];
        var evaluation = ParserCorpus.Evaluate(c, ParserCorpus.Variants.Single(v => v.Name == variant))!;
        if (ParserCorpus.Verdict(evaluation) is { } failure)
        {
            Assert.Fail($"{failure}\n  original: '{c.Release}' [{c.File}]");
        }
    }

    [Fact]
    public void Corpus_IdsAreUnique_AndFieldsAreKnown()
    {
        var duplicates = ParserCorpus.Cases.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.True(duplicates.Length == 0, $"Duplicate corpus ids: {string.Join(", ", duplicates)}");

        var unknown = ParserCorpus.Cases
            .SelectMany(c => c.Expect.Select(kv => (c.File, Field: kv.Key)))
            .Where(x => !ParserCorpus.Fields.Contains(x.Field))
            .ToArray();
        Assert.True(unknown.Length == 0, $"Unknown expect fields: {string.Join(", ", unknown.Select(u => $"{u.File} '{u.Field}'"))}");

        Assert.Contains(ParserCorpus.Cases, c => c.Id.StartsWith("managerr/", StringComparison.Ordinal));
        Assert.Contains(ParserCorpus.Cases, c => c.Id.StartsWith("sonarr/", StringComparison.Ordinal));
    }

    [Fact]
    public void KnownGaps_OnlyNameUpstreamCasesThatExist()
    {
        var variantNames = ParserCorpus.Variants.Select(v => v.Name).ToHashSet();
        foreach (var key in ParserCorpus.KnownGaps.Keys)
        {
            var hash = key.IndexOf('#');
            var id = hash < 0 ? key : key[..hash];
            Assert.True(ParserCorpus.ById.ContainsKey(id), $"{ParserCorpus.GapsFileName} names '{key}', which is not in the corpus any more");
            Assert.True(hash < 0 || variantNames.Contains(key[(hash + 1)..]), $"{ParserCorpus.GapsFileName} names unknown variant '{key}'");

            // Managerr's own cases are regressions that were fixed: they always pass
            Assert.True(hash >= 0 || ParserCorpus.IsUpstream(id), $"'{key}' is Managerr's own case and may not be a known gap; fix the parser or the case");
        }
    }

    /// <summary>Regenerates known-gaps.json when MANAGERR_UPDATE_PARSER_GAPS=1; a no-op otherwise.</summary>
    [Fact]
    public void UpdateKnownGaps()
    {
        if (Environment.GetEnvironmentVariable(ParserCorpus.UpdateGapsVariable) != "1")
        {
            return;
        }

        var count = ParserCorpus.WriteKnownGaps(ParserCorpus.SourceDirectory());
        Assert.Fail($"Wrote {count} known gaps to {Path.Combine(ParserCorpus.SourceDirectory(), ParserCorpus.GapsFileName)}. Review the diff, then rerun without {ParserCorpus.UpdateGapsVariable}.");
    }
}
