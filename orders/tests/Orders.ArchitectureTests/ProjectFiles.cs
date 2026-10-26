using System.Xml.Linq;

namespace Orders.ArchitectureTests;

/// <summary>Reads the declared references of a project from its .csproj, so the rules also catch a reference no code uses yet.</summary>
internal static class ProjectFiles
{
    public static IReadOnlyCollection<string> ProjectReferences(string project) => Includes(project, "ProjectReference")
        .Select(path => Path.GetFileNameWithoutExtension(path.Replace('\\', '/')))
        .ToList();

    public static IReadOnlyCollection<string> PackageReferences(string project) => Includes(project, "PackageReference").ToList();

    public static IReadOnlyCollection<string> FrameworkReferences(string project) => Includes(project, "FrameworkReference").ToList();

    private static IEnumerable<string> Includes(string project, string element) =>
        XDocument.Load(Locate(project)).Descendants(element)
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>();

    private static string Locate(string project)
    {
        var solutionDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (solutionDirectory is not null && !solutionDirectory.EnumerateFiles("*.slnx").Any())
        {
            solutionDirectory = solutionDirectory.Parent;
        }

        solutionDirectory.ShouldNotBeNull("no .slnx found above the test output directory");

        string[] candidates =
        [
            Path.Combine(solutionDirectory.FullName, "src", project, $"{project}.csproj"),
            Path.Combine(solutionDirectory.Parent!.FullName, "shared", project, $"{project}.csproj"),
        ];

        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException($"Project file for {project} not found.", project);
    }
}
