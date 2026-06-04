using System.Collections;
using System.Reflection;
using LiCvWriter.Application.Models;

namespace LiCvWriter.Infrastructure.Foundry;

internal static class FoundryCatalogMetadataReader
{
    public static IReadOnlyList<FoundryExecutionProviderInfo> MapExecutionProviders(IEnumerable? rawExecutionProviders)
    {
        if (rawExecutionProviders is null)
        {
            return [];
        }

        var executionProviders = new List<FoundryExecutionProviderInfo>();
        foreach (var rawExecutionProvider in rawExecutionProviders)
        {
            if (rawExecutionProvider is null)
            {
                continue;
            }

            var executionProviderType = rawExecutionProvider.GetType();
            var name = ReadStringProperty(executionProviderType, rawExecutionProvider, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var displayName = ReadStringProperty(executionProviderType, rawExecutionProvider, "DisplayName");
            executionProviders.Add(new FoundryExecutionProviderInfo(
                name,
                string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                ReadBooleanProperty(executionProviderType, rawExecutionProvider, "IsRegistered")));
        }

        return executionProviders
            .OrderByDescending(static executionProvider => executionProvider.IsRegistered)
            .ThenBy(static executionProvider => executionProvider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? ReadModelDescription(object modelInfo)
    {
        var type = modelInfo.GetType();
        foreach (var propertyName in new[] { "Description", "Summary", "ShortDescription" })
        {
            var value = ReadStringProperty(type, modelInfo, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    public static IReadOnlyList<string> ReadModelMetadata(object modelInfo)
    {
        var type = modelInfo.GetType();
        var metadata = new List<string>();

        foreach (var propertyName in new[] { "Task", "Tasks", "Modality", "Modalities", "Capability", "Capabilities" })
        {
            metadata.AddRange(ReadPropertyValues(type, modelInfo, propertyName));
        }

        return metadata;
    }

    private static bool ReadBooleanProperty(Type type, object instance, string propertyName)
        => type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance) as bool? ?? false;

    private static string ReadStringProperty(Type type, object instance, string propertyName)
        => type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance)?.ToString() ?? string.Empty;

    private static IReadOnlyList<string> ReadPropertyValues(Type type, object instance, string propertyName)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null)
        {
            return Array.Empty<string>();
        }

        var value = property.GetValue(instance);
        return value switch
        {
            null => Array.Empty<string>(),
            string text when !string.IsNullOrWhiteSpace(text) => [text.Trim()],
            IEnumerable sequence when value is not string => sequence
                .Cast<object?>()
                .Select(static item => item?.ToString())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item!.Trim())
                .ToArray(),
            _ => value.ToString() is { } text && !string.IsNullOrWhiteSpace(text)
                ? [text.Trim()]
                : Array.Empty<string>()
        };
    }
}