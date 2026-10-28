using System.Text.Json.Nodes;

namespace IndustrialIot.Deployment.Tests;

/// <summary>
/// The SCADA page's Keycloak client: browser login with PKCE only, exact redirects, and tokens that carry Viewer
/// but never Operator (Operator includes Viewer, the client is scoped to Viewer).
/// </summary>
public class ScadaRealmTests
{
    private static readonly JsonNode Realm = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo.Root, "deployments", "keycloak", "iiot-realm.json")))!;

    // 8080 is the compose default; 8090 is the documented IIOT_API_PORT where Windows http.sys holds 8080.
    private static readonly string[] Origins = ["http://localhost:8080", "http://127.0.0.1:8080", "http://localhost:8090", "http://127.0.0.1:8090"];

    [Fact]
    public void ScadaClient_IsPublicAuthorizationCodeWithPkceOnly()
    {
        var client = Client("iiot-scada");

        Assert.True(Bool(client, "enabled"));
        Assert.True(Bool(client, "publicClient"));
        Assert.True(Bool(client, "standardFlowEnabled"));
        Assert.False(Bool(client, "implicitFlowEnabled"));
        Assert.False(Bool(client, "directAccessGrantsEnabled"));
        Assert.False(Bool(client, "serviceAccountsEnabled"));
        Assert.Equal("S256", client["attributes"]!["pkce.code.challenge.method"]!.GetValue<string>());
    }

    [Fact]
    public void ScadaClient_RedirectsAndOriginsAreExact()
    {
        var client = Client("iiot-scada");

        Assert.Equal(Origins.Select(o => o + "/scada/"), Strings(client["redirectUris"]));
        Assert.Equal(Origins, Strings(client["webOrigins"]));

        // Keycloak stores attributes as strings; several logout redirects are one string joined with "##".
        Assert.Equal(
            "http://localhost:8080/scada/##http://127.0.0.1:8080/scada/##http://localhost:8090/scada/##http://127.0.0.1:8090/scada/",
            client["attributes"]!["post.logout.redirect.uris"]!.GetValue<string>());

        foreach (var value in Strings(client["redirectUris"]).Concat(Strings(client["webOrigins"])))
        {
            Assert.DoesNotContain("*", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ScadaClient_ScopesAndMappersMatchTheApiContract()
    {
        var scada = Client("iiot-scada");
        var cli = Client("iiot-cli");

        Assert.False(Bool(scada, "fullScopeAllowed"));
        Assert.Equal(["basic"], Strings(scada["defaultClientScopes"]));
        Assert.Empty(Strings(scada["optionalClientScopes"]));

        // Same "roles" claim and iiot-cloud-api audience the Cloud validates.
        Assert.True(JsonNode.DeepEquals(cli["protocolMappers"], scada["protocolMappers"]));
    }

    [Fact]
    public void ScadaTokens_CarryViewerOnly_AndOperatorsStillRead()
    {
        var mappings = Realm["scopeMappings"]!.AsArray().Where(m => m!["client"]!.GetValue<string>() == "iiot-scada").ToList();
        var mapping = Assert.Single(mappings);
        Assert.Equal(["Viewer"], Strings(mapping!["roles"]));

        var operatorRole = Realm["roles"]!["realm"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "Operator")!;
        Assert.True(Bool(operatorRole, "composite"));
        Assert.Equal(["Viewer"], Strings(operatorRole["composites"]!["realm"]));
    }

    [Fact]
    public void CliClient_IsUnchanged()
    {
        var cli = Client("iiot-cli");

        Assert.True(Bool(cli, "directAccessGrantsEnabled"));
        Assert.False(Bool(cli, "standardFlowEnabled"));
        Assert.True(Bool(cli, "fullScopeAllowed"));
        Assert.Null(cli["redirectUris"]);
    }

    private static JsonNode Client(string clientId) =>
        Realm["clients"]!.AsArray().Single(c => c!["clientId"]!.GetValue<string>() == clientId)!;

    private static bool Bool(JsonNode node, string name) => node[name]!.GetValue<bool>();

    private static string[] Strings(JsonNode? array) => [.. array!.AsArray().Select(v => v!.GetValue<string>())];
}
