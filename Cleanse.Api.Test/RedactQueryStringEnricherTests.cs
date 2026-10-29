using Cleanse.Utils.Logging;
using Elastic.CommonSchema;
using Elastic.CommonSchema.Serilog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Cleanse.Api.Test;

/// <summary>
/// The ECS HTTP context enricher records the full URL, its query string and the referer. The issues
/// search puts a caseworker's search term in the query string, so these must leave the logs with
/// the path only.
/// </summary>
public class RedactQueryStringEnricherTests
{
    private const string Term = "Keeper-Surname";

    [Fact]
    public void The_query_string_is_removed_from_every_url_field_and_the_referer()
    {
        var logEvent = EventWithHttpContext(new SpecialProperties.HttpContextEnrichments
        {
            Url = new Url
            {
                Path = "/v1/issues",
                Query = $"groupId=cts-sam&searchTerm={Term}",
                Full = $"https://api/v1/issues?groupId=cts-sam&searchTerm={Term}",
                Original = $"https://api/v1/issues?groupId=cts-sam&searchTerm={Term}"
            },
            Http = new Http { RequestMethod = "GET", RequestReferrer = $"https://ui/issues?searchTerm={Term}" }
        });

        new RedactQueryStringEnricher().Enrich(logEvent, new LogEventPropertyFactory());

        var context = Context(logEvent);
        Assert.Null(context.Url!.Query);
        Assert.Equal("https://api/v1/issues", context.Url.Full);
        Assert.Equal("https://api/v1/issues", context.Url.Original);
        Assert.Equal("/v1/issues", context.Url.Path);
        Assert.Equal("https://ui/issues", context.Http!.RequestReferrer);
        Assert.Equal("GET", context.Http.RequestMethod);
    }

    [Fact]
    public void A_url_without_a_query_string_is_left_alone()
    {
        var logEvent = EventWithHttpContext(new SpecialProperties.HttpContextEnrichments
        {
            Url = new Url { Path = "/health", Full = "https://api/health", Original = "https://api/health" }
        });

        new RedactQueryStringEnricher().Enrich(logEvent, new LogEventPropertyFactory());

        var context = Context(logEvent);
        Assert.Equal("https://api/health", context.Url!.Full);
        Assert.Null(context.Url.Query);
        Assert.Null(context.Http);
    }

    [Fact]
    public void An_event_outside_a_request_is_ignored()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplateParser().Parse("background work"), []);

        new RedactQueryStringEnricher().Enrich(logEvent, new LogEventPropertyFactory());

        Assert.Empty(logEvent.Properties);
    }

    [Fact]
    public void The_service_logger_strips_query_strings_before_they_are_written()
    {
        // Drives the real CdpLogging configuration end to end: the main logger's only sink is a
        // file written by the production ECS formatter, so what lands in the file is what
        // OpenSearch would receive.
        var path = Path.Combine(Path.GetTempPath(), $"cleanse-log-{Guid.NewGuid():N}.json");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Serilog:WriteTo:0:Name"] = "File",
                    ["Serilog:WriteTo:0:Args:path"] = path,
                    ["Serilog:WriteTo:0:Args:formatter"] =
                        "Elastic.CommonSchema.Serilog.EcsTextFormatter, Elastic.CommonSchema.Serilog"
                })
                .Build();
            var context = new HostBuilderContext(new Dictionary<object, object>()) { Configuration = configuration };
            var loggerConfiguration = new LoggerConfiguration();
            CdpLogging.Configuration(context, loggerConfiguration);

            using (var logger = loggerConfiguration.CreateLogger())
            {
                // Written as the HTTP context enricher writes it, as a scalar holding the object.
                logger.Write(EventWithHttpContext(new SpecialProperties.HttpContextEnrichments
                {
                    Url = new Url
                    {
                        Path = "/v1/issues",
                        Query = $"searchTerm={Term}",
                        Full = $"https://api/v1/issues?searchTerm={Term}",
                        Original = $"https://api/v1/issues?searchTerm={Term}"
                    },
                    Http = new Http { RequestMethod = "GET", RequestReferrer = $"https://ui/issues?searchTerm={Term}" }
                }));
            }

            // The sink still holds the file for writing, so the reader must allow that.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var written = reader.ReadToEnd();
            Assert.Contains("\"path\":\"/v1/issues\"", written, StringComparison.Ordinal);
            Assert.DoesNotContain(Term, written, StringComparison.Ordinal);
            Assert.DoesNotContain("\"query\"", written, StringComparison.Ordinal);
        }
        finally
        {
            // CdpLogging owns the sub-logger holding the file for the life of the process, as it
            // does in production, so the handle may still be open here.
            try
            {
                System.IO.File.Delete(path);
            }
            catch (IOException)
            {
                // Left for the temp directory to collect.
            }
        }
    }

    private static LogEvent EventWithHttpContext(SpecialProperties.HttpContextEnrichments enrichments) =>
        new(
            DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplateParser().Parse("request completed"),
            [new LogEventProperty(SpecialProperties.SpecialKeys.HttpContext, new ScalarValue(enrichments))]);

    private static SpecialProperties.HttpContextEnrichments Context(LogEvent logEvent) =>
        Assert.IsType<SpecialProperties.HttpContextEnrichments>(
            Assert.IsType<ScalarValue>(logEvent.Properties[SpecialProperties.SpecialKeys.HttpContext]).Value);

    private sealed class LogEventPropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }
}
