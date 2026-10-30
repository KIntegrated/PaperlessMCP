# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
dotnet build                # Build the solution
dotnet test                 # Run all tests
dotnet test --filter "FullyQualifiedName~DocumentToolsTests"  # Run specific test class
dotnet test --logger "console;verbosity=detailed"             # Verbose test output
```

**Running locally:**
```bash
# Set required environment variables
export PAPERLESS_BASE_URL=https://your-instance.com
export PAPERLESS_API_TOKEN=your-token

# stdio mode (for Claude Desktop)
dotnet run --project PaperlessMCP -- --stdio

# HTTP mode (runs at http://localhost:5000/mcp)
dotnet run --project PaperlessMCP
```

## Architecture

PaperlessMCP is an MCP (Model Context Protocol) server that bridges Paperless-ngx with AI assistants. It supports dual transport modes: stdio for Claude Desktop integration and HTTP for remote usage.

### Key Components

- **Program.cs** - Entry point with dual transport setup (stdio vs HTTP). Configures DI, HttpClient with Polly retry policies, and MCP server registration.

- **Client/PaperlessClient.cs** - Central API client for all Paperless-ngx operations. Uses snake_case JSON serialization to match API conventions.

- **Client/PaperlessAuthHandler.cs** - DelegatingHandler that injects API token authentication.

- **Tools/** - MCP tool implementations, one file per entity type:
  - `DocumentTools.cs` - Search, CRUD, bulk operations, upload (base64 and file path)
  - `TagTools.cs`, `CorrespondentTools.cs`, `DocumentTypeTools.cs`, `StoragePathTools.cs` - Entity management with matching algorithms
  - `CustomFieldTools.cs` - Custom field definitions and assignments
  - `AttributeSearchTools.cs` - `paperless_attributes_search`: wildcard/regex search over the names of all attribute entities (tags, correspondents, document types, storage paths, custom fields), returning compact id/name/type results with a server-side `name__icontains` prefilter
  - `HealthTools.cs` - Ping and capabilities

- **Models/** - Request/response DTOs organized by entity (Documents/, Tags/, etc.). Uses `PaginatedResponse<T>` for list endpoints.

### Configuration

Environment variables (take precedence over appsettings.json):
- `PAPERLESS_BASE_URL` / `PAPERLESS_URL` - Paperless-ngx instance URL
- `PAPERLESS_API_TOKEN` / `PAPERLESS_TOKEN` - API authentication token
- `MAX_PAGE_SIZE` - Max items per page (default: 100)
- `MCP_PORT` - HTTP server port (default: 5000)
- `HTTP_TIMEOUT_SECONDS` - Timeout for Paperless API requests (default: 30)

### Testing

Tests use xUnit with FluentAssertions for assertions, NSubstitute for mocking, and RichardSzalay.MockHttp for HTTP mocking. Test files mirror the main project structure under `PaperlessMCP.Tests/`.

## Conventions

- Use [Conventional Commits](https://www.conventionalcommits.org/) - version bumps are automatic based on commit type (`fix:` = patch, `feat:` = minor, `feat!:` = major)
- Trunk-based development: feature branches merge directly to `main`
- All destructive operations (delete, bulk operations) require explicit `confirm=true` and default to dry-run mode

## Versioning & Releases

**Single source of truth: git tags (`vX.Y.Z`).** Everything else is derived from the tag at release time — never hand-edit a version anywhere.

**Releases are fully automated** by `.woodpecker/release.yml` on every push to `main`:
1. `calculate-version` reads the latest tag and the conventional commits since it, then computes the next version (`feat` → minor, `fix`/`perf`/`refactor`/`build`/`ci`/`docs`/`style`/`test` → patch, `!`/`BREAKING CHANGE` → major, nothing → no release).
2. The new version is injected into the NuGet package (`/p:Version`), the Docker image (`--build-arg VERSION` + `:vX.Y.Z`/`:latest`), `version.json`, and `PaperlessMCP.csproj` `<Version>` (the csproj literal is rewritten and committed so a local `dotnet build` reports the real version).
3. A `chore(release): bump version to X.Y.Z [skip ci]` commit, a `vX.Y.Z` tag, and a GitHub release are created and pushed.

Do **not** run `cog bump` — it is not part of the release flow and would fight the pipeline. The version comes from tags, not from any file.

**Cocogitto's only job here is commit-message validation.** A `commit-msg` hook (and the `pr.yml` commitlint step) enforce Conventional Commits. Install the local hook after cloning:
```bash
cog install-hook commit-msg
```

**Commit format:**
```
<type>[optional scope]: <description>

[optional body]

[optional footer(s)]
```

Types: `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `build`, `ci`, `chore`, `revert`

```bash
cog check                # Verify all commits follow conventional format
```
