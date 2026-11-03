using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialIot.Edge.Configuration;
using IndustrialIot.Edge.Settings;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Hosting;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// A PLC's tags as CSV for a spreadsheet: one formatter and one parser, so a file exported and imported again gives the
/// same tags; errors name the line a record starts on and the column, never the cell; and no text cell can run as a
/// formula when the file is opened in Excel.
/// </summary>
public sealed class TagCsvTests : IAsyncDisposable
{
    private const string Header = "Tag Name,Address,Data Type,Client Access,Description,Min,Max,Length";
    private readonly int _port = FreePort();
    private EdgeTestHost? _edge;
    private HttpClient? _http;

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_edge is not null)
        {
            await _edge.Host.StopAsync();
            await _edge.DisposeAsync();
        }
    }

    [Fact]
    public void ExportThenImport_GivesTheSameTags()
    {
        string[] dataTypes = ["Bool", "Byte", "Word", "Int", "DWord", "DInt", "Real", "String", "LReal", "DTL"];
        var tags = new List<DeviceTagOptions>();
        tags.AddRange(dataTypes.Select((type, i) => Tag($"T{i}", $"DB1.DBW{i * 2}", type,
            length: type == "String" ? 20 : null, writable: i % 2 == 0)));
        tags.Add(Tag("Limits", "DB2.DBW0", "Int", min: -1500, max: 1500.50m));
        tags.Add(Tag("Comma", "DB2.DBW2", description: "speed, belt 1"));
        tags.Add(Tag("Quote", "DB2.DBW4", description: "the \"main\" line"));
        tags.Add(Tag("Text", "DB2.DBW6", description: "Tốc độ băng tải 😀\r\nline two"));
        tags.Add(Tag("Eq", "DB2.DBW8", description: "=HYPERLINK(\"http://x\",\"click\")"));
        tags.Add(Tag("Plus", "DB2.DBW10", description: "+1"));
        tags.Add(Tag("Minus", "DB2.DBW12", description: "-cmd"));
        tags.Add(Tag("At", "DB2.DBW14", description: "@SUM(A1)"));
        tags.Add(Tag("Apostrophe", "DB2.DBW16", description: "'=x"));
        tags.Add(Tag("Apostrophes", "DB2.DBW18", description: "''=x"));
        tags.Add(Tag("Spaces", "  DB2.DBW20 ", description: "  padded  "));
        tags.Add(Tag("Empty", "DB2.DBW22", description: ""));

        var csv = TagCsv.Format(tags);
        var parsed = TagCsv.Parse(csv);

        Assert.Empty(parsed.Errors);
        Assert.Equal(tags.Count, parsed.Tags!.Count);
        for (var i = 0; i < tags.Count; i++)
        {
            var (before, after) = (tags[i], parsed.Tags[i]);
            Assert.Equal(before.Name, after.Name);
            Assert.Equal(before.Address, after.Address);
            Assert.Equal(before.DataType, after.DataType);
            Assert.Equal(before.Writable, after.Writable);
            Assert.Equal(string.IsNullOrEmpty(before.Description) ? null : before.Description, after.Description);
            Assert.Equal(before.Min, after.Min);
            Assert.Equal(before.Max, after.Max);
            Assert.Equal(before.Length, after.Length);
        }
    }

    [Fact]
    public void Export_StartsNoTextCellWithAFormulaCharacter()
    {
        var csv = TagCsv.Format([
            Tag("=Name", "+DB1.DBW0", description: "=1+1"),
            Tag("Neg", "DB1.DBW2", min: -5, description: "@x"),
            Tag("Tab", "DB1.DBW4", description: "\tx"),
        ]);

        var cells = Records(csv).Skip(1).SelectMany(r => new[] { r[0], r[1], r[4] }).ToList();

        Assert.All(cells, cell => Assert.DoesNotMatch("^[=+\\-@\\t\\r]", cell));
        Assert.Equal("-5", Records(csv)[2][5]); // a negative Min stays a plain number
    }

    [Fact]
    public void Export_HasTheHeader_CrLf_NoBom_AndQuotesOnlyWhereNeeded()
    {
        var csv = TagCsv.Format([Tag("Speed", "DB1.DBW0", writable: true), Tag("Note", "DB1.DBW2", description: "a,b")]);

        Assert.StartsWith(Header + "\r\n", csv, StringComparison.Ordinal);
        Assert.False(csv.StartsWith('\uFEFF'));
        Assert.Contains("\r\nSpeed,DB1.DBW0,Int,Read/Write,,,,\r\n", csv, StringComparison.Ordinal);
        Assert.Contains("\r\nNote,DB1.DBW2,Int,Read Only,\"a,b\",,,\r\n", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", csv.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void AnError_NamesThePhysicalLineTheRecordStartsOn_AndTheColumn()
    {
        // Line 2 is a record whose description spans lines 2-4; the bad record starts on line 5.
        var csv = Header + "\r\n"
            + "Ok,DB1.DBW0,Int,Read Only,\"one, \"\"two\"\"\r\nthree\r\nfour\",,,\r\n"
            + "Bad,DB1.DBW2,Foo,Read Only,,,,\r\n";

        var parsed = TagCsv.Parse(csv);

        var error = Assert.Single(parsed.Errors);
        Assert.Equal((5, "Data Type"), (error.Line, error.Column));
        Assert.Null(parsed.Tags);
        Assert.DoesNotContain("Foo", error.Message);
    }

    public static TheoryData<string, int, string> BadFiles => new()
    {
        { "Name,Address\r\nA,DB1.DBW0\r\n", 1, "Tag Name" },
        { "Tag Name,Address,Data Type,Client Access,Description,Max,Min,Length\r\n", 1, "Min" },
        { Header + "\r\nA,DB1.DBW0,Int,RW,,,,\r\n", 2, "Client Access" },
        { Header + "\r\nA,DB1.DBW0,Int,Read Only,,ten,,\r\n", 2, "Min" },
        { Header + "\r\nA,DB1.DBW0,String,Read Only,,,,1.5\r\n", 2, "Length" },
        { Header + "\r\n,DB1.DBW0,Int,Read Only,,,,\r\n", 2, "Tag Name" },
        { Header + "\r\nA,DB1.DBW0,Int,Read Only\r\n", 2, "Description" },
        { Header + "\r\nA,DB1.DBW0,,Read Only,,,,\r\n", 2, "Data Type" },
        { Header + "\r\nA,DB\"1,Int,Read Only,,,,\r\n", 2, "Address" },
        { Header + "\r\nA,\"DB1\r\n", 2, "Address" },
        { "", 1, "Tag Name" },
    };

    [Theory]
    [MemberData(nameof(BadFiles))]
    public void ABadFile_IsRefused_NamingLineAndColumn(string csv, int line, string column)
    {
        var parsed = TagCsv.Parse(csv);

        Assert.Null(parsed.Tags);
        Assert.Contains(parsed.Errors, e => e.Line == line && e.Column == column);
    }

    [Fact]
    public void BomLfAndHeaderSpellings_ParseLikeTheExport()
    {
        var crlf = Header + "\r\nA,DB1.DBW0,int,read only,,1,2,\r\n";
        var lf = "\uFEFF tag name , ADDRESS,data type,Client access,description,min,MAX,length\nA,DB1.DBW0,int,read only,,1,2,\n\n\n";

        var a = TagCsv.Parse(crlf);
        var b = TagCsv.Parse(lf);

        Assert.Empty(a.Errors);
        Assert.Empty(b.Errors);
        Assert.Equal(JsonSerializer.Serialize(a.Tags), JsonSerializer.Serialize(b.Tags));
        Assert.Equal("int", a.Tags![0].DataType); // kept as written; validate decides
        Assert.False(a.Tags[0].Writable);
    }

    [Fact]
    public async Task Routes_FormatAndParse_ThroughTheMonitor()
    {
        var http = await StartAsync();
        var tags = new JsonArray(new JsonObject { ["name"] = "Speed", ["address"] = "DB1.DBW0", ["dataType"] = "Int", ["writable"] = true, ["description"] = "=x" });

        using var formatted = await PostAsync(http, "/api/settings/csv/format", new JsonObject { ["tags"] = tags }.ToJsonString());
        var csv = JsonDocument.Parse(await formatted.Content.ReadAsStringAsync()).RootElement.GetProperty("csv").GetString()!;
        using var parsed = await PostAsync(http, "/api/settings/csv/parse", new JsonObject { ["csv"] = csv }.ToJsonString());
        var back = JsonDocument.Parse(await parsed.Content.ReadAsStringAsync()).RootElement;
        using var bad = await PostAsync(http, "/api/settings/csv/parse", new JsonObject { ["csv"] = Header + "\r\nA,DB1,Foo,Read Only,,,,\r\n" }.ToJsonString());
        var errors = JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("errors");

        Assert.Equal(HttpStatusCode.OK, formatted.StatusCode);
        Assert.Contains("'=x", csv, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, parsed.StatusCode);
        var tag = Assert.Single(back.GetProperty("tags").EnumerateArray());
        Assert.Equal("Speed", tag.GetProperty("name").GetString());
        Assert.True(tag.GetProperty("writable").GetBoolean());
        Assert.Equal("=x", tag.GetProperty("description").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(2, errors[0].GetProperty("line").GetInt32());
        Assert.Equal("Data Type", errors[0].GetProperty("column").GetString());
    }

    [Theory]
    [InlineData("/api/settings/csv/format")]
    [InlineData("/api/settings/csv/parse")]
    public async Task Routes_RefuseACrossSiteRequest(string path)
    {
        var http = await StartAsync();

        using var response = await PostAsync(http, path, "{}", origin: "http://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static DeviceTagOptions Tag(
        string name, string address, string dataType = "Int", int? length = null, bool writable = false,
        decimal? min = null, decimal? max = null, string? description = null) => new()
    {
        Name = name,
        Address = address,
        DataType = dataType,
        Length = length,
        Writable = writable,
        Min = min,
        Max = max,
        Description = description,
    };

    /// <summary>A minimal RFC 4180 reader for assertions on the exported text.</summary>
    private static List<List<string>> Records(string csv)
    {
        var records = new List<List<string>> { new() };
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                records[^1].Add(cell.ToString());
                cell.Clear();
            }
            else if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
            {
                records[^1].Add(cell.ToString());
                cell.Clear();
                records.Add([]);
                i++;
            }
            else
            {
                cell.Append(c);
            }
        }

        return [.. records.Where(r => r.Count > 0)];
    }

    private async Task<HttpClient> StartAsync()
    {
        _edge = EdgeTestHost.Create(
            new Dictionary<string, string?>
            {
                ["Edge:Monitor:Enabled"] = "true",
                ["Edge:Monitor:Port"] = _port.ToString(CultureInfo.InvariantCulture),
            },
            services => EdgeTestHost.WithoutHostedService<Polling.TagPoller>(services),
            runAgentServices: true);
        await _edge.Host.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };
        return _http;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, string path, string body, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return await http.SendAsync(request);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
