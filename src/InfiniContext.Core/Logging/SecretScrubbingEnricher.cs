using System.Text;
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace InfiniContext.Core.Logging;

/// <summary>
/// Serilog enricher that redacts secret-shaped property names and truncates oversized string values.
///
/// Redaction: any property whose <c>Name</c> matches <c>^.*(KEY|TOKEN|SECRET)$</c> (case-insensitive)
/// has its scalar value replaced with <c>&lt;redacted&gt;</c>. Recurses into <see cref="StructureValue"/>,
/// <see cref="DictionaryValue"/>, and <see cref="SequenceValue"/> so nested fields and env-var bags are
/// covered too. Pattern matches the design doc §7.4 contract.
///
/// Truncation: any string scalar whose UTF-8 length exceeds <see cref="MaxFieldBytes"/> is replaced with
/// a head fragment plus <c>&lt;… N bytes elided&gt;</c>. Keeps log files human-scannable; the full payload
/// is still recoverable from claude's own jsonl store.
/// </summary>
public sealed class SecretScrubbingEnricher : ILogEventEnricher
{
    public const int DefaultMaxFieldBytes = 4096;
    private const string RedactedMarker = "<redacted>";

    private static readonly Regex SecretPattern = new(
        // Property names that end with a credential word. Original contract was
        // "KEY|TOKEN|SECRET"; expanded with "PASSWORD|PASSWD|PWD|AUTH|AUTHORIZATION|
        // BEARER|COOKIE|CREDENTIAL|CREDENTIALS" to close well-known gaps reported in the
        // Stage 1 review. Stays anchored ends-with to avoid false positives like
        // "Cwd" or "kettle".
        @"^.*(KEY|TOKEN|SECRET|PASSWORD|PASSWD|PWD|AUTH|AUTHORIZATION|BEARER|COOKIE|CREDENTIAL|CREDENTIALS)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Value-shape patterns that look like secrets regardless of property name. Catches
    // ExtraArgs items like "--header=Authorization: Bearer eyJ..." that wouldn't otherwise scrub.
    private static readonly Regex ValueLooksLikeSecret = new(
        @"(sk-[A-Za-z0-9_-]{16,})|" +                       // Anthropic / OpenAI-style API keys
        @"(eyJ[A-Za-z0-9_=-]+\.eyJ[A-Za-z0-9_=-]+\.[A-Za-z0-9_=-]+)|" + // JWT (3 dot-separated b64 segments)
        @"(?i:Bearer\s+[A-Za-z0-9._=-]{8,})|" +              // Bearer <token>
        @"(?i:Authorization\s*[:=]\s*\S{8,})",               // Authorization: <anything-of-length>
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly int _maxFieldBytes;

    public SecretScrubbingEnricher(int maxFieldBytes = DefaultMaxFieldBytes)
    {
        if (maxFieldBytes < 64) throw new ArgumentOutOfRangeException(nameof(maxFieldBytes));
        _maxFieldBytes = maxFieldBytes;
    }

    /// <summary>Maximum UTF-8 length of any single string scalar before truncation kicks in.</summary>
    public int MaxFieldBytes => _maxFieldBytes;

    /// <summary>Public for unit tests; case-insensitive match against the standard pattern.</summary>
    public static bool IsSecretPropertyName(string name) =>
        !string.IsNullOrEmpty(name) && SecretPattern.IsMatch(name);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // logEvent.Properties is a read-only view backed by a mutable dictionary; we replace via
        // AddOrUpdateProperty. Snapshot the keys first because we mutate during iteration.
        var keys = new string[logEvent.Properties.Count];
        var i = 0;
        foreach (var kv in logEvent.Properties) keys[i++] = kv.Key;

        foreach (var key in keys)
        {
            var value = logEvent.Properties[key];
            var transformed = Transform(key, value);
            if (!ReferenceEquals(transformed, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(key, transformed));
            }
        }
    }

    private LogEventPropertyValue Transform(string nameInScope, LogEventPropertyValue value, bool inSecretContext = false)
    {
        var nameIsSecret = inSecretContext || IsSecretPropertyName(nameInScope);
        return value switch
        {
            ScalarValue sv => TransformScalar(sv, nameIsSecret),
            SequenceValue seq => TransformSequence(seq, nameIsSecret),
            StructureValue st => TransformStructure(st, nameIsSecret),
            DictionaryValue dv => TransformDictionary(dv, nameIsSecret),
            _ => value,
        };
    }

    private LogEventPropertyValue TransformScalar(ScalarValue sv, bool nameIsSecret)
    {
        if (nameIsSecret && sv.Value is not null) return new ScalarValue(RedactedMarker);
        if (sv.Value is string s)
        {
            // Value-shape detection: scrub strings that look like secrets even when the property
            // name doesn't (e.g. argv items like "--header=Authorization: Bearer …").
            if (ValueLooksLikeSecret.IsMatch(s))
            {
                return new ScalarValue(ValueLooksLikeSecret.Replace(s, RedactedMarker));
            }
            var bytes = Encoding.UTF8.GetByteCount(s);
            if (bytes > _maxFieldBytes)
            {
                var headBytes = Math.Max(0, _maxFieldBytes - 32);
                var head = TruncateUtf8(s, headBytes);
                return new ScalarValue($"{head}<… {bytes} bytes elided>");
            }
        }
        return sv;
    }

    private SequenceValue TransformSequence(SequenceValue seq, bool nameIsSecret)
    {
        if (nameIsSecret)
        {
            var redacted = new LogEventPropertyValue[seq.Elements.Count];
            for (var i = 0; i < seq.Elements.Count; i++) redacted[i] = new ScalarValue(RedactedMarker);
            return new SequenceValue(redacted);
        }

        var changed = false;
        var items = new LogEventPropertyValue[seq.Elements.Count];
        for (var i = 0; i < seq.Elements.Count; i++)
        {
            items[i] = Transform("", seq.Elements[i]);
            if (!ReferenceEquals(items[i], seq.Elements[i])) changed = true;
        }
        return changed ? new SequenceValue(items) : seq;
    }

    private StructureValue TransformStructure(StructureValue st, bool nameIsSecret)
    {
        var changed = false;
        var props = new LogEventProperty[st.Properties.Count];
        for (var i = 0; i < st.Properties.Count; i++)
        {
            var p = st.Properties[i];
            // Inside a secret-named structure (e.g. "ApiKey" -> { "value": "..." }) every leaf is suspect.
            var newVal = Transform(p.Name, p.Value, inSecretContext: nameIsSecret);
            if (!ReferenceEquals(newVal, p.Value)) { changed = true; props[i] = new LogEventProperty(p.Name, newVal); }
            else props[i] = p;
        }
        return changed ? new StructureValue(props, st.TypeTag) : st;
    }

    private DictionaryValue TransformDictionary(DictionaryValue dv, bool nameIsSecret)
    {
        var changed = false;
        var entries = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dv.Elements.Count);
        foreach (var kvp in dv.Elements)
        {
            var keyName = (kvp.Key.Value as string) ?? "";
            var newVal = Transform(keyName, kvp.Value, inSecretContext: nameIsSecret);
            if (!ReferenceEquals(newVal, kvp.Value)) changed = true;
            entries.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(kvp.Key, newVal));
        }
        return changed ? new DictionaryValue(entries) : dv;
    }

    private static string TruncateUtf8(string s, int maxBytes)
    {
        if (maxBytes <= 0) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= maxBytes) return s;
        var len = maxBytes;
        while (len > 0 && (bytes[len] & 0xC0) == 0x80) len--;
        return Encoding.UTF8.GetString(bytes, 0, len);
    }
}
