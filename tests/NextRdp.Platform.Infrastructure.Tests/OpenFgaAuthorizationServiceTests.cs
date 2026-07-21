using System.Net;
using System.Text;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure.Tests;

public sealed class OpenFgaAuthorizationServiceTests
{
    [Theory]
    [InlineData(PlatformAction.ManageProductivity, "member")]
    [InlineData(PlatformAction.RunGatewayDiagnostics, "viewer")]
    public async Task Phase2ActionsUseValidScopedRelations(PlatformAction action, string expectedRelation)
    {
        var handler = new RecordingHandler();
        var service = new OpenFgaAuthorizationService(
            new HttpClient(handler) { BaseAddress = new Uri("http://openfga.local/") },
            new OpenFgaOptions(new Uri("http://openfga.local/"), "store", "model"));
        var actor = new PlatformActor(Guid.CreateVersion7(), "test-user", Guid.CreateVersion7(),
            new HashSet<WorkspaceRole> { WorkspaceRole.WorkspaceAdministrator }, [], true, 1);
        var target = new AuthorizationTarget(actor.OrganizationId!.Value, Guid.CreateVersion7(), "workspace", Guid.CreateVersion7());

        var decision = await service.AuthorizeAsync(actor, action, target);

        Assert.True(decision.Allowed);
        Assert.Contains($"\"relation\":\"{expectedRelation}\"", handler.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"relation\":\"denied\"", handler.RequestBody, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"allowed\":true}", Encoding.UTF8, "application/json")
            };
        }
    }
}
