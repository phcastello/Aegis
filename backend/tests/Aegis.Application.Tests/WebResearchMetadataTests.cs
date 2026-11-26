using Aegis.Application.Models;
using System.Text.Json;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class WebResearchMetadataTests
{
    [Fact]
    public void MergesConsultedOpenedAndCitedUrlsWithoutLeakingQueries()
    {
        using var response = JsonDocument.Parse("""
            [
              {"type":"web_search_call","status":"completed","action":{"type":"search","query":"private query","sources":[
                {"type":"url","url":"https://example.org/a"},
                {"type":"url","url":"https://example.org/b"},
                {"type":"url","url":"https://example.org/a"}
              ]}},
              {"type":"web_search_call","status":"completed","action":{"type":"open_page","url":"https://example.org/a"}},
              {"type":"web_search_call","status":"completed","action":{"type":"find_in_page","url":"https://example.org/d","pattern":"private pattern"}},
              {"type":"web_search_call","status":"failed","action":{"type":"open_page","url":"https://example.org/failed"}},
              {"type":"message","phase":"final_answer","content":[
                {"type":"output_text","text":"  Fato confirmado. Outro fato.  ","annotations":[
                  {"type":"url_citation","url":"https://example.org/a","title":"Fonte A","start_index":2,"end_index":17},
                  {"type":"url_citation","url":"https://example.org/c","title":"Fonte C","start_index":19,"end_index":29}
                ]}
              ]}
            ]
            """);
        const string answer = "Fato confirmado. Outro fato.";
        var json = WebResearchMetadata.Merge("""{"provider":"openai","usage":{"output_tokens":5}}""",
            response.RootElement.EnumerateArray(), answer, true);

        Assert.NotNull(json);
        Assert.DoesNotContain("private query", json);
        Assert.DoesNotContain("private pattern", json);
        using var metadata = JsonDocument.Parse(json!);
        Assert.Equal(5, metadata.RootElement.GetProperty("usage").GetProperty("output_tokens").GetInt32());
        var evidence = Assert.IsType<WebResearchEvidence>(WebResearchMetadata.Read(json, answer));
        Assert.Collection(evidence.Sources,
            a => { Assert.Equal("https://example.org/a", a.Url); Assert.Equal("opened", a.Kind); Assert.Equal("Fonte A", a.Title); Assert.True(a.Cited); },
            b => { Assert.Equal("https://example.org/b", b.Url); Assert.Equal("consulted", b.Kind); Assert.False(b.Cited); },
            d => { Assert.Equal("https://example.org/d", d.Url); Assert.Equal("consulted", d.Kind); Assert.False(d.Cited); },
            c => { Assert.Equal("https://example.org/c", c.Url); Assert.Equal("citation", c.Kind); Assert.True(c.Cited); });
        Assert.Collection(evidence.Citations,
            a => { Assert.Equal(0, a.StartIndex); Assert.Equal(15, a.EndIndex); },
            c => { Assert.Equal(17, c.StartIndex); Assert.Equal(27, c.EndIndex); });
    }

    [Fact]
    public void MapsAnnotationsAcrossPartsOfTheExactFinalTextOnly()
    {
        using var response = JsonDocument.Parse("""
            [
              {"type":"message","phase":"commentary","content":[
                {"type":"output_text","text":"Analisando","annotations":[
                  {"type":"url_citation","url":"https://example.org/comment","start_index":0,"end_index":10}
                ]}
              ]},
              {"type":"message","phase":"final_answer","content":[
                {"type":"output_text","text":"Primeiro. ","annotations":[]},
                {"type":"output_text","text":"Segundo.","annotations":[
                  {"type":"url_citation","url":"https://example.org/result","start_index":0,"end_index":7}
                ]}
              ]}
            ]
            """);
        var items = response.RootElement.EnumerateArray().ToArray();
        var matching = WebResearchMetadata.Merge(null, items, "Primeiro. Segundo.", true);
        var mapped = Assert.IsType<WebResearchEvidence>(WebResearchMetadata.Read(matching));
        var cite = Assert.Single(mapped.Citations);
        Assert.Equal(10, cite.StartIndex);
        Assert.Equal(17, cite.EndIndex);
        Assert.Equal("https://example.org/result", cite.Url);

        var transformed = WebResearchMetadata.Merge(null, items, "Primeiro.  Segundo.", true);
        var unmatched = Assert.IsType<WebResearchEvidence>(WebResearchMetadata.Read(transformed));
        Assert.Empty(unmatched.Citations);
        Assert.Equal(2, unmatched.Sources.Count);

        var functionReply = WebResearchMetadata.Merge(null, items, "Primeiro. Segundo.", false);
        Assert.Empty(Assert.IsType<WebResearchEvidence>(WebResearchMetadata.Read(functionReply)).Citations);
    }

    [Fact]
    public void RejectsUnsafeLinksAndInvalidAnnotationRanges()
    {
        using var response = JsonDocument.Parse("""
            [
              {"type":"web_search_call","status":"completed","action":{"type":"search","sources":[
                {"type":"url","url":"javascript:alert(1)"},
                {"type":"url","url":"https://name:secret@example.org/private"},
                {"type":"url","url":"file:///private"},
                {"type":"url","url":"https://safe.example.org"}
              ]}},
              {"type":"message","content":[
                {"type":"output_text","text":"Fato.","annotations":[
                  {"type":"url_citation","url":"https://safe.example.org","start_index":-1,"end_index":9},
                  {"type":"url_citation","url":"https://safe.example.org","start_index":0,"end_index":999},
                  {"type":"url_citation","url":"https://safe.example.org","start_index":1,"end_index":1},
                  {"type":"url_citation","url":"data:text/plain,abc","start_index":0,"end_index":2}
                ]}
              ]}
            ]
            """);
        var json = WebResearchMetadata.Merge(null, response.RootElement.EnumerateArray(), "Fato.", true);
        var evidence = Assert.IsType<WebResearchEvidence>(WebResearchMetadata.Read(json));
        Assert.Equal("https://safe.example.org/", Assert.Single(evidence.Sources).Url);
        Assert.Empty(evidence.Citations);
    }

    [Fact]
    public void AstralCharactersKeepSourceWhenCharacterIndexConventionIsAmbiguous()
    {
        using var response = JsonDocument.Parse("""
            [{"type":"message","content":[
              {"type":"output_text","text":"🔍Fato.","annotations":[
                {"type":"url_citation","url":"https://example.org","start_index":1,"end_index":5}
              ]}
            ]}]
            """);
        var json = WebResearchMetadata.Merge(null, response.RootElement.EnumerateArray(), "🔍Fato.", true);
        var evidence = Assert.IsType<WebResearchEvidence>(WebResearchMetadata.Read(json));
        Assert.True(Assert.Single(evidence.Sources).Cited);
        Assert.Empty(evidence.Citations);
    }

    [Fact]
    public void ReadsVoiceWrapperAndPreservesExistingMetadataWithoutEvidence()
    {
        const string original = """{"provider":"openai"}""";
        Assert.Equal(original, WebResearchMetadata.Merge(original, [], "ok", true));
        Assert.Null(WebResearchMetadata.Read(original));
        Assert.Null(WebResearchMetadata.Read("not json"));
        Assert.Null(WebResearchMetadata.Read("""{"modelMetadata":"not json"}"""));

        const string modelMetadata = """
            {"webResearch":{"sources":[{"url":"https://example.org","title":null,"kind":"consulted"}],"citations":[]}}
            """;
        var wrapper = JsonSerializer.Serialize(new { voiceSegments = new { version = 1, state = "complete" }, modelMetadata });
        var evidence = Assert.IsType<WebResearchEvidence>(WebResearchMetadata.Read(wrapper));
        Assert.Equal("https://example.org/", Assert.Single(evidence.Sources).Url);
        Assert.Empty(evidence.Citations);
    }

    [Fact]
    public void ReadingStoredEvidenceStillValidatesUrlsAndBounds()
    {
        const string metadata = """
            {"webResearch":{"sources":[{"url":"https://example.org","kind":"consulted"},{"url":"javascript:alert(1)","kind":"opened"}],
            "citations":[{"url":"https://example.org","startIndex":2,"endIndex":50},{"url":"file:///local","startIndex":0,"endIndex":2}]}}
            """;
        var evidence = Assert.IsType<WebResearchEvidence>(WebResearchMetadata.Read(metadata, "Fato."));
        Assert.Single(evidence.Sources);
        Assert.Empty(evidence.Citations);
    }
}
