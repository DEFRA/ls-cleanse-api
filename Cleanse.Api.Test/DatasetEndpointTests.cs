using System.Net;
using System.Text;
using System.Text.Json;
using Cleanse.Api.Test.Support;
using Cleanse.Core.Contracts;

namespace Cleanse.Api.Test;

/// <summary>
/// SPOL datasets over HTTP: created by an admin, loaded a page at a time with the dataset's key,
/// renamed, deleted and processed. The front end calls these for both the admin and the connector.
/// </summary>
public class DatasetEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static SpolDatasetRowDto Item(int itemId) => new()
    {
        ItemId = itemId,
        Cph = $"01/036/{itemId:D4}",
        RuleNo = "1",
        Status = "New",
        DateReceived = "2026-04-01T00:00:00+01:00"
    };

    private static SpolDatasetRowDto[] Items(int from, int count) => [.. Enumerable.Range(from, count).Select(Item)];

    private async Task<JsonElement> GetAsync(HttpClient client, Guid id) =>
        await (await client.GetAsync($"/v1/datasets/{id}")).ReadJsonAsync();

    [Fact]
    public async Task Creating_a_dataset_gives_its_key_once()
    {
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.PostJsonAsync("/v1/datasets", new { name = "  November export  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var created = await response.ReadJsonAsync();
        var id = created.GetProperty("dataset").GetProperty("id").GetGuid();
        var key = created.GetProperty("key").GetString()!;
        Assert.StartsWith("cleanse_ds_", key, StringComparison.Ordinal);
        Assert.True(key.Length > 50, key.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal($"/v1/datasets/{id}", response.Headers.Location?.ToString());

        var dataset = await GetAsync(client, id);
        Assert.Equal("November export", dataset.GetProperty("name").GetString());
        Assert.Equal("empty", dataset.GetProperty("status").GetString());
        Assert.Equal(0, dataset.GetProperty("rowCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, dataset.GetProperty("lastProcessing").ValueKind);

        var listed = await (await client.GetAsync("/v1/datasets?pageSize=500")).ReadJsonAsync();
        Assert.Contains(listed.GetProperty("items").EnumerateArray(), d => d.GetProperty("id").GetGuid() == id);
        Assert.DoesNotContain(key, dataset.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, listed.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_name_a_dataset_is_named_after_when_it_was_created()
    {
        using var client = factory.CreateAuthenticatedClient();

        var (id, _) = await DatasetApi.CreateAsync(client);

        Assert.StartsWith("SPOL ", (await GetAsync(client, id)).GetProperty("name").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dataset_can_be_renamed_at_the_version_it_was_read()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (id, _) = await DatasetApi.CreateAsync(client, "November export");
        var before = await GetAsync(client, id);
        var version = before.GetProperty("version").GetInt32();

        var renamed = await client.PutJsonAsync($"/v1/datasets/{id}/name", new { name = "SPOL 27 Nov", expectedVersion = version });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var after = await renamed.ReadJsonAsync();
        Assert.Equal("SPOL 27 Nov", after.GetProperty("name").GetString());
        Assert.True(after.GetProperty("updatedAtUtc").GetDateTimeOffset() >= before.GetProperty("updatedAtUtc").GetDateTimeOffset());
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PutJsonAsync($"/v1/datasets/{id}/name", new { name = "Again", expectedVersion = version })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutJsonAsync($"/v1/datasets/{id}/name", new { name = "   ", expectedVersion = version + 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutJsonAsync($"/v1/datasets/{id}/name", new { name = new string('x', 101), expectedVersion = version + 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutJsonAsync($"/v1/datasets/{Guid.NewGuid()}/name", new { name = "Nobody", expectedVersion = 1 })).StatusCode);
    }

    [Fact]
    public async Task A_load_replaces_the_rows_and_the_dataset_is_ready_once_all_of_them_arrive()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (id, key) = await DatasetApi.CreateAsync(client);
        var first = Guid.NewGuid();

        var page1 = await (await DatasetApi.LoadAsync(client, id, key,
            DatasetApi.Page(Items(1, 2), first, page: 1, pageCount: 2, totalRows: 3))).ReadJsonAsync();
        Assert.Equal("loading", page1.GetProperty("status").GetString());
        Assert.Equal(2, page1.GetProperty("rowCount").GetInt32());
        Assert.Equal(2, page1.GetProperty("rowsInPage").GetInt32());
        Assert.Equal(3, page1.GetProperty("totalRows").GetInt32());

        // Sent twice, a page changes nothing.
        var again = await (await DatasetApi.LoadAsync(client, id, key,
            DatasetApi.Page(Items(1, 2), first, page: 1, pageCount: 2, totalRows: 3))).ReadJsonAsync();
        Assert.Equal(2, again.GetProperty("rowCount").GetInt32());

        var page2 = await (await DatasetApi.LoadAsync(client, id, key,
            DatasetApi.Page(Items(3, 1), first, page: 2, pageCount: 2, totalRows: 3))).ReadJsonAsync();
        Assert.Equal("ready", page2.GetProperty("status").GetString());
        Assert.Equal(3, page2.GetProperty("rowCount").GetInt32());

        var seen = await (await DatasetApi.GetWithKeyAsync(client, id, key)).ReadJsonAsync();
        Assert.Equal("ready", seen.GetProperty("status").GetString());
        Assert.Equal(3, seen.GetProperty("rowCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, seen.GetProperty("lastLoadedAtUtc").ValueKind);

        // A new load's first page replaces the rows, and the older load's pages are refused.
        var replaced = await (await DatasetApi.LoadAsync(client, id, key,
            DatasetApi.Page(Items(10, 1), page: 1, pageCount: 2, totalRows: 2))).ReadJsonAsync();
        Assert.Equal("loading", replaced.GetProperty("status").GetString());
        Assert.Equal(1, replaced.GetProperty("rowCount").GetInt32());

        var stale = await DatasetApi.LoadAsync(client, id, key,
            DatasetApi.Page(Items(3, 1), first, page: 2, pageCount: 2, totalRows: 3));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("dataset.load_superseded", (await stale.ReadJsonAsync()).GetProperty("code").GetString());
        Assert.Equal(1, (await GetAsync(client, id)).GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public async Task A_wrong_key_and_an_unknown_dataset_get_the_same_answer_and_change_nothing()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (id, key) = await DatasetApi.CreateAsync(client);
        var (_, otherKey) = await DatasetApi.CreateAsync(client);

        var wrongKey = await DatasetApi.LoadAsync(client, id, otherKey, DatasetApi.Page(Items(1, 1)));
        var unknown = await DatasetApi.LoadAsync(client, Guid.NewGuid(), key, DatasetApi.Page(Items(1, 1)));
        var noKey = await DatasetApi.LoadAsync(client, id, null, DatasetApi.Page(Items(1, 1)));

        foreach (var response in new[] { wrongKey, unknown, noKey })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var problem = await response.ReadJsonAsync();
            Assert.Equal("dataset.unauthorised", problem.GetProperty("code").GetString());
            Assert.Equal("The dataset id or key is not valid.", problem.GetProperty("detail").GetString());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await DatasetApi.GetWithKeyAsync(client, id, otherKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DatasetApi.GetWithKeyAsync(client, Guid.NewGuid(), key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1/datasets/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(0, (await GetAsync(client, id)).GetProperty("rowCount").GetInt32());
    }

    [Theory]
    [InlineData("""{ "itemId": 6, "surprise": "x" }""", "Item 6", "surprise")]
    [InlineData("""{ "itemId": 7, "dateReceived": "2026-04-01T00:00:00" }""", "Item 7", "dateReceived")]
    [InlineData("""{ "itemId": 8, "cph": 123 }""", "Item 8", "cph")]
    [InlineData("""{ "cph": "01/036/8000" }""", "Row 2", "itemId")]
    public async Task An_invalid_row_refuses_the_whole_page_naming_the_item_and_property(
        string row, string item, string property)
    {
        using var client = factory.CreateAuthenticatedClient();
        var (id, key) = await DatasetApi.CreateAsync(client);
        var body = $$"""
            { "loadId": "{{Guid.NewGuid()}}", "page": 1, "pageCount": 1, "totalRows": 2,
              "rows": [ { "itemId": 5, "cph": "01/036/8000" }, {{row}} ] }
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/datasets/{id}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(DatasetApi.KeyHeader, key);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = (await response.ReadJsonAsync()).GetProperty("detail").GetString();
        Assert.Contains(item, detail, StringComparison.Ordinal);
        Assert.Contains(property, detail, StringComparison.Ordinal);
        Assert.Equal(0, (await GetAsync(client, id)).GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public async Task A_page_of_more_than_500_rows_is_too_large()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (id, key) = await DatasetApi.CreateAsync(client);

        var response = await DatasetApi.LoadAsync(client, id, key, DatasetApi.Page(Items(1, 501)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_page_must_be_json()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (id, key) = await DatasetApi.CreateAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/datasets/{id}")
        {
            Content = new StringContent("itemId,cph\n1,01/036/8000", Encoding.UTF8, "text/csv")
        };
        request.Headers.Add(DatasetApi.KeyHeader, key);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Only_a_ready_dataset_can_be_processed()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (empty, _) = await DatasetApi.CreateAsync(client);
        var (loading, key) = await DatasetApi.CreateAsync(client);
        await DatasetApi.LoadAsync(client, loading, key, DatasetApi.Page(Items(1, 1), pageCount: 2, totalRows: 2));

        foreach (var id in new[] { empty, loading })
        {
            var response = await DatasetApi.ProcessAsync(client, id, $"ready-{Guid.NewGuid():N}");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("dataset.not_ready", (await response.ReadJsonAsync()).GetProperty("code").GetString());
        }

        Assert.Equal(HttpStatusCode.NotFound,
            (await DatasetApi.ProcessAsync(client, Guid.NewGuid(), "cts-sam")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_dataset_keeps_the_reports_of_its_processing()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (id, jobId) = await DatasetApi.ImportAsync(client, $"delete-{Guid.NewGuid():N}", Item(1));
        await DatasetApi.WaitForImportAsync(client, jobId);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/v1/datasets/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1/datasets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1/imports/{jobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1/imports/{jobId}/rejections")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/v1/datasets/{id}")).StatusCode);
    }

    [Fact]
    public async Task Only_the_front_end_can_reach_datasets()
    {
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/datasets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostJsonAsync("/v1/datasets", new { })).StatusCode);
    }
}

/// <summary>Owns its own host, because the reset clears everything else in it.</summary>
public class DatasetResetTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task The_reset_keeps_datasets_but_not_their_processing()
    {
        var client = factory.CreateResetClient();
        var (id, jobId) = await DatasetApi.ImportAsync(client, $"reset-{Guid.NewGuid():N}",
            new SpolDatasetRowDto { ItemId = 1, Cph = "01/036/8000", RuleNo = "1", Status = "New" });
        await DatasetApi.WaitForImportAsync(client, jobId);

        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync("/v1/maintenance/reset", new { })).StatusCode);

        var dataset = await (await client.GetAsync($"/v1/datasets/{id}")).ReadJsonAsync();
        Assert.Equal("ready", dataset.GetProperty("status").GetString());
        Assert.Equal(1, dataset.GetProperty("rowCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, dataset.GetProperty("lastProcessing").ValueKind);
    }
}
