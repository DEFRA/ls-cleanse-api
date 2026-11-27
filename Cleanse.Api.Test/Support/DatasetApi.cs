using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cleanse.Core.Contracts;

namespace Cleanse.Api.Test.Support;

/// <summary>
/// Drives SPOL datasets over HTTP as ls-cleanse-ui and the connector do: create one, load it a
/// page at a time with its key, then process it.
/// </summary>
internal static class DatasetApi
{
    public const string KeyHeader = "x-dataset-key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<(Guid Id, string Key)> CreateAsync(HttpClient client, string? name = null)
    {
        var response = await client.PostJsonAsync("/v1/datasets", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadJsonAsync();
        return (created.GetProperty("dataset").GetProperty("id").GetGuid(), created.GetProperty("key").GetString()!);
    }

    /// <summary>One page of a load. Rows without an item ID are numbered from 1, in order.</summary>
    public static object Page(
        IReadOnlyList<SpolDatasetRowDto> rows, Guid? loadId = null, int page = 1, int pageCount = 1, int? totalRows = null) =>
        new
        {
            loadId = loadId ?? Guid.NewGuid(),
            page,
            pageCount,
            totalRows = totalRows ?? rows.Count,
            rows = rows.Select((row, index) => row.ItemId == 0 ? row with { ItemId = index + 1 } : row).ToArray()
        };

    public static async Task<HttpResponseMessage> LoadAsync(HttpClient client, Guid id, string? key, object page)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/datasets/{id}")
        {
            Content = JsonContent.Create(page, options: Json)
        };

        if (key is not null)
        {
            request.Headers.Add(KeyHeader, key);
        }

        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> GetWithKeyAsync(HttpClient client, Guid id, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/datasets/{id}");
        request.Headers.Add(KeyHeader, key);
        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> ProcessAsync(HttpClient client, Guid id, string groupId) =>
        client.PostJsonAsync($"/v1/datasets/{id}/process", new { groupId });

    /// <summary>A new dataset holding <paramref name="rows"/>, in one page, and processing started.</summary>
    public static async Task<(Guid DatasetId, Guid JobId)> ImportAsync(
        HttpClient client, string groupId, params SpolDatasetRowDto[] rows)
    {
        var (id, key) = await CreateAsync(client);
        var loaded = await LoadAsync(client, id, key, Page(rows));
        Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
        Assert.Equal("ready", (await loaded.ReadJsonAsync()).GetProperty("status").GetString());

        var jobId = await StartProcessingAsync(client, id, groupId);
        return (id, jobId);
    }

    public static async Task<Guid> StartProcessingAsync(HttpClient client, Guid id, string groupId)
    {
        var response = await ProcessAsync(client, id, groupId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> WaitForImportAsync(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var job = await (await client.GetAsync($"/v1/imports/{jobId}")).ReadJsonAsync();
            if (job.GetProperty("status").GetString() is "completed" or "failed" or "abandoned")
            {
                return job;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Import {jobId} did not finish.");
    }
}
