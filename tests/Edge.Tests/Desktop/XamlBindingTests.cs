using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.Edge.Tests.Desktop;

/// <summary>
/// The WPF window cannot run on CI (Linux), so its XAML is read as text: every <c>{Binding …}</c> path must name public
/// properties, segment by segment, of the view model it binds to (plan D-02), and the password dialog binds nothing
/// (D-06). A template, column or style binds its own items, so it must name their type with d:DataContext.
/// </summary>
public sealed partial class XamlBindingTests
{
    private const string Design = "http://schemas.microsoft.com/expression/blend/2008";

    // Each file of the window and the view model its root binds to; null: the file must bind nothing.
    public static readonly TheoryData<string, Type?> Files = new()
    {
        { "MainWindow.xaml", typeof(MainViewModel) },
        { "ChannelDialog.xaml", typeof(ChannelDialogViewModel) },
        { "DriverDialog.xaml", typeof(ChannelDialogViewModel) },
        { "DeviceDialog.xaml", typeof(DeviceDialogViewModel) },
        { "TagDialog.xaml", typeof(TagDialogViewModel) },
        { "PasswordDialog.xaml", null },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryBindingPath_NamesAPublicPropertyOfItsViewModel(string file, Type? root)
    {
        var path = Path.Combine(RepositoryRoot(), "src", "EdgeDesktop", file);
        Assert.True(File.Exists(path), $"{file} is missing");

        var xaml = XDocument.Load(path, LoadOptions.SetLineInfo);
        var problems = Check(xaml, root);

        Assert.Empty(problems);
        // A file with a view model binds it: a file that lost all its bindings would otherwise pass unchecked.
        Assert.True(root is null || xaml.Descendants().Attributes().Any(a => a.Value.TrimStart().StartsWith("{Binding", StringComparison.Ordinal)),
            $"{file} binds nothing");
    }

    [Fact]
    public void TheChecker_FindsAnUnknownPath_AnUnannotatedTemplate_AndABindingWhereNoneIsAllowed()
    {
        var xaml = XDocument.Parse("""
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                    xmlns:core="clr-namespace:IndustrialIot.EdgeDesktop.Core;assembly=IndustrialIot.EdgeDesktop.Core">
              <TextBlock Text="{Binding Monitor.EdgeId}" />
              <TextBlock Text="{Binding Monitor.EdgeIdd}" />
              <TextBlock Text="{Binding IsChecked, ElementName=Other}" />
              <TreeView ItemsSource="{Binding Tree}">
                <TreeView.ItemTemplate>
                  <HierarchicalDataTemplate d:DataContext="{d:DesignInstance core:TreeNode}" ItemsSource="{Binding Children}">
                    <TextBlock Text="{Binding Text}" />
                  </HierarchicalDataTemplate>
                </TreeView.ItemTemplate>
              </TreeView>
              <ListView ItemsSource="{Binding Tags}">
                <ListView.ItemTemplate>
                  <DataTemplate>
                    <TextBlock Text="{Binding Path=Name, Mode=OneWay}" />
                  </DataTemplate>
                </ListView.ItemTemplate>
              </ListView>
            </Window>
            """, LoadOptions.SetLineInfo);

        var problems = Check(xaml, typeof(MainViewModel));

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("Monitor.EdgeIdd", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("without d:DataContext", StringComparison.Ordinal));
        var none = Check(xaml, null);
        Assert.Equal(7, none.Count); // every binding but the ElementName one
        Assert.All(none, p => Assert.Contains("binds nothing", p, StringComparison.Ordinal));
    }

    private static List<string> Check(XDocument xaml, Type? root)
    {
        var problems = new List<string>();
        foreach (var element in xaml.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                var value = attribute.Value.Trim();
                if (!value.StartsWith("{Binding", StringComparison.Ordinal))
                {
                    continue;
                }

                var line = ((IXmlLineInfo)attribute).LineNumber;
                var arguments = Arguments(value["{Binding".Length..^1]);
                if (arguments.Any(a => a.StartsWith("ElementName=", StringComparison.Ordinal) || a.StartsWith("RelativeSource=", StringComparison.Ordinal)))
                {
                    continue; // binds another element, not the view model
                }

                if (root is null)
                {
                    problems.Add($"line {line}: {value} in a file that binds nothing");
                    continue;
                }

                var bindingPath = arguments.FirstOrDefault(a => !a.Contains('=', StringComparison.Ordinal))
                    ?? arguments.FirstOrDefault(a => a.StartsWith("Path=", StringComparison.Ordinal))?["Path=".Length..]
                    ?? "";
                if (Context(element, root, out var context) is { } unannotated)
                {
                    problems.Add($"line {line}: {unannotated} without d:DataContext");
                }
                else if (bindingPath.Length > 0 && Resolve(context!, bindingPath) is { } unknown)
                {
                    problems.Add($"line {line}: {bindingPath} ({unknown} not on {context!.Name})");
                }
            }
        }

        return problems;
    }

    /// <summary>The type the element binds to: the nearest d:DataContext, or the root's type outside any template.</summary>
    private static string? Context(XElement element, Type root, out Type? context)
    {
        for (var at = element; at is not null; at = at.Parent)
        {
            if (at.Attribute(XName.Get("DataContext", Design)) is { } annotation)
            {
                context = DesignType(at, annotation.Value);
                return context is null ? $"{at.Name.LocalName} (unknown {annotation.Value})" : null;
            }

            var name = at.Name.LocalName;
            if (name.EndsWith("Template", StringComparison.Ordinal) || name.EndsWith("Column", StringComparison.Ordinal) || name == "Style")
            {
                context = null;
                return name;
            }
        }

        context = root;
        return null;
    }

    private static Type? DesignType(XElement at, string annotation)
    {
        var match = DesignInstance().Match(annotation);
        if (!match.Success)
        {
            return null;
        }

        var ns = at.GetNamespaceOfPrefix(match.Groups["prefix"].Value)?.NamespaceName ?? "";
        var clr = ns.StartsWith("clr-namespace:", StringComparison.Ordinal) ? ns["clr-namespace:".Length..].Split(';')[0] : null;
        return clr is null ? null : typeof(MainViewModel).Assembly.GetType($"{clr}.{match.Groups["type"].Value}");
    }

    /// <summary>The first segment of <paramref name="path"/> that is not a public property, or null.</summary>
    private static string? Resolve(Type type, string path)
    {
        foreach (var segment in path.Split('.'))
        {
            var name = segment.Split('[')[0];
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
            {
                return segment;
            }

            type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (segment.Contains('[', StringComparison.Ordinal))
            {
                type = ItemType(type) ?? typeof(object);
            }
        }

        return null;
    }

    // An indexer's value: TValue of a dictionary, T of a list.
    private static Type? ItemType(Type type)
    {
        var interfaces = type.IsInterface ? [type, .. type.GetInterfaces()] : type.GetInterfaces();
        var dictionary = interfaces.FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
        if (dictionary is not null)
        {
            return dictionary.GetGenericArguments()[1];
        }

        return interfaces.FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
    }

    /// <summary>The comma-separated arguments of a markup extension, keeping quoted and nested parts whole.</summary>
    private static List<string> Arguments(string text)
    {
        var arguments = new List<string>();
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\'':
                    quoted = !quoted;
                    break;
                case '{' when !quoted:
                    depth++;
                    break;
                case '}' when !quoted:
                    depth--;
                    break;
                case ',' when !quoted && depth == 0:
                    arguments.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        arguments.Add(text[start..].Trim());
        return [.. arguments.Where(a => a.Length > 0)];
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "IndustrialIot.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("IndustrialIot.sln not found above the test output.");
    }

    [GeneratedRegex(@"DesignInstance\s+(?:Type=)?(?<prefix>\w+):(?<type>\w+)")]
    private static partial Regex DesignInstance();
}
