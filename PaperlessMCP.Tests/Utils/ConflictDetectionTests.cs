using System.Net;
using System.Text.Json;
using FluentAssertions;
using PaperlessMCP.Client;
using PaperlessMCP.Tests.Fixtures;
using PaperlessMCP.Utils;
using Xunit;

namespace PaperlessMCP.Tests.Utils;

public class ConflictDetectionTests
{
    private static ApiError Error(HttpStatusCode status, string body) =>
        new(status, status.ToString(), body);

    [Fact]
    public void IsUniqueConstraintError_DetectsDrfUniqueValidatorBody()
    {
        var error = Error(HttpStatusCode.BadRequest,
            """{"name": ["correspondent with this name already exists."]}""");

        ConflictDetection.IsUniqueConstraintError(error, out var field, out var message)
            .Should().BeTrue();
        field.Should().Be("name");
        message.Should().Contain("already exists");
    }

    [Fact]
    public void IsUniqueConstraintError_DetectsNestedFieldErrors()
    {
        var error = Error(HttpStatusCode.BadRequest,
            """{"errors": {"name": ["tag with this name already exists."]}}""");

        ConflictDetection.IsUniqueConstraintError(error, out _, out _).Should().BeTrue();
    }

    [Fact]
    public void IsUniqueConstraintError_Detects409()
    {
        var error = Error(HttpStatusCode.Conflict,
            """{"name": ["correspondent with this name already exists."]}""");

        ConflictDetection.IsUniqueConstraintError(error, out _, out _).Should().BeTrue();
    }

    [Fact]
    public void IsUniqueConstraintError_IgnoresOtherValidationErrors()
    {
        var error = Error(HttpStatusCode.BadRequest,
            """{"name": ["This field is required."]}""");

        ConflictDetection.IsUniqueConstraintError(error, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void IsUniqueConstraintError_IgnoresServerErrors()
    {
        var error = Error(HttpStatusCode.InternalServerError,
            "correspondent with this name already exists.");

        ConflictDetection.IsUniqueConstraintError(error, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void IsUniqueConstraintError_HandlesNonJsonBodyWithFallbackHeuristic()
    {
        var error = Error(HttpStatusCode.BadRequest,
            "<html>correspondent with this name already exists.</html>");

        ConflictDetection.IsUniqueConstraintError(error, out var field, out _).Should().BeTrue();
        field.Should().Be("name");
    }

    [Fact]
    public void IsUniqueConstraintError_HandlesEmptyBody()
    {
        ConflictDetection.IsUniqueConstraintError(Error(HttpStatusCode.BadRequest, ""), out _, out _)
            .Should().BeFalse();
    }

    public class BuildCreateFailureAsyncTests : IDisposable
    {
        private readonly MockHttpClientFactory _factory = new();

        [Fact]
        public async Task UniqueConflict_WithExistingObject_ReturnsConflictWithExistingId()
        {
            _factory.SetupGet("api/correspondents/",
                """
                {"count":2,"next":null,"previous":null,"results":[
                    {"id":499,"slug":"tomorrow","name":"Tomorrow","match":"","matching_algorithm":1,"document_count":3,"last_correspondence":null,"owner":null},
                    {"id":500,"slug":"vodafone","name":"Vodafone Kabel","match":"","matching_algorithm":0,"document_count":9,"last_correspondence":null,"owner":null}]}
                """);

            var error = Error(HttpStatusCode.BadRequest,
                """{"name": ["correspondent with this name already exists."]}""");

            var response = await ConflictDetection.BuildCreateFailureAsync(
                _factory.Client, "correspondent", "Tomorrow", error);

            var json = JsonDocument.Parse(JsonSerializer.Serialize(response)).RootElement;
            json.GetProperty("error").GetProperty("code").GetString().Should().Be("CONFLICT");
            var details = json.GetProperty("error").GetProperty("details");
            details.GetProperty("existing_id").GetInt32().Should().Be(499);
            details.GetProperty("existing_name").GetString().Should().Be("Tomorrow");
            details.GetProperty("conflict").GetString().Should().Be("unique_name");
            json.GetProperty("error").GetProperty("message").GetString()
                .Should().Contain("ID 499").And.Contain("already exists");
        }

        [Fact]
        public async Task UniqueConflict_WithCaseInsensitiveMatch_ReportsSpellingOfExisting()
        {
            _factory.SetupGet("api/correspondents/",
                """
                {"count":1,"next":null,"previous":null,"results":[
                    {"id":7,"slug":"tomorrow","name":"TOMORROW","match":"","matching_algorithm":0,"document_count":1,"last_correspondence":null,"owner":null}]}
                """);

            var error = Error(HttpStatusCode.BadRequest,
                """{"name": ["correspondent with this name already exists."]}""");

            var response = await ConflictDetection.BuildCreateFailureAsync(
                _factory.Client, "correspondent", "Tomorrow", error);

            var json = JsonDocument.Parse(JsonSerializer.Serialize(response)).RootElement;
            json.GetProperty("error").GetProperty("details").GetProperty("existing_id").GetInt32().Should().Be(7);
            json.GetProperty("error").GetProperty("message").GetString()
                .Should().Contain("TOMORROW");
        }

        [Fact]
        public async Task UniqueConflict_WithoutResolvedId_PointsAtAttributesSearch()
        {
            _factory.SetupGet("api/correspondents/",
                """{"count":0,"next":null,"previous":null,"results":[]}""");

            var error = Error(HttpStatusCode.BadRequest,
                """{"name": ["correspondent with this name already exists."]}""");

            var response = await ConflictDetection.BuildCreateFailureAsync(
                _factory.Client, "correspondent", "Tomorrow", error);

            var json = JsonDocument.Parse(JsonSerializer.Serialize(response)).RootElement;
            json.GetProperty("error").GetProperty("code").GetString().Should().Be("CONFLICT");
            json.GetProperty("error").GetProperty("details").GetProperty("existing_id")
                .ValueKind.Should().Be(JsonValueKind.Null);
            json.GetProperty("error").GetProperty("message").GetString()
                .Should().Contain("paperless_attributes_search");
        }

        [Fact]
        public async Task OtherUpstreamFailure_IncludesStatusAndBody()
        {
            var error = Error(HttpStatusCode.InternalServerError,
                """{"detail": "Internal Server Error"}""");

            var response = await ConflictDetection.BuildCreateFailureAsync(
                _factory.Client, "correspondent", "Something New", error);

            var json = JsonDocument.Parse(JsonSerializer.Serialize(response)).RootElement;
            json.GetProperty("error").GetProperty("code").GetString().Should().Be("UPSTREAM_ERROR");
            var details = json.GetProperty("error").GetProperty("details");
            details.GetProperty("status_code").GetInt32().Should().Be(500);
            details.GetProperty("response_body").GetString().Should().Contain("Internal Server Error");
            json.GetProperty("error").GetProperty("message").GetString()
                .Should().Contain("HTTP 500");
        }

        public void Dispose() => _factory.Dispose();
    }
}
