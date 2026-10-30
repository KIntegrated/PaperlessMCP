using System.Net;
using System.Text.Json;
using FluentAssertions;
using PaperlessMCP.Tests.Fixtures;
using RichardSzalay.MockHttp;
using PaperlessMCP.Tools;
using Xunit;

namespace PaperlessMCP.Tests.Tools;

public class TagToolsTests : IDisposable
{
    private readonly MockHttpClientFactory _factory;

    public TagToolsTests()
    {
        _factory = new MockHttpClientFactory();
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task List_ReturnsTagList()
    {
        // Arrange
        _factory.MockHandler
            .When(HttpMethod.Get, "https://paperless.example.com/api/tags/*")
            .Respond("application/json", TestFixtures.Tags.CreateTagListJson(5));

        // Act
        var result = await TagTools.List(_factory.Client);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetArrayLength().Should().Be(5);
        json.RootElement.GetProperty("meta").GetProperty("total").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task List_WithPagination_IncludesMetadata()
    {
        // Arrange
        _factory.MockHandler
            .When(HttpMethod.Get, "https://paperless.example.com/api/tags/*")
            .Respond("application/json", TestFixtures.Tags.CreateTagListJson(10));

        // Act
        var result = await TagTools.List(_factory.Client, page: 2, pageSize: 5);

        // Assert
        var json = JsonDocument.Parse(result);
        var meta = json.RootElement.GetProperty("meta");
        meta.GetProperty("page").GetInt32().Should().Be(2);
        meta.GetProperty("page_size").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Get_WhenTagExists_ReturnsTag()
    {
        // Arrange
        _factory.SetupGet("api/tags/1/", TestFixtures.Tags.CreateTagJson(1, "Important"));

        // Act
        var result = await TagTools.Get(_factory.Client, 1);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("id").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("result").GetProperty("name").GetString().Should().Be("Important");
    }

    [Fact]
    public async Task Get_WhenTagNotFound_ReturnsError()
    {
        // Arrange
        _factory.SetupGetWithStatus("api/tags/999/", HttpStatusCode.NotFound);

        // Act
        var result = await TagTools.Get(_factory.Client, 999);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Create_WhenSuccessful_ReturnsCreatedTag()
    {
        // Arrange
        _factory.SetupPost("api/tags/", TestFixtures.Tags.CreateTagJson(5, "New Tag"));

        // Act
        var result = await TagTools.Create(_factory.Client, "New Tag", color: "#00ff00");

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("name").GetString().Should().Be("New Tag");
    }

    [Fact]
    public async Task Create_WithParent_ReturnsCreatedTagWithParent()
    {
        // Arrange
        string? capturedBody = null;
        _factory.MockHandler
            .When(HttpMethod.Post, "https://paperless.example.com/api/tags/")
            .With(request =>
            {
                capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", TestFixtures.Tags.CreateTagJson(5, "Child Tag", parent: 1));

        // Act
        var result = await TagTools.Create(_factory.Client, "Child Tag", parent: 1);

        // Assert
        capturedBody.Should().Contain("\"parent\":1");
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("parent").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Create_WhenFails_ReturnsError()
    {
        // Arrange
        _factory.SetupPostWithStatus("api/tags/", HttpStatusCode.BadRequest);

        // Act
        var result = await TagTools.Create(_factory.Client, "Bad Tag");

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("UPSTREAM_ERROR");
    }

    [Fact]
    public async Task Create_WhenDuplicate_ReturnsConflictWithExistingId()
    {
        // Arrange: Paperless rejects the name colliding with an existing tag; the
        // conflict path resolves the existing tag's ID via the list endpoint.
        var errorBody = """{"name": ["tag with this name already exists."]}""";
        _factory.SetupPostWithError("api/tags/", HttpStatusCode.BadRequest, errorBody);
        var existingTag = TestFixtures.Tags.CreateTag(77, "Existing Tag");
        _factory.SetupGet("api/tags/", JsonSerializer.Serialize(
            new
            {
                count = 1,
                next = (string?)null,
                previous = (string?)null,
                results = new[] { existingTag }
            }));

        // Act
        var result = await TagTools.Create(_factory.Client, "Existing Tag");

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("CONFLICT");
        var details = json.RootElement.GetProperty("error").GetProperty("details");
        details.GetProperty("existing_id").GetInt32().Should().Be(77);
        details.GetProperty("existing_name").GetString().Should().Be("Existing Tag");
        details.GetProperty("status_code").GetInt32().Should().Be(400);
        json.RootElement.GetProperty("error").GetProperty("message").GetString()
            .Should().Contain("ID 77");
    }

    [Fact]
    public async Task Create_WhenUnauthorized_ReturnsErrorWithDetails()
    {
        // Arrange
        var errorBody = """{"detail": "Authentication credentials were not provided."}""";
        _factory.SetupPostWithError("api/tags/", HttpStatusCode.Unauthorized, errorBody);

        // Act
        var result = await TagTools.Create(_factory.Client, "New Tag");

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("UPSTREAM_ERROR");

        var details = json.RootElement.GetProperty("error").GetProperty("details");
        details.GetProperty("status_code").GetInt32().Should().Be(401);
        details.GetProperty("response_body").GetString().Should().Contain("Authentication credentials");
    }

    [Fact]
    public async Task Update_WhenSuccessful_ReturnsUpdatedTag()
    {
        // Arrange
        _factory.SetupPatch("api/tags/1/", TestFixtures.Tags.CreateTagJson(1, "Updated Name"));

        // Act
        var result = await TagTools.Update(_factory.Client, 1, name: "Updated Name");

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("name").GetString().Should().Be("Updated Name");
    }

    [Fact]
    public async Task Update_WithoutParent_OmitsParentField()
    {
        // Arrange
        string? capturedBody = null;
        _factory.MockHandler
            .When(HttpMethod.Patch, "https://paperless.example.com/api/tags/1/")
            .With(request =>
            {
                capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", TestFixtures.Tags.CreateTagJson(1, "Updated Child", parent: 2));

        // Act
        var result = await TagTools.Update(_factory.Client, 1, name: "Updated Child");

        // Assert
        capturedBody.Should().NotContain("\"parent\"");
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("parent").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Update_WithParent_SendsParentId()
    {
        // Arrange
        string? capturedBody = null;
        _factory.MockHandler
            .When(HttpMethod.Patch, "https://paperless.example.com/api/tags/1/")
            .With(request =>
            {
                capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", TestFixtures.Tags.CreateTagJson(1, "Child Tag", parent: 2));

        // Act
        var result = await TagTools.Update(_factory.Client, 1, parent: 2);

        // Assert
        capturedBody.Should().Contain("\"parent\":2");
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("parent").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Update_WithClearParent_SendsExplicitNull()
    {
        // Arrange
        string? capturedBody = null;
        _factory.MockHandler
            .When(HttpMethod.Patch, "https://paperless.example.com/api/tags/1/")
            .With(request =>
            {
                capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", TestFixtures.Tags.CreateTagJson(1, "Root Tag"));

        // Act
        var result = await TagTools.Update(_factory.Client, 1, clearParent: true);

        // Assert
        capturedBody.Should().Contain("\"parent\":null");
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("parent").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Update_WithParentAndClearParent_ReturnsValidationError()
    {
        // Act
        var result = await TagTools.Update(_factory.Client, 1, parent: 2, clearParent: true);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("VALIDATION");
    }

    [Fact]
    public async Task Delete_WithoutConfirmation_ReturnsDryRun()
    {
        // Arrange
        _factory.SetupGet("api/tags/1/", TestFixtures.Tags.CreateTagJson(1, "Tag to Delete"));

        // Act
        var result = await TagTools.Delete(_factory.Client, 1, confirm: false);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("CONFIRMATION_REQUIRED");
    }

    [Fact]
    public async Task Delete_WithConfirmation_DeletesTag()
    {
        // Arrange
        _factory.SetupDelete("api/tags/1/", HttpStatusCode.NoContent);

        // Act
        var result = await TagTools.Delete(_factory.Client, 1, confirm: true);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("deleted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task BulkDelete_WithDryRun_ReturnsPreview()
    {
        // Act
        var result = await TagTools.BulkDelete(_factory.Client, "1,2,3", dryRun: true);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("executed").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("result").GetProperty("affected_ids").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task BulkDelete_WithConfirmation_ExecutesDeletion()
    {
        // Arrange
        _factory.SetupPost("api/bulk_edit_objects/", "{}");

        // Act
        var result = await TagTools.BulkDelete(_factory.Client, "1,2,3", dryRun: false, confirm: true);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("result").GetProperty("executed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task BulkDelete_WhenUpstreamFails_ReturnsErrorDetails()
    {
        // Arrange
        const string errorBody = "{\"detail\":\"Object is protected\"}";
        _factory.SetupPostWithError("api/bulk_edit_objects/", HttpStatusCode.Conflict, errorBody);

        // Act
        var result = await TagTools.BulkDelete(_factory.Client, "1,2,3", dryRun: false, confirm: true);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetProperty("message").GetString()
            .Should().Contain("HTTP 409").And.Contain("Object is protected");
    }

    [Fact]
    public async Task BulkDelete_WithEmptyIds_ReturnsValidationError()
    {
        // Act
        var result = await TagTools.BulkDelete(_factory.Client, "", dryRun: false, confirm: true);

        // Assert
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("VALIDATION");
    }
}
