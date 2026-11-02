using System.Text.Json;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;

namespace IndustrialIot.Shared.Tests;

public class EdgeInventoryContractTests
{
    // Snapshot JSON: the exact wire format of a status part (field names and order).
    private const string StatusPartJson =
        """{"schemaVersion":1,"edgeId":"edge-01","bootId":"0f8fad5b-d9cb-469f-a165-70867728950e","round":7,"part":0,"partCount":2,"sentUtc":"2026-11-02T08:00:00.123Z","commandsEnabled":false,"devices":[{"name":"Line","model":"S7-1200","enabled":true,"writesEnabled":false,"state":"connected","lastGoodUtc":"2026-11-02T07:59:59.500Z","lastError":"tagErrors","tagCount":2},{"name":"Press","model":"FX5U","enabled":false,"writesEnabled":false,"state":"disabled","lastGoodUtc":null,"lastError":null,"tagCount":0}],"tags":null}""";

    private static EdgeInventory StatusPart() => new()
    {
        SchemaVersion = 1,
        EdgeId = "edge-01",
        BootId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
        Round = 7,
        Part = 0,
        PartCount = 2,
        SentUtc = new DateTimeOffset(2026, 11, 2, 8, 0, 0, 123, TimeSpan.Zero),
        CommandsEnabled = false,
        Devices =
        [
            Device("Line") with
            {
                Model = "S7-1200",
                State = InventoryDeviceState.Connected,
                LastGoodUtc = new DateTimeOffset(2026, 11, 2, 7, 59, 59, 500, TimeSpan.Zero),
                LastError = InventoryDeviceError.TagErrors,
                TagCount = 2,
            },
            Device("Press") with { Model = "FX5U", Enabled = false, State = InventoryDeviceState.Disabled },
        ],
    };

    private static EdgeInventory TagsPart() => StatusPart() with
    {
        Part = 1,
        Devices = null,
        Tags = new InventoryTags
        {
            Device = "Line",
            Items =
            [
                Tag("Line.Speed") with { Name = "Speed", DataType = "Int", Writable = true, Min = 0, Max = 3000, Description = "Belt speed" },
                Tag("Line.Batch") with { Name = "Batch", DataType = "String", Length = 20 },
            ],
        },
    };

    private static InventoryDevice Device(string name) => new()
    {
        Name = name,
        Model = "S7-1200",
        Enabled = true,
        WritesEnabled = false,
        State = InventoryDeviceState.Waiting,
        LastGoodUtc = null,
        LastError = null,
        TagCount = 0,
    };

    private static InventoryTag Tag(string tagId) => new()
    {
        TagId = tagId,
        Name = tagId,
        DataType = "Int",
        Length = null,
        Writable = false,
        Min = null,
        Max = null,
        Description = null,
    };

    [Fact]
    public void StatusPart_HasTheAgreedWireFormat()
    {
        Assert.Equal(StatusPartJson, ContractJson.Serialize(StatusPart()));
    }

    [Fact]
    public void BothPartKinds_RoundTripByteForByte()
    {
        foreach (var part in new[] { StatusPart(), TagsPart() })
        {
            var json = ContractJson.Serialize(part);
            Assert.Equal(json, ContractJson.Serialize(ContractJson.Deserialize<EdgeInventory>(json)));
        }
    }

    [Fact]
    public void LastErrorTagErrors_IsCamelCaseOnTheWire()
    {
        Assert.Contains("\"lastError\":\"tagErrors\"", ContractJson.Serialize(StatusPart()));
        Assert.Throws<ContractValidationException>(() =>
            ContractJson.Deserialize<EdgeInventory>(StatusPartJson.Replace("\"tagErrors\"", "\"tag_errors\"")));
    }

    [Fact]
    public void NoPart_CarriesANetworkDetailOrErrorText()
    {
        string[] forbidden = ["host", "port", "rack", "slot", "address", "error"];
        foreach (var part in new[] { StatusPart(), TagsPart() })
        {
            using var document = JsonDocument.Parse(ContractJson.Serialize(part));
            var names = PropertyNames(document.RootElement).ToList();
            Assert.NotEmpty(names);
            Assert.DoesNotContain(names, name => forbidden.Contains(name, StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void SharedProject_ReferencesNoOtherProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IndustrialIot.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var project = File.ReadAllText(Path.Combine(dir.FullName, "src", "Shared", "IndustrialIot.Shared.csproj"));
        Assert.DoesNotContain("ProjectReference", project);
    }

    public static TheoryData<string, Func<EdgeInventory, EdgeInventory>> Invalid() => new()
    {
        { "schemaVersion", p => p with { SchemaVersion = 2 } },
        { "edgeId", p => p with { EdgeId = "edge/01" } },
        { "bootId", p => p with { BootId = Guid.Empty } },
        { "round", p => p with { Round = -1 } },
        { "part", p => p with { Part = -1 } },
        { "part", p => p with { Part = 2 } },
        { "partCount", p => p with { PartCount = 0, Part = 0 } },
        { "partCount", p => p with { PartCount = 201 } },
        { "sentUtc", p => p with { SentUtc = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)) } },
        { "devices", p => p with { Devices = null, Tags = null } },
        { "name", p => p with { Devices = [Device("Line 1")] } },
        { "name", p => p with { Devices = [Device(new string('a', 33))] } },
        { "model", p => p with { Devices = [Device("Line") with { Model = "S7\u0000" }] } },
        { "model", p => p with { Devices = [Device("Line") with { Model = "10.0.0.5:102" }] } },
        { "devices", p => p with { Devices = [Device("Line"), Device("LINE")] } },
        { "state", p => p with { Devices = [Device("Line") with { State = (InventoryDeviceState)42 }] } },
        { "lastError", p => p with { Devices = [Device("Line") with { LastError = (InventoryDeviceError)42 }] } },
        { "tagCount", p => p with { Devices = [Device("Line") with { TagCount = -1 }] } },
        { "lastGoodUtc", p => p with { Devices = [Device("Line") with { LastGoodUtc = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(7)) }] } },
        { "device", p => WithTag(p, Tag("Line.A"), device: "Line A") },
        { "items", p => p with { Devices = null, Tags = new InventoryTags { Device = "Line", Items = null! } } },
        { "tagId", p => WithTag(p, Tag("Line A")) },
        { "name", p => WithTag(p, Tag("Line.A") with { Name = "A B" }) },
        { "name", p => WithTag(p, Tag("Line.A") with { Name = new string('a', 65) }) },
        { "dataType", p => WithTag(p, Tag("Line.A") with { DataType = "1Int" }) },
        { "dataType", p => WithTag(p, Tag("Line.A") with { DataType = "Int64Plus123456789" }) },
        { "length", p => WithTag(p, Tag("Line.A") with { DataType = "String", Length = 0 }) },
        { "length", p => WithTag(p, Tag("Line.A") with { DataType = "String", Length = 257 }) },
        { "min", p => WithTag(p, Tag("Line.A") with { Min = 10, Max = 1 }) },
        { "description", p => WithTag(p, Tag("Line.A") with { Description = new string('d', 257) }) },
        { "description", p => WithTag(p, Tag("Line.A") with { Description = "line\nbreak" }) },
        { "description", p => WithTag(p, Tag("Line.A") with { Description = "   " }) },
        { "description", p => WithTag(p, Tag("Line.A") with { Description = "lone \uD800" }) },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void PartBreakingABound_IsRejectedNamingTheField(string field, Func<EdgeInventory, EdgeInventory> breakIt)
    {
        var part = breakIt(StatusPart());

        var error = Assert.Throws<ContractValidationException>(() => ContractJson.Serialize(part));

        Assert.Equal(field, error.Field);
    }

    [Fact]
    public void BoundaryValues_AreAccepted()
    {
        var part = WithTag(StatusPart() with { Part = 199, PartCount = 200 }, Tag(new string('t', 64)) with
        {
            Name = new string('n', 64),
            DataType = "LReal",
            Length = 256,
            Min = 5,
            Max = 5,
            Description = new string('d', 254) + "😀", // an emoji at the end, 256 units
        });

        ContractJson.Serialize(part);
        ContractJson.Serialize(StatusPart() with { Devices = [Device(new string('a', 32)) with { Model = new string('m', 32) }] });
    }

    [Fact]
    public void JsonWithoutRequiredFields_IsRejected()
    {
        Assert.Throws<ContractValidationException>(() =>
            ContractJson.Deserialize<EdgeInventory>(StatusPartJson.Replace("\"bootId\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",", "")));
    }

    private static EdgeInventory WithTag(EdgeInventory part, InventoryTag tag, string device = "Line") => part with
    {
        Devices = null,
        Tags = new InventoryTags { Device = device, Items = [tag] },
    };

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var name in PropertyNames(property.Value))
                    {
                        yield return name;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var name in PropertyNames(item))
                    {
                        yield return name;
                    }
                }

                break;
        }
    }
}
