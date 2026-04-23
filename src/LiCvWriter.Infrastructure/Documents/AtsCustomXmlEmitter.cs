using System.Text;
using System.Xml;
using DocumentFormat.OpenXml.CustomXmlDataProperties;
using DocumentFormat.OpenXml.Packaging;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Infrastructure.Documents;

/// <summary>
/// Attaches a public-safe candidate snapshot as a <see cref="CustomXmlPart"/>
/// inside an exported <c>.docx</c>. The XML payload uses a stable namespace
/// and element schema so ATS / LLM-based CV parsers can consume structured
/// candidate fields directly without re-parsing the rendered document body.
/// </summary>
/// <remarks>
/// Schema namespace: <c>urn:licvwriter:cv:v1</c>.
/// Deliberately excludes any internal assessment data — fit scores, gaps,
/// model names, prompt content — because the host document is shipped
/// externally.
/// </remarks>
internal static class AtsCustomXmlEmitter
{
    private const string Namespace = "urn:licvwriter:cv:v1";

    /// <summary>
    /// Emits the snapshot into a new <see cref="CustomXmlPart"/> on
    /// <paramref name="document"/>. No-ops when <paramref name="snapshot"/>
    /// is <see langword="null"/>.
    /// </summary>
    public static void Attach(WordprocessingDocument document, AtsCandidateSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (snapshot is null)
        {
            return;
        }

        var mainPart = document.MainDocumentPart
            ?? throw new InvalidOperationException("Document is missing its main part.");

        var customXmlPart = mainPart.AddCustomXmlPart(CustomXmlPartType.CustomXml);
        var propertiesPart = customXmlPart.AddNewPart<CustomXmlPropertiesPart>();

        var xml = BuildXml(snapshot);
        using (var stream = customXmlPart.GetStream(FileMode.Create))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(xml);
        }

        // The properties part declares the schema namespace so consumers can
        // discover the part by its <ds:uri/> rather than having to read every
        // CustomXmlPart in the package.
        propertiesPart.DataStoreItem = new DataStoreItem
        {
            // Word's data-store schema requires the itemID to be a brace-wrapped,
            // upper-case-hex GUID — uppercase via "B" format specifier + ToUpperInvariant.
            ItemId = Guid.NewGuid().ToString("B").ToUpperInvariant(),
            SchemaReferences = new SchemaReferences(new SchemaReference { Uri = Namespace })
        };
        propertiesPart.DataStoreItem.Save();
    }

    internal static string BuildXml(AtsCandidateSnapshot snapshot)
    {
        var settings = new XmlWriterSettings
        {
            Indent = false,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = false
        };

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("candidateData", Namespace);

            WriteText(writer, "name", snapshot.FullName);
            WriteOptional(writer, "headline", snapshot.Headline);

            writer.WriteStartElement("contact", Namespace);
            WriteOptional(writer, "email", snapshot.Contact?.Email);
            WriteOptional(writer, "phone", snapshot.Contact?.Phone);
            WriteOptional(writer, "linkedIn", snapshot.Contact?.LinkedInUrl);
            WriteOptional(writer, "city", snapshot.Contact?.City);
            writer.WriteEndElement(); // contact

            writer.WriteStartElement("targetRole", Namespace);
            WriteOptional(writer, "title", snapshot.TargetRoleTitle);
            WriteOptional(writer, "company", snapshot.TargetCompanyName);
            writer.WriteEndElement(); // targetRole

            writer.WriteStartElement("skills", Namespace);
            writer.WriteString(string.Join(", ", snapshot.Skills));
            writer.WriteEndElement();

            writer.WriteStartElement("experience", Namespace);
            foreach (var role in snapshot.Experience)
            {
                writer.WriteStartElement("role", Namespace);
                WriteText(writer, "title", role.Title);
                WriteText(writer, "company", role.Company);
                WriteOptional(writer, "period", role.Period);
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); // experience

            writer.WriteStartElement("education", Namespace);
            foreach (var entry in snapshot.Education)
            {
                writer.WriteStartElement("entry", Namespace);
                WriteOptional(writer, "degree", entry.Degree);
                WriteText(writer, "institution", entry.Institution);
                WriteOptional(writer, "period", entry.Period);
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); // education

            writer.WriteStartElement("certifications", Namespace);
            foreach (var cert in snapshot.Certifications)
            {
                WriteText(writer, "entry", cert);
            }
            writer.WriteEndElement(); // certifications

            writer.WriteStartElement("languages", Namespace);
            foreach (var lang in snapshot.Languages)
            {
                writer.WriteStartElement("entry", Namespace);
                WriteText(writer, "language", lang.Language);
                WriteOptional(writer, "level", lang.Level);
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); // languages

            writer.WriteEndElement(); // candidateData
            writer.WriteEndDocument();
        }

        return sb.ToString();
    }

    private static void WriteText(XmlWriter writer, string localName, string value)
    {
        writer.WriteStartElement(localName, Namespace);
        writer.WriteString(value ?? string.Empty);
        writer.WriteEndElement();
    }

    private static void WriteOptional(XmlWriter writer, string localName, string? value)
    {
        writer.WriteStartElement(localName, Namespace);
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(value);
        }
        writer.WriteEndElement();
    }
}
