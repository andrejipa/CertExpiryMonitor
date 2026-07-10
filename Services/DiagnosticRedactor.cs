using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CertExpiryMonitor.Services;

internal static class DiagnosticRedactor
{
    private static readonly Regex LongHexRegex = new(@"\b[0-9A-Fa-f]{40,}\b", RegexOptions.Compiled);
    private static readonly Regex DocumentLikeRegex = new(@"\d[\d.\-/\s]{9,}\d", RegexOptions.Compiled);
    private static readonly Regex SecretAssignmentRegex = new(
        @"\b(password|senha|pfx|private\s+key|chave\s+privada)\b\s*[:=]\s*[^,\r\n;\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PfxPathRegex = new(
        @"(?i)\b(?:[A-Z]:\\|\\\\)?[^\s,;:""]+\.pfx\b",
        RegexOptions.Compiled);
    private static readonly Regex WindowsUserProfilePathRegex = new(
        @"(?i)\b(?<drive>[A-Z]:\\Users\\)(?<user>[^\\\r\n,;:""]+)(?<suffix>\\[^\r\n,;""]*)?",
        RegexOptions.Compiled);
    private static readonly Regex ThumbprintAssignmentRegex = new(
        @"(?i)\b(?<name>thumb(?:print)?)\s*[:=]\s*[0-9A-F]{6,40}\b",
        RegexOptions.Compiled);
    private static readonly Regex CertificatePrefixRegex = new(
        @"(?i)\b(?<label>certificate|certificado)\s+[0-9A-F]{6,16}\b",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] SecretNameFragments =
    [
        "password",
        "senha",
        "pfx",
        "private",
        "privada",
        "key",
        "chave"
    ];

    private static readonly string[] NameFragments =
    [
        "subject",
        "simpleName",
        "holder",
        "titular",
        "name",
        "nome"
    ];

    public static string HashThumbprint(string? thumbprint)
    {
        var normalized = JsonStateStore.NormalizeThumbprint(thumbprint ?? string.Empty);
        if (normalized.Length == 0) return string.Empty;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash);
    }

    public static string SerialPrefix(string? serialNumber)
    {
        if (string.IsNullOrWhiteSpace(serialNumber)) return string.Empty;

        var normalized = new string(serialNumber
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToUpperInvariant();

        return normalized.Length <= 8 ? normalized : normalized[..8];
    }

    public static string DocumentLast4(string? document)
    {
        if (string.IsNullOrWhiteSpace(document)) return string.Empty;

        var digits = new string(document.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return string.Empty;
        return digits.Length <= 4 ? digits : digits[^4..];
    }

    public static string? RedactDetailsToJson(object? details)
    {
        if (details is null)
        {
            return null;
        }

        try
        {
            var json = JsonSerializer.Serialize(details, JsonOptions);
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                WriteRedactedElement(document.RootElement, writer, propertyName: null);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return """{"serialization":"failed"}""";
        }
    }

    public static string RedactText(string? value)
    {
        return RedactFreeText(value ?? string.Empty);
    }

    private static void WriteRedactedElement(JsonElement element, Utf8JsonWriter writer, string? propertyName)
    {
        if (propertyName is not null && IsSecretProperty(propertyName))
        {
            writer.WriteStringValue("(redacted)");
            return;
        }

        if (propertyName is not null && IsThumbprintProperty(propertyName))
        {
            writer.WriteStringValue($"sha256:{HashThumbprint(ReadElementAsString(element))}");
            return;
        }

        if (propertyName is not null && IsSerialProperty(propertyName))
        {
            writer.WriteStringValue(SerialPrefix(ReadElementAsString(element)));
            return;
        }

        if (propertyName is not null && IsDocumentProperty(propertyName))
        {
            var last4 = DocumentLast4(ReadElementAsString(element));
            writer.WriteStringValue(last4.Length == 0 ? "(redacted)" : $"last4:{last4}");
            return;
        }

        if (propertyName is not null && IsNameProperty(propertyName))
        {
            writer.WriteStringValue("(redacted)");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedactedElement(property.Value, writer, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedactedElement(item, writer, propertyName: null);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactFreeText(element.GetString() ?? string.Empty));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string RedactFreeText(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        value = SecretAssignmentRegex.Replace(value, match => $"{match.Groups[1].Value}=(redacted)");
        value = PfxPathRegex.Replace(value, "(redacted-pfx)");
        value = WindowsUserProfilePathRegex.Replace(value, match =>
            $"{match.Groups["drive"].Value}(redacted){match.Groups["suffix"].Value}");
        value = ThumbprintAssignmentRegex.Replace(value, match => $"{match.Groups["name"].Value}=(redacted-thumbprint)");
        value = CertificatePrefixRegex.Replace(value, match => $"{match.Groups["label"].Value} (redacted-thumbprint-prefix)");

        var normalized = JsonStateStore.NormalizeThumbprint(value);
        if (normalized.Length >= 40 && normalized.All(Uri.IsHexDigit))
        {
            return $"sha256:{HashThumbprint(normalized)}";
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length is 11 or 14 && IsOnlyDocumentText(value))
        {
            return $"document_last4:{DocumentLast4(digits)}";
        }

        value = LongHexRegex.Replace(value, match => $"sha256:{HashThumbprint(match.Value)}");
        return DocumentLikeRegex.Replace(value, match =>
        {
            var matchDigits = new string(match.Value.Where(char.IsDigit).ToArray());
            return matchDigits.Length is 11 or 14
                ? $"document_last4:{DocumentLast4(matchDigits)}"
                : match.Value;
        });
    }

    private static string ReadElementAsString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        };
    }

    private static bool IsOnlyDocumentText(string value)
        => value.All(c => char.IsDigit(c) || c is '.' or '-' or '/' || char.IsWhiteSpace(c));

    private static bool IsSecretProperty(string propertyName)
        => SecretNameFragments.Any(fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool IsThumbprintProperty(string propertyName)
        => propertyName.Contains("thumbprint", StringComparison.OrdinalIgnoreCase);

    private static bool IsSerialProperty(string propertyName)
        => propertyName.Contains("serial", StringComparison.OrdinalIgnoreCase);

    private static bool IsDocumentProperty(string propertyName)
        => propertyName.Contains("document", StringComparison.OrdinalIgnoreCase) ||
           propertyName.Contains("cpf", StringComparison.OrdinalIgnoreCase) ||
           propertyName.Contains("cnpj", StringComparison.OrdinalIgnoreCase);

    private static bool IsNameProperty(string propertyName)
        => NameFragments.Any(fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
