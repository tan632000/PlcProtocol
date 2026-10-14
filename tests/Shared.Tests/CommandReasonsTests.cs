using System.Text.RegularExpressions;
using IndustrialIot.Shared.Common;

namespace IndustrialIot.Shared.Tests;

public class CommandReasonsTests
{
    [Fact]
    public void All_ExactSetUniqueSnakeCase()
    {
        // The agreed list (task-02). Removing or renaming a code must break this test.
        string[] expected =
        [
            "unknown_tag",
            "tag_not_writable",
            "input_area_not_writable",
            "invalid_value",
            "value_out_of_range",
            "edge_mismatch",
            "clock_skew",
            "plc_unavailable",
            "plc_busy",
            "plc_access_denied",
            "write_outcome_unknown",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), CommandReasons.All.Order(StringComparer.Ordinal));
        Assert.Equal(CommandReasons.All.Count, CommandReasons.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(CommandReasons.All, code => Assert.Matches(new Regex(@"^[a-z][a-z0-9_]{0,63}\z"), code));
    }
}
