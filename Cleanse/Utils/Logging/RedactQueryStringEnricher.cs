using Elastic.CommonSchema.Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Cleanse.Utils.Logging;

/// <summary>
/// Strips query strings from the URL fields the ECS HTTP context enricher adds to every log event.
/// The issues search carries whatever a caseworker typed, and the Referer header carries the
/// previous page's query string, so without this a keeper's surname or postcode could reach the
/// logs. Must be registered after <c>WithEcsHttpContext</c>, whose property it rewrites.
/// </summary>
public sealed class RedactQueryStringEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (!logEvent.Properties.TryGetValue(SpecialProperties.SpecialKeys.HttpContext, out var property)
            || property is not ScalarValue { Value: SpecialProperties.HttpContextEnrichments context })
        {
            return;
        }

        if (context.Url is { } url)
        {
            url.Query = null;
            url.Full = StripQuery(url.Full);
            url.Original = StripQuery(url.Original);
        }

        if (context.Http is { } http)
        {
            http.RequestReferrer = StripQuery(http.RequestReferrer);
        }
    }

    internal static string? StripQuery(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var index = value.IndexOf('?', StringComparison.Ordinal);
        return index < 0 ? value : value[..index];
    }
}
