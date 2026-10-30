using System.Net;
using System.Text.Json;
using FluentAssertions;
using PaperlessMCP.Tests.Fixtures;
using RichardSzalay.MockHttp;
using PaperlessMCP.Tools;
using Xunit;

namespace PaperlessMCP.Tests.Tools;

public class CorrespondentToolsTests : IDisposable
{
    private readonly MockHttpClientFactory _factory;

    public CorrespondentToolsTests()
    {
        _factory = new MockHttpClientFactory();
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task List_ReturnsCorrespondentList()
    {
        // Arrange
        _factory.MockHandler
            .When(HttpMethod.Get, "https://paperless.example.com/api/correspondents/*")
            .Respond("application/json", TestFixtures.Correspondents.CreateCorrespondentListJson(5));

        // Act
        var result = await CorrespondentTools.List(_factory.Client);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetArrayLength().Should().Be(5);
    }

    [Fact]
    public async Task Get_WhenExists_ReturnsCorrespondent()
    {
        // Arrange
        _factory.SetupGet("api/correspondents/1/", TestFixtures.Correspondents.CreateCorrespondentJson(1, "ACME Corp"));

        // Act
        var result = await CorrespondentTools.Get(_factory.Client, 1);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("name").GetString().Should().Be("ACME Corp");
    }

    [Fact]
    public async Task Get_WhenNotFound_ReturnsError()
    {
        // Arrange
        _factory.SetupGetWithStatus("api/correspondents/999/", HttpStatusCode.NotFound);

        // Act
        var result = await CorrespondentTools.Get(_factory.Client, 999);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Create_WhenSuccessful_ReturnsCreatedCorrespondent()
    {
        // Arrange
        _factory.SetupPost("api/correspondents/", TestFixtures.Correspondents.CreateCorrespondentJson(1, "New Company"));

        // Act
        var result = await CorrespondentTools.Create(_factory.Client, "New Company");

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("name").GetString().Should().Be("New Company");
    }

    [Fact]
    public async Task Create_WhenDuplicateName_ReturnsConflictWithExistingId()
    {
        // Arrange: Paperless rejects with DRF unique validation; the lookup that
        // resolves the existing ID hits the (query-filtered) list endpoint.
        _factory.SetupPostWithError("api/correspondents/", HttpStatusCode.BadRequest,
            """{"name": ["correspondent with this name already exists."]}""");
        _factory.SetupGet("api/correspondents/",
            """
            {"count":2,"next":null,"previous":null,"results":[
                {"id":499,"slug":"tomorrow","name":"Tomorrow","match":"","matching_algorithm":1,"document_count":3,"last_correspondence":null,"owner":null},
                {"id":500,"slug":"vodafone","name":"Vodafone Kabel","match":"","matching_algorithm":0,"document_count":9,"last_correspondence":null,"owner":null}]}
            """
        );

        // Act
        var result = await CorrespondentTools.Create(_factory.Client, "Tomorrow");

        // Assert
        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("ok").GetBoolean().Should().BeFalse();
        var error = json.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("CONFLICT");
        error.GetProperty("details").GetProperty("existing_id").GetInt32().Should().Be(499);
        error.GetProperty("details").GetProperty("existing_name").GetString().Should().Be("Tomorrow");
        error.GetProperty("message").GetString().Should().Contain("ID 499");
    }

    [Fact]
    public async Task Create_WhenOtherUpstreamFailure_IncludesStatusAndUpstreamBody()
    {
        // Arrange
        _factory.SetupPostWithError("api/correspondents/", HttpStatusCode.InternalServerError,
            """{"detail": "Internal Server Error"}""");

        // Act
        var result = await CorrespondentTools.Create(_factory.Client, "New Company");

        // Assert
        var json = JsonDocument.Parse(result).RootElement;
        var error = json.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("UPSTREAM_ERROR");
        error.GetProperty("details").GetProperty("status_code").GetInt32().Should().Be(500);
        error.GetProperty("details").GetProperty("response_body").GetString()
            .Should().Contain("Internal Server Error");
    }

    [Fact]
    public async Task Update_WhenSuccessful_ReturnsUpdatedCorrespondent()
    {
        // Arrange
        _factory.SetupPatch("api/correspondents/1/", TestFixtures.Correspondents.CreateCorrespondentJson(1, "Updated Company"));

        // Act
        var result = await CorrespondentTools.Update(_factory.Client, 1, name: "Updated Company");

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("name").GetString().Should().Be("Updated Company");
    }

    [Fact]
    public async Task Delete_WithoutConfirmation_ReturnsDryRun()
    {
        // Arrange
        _factory.SetupGet("api/correspondents/1/", TestFixtures.Correspondents.CreateCorrespondentJson(1, "To Delete"));

        // Act
        var result = await CorrespondentTools.Delete(_factory.Client, 1, confirm: false);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("CONFIRMATION_REQUIRED");
    }

    [Fact]
    public async Task Delete_WithConfirmation_DeletesCorrespondent()
    {
        // Arrange
        _factory.SetupDelete("api/correspondents/1/", HttpStatusCode.NoContent);

        // Act
        var result = await CorrespondentTools.Delete(_factory.Client, 1, confirm: true);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("deleted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task BulkDelete_WithDryRun_ReturnsPreview()
    {
        // Act
        var result = await CorrespondentTools.BulkDelete(_factory.Client, "1,2", dryRun: true);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("executed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task BulkDelete_WithConfirmation_ExecutesDeletion()
    {
        // Arrange
        _factory.SetupPost("api/bulk_edit_objects/", "{}");

        // Act
        var result = await CorrespondentTools.BulkDelete(_factory.Client, "1,2", dryRun: false, confirm: true);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("executed").GetBoolean().Should().BeTrue();
    }
}
