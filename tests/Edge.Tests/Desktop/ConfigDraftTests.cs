using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.Edge.Tests.Desktop;

/// <summary>
/// The editable copy of the Edge's settings: it sends back exactly what it was given until edited, tracks unsaved and
/// stale states, edits nodes by reference (never by position), and finds the node and field of each error path.
/// </summary>
public class ConfigDraftTests
{
    private const string Get = """
        {"revision":"rev-7","source":"file","settings":{"channels":[
          {"name":"Siemens","driver":"S7","devices":[
            {"name":"S7A","model":"S7-1200","host":"10.0.0.1","port":null,"rack":0,"slot":1,"timeoutMs":3000,
             "pollIntervalMs":1000,"enabled":true,"writesEnabled":true,"legacyTagIds":false,"tags":[
              {"name":"Speed","address":"DB1.DBW2","dataType":"Int","length":null,"writable":true,"min":3,"max":10.5,"description":null},
              {"name":"Text","address":"DB1.DBB10","dataType":"String","length":20,"writable":false,"min":null,"max":null,"description":"Tên máy"}]},
            {"name":"S7B","model":"S7-1200","host":"10.0.0.2","port":102,"rack":null,"slot":null,"timeoutMs":1000,
             "pollIntervalMs":500,"enabled":false,"writesEnabled":false,"legacyTagIds":true,"tags":[]}]},
          {"name":"Mitsubishi","driver":"Slmp3E","devices":[
            {"name":"FxA","model":"FX5U","host":"10.0.0.3","port":5000,"rack":null,"slot":null,"timeoutMs":3000,
             "pollIntervalMs":1000,"enabled":true,"writesEnabled":false,"legacyTagIds":false,"tags":[
              {"name":"D100","address":"D100","dataType":"Int","length":null,"writable":false,"min":null,"max":null,"description":null}]}]}]},
         "models":[{"name":"S7-1200","driver":"S7","defaultPort":102,"defaultRack":0,"defaultSlot":1,"portHint":null,
                    "dataTypes":["Bool","Int"],"writesAllowed":true}],
         "warnings":[],"commandsEnabled":true,"passwordSet":true}
        """;

    private static ConfigDraft Draft() =>
        new(JsonSerializer.Deserialize<SettingsSnapshot>(Get, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);

    [Fact]
    public void Unedited_SendsBackTheSettingsItWasGiven_IgnoringKeyCase()
    {
        var draft = Draft();

        var sent = draft.ToSettingsJson();

        Assert.Equal(Normalize(JsonNode.Parse(Get)!["settings"]!), Normalize(sent));
        Assert.Equal("rev-7", draft.Revision);
        Assert.True(draft.CommandsEnabled);
        Assert.False(draft.IsDirty);
        Assert.False(draft.IsStale);
    }

    [Fact]
    public void Sent_UsesTheWebPagesKeys()
    {
        var sent = Draft().ToSettingsJson();

        var device = sent["Channels"]![0]!["Devices"]![0]!.AsObject();
        Assert.Equal(
            ["Name", "Model", "Host", "Port", "Rack", "Slot", "TimeoutMs", "PollIntervalMs", "Enabled", "WritesEnabled", "LegacyTagIds", "Tags"],
            device.Select(p => p.Key));
        Assert.Equal(
            ["Name", "Address", "DataType", "Length", "Writable", "Min", "Max", "Description"],
            device["Tags"]![0]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void Edits_MakeItDirty_AndStaleIsSeparate()
    {
        var draft = Draft();
        var device = draft.Channels[0].Devices[0];

        draft.AddTag(device, new DraftTag { Name = "New", Address = "DB1.DBW4", DataType = "Int" });

        Assert.True(draft.IsDirty);
        Assert.False(draft.IsStale);
        draft.MarkStale();
        Assert.True(draft.IsStale);
    }

    [Fact]
    public void Update_GoesToTheSameNode_EvenAfterTheOrderChanged()
    {
        var draft = Draft();
        var first = draft.Channels[0].Devices[0];
        var second = draft.Channels[0].Devices[1];
        var edited = second.Clone();
        edited.Name = "Renamed";
        edited.WritesEnabled = true;

        draft.RemoveDevice(first);
        draft.Update(second, edited);

        var only = Assert.Single(draft.Channels[0].Devices);
        Assert.Same(second, only);
        Assert.Equal("Renamed", only.Name);
        Assert.True(only.WritesEnabled);
    }

    [Fact]
    public void Update_OfARemovedNode_IsRefused()
    {
        var draft = Draft();
        var tag = draft.Channels[0].Devices[0].Tags[0];
        draft.RemoveTag(tag);

        Assert.Throws<InvalidOperationException>(() => draft.Update(tag, tag.Clone()));
    }

    [Fact]
    public void AddingANodeAlreadyInTheDraft_IsRefused()
    {
        var draft = Draft();
        var channel = draft.Channels[0];
        var device = channel.Devices[0];

        Assert.Throws<InvalidOperationException>(() => draft.AddChannel(channel));
        Assert.Throws<InvalidOperationException>(() => draft.AddDevice(draft.Channels[1], device));
        Assert.Throws<InvalidOperationException>(() => draft.AddTag(device, device.Tags[0]));
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var draft = Draft();
        var device = draft.Channels[0].Devices[0];

        var copy = device.Clone();
        copy.Tags[0].Name = "Changed";

        Assert.Equal("Speed", device.Tags[0].Name);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void ChannelsWithoutPlc_AreListed()
    {
        var draft = Draft();

        var empty = draft.AddChannel(new DraftChannel { Name = "Empty", Driver = "Mc1E" });

        Assert.Same(empty, Assert.Single(draft.ChannelsWithoutDevices));
    }

    [Fact]
    public void Locate_FollowsThePathGrammar()
    {
        var draft = Draft();
        var siemens = draft.Channels[0];
        var mitsubishi = draft.Channels[1];
        var s7a = siemens.Devices[0];

        Assert.Equal(new ErrorLocation(mitsubishi, null, null, "Name"), draft.Locate("Edge:Channels:1:Name"));
        Assert.Equal(new ErrorLocation(siemens, null, null, null), draft.Locate("Edge:Channels:0"));
        Assert.Equal(new ErrorLocation(siemens, s7a, null, "Tags"), draft.Locate("Edge:Channels:0:Devices:0:Tags"));
        Assert.Equal(new ErrorLocation(siemens, s7a, null, "Host"), draft.Locate("Edge:Channels:0:Devices:0:Host"));
        Assert.Equal(new ErrorLocation(siemens, s7a, s7a.Tags[1], "Address"), draft.Locate("Edge:Channels:0:Devices:0:Tags:1:Address"));
        Assert.Equal(new ErrorLocation(siemens, s7a, s7a.Tags[0], null), draft.Locate("Edge:Channels:0:Devices:0:Tags:0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("settings")]
    [InlineData("revision")]
    [InlineData("Edge:Channels:5:Name")]
    [InlineData("Edge:Channels:0:Devices:9:Host")]
    [InlineData("Edge:Channels:0:Devices:0:Tags:7:Address")]
    [InlineData("proposed:port")]
    public void Locate_LeavesOtherPathsUnmapped(string path) => Assert.Null(Draft().Locate(path));

    private static string Normalize(JsonNode node) => node switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Key.ToLowerInvariant() + ":" + (p.Value is null ? "null" : Normalize(p.Value)))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(v => v is null ? "null" : Normalize(v))) + "]",
        _ => node.ToJsonString(),
    };
}
