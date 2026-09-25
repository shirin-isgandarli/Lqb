using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Lqb.Design;

/// <summary>
/// Writes one Liquibase "formatted SQL" changeset per run and includes it in the
/// XML master changelog, so `liquibase update` picks it up in order.
/// </summary>
internal static class ChangelogWriter
{
    private const string MasterFileName = "db.changelog-master.xml";
    private const string LegacyYamlMasterFileName = "db.changelog-master.yaml";

    private static readonly XNamespace Ns = "http://www.liquibase.org/xml/ns/dbchangelog";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public static string Write(string changelogDir, string id, string author, ScaffoldResult result)
    {
        var changesDir = Path.Combine(changelogDir, "changes");
        Directory.CreateDirectory(changesDir);

        var fileName = id + ".sql";
        var path = Path.Combine(changesDir, fileName);
        File.WriteAllText(path, BuildChangeset(id, author, result));

        AppendToMaster(changelogDir, "changes/" + fileName);
        return path;
    }

    private static string BuildChangeset(string id, string author, ScaffoldResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--liquibase formatted sql");
        sb.AppendLine();

        // splitStatements:false: EF's SQL can contain multi-statement blocks (for example
        // SQL Server's DECLARE ... for dropping default constraints) that must not be split on ';'.
        sb.Append($"--changeset {Sanitize(author)}:{id} splitStatements:false");
        if (!result.RunInTransaction) sb.Append(" runInTransaction:false");
        sb.AppendLine();

        sb.AppendLine(result.UpSql);

        foreach (var line in result.DownSql.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0) sb.AppendLine("--rollback " + trimmed);
        }

        return sb.ToString();
    }

    private static void AppendToMaster(string changelogDir, string relativePath)
    {
        var masterPath = Path.Combine(changelogDir, MasterFileName);
        var legacyYamlPath = Path.Combine(changelogDir, LegacyYamlMasterFileName);
        var convertFromYaml = !File.Exists(masterPath) && File.Exists(legacyYamlPath);

        var document = File.Exists(masterPath) ? XDocument.Load(masterPath) : CreateMaster();
        var root = document.Root;
        if (root is null || root.Name != Ns + "databaseChangeLog")
        {
            throw new LqbException($"{masterPath} isn't a Liquibase changelog (expected a <databaseChangeLog> root).");
        }

        // Version 0.1.0 wrote a YAML master; carry its includes over in the same order.
        if (convertFromYaml)
        {
            var yaml = File.ReadAllText(legacyYamlPath);
            foreach (Match match in Regex.Matches(yaml, @"^\s*file:\s*(\S+)\s*$", RegexOptions.Multiline))
            {
                AddInclude(root, match.Groups[1].Value);
            }
        }

        AddInclude(root, relativePath);

        var settings = new XmlWriterSettings { Indent = true, IndentChars = "    ", Encoding = new UTF8Encoding(false) };
        using (var writer = XmlWriter.Create(masterPath, settings))
        {
            document.Save(writer);
        }

        if (convertFromYaml)
        {
            File.Delete(legacyYamlPath);
            Console.WriteLine($"Converted {LegacyYamlMasterFileName} to {MasterFileName}.");
        }
    }

    private static void AddInclude(XElement root, string relativePath)
    {
        var alreadyIncluded = root.Elements(Ns + "include").Any(e => (string?)e.Attribute("file") == relativePath);
        if (alreadyIncluded) return;

        root.Add(new XElement(Ns + "include",
            new XAttribute("file", relativePath),
            new XAttribute("relativeToChangelogFile", "true")));
    }

    private static XDocument CreateMaster() =>
        new(new XDeclaration("1.0", "UTF-8", null),
            new XElement(Ns + "databaseChangeLog",
                new XAttribute("xmlns", Ns.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
                new XAttribute(Xsi + "schemaLocation",
                    "http://www.liquibase.org/xml/ns/dbchangelog " +
                    "http://www.liquibase.org/xml/ns/dbchangelog/dbchangelog-latest.xsd")));

    // Liquibase reads "author:id" from the header, so spaces and colons would break it.
    private static string Sanitize(string author) => Regex.Replace(author.Trim(), @"[\s:]+", "_");
}
