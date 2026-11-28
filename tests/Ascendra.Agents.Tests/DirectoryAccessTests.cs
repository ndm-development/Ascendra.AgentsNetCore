using System.Security.Claims;
using Ascendra.Agents.Application;
using Ascendra.Agents.Domain;
using Ascendra.Agents.Infrastructure;
using Ascendra.Agents.Web.Auth;
using Ascendra.Agents.Web.Infrastructure;
using Ascendra.Agents.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ascendra.Agents.Tests;

public sealed class DirectoryAccessTests
{
    [PostgresFact]
    public async Task DatabaseBootstrapAndRefreshDoNotDependOnHeadcountAfterImport()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var host = new DirectoryTestHost(test.Service, postgres: true);
        var paths = new OperationalDataPaths(host.DataOptions, host.Environment);
        DirectoryRulesTests.WriteWorkbook(paths.Input("headcount.xlsx"));
        var bootstrap = new AgentDirectoryBootstrapService(test.Repository, test.Service, new(), paths,
            host.DirectoryOptions, NullLogger<AgentDirectoryBootstrapService>.Instance);
        await bootstrap.StartAsync(default);
        File.Delete(paths.Input("headcount.xlsx"));
        await bootstrap.StartAsync(default);
        await host.Snapshots.RefreshAsync();
        Assert.True(host.Snapshots.Health.IsReady);
        Assert.Single(host.Snapshots.Current.Users);
        Assert.Equal(2, host.Snapshots.Current.AgentDirectory.Count);
        var version = host.Snapshots.Current.Version;
        var notifications = 0;
        host.Snapshots.Changed += () => notifications++;
        var manager = (await test.Service.GetAsync()).Directory.Single(row => row.Id == "M");
        await test.Service.SaveAsync(manager with { FirstName = "Nombre actualizado" }, "M", manager.Revision);
        await host.Snapshots.RefreshAsync();
        Assert.NotEqual(version, host.Snapshots.Current.Version);
        Assert.StartsWith("Nombre actualizado", Assert.Single(host.Snapshots.Current.Users).DisplayName);
        Assert.Equal(1, notifications);
        Assert.Equal(1, await test.CountAsync("crm_agent_directory_imports"));
    }

    [PostgresFact]
    public async Task LoginAndSessionsUseFreshDatabaseIdentityWithoutSnapshotRefresh()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.Service.SeedIfEmptyAsync(TestDatabase.Roster, "source");
        using var host = new DirectoryTestHost(test.Service, postgres: true);
        await host.Snapshots.RefreshAsync();
        Assert.NotNull(await host.Authenticator.AuthenticateAsync(" M ", "test-only"));
        Assert.Null(await host.Authenticator.AuthenticateAsync("m", "incorrect"));
        Assert.Null(await host.Authenticator.AuthenticateAsync("i", "test-only"));
        var session = Principal("M", "m", "Gerente");
        Assert.True(await host.Authenticator.IsSessionCurrentAsync(session));
        Assert.False(await host.Authenticator.IsSessionCurrentAsync(Principal("A", "m", "Gerente")));
        var manager = (await test.Service.GetAsync()).Directory.Single(row => row.Id == "M");
        manager = await test.Service.SaveAsync(manager with { Role = "Agente" }, "D", 1);
        Assert.False(await host.Authenticator.IsSessionCurrentAsync(session));
        Assert.Equal("Agente", (await host.Authenticator.AuthenticateAsync("m", "test-only"))!.Role);
        Assert.Equal("Gerente", host.Snapshots.Current.Users.Single(row => row.Id == "M").Role);
        var promises = new PromiseRegistrationService(host.Snapshots, test.Database, test.Service,
            host.DirectoryOptions, host.ProcessingOptions, NullLogger<PromiseRegistrationService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => promises.DeleteAsync(1,
            DirectoryRulesTests.Person("M", "Gerente").ToUser()));
        await test.Service.SaveAsync(manager with { IsActive = false }, "D", manager.Revision);
        Assert.Null(await host.Authenticator.AuthenticateAsync("m", "test-only"));
        Assert.False(await host.Authenticator.IsSessionCurrentAsync(Principal("M", "m", "Agente")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.RequireActiveAsync("M"));
    }

    [Fact]
    public async Task LegacyModeDoesNotReadDatabaseDirectory()
    {
        var repository = new SwitchableDirectoryRepository { Fail = true };
        var service = new AgentDirectoryService(repository);
        using var host = new DirectoryTestHost(service, postgres: false);
        var paths = new OperationalDataPaths(host.DataOptions, host.Environment);
        DirectoryRulesTests.WriteWorkbook(paths.Input("headcount.xlsx"));
        var bootstrap = new AgentDirectoryBootstrapService(repository, service, new(), paths,
            host.DirectoryOptions, NullLogger<AgentDirectoryBootstrapService>.Instance);
        await bootstrap.StartAsync(default);
        await host.Snapshots.RefreshAsync();
        Assert.True(host.Snapshots.Health.IsReady);
        Assert.NotNull(await host.Authenticator.AuthenticateAsync("m", "test-only"));
        Assert.True(await host.Authenticator.IsSessionCurrentAsync(Principal("M", "m", "Gerente")));
        Assert.Equal(0, repository.Reads);
    }

    [Fact]
    public async Task DatabaseFailureCannotAuthenticateFromOldSnapshotOrExcel()
    {
        var repository = new SwitchableDirectoryRepository();
        using var host = new DirectoryTestHost(new(repository), postgres: true);
        DirectoryRulesTests.WriteWorkbook(Path.Combine(host.Environment.ContentRootPath, "headcount.xlsx"));
        await host.Snapshots.RefreshAsync();
        Assert.True(host.Snapshots.Health.IsReady);
        repository.Fail = true;
        await host.Snapshots.RefreshAsync();
        Assert.False(host.Snapshots.Health.IsReady);
        Assert.NotEmpty(host.Snapshots.Current.Users);
        await Assert.ThrowsAsync<TimeoutException>(() => host.Authenticator.AuthenticateAsync("m", "test-only"));
        await Assert.ThrowsAsync<TimeoutException>(() => host.Authenticator.IsSessionCurrentAsync(Principal("M", "m", "Gerente")));
        repository.Fail = false;
        await host.Snapshots.RefreshAsync();
        Assert.True(host.Snapshots.Health.IsReady);
    }

    private static ClaimsPrincipal Principal(string id, string username, string role) => new(new ClaimsIdentity(
        [new(AgentClaimTypes.AgentId, id), new(ClaimTypes.NameIdentifier, username), new(ClaimTypes.Role, role)], "test"));

    private sealed class SwitchableDirectoryRepository : IAgentDirectoryRepository
    {
        public bool Fail { get; set; }
        public int Reads { get; private set; }
        public Task<IReadOnlyList<AgentDirectoryRecord>> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            if (Fail) throw new TimeoutException("Synthetic database outage.");
            return Task.FromResult(TestDatabase.Roster);
        }
        public Task<bool> SeedIfEmptyAsync(IReadOnlyList<AgentDirectoryRecord> directory, string fingerprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentDirectoryRecord> SaveAsync(AgentDirectoryRecord agent, string editorId, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

internal sealed class DirectoryTestHost : IDisposable
{
    private readonly string _credentialsVariable = $"ASCENDRA_TEST_CREDENTIALS_{Guid.NewGuid():N}";
    public TestEnvironment Environment { get; }
    public StaticOptions<AgentDataOptions> DataOptions { get; }
    public IOptions<AgentDirectoryOptions> DirectoryOptions { get; }
    public IOptions<OperationalProcessingOptions> ProcessingOptions { get; }
    public ExcelSnapshotStore Snapshots { get; }
    public AgentAuthenticator Authenticator { get; }

    public DirectoryTestHost(AgentDirectoryService service, bool postgres)
    {
        Environment = new TestEnvironment { ContentRootPath = Path.Combine(Path.GetTempPath(), $"ascendra-directory-{Guid.NewGuid():N}") };
        Directory.CreateDirectory(Environment.ContentRootPath);
        DataOptions = new(new() { InputPath = Environment.ContentRootPath, GeneratedPath = Environment.ContentRootPath });
        DirectoryOptions = Options.Create(new AgentDirectoryOptions { UsePostgres = postgres });
        ProcessingOptions = Options.Create(new OperationalProcessingOptions());
        Snapshots = new(DataOptions, Environment, service, DirectoryOptions, ProcessingOptions,
            new OperationalProcessingService(new EmptyOperationalProcessingRepository()), NullLogger<ExcelSnapshotStore>.Instance);
        System.Environment.SetEnvironmentVariable(_credentialsVariable, "{\"m\":\"test-only\",\"i\":\"test-only\"}");
        Authenticator = new(Snapshots, service, DirectoryOptions,
            new StaticOptions<SecurityOptions>(new() { CredentialsEnvironmentVariable = _credentialsVariable }),
            Environment, NullLogger<AgentAuthenticator>.Instance);
    }

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable(_credentialsVariable, null);
        Directory.Delete(Environment.ContentRootPath, recursive: true);
    }
}

internal sealed class EmptyOperationalProcessingRepository : IOperationalProcessingRepository
{
    public Task<string> ReadSourceFingerprintAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);
    public Task<OperationalProcessingInput> ReadInputAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<OperationalProcessingRun?> ReadCurrentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<OperationalProcessingRun?>(null);
    public Task<OperationalProcessingRun> SaveAsync(string sourceFingerprint, OperationalProcessingResult result,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "Ascendra.Tests";
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = string.Empty;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
