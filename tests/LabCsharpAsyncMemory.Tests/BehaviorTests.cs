using System.Net;
using System.Text.Json;

namespace LabCsharpAsyncMemory.Tests;

public sealed class BehaviorTests(AsyncApiFactory factory) : IClassFixture<AsyncApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Blocking_holds_workers_and_takes_longer()
    {
        BurstPayload awaited = await GetBurst("await", 16);
        BurstPayload blocked = await GetBurst("block", 16);

        Assert.Equal("await", awaited.Pattern);
        Assert.Equal("GetResult", blocked.Pattern);
        Assert.True(awaited.PeakThreads <= 1);
        Assert.True(blocked.PeakThreads > awaited.PeakThreads);
        Assert.True(blocked.ElapsedMs > awaited.ElapsedMs * 2, $"block {blocked.ElapsedMs} await {awaited.ElapsedMs}");
    }

    [Fact]
    public async Task Cancel_after_stops_the_wait()
    {
        WorkPayload cancelled = await GetWork("durationMs=4000&cancelAfterMs=200");
        WorkPayload finished = await GetWork("durationMs=400");

        Assert.False(cancelled.Completed);
        Assert.True(cancelled.Cancelled);
        Assert.True(cancelled.ElapsedMs < 1000);
        Assert.True(finished.Completed);
        Assert.False(finished.Cancelled);
    }

    [Fact]
    public async Task Scoped_buffer_returns_and_orphaned_buffer_does_not()
    {
        int first = await GetLive("/memory/scoped-buffer");
        int second = await GetLive("/memory/scoped-buffer");
        int orphan = await GetLive("/memory/orphaned-buffer");
        int again = await GetLive("/memory/orphaned-buffer");

        Assert.Equal(first, second);
        Assert.Equal(orphan + 1, again);
    }

    [Fact]
    public async Task Concat_allocates_more_than_the_builder_for_the_same_text()
    {
        AllocationPayload concat = await GetAllocation("/memory/string-concat?partCount=2000");
        AllocationPayload builder = await GetAllocation("/memory/string-builder?partCount=2000");

        Assert.Equal(concat.Length, builder.Length);
        Assert.Equal(2000, concat.Length);
        Assert.True(concat.AllocatedBytes > builder.AllocatedBytes * 5);
    }

    [Fact]
    public async Task Invalid_query_names_the_field_and_the_allowed_range()
    {
        HttpResponseMessage burst = await _client.GetAsync("/async/wait-burst?waitMode=spin&waitCount=16");
        HttpResponseMessage duration = await _client.GetAsync("/async/cancellable-wait?durationMs=0");
        HttpResponseMessage cancel = await _client.GetAsync("/async/cancellable-wait?durationMs=400&cancelAfterMs=0");
        HttpResponseMessage parts = await _client.GetAsync("/memory/string-concat?partCount=9000");

        Assert.Equal(HttpStatusCode.BadRequest, burst.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duration.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, parts.StatusCode);

        string burstBody = await burst.Content.ReadAsStringAsync();
        string durationBody = await duration.Content.ReadAsStringAsync();
        string cancelBody = await cancel.Content.ReadAsStringAsync();
        string partsBody = await parts.Content.ReadAsStringAsync();

        Assert.Contains("waitMode must be await or block", burstBody, StringComparison.Ordinal);
        Assert.Contains("durationMs must be from 1 to 10000", durationBody, StringComparison.Ordinal);
        Assert.Contains("cancelAfterMs must be from 1 to 10000", cancelBody, StringComparison.Ordinal);
        Assert.Contains("partCount must be from 1 to 5000", partsBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duration_parameter_is_a_single_integer_in_the_openapi_document()
    {
        HttpResponseMessage response = await _client.GetAsync("/openapi/v1.json");
        _ = response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement parameters = document.RootElement
            .GetProperty("paths")
            .GetProperty("/async/cancellable-wait")
            .GetProperty("get")
            .GetProperty("parameters");

        JsonElement duration = parameters.EnumerateArray().First(item => item.GetProperty("name").GetString() == "durationMs");
        JsonElement type = duration.GetProperty("schema").GetProperty("type");

        Assert.Equal(JsonValueKind.String, type.ValueKind);
        Assert.Equal("integer", type.GetString());
        Assert.True(duration.GetProperty("required").GetBoolean());
    }

    [Fact]
    public async Task Swagger_ui_is_available_in_development()
    {
        HttpResponseMessage response = await _client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<BurstPayload> GetBurst(string mode, int count)
    {
        HttpResponseMessage response = await _client.GetAsync($"/async/wait-burst?waitMode={mode}&waitCount={count}");
        _ = response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        return new BurstPayload(
            root.GetProperty("pattern").GetString()!,
            root.GetProperty("peakThreads").GetInt32(),
            root.GetProperty("elapsedMs").GetInt64());
    }

    private async Task<WorkPayload> GetWork(string query)
    {
        HttpResponseMessage response = await _client.GetAsync($"/async/cancellable-wait?{query}");
        _ = response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        return new WorkPayload(
            root.GetProperty("completed").GetBoolean(),
            root.GetProperty("cancelled").GetBoolean(),
            root.GetProperty("elapsedMs").GetInt64());
    }

    private async Task<int> GetLive(string path)
    {
        HttpResponseMessage response = await _client.GetAsync(path);
        _ = response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("liveBuffers").GetInt32();
    }

    private async Task<AllocationPayload> GetAllocation(string path)
    {
        HttpResponseMessage response = await _client.GetAsync(path);
        _ = response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        return new AllocationPayload(root.GetProperty("length").GetInt32(), root.GetProperty("allocatedBytes").GetInt64());
    }

    private sealed record BurstPayload(string Pattern, int PeakThreads, long ElapsedMs);

    private sealed record WorkPayload(bool Completed, bool Cancelled, long ElapsedMs);

    private sealed record AllocationPayload(int Length, long AllocatedBytes);
}

public sealed class AsyncApiFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>;
