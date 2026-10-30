using System.Net;
using System.Text.Json;
using FluentAssertions;
using PaperlessMCP.Tests.Fixtures;
using PaperlessMCP.Tools;
using RichardSzalay.MockHttp;
using Xunit;

namespace PaperlessMCP.Tests.Tools;

public class AttributeSearchToolsTests : IDisposable
{
    private readonly MockHttpClientFactory _factory = new();

    // ─── Minimal list-JSON builders (only fields the models deserialize) ───
    private static string TagListJson(params (int Id, string Name)[] items) =>
        JsonSerializer.Serialize(new
        {
            count = items.Length,
            next = (string?)null,
            previous = (string?)null,
            results = items.Select(t => new
            {
                id = t.Id, slug = "s", name = t.Name, color = "#000000",
                text_color = (string?)null, match = "", matching_algorithm = 0,
                is_inbox_tag = false, document_count = 0, owner = (int?)null
            })
        });

    private static string CorrespondentListJson(params (int Id, string Name, string Match)[] items) =>
        JsonSerializer.Serialize(new
        {
            count = items.Length,
            next = (string?)null,
            previous = (string?)null,
            results = items.Select(c => new
            {
                id = c.Id, slug = "s", name = c.Name, match = c.Match,
                matching_algorithm = 0, document_count = 0,
                last_correspondence = (string?)null, owner = (int?)null
            })
        });

    private static string DocumentTypeListJson(params (int Id, string Name)[] items) =>
        JsonSerializer.Serialize(new
        {
            count = items.Length,
            next = (string?)null,
            previous = (string?)null,
            results = items.Select(d => new
            {
                id = d.Id, slug = "s", name = d.Name, match = "",
                matching_algorithm = 0
            })
        });

    private static string StoragePathListJson(params (int Id, string Name, string Path)[] items) =>
        JsonSerializer.Serialize(new
        {
            count = items.Length,
            next = (string?)null,
            previous = (string?)null,
            results = items.Select(s => new
            {
                id = s.Id, slug = "s", name = s.Name, path = s.Path,
                match = "", matching_algorithm = 0
            })
        });

    private static string CustomFieldListJson(params (int Id, string Name, string DataType)[] items) =>
        JsonSerializer.Serialize(new
        {
            count = items.Length,
            next = (string?)null,
            previous = (string?)null,
            results = items.Select(f => new { id = f.Id, name = f.Name, data_type = f.DataType })
        });


    private void SetupAllEntities()
    {
        _factory.SetupGet("api/tags/", TagListJson((1, "insurance"), (2, "Tomorrow")));
        _factory.SetupGet("api/correspondents/",
            CorrespondentListJson((499, "Tomorrow-2026-05-Main-en", ""), (500, "Vodafone Kabel", "")));
        _factory.SetupGet("api/document_types/", DocumentTypeListJson());
        _factory.SetupGet("api/storage_paths/", StoragePathListJson());
        _factory.SetupGet("api/custom_fields/", CustomFieldListJson());
    }

    [Fact]
    public async Task Wildcard_SearchesAllEntityTypesByDefault()
    {
        SetupAllEntities();

        var result = await AttributeSearchTools.Search(
            _factory.Client, "*vodafone*");

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("ok").GetBoolean().Should().BeTrue();
        var resultEl = json.GetProperty("result");
        resultEl.GetProperty("matches").GetArrayLength().Should().Be(1);

        var match = resultEl.GetProperty("matches")[0];
        match.GetProperty("id").GetInt32().Should().Be(500);
        match.GetProperty("type").GetString().Should().Be("correspondents");
        match.GetProperty("name").GetString().Should().Be("Vodafone Kabel");

        resultEl.GetProperty("counts").GetProperty("correspondents").GetInt32().Should().Be(1);
        resultEl.GetProperty("counts").GetProperty("tags").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Wildcard_MatchesCaseInsensitivelyByDefault()
    {
        _factory.SetupGet("api/correspondents/",
            CorrespondentListJson((17, "Vodafone Kabel", "")));

        var result = await AttributeSearchTools.Search(
            _factory.Client, "vodafone*", entities: "correspondents");

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("result").GetProperty("matches").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Wildcard_QuestionMarkMatchesExactlyOneCharacter()
    {
        _factory.SetupGet("api/correspondents/",
            CorrespondentListJson((1, "Acmes", ""), (2, "Acme", ""), (3, "Acmexyz", "")));

        var result = await AttributeSearchTools.Search(
            _factory.Client, "acme?", entities: "correspondents");

        var json = JsonDocument.Parse(result).RootElement;
        var matches = json.GetProperty("result").GetProperty("matches");
        matches.GetArrayLength().Should().Be(1);
        matches[0].GetProperty("id").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Wildcard_IsAnchoredSoPartialPrefixesDoNotMatch()
    {
        _factory.SetupGet("api/correspondents/",
            CorrespondentListJson((1, "Bank Guarantee", ""), (2, "Bank", "")));

        var result = await AttributeSearchTools.Search(
            _factory.Client, "bank", entities: "correspondents");
        if (true) { Console.Error.WriteLine("BANKRESULT=" + result); }

        var json = JsonDocument.Parse(result).RootElement;
        var matches = json.GetProperty("result").GetProperty("matches");
        matches.GetArrayLength().Should().Be(1);
        matches[0].GetProperty("id").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task MatchCaseTrue_RequiresExactCase()
    {
        _factory.SetupGet("api/correspondents/",
            CorrespondentListJson((1, "Vodafone Kabel", "")));

        var result = await AttributeSearchTools.Search(
            _factory.Client, "vodafone*", entities: "correspondents", matchCase: true);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("result").GetProperty("matches").GetArrayLength().Should().Be(0);

        var caseMatch = await AttributeSearchTools.Search(
            _factory.Client, "Vodafone*", entities: "correspondents", matchCase: true);
        JsonDocument.Parse(caseMatch).RootElement.GetProperty("result")
            .GetProperty("matches").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task RegexMode_MatchesRawRegularExpression()
    {
        _factory.SetupGet("api/correspondents/",
            CorrespondentListJson((499, "Tomorrow-2026-05-Main-en", "")));

        var result = await AttributeSearchTools.Search(
            _factory.Client, @"^Tomorrow-2026-\d{2}", entities: "correspondents", regex: true);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("result").GetProperty("mode").GetString().Should().Be("regex");
        json.GetProperty("result").GetProperty("matches").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task InvalidPattern_ReturnsValidationError()
    {
        _factory.SetupGet("api/correspondents/", CorrespondentListJson());

        var result = await AttributeSearchTools.Search(
            _factory.Client, "([invalid", regex: true);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetProperty("code").GetString().Should().Be("VALIDATION");
        json.GetProperty("error").GetProperty("message").GetString()
            .Should().Contain("Invalid regex pattern");
    }

    [Fact]
    public async Task InvalidEntityNames_ReturnsValidationErrorListingValidTypes()
    {
        _factory.SetupGet("api/correspondents/", CorrespondentListJson());

        var result = await AttributeSearchTools.Search(
            _factory.Client, "acme", entities: "widgets,gizmos");

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetProperty("code").GetString().Should().Be("VALIDATION");
        json.GetProperty("error").GetProperty("message").GetString()
            .Should().Contain("tags").And.Contain("correspondents");
    }

    [Fact]
    public async Task NoMatches_ReturnsEmptyMatchesAndZeroCounts()
    {
        _factory.SetupGet("api/tags/", TagListJson((1, "insurance")));
        _factory.SetupGet("api/correspondents/", CorrespondentListJson());
        _factory.SetupGet("api/document_types/", DocumentTypeListJson());
        _factory.SetupGet("api/storage_paths/", StoragePathListJson());
        _factory.SetupGet("api/custom_fields/", CustomFieldListJson());

        var result = await AttributeSearchTools.Search(_factory.Client, "*nothing*");

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.GetProperty("result").GetProperty("matches").GetArrayLength().Should().Be(0);
        foreach (var count in json.GetProperty("result").GetProperty("counts").EnumerateObject())
        {
            count.Value.GetInt32().Should().Be(0);
        }
    }

    [Fact]
    public async Task LiteralPrefilter_IsAppliedToTheApiQuery()
    {
        // 'foda' is the literal chunk between the wildcards; the request must carry
        // it as name__icontains so Paperless narrows server-side instead of
        // streaming every correspondent through the wire.
        var request = _factory.MockHandler
            .Expect("https://paperless.example.com/api/correspondents/")
            .WithQueryString("name__icontains", "voda")
            .Respond("application/json",
                CorrespondentListJson((500, "Vodafone Kabel", "")));
        _ = request;

        var result = await AttributeSearchTools.Search(
            _factory.Client, "*voda*", entities: "correspondents");

        _factory.MockHandler.VerifyNoOutstandingExpectation();
        JsonDocument.Parse(result).RootElement.GetProperty("result")
            .GetProperty("matches").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ShortPatternsAndRegexMode_DoNotAddPrefilter()
    {
        // MockHttp matches URLs ignoring the query string unless WithQueryString is
        // attached, so an Expect with a predicate rejecting name__icontains will
        // fail during verification if the tool ever sends the prefilter.
        _factory.MockHandler
            .Expect("https://paperless.example.com/api/correspondents/")
            .With(msg => !(msg.RequestUri?.Query.Contains("name__icontains") ?? false))
            .Respond("application/json", CorrespondentListJson((1, "ac", ""), (2, "acme", "")));

        // Expectations are consumed after one match; the regex search re-issues the
        // same request shape, so it needs its own identical registration.
        var shortResult = await AttributeSearchTools.Search(
            _factory.Client, "ac", entities: "correspondents");
        JsonDocument.Parse(shortResult).RootElement.GetProperty("result")
            .GetProperty("matches").GetArrayLength().Should().Be(1,
                "'ac' is anchored, so only the entry literally named 'ac' matches");

        _factory.MockHandler
            .Expect("https://paperless.example.com/api/correspondents/")
            .With(msg => !(msg.RequestUri?.Query.Contains("name__icontains") ?? false))
            .Respond("application/json", CorrespondentListJson((1, "ac", ""), (2, "acme", "")));

        var regexResult = await AttributeSearchTools.Search(
            _factory.Client, "ac.*", entities: "correspondents", regex: true);
        JsonDocument.Parse(regexResult).RootElement.GetProperty("result")
            .GetProperty("matches").GetArrayLength().Should().Be(2);

        _factory.MockHandler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task IncludeMatchPatterns_MatchesAutoAssignmentField()
    {
        // Paperless tags serialize match strings for auto-assignment.
        var items = new[]
        {
            new { id = 9, slug = "s", name = "Completely Unrelated",
                color = "#000000", text_color = (string?)null,
                match = "Tomorrow Utility Bill", matching_algorithm = 1,
                is_inbox_tag = false, document_count = 0, owner = (int?)null }
        };
        _factory.MockHandler
            .When(HttpMethod.Get, "https://paperless.example.com/api/tags/")
            .Respond("application/json", JsonSerializer.Serialize(new
            {
                count = 1, next = (string?)null, previous = (string?)null, results = items
            }));

        var noInclusion = await AttributeSearchTools.Search(
            _factory.Client, "tomorrow*", entities: "tags");
        JsonDocument.Parse(noInclusion).RootElement.GetProperty("result")
            .GetProperty("matches").GetArrayLength().Should().Be(0);

        var withInclusion = await AttributeSearchTools.Search(
            _factory.Client, "tomorrow*", entities: "tags", includeMatchPatterns: true);
        var matches = JsonDocument.Parse(withInclusion).RootElement
            .GetProperty("result").GetProperty("matches");
        matches.GetArrayLength().Should().Be(1);
        matches[0].GetProperty("id").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task EntityFilter_RestrictsWhichEntitiesAreQueried()
    {
        // Expect: correspondents MUST be requested (only empty list is served);
        // tags are served via a When matcher. Everything not named in entities
        // should not be queried at all — with no expectations registered for the
        // other types, VerifyNoOutstandingExpectation proves only that the two
        // listed types ran; the counts object proves no phantom types appear in
        // the response.
        _factory.SetupGet("api/tags/", TagListJson((2, "Tomorrow")));
        _factory.MockHandler
            .Expect("https://paperless.example.com/api/correspondents/")
            .Respond("application/json", CorrespondentListJson());

        var result = await AttributeSearchTools.Search(
            _factory.Client, "tomorrow", entities: "tags,correspondents");

        _factory.MockHandler.VerifyNoOutstandingExpectation();
        var json = JsonDocument.Parse(result).RootElement;
        var resultEl = json.GetProperty("result");
        resultEl.GetProperty("entities").GetArrayLength().Should().Be(2);
        resultEl.GetProperty("counts").EnumerateObject().Should().HaveCount(2,
            "only the requested entity types may appear in the response");
        resultEl.GetProperty("counts").GetProperty("tags").GetInt32().Should().Be(1);
        resultEl.GetProperty("counts").GetProperty("correspondents").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task PageCapExhausted_AddsIncompleteWarning()
    {
        // Shrink the page budget so both pages are full pages: the loop ends
        // with data still flowing, which must surface as a completeness warning
        // instead of a silent stop.
        var original = AttributeSearchTools.MaxPagesPerEntity;
        AttributeSearchTools.MaxPagesPerEntity = 2;
        // Fixture default is 100; with a 2-item page size below, both mocked
        // pages count as "full" pages, so the loop ends on the cap, not a data end.
        _factory.Options.MaxPageSize = 2;
        try
        {
            _factory.MockHandler
                .Expect("https://paperless.example.com/api/tags/")
                .WithQueryString("page", "1")
                .Respond("application/json", TagListJson((1, "Tomorrow Gas"), (2, "Tomcat")));
            _factory.MockHandler
                .Expect("https://paperless.example.com/api/tags/")
                .WithQueryString("page", "2")
                .Respond("application/json", TagListJson((3, "Tomorrow Power"), (4, "Tomato")));

            var result = await AttributeSearchTools.Search(
                _factory.Client, "tom*", entities: "tags");

            _factory.MockHandler.VerifyNoOutstandingExpectation();
            var json = JsonDocument.Parse(result).RootElement;
            json.GetProperty("ok").GetBoolean().Should().BeTrue();
            json.GetProperty("warnings").GetArrayLength().Should().BeGreaterThanOrEqualTo(1,
                "the loop ended because the page cap, not a data end, was reached");
            json.GetProperty("warnings")[0].GetString().Should()
                .Contain("may be incomplete").And.Contain("tags");
        }
        finally
        {
            AttributeSearchTools.MaxPagesPerEntity = original;
        }
    }

    [Fact]
    public async Task MultiPagePagination_GathersMatchesAcrossPages()
    {
        // The client never requests page 2 unless page 1 filled its page size, so
        // page 1 must return exactly 100 items (the fixture's MAX_PAGE_SIZE) with
        // 'Tomorrow Gas' hidden among them; the match on page 2 proves the loop
        // kept scanning past the full page.
        var many = Enumerable.Range(0, 100)
            .Select(i => (Id: 100 + i, Name: i == 42 ? "Tomorrow Gas" : $"Filler {i}"))
            .ToArray();
        _factory.MockHandler
            .Expect("https://paperless.example.com/api/tags/")
            .WithQueryString("page", "1")
            .Respond("application/json", JsonSerializer.Serialize(new
            {
                count = 101, next = "https://paperless.example.com/api/tags/?page=2",
                previous = (string?)null,
                results = many.Select(t => new
                {
                    id = t.Id, slug = "s", name = t.Name, color = "#000000",
                    text_color = (string?)null, match = "", matching_algorithm = 0,
                    is_inbox_tag = false, document_count = 0, owner = (int?)null
                })
            }));
        _factory.MockHandler
            .Expect("https://paperless.example.com/api/tags/")
            .WithQueryString("page", "2")
            .Respond("application/json", TagListJson((3, "Tomorrow Power")));

        var result = await AttributeSearchTools.Search(
            _factory.Client, "tomorrow*", entities: "tags");

        _factory.MockHandler.VerifyNoOutstandingExpectation();
        var json = JsonDocument.Parse(result).RootElement;
        var matches = json.GetProperty("result").GetProperty("matches");
        matches.GetArrayLength().Should().Be(2);
        matches.EnumerateArray().Select(m => m.GetProperty("id").GetInt32()).Should().Equal(142, 3);
    }

    public void Dispose() => _factory.Dispose();
}
